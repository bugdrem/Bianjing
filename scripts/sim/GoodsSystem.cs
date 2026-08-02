using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 货品系统：
/// 日结——各户消耗口粮/柴薪 → 家中无存自动上市购买（钱货当场两讫）→ 买不到则记短缺天数并扣兴致，
/// 并对全部库存（建筑/背包/地面堆）计龄一天（为变质铺垫）；
/// 月结——农田按收获周期产粮，收成散落在田格上化为物资堆，由农夫拾运入仓。
/// 货款优先分给铺面雇工，无雇工的官营铺面收入入官库。只动库存/Money/短缺计数，不做任何雇佣与生死决策。
/// </summary>
public class GoodsSystem
{
    /// <summary>每人每日口粮 / 柴薪 / 饮水消耗（份）：转发自 EconomyConfig。</summary>
    private static double FoodPerDay => EconomyConfig.FoodPerDay;
    private static double FuelPerDay => EconomyConfig.FuelPerDay;
    private static double WaterPerDay => EconomyConfig.WaterPerDay;

    /// <summary>口粮扣减优先级：先吃主粮，再果品，最后野味。</summary>
    private static readonly string[] FoodOrder = { Goods.Grain, Goods.Fruit, Goods.Game };

    private readonly Random _rng = new();

    public void TickDay(GameState gs)
    {
        var workersOf = BuildWorkerIndex(gs);
        AgeAllInventories(gs);

        foreach (var c in gs.Citizens.Values)
        {
            gs.Buildings.TryGetValue(c.HomeId, out var home);

            if (ConsumeFood(gs, c, home, workersOf))
                c.FoodShortDays = 0;
            else
            {
                c.FoodShortDays++;
                c.Fun = Math.Max(0, c.Fun - EconomyConfig.HungerFunPenalty); // 断炊：一天比一天丧气
            }

            if (ConsumeFuel(gs, c, home, workersOf))
                c.FuelShortDays = 0;
            else
            {
                c.FuelShortDays++;
                c.Fun = Math.Max(0, c.Fun - EconomyConfig.ColdFunPenalty); // 缺柴：冷灶伤神
            }

            // 饮水：只扣家中存水（水不上市无处可买）；缺水暂不设惩罚，由储备阈值驱动居民去井/河边打水
            home?.TakeGoods(Goods.Water, WaterPerDay);

            ConsumeTierNeeds(gs, c, home, workersOf);
        }
    }

    /// <summary>里程碑分级需求（只限成人）：县城要副食、州城要酒馔、京城要器用——
    /// 候选货品任一满足即可，家中无存则上市购买（成品消费端由此打通），断供扣兴致。</summary>
    private static void ConsumeTierNeeds(GameState gs, Citizen c, BuildingInstance home,
        Dictionary<int, List<Citizen>> workersOf)
    {
        if (c.IsChild)
            return;
        foreach (var need in Milestones.TierNeeds)
        {
            if (gs.MilestoneLevel < need.MilestoneRequired)
                break; // TierNeeds 按里程碑升序排列，后面的更不满足
            double left = need.PerDay;
            foreach (var id in need.GoodsIds)
            {
                if (home != null)
                    left -= home.TakeGoods(id, left);
                if (left > 0.0001)
                    left -= BuyGoods(gs, c, id, left, workersOf);
                if (left <= 0.0001)
                    break;
            }
            if (left > 0.0001)
                c.Fun = Math.Max(0, c.Fun - need.FunPenalty); // 断供：日子没滋味
        }
    }

    /// <summary>月结：产业建筑（粮田/采矿场/制盐厂）到期收获——产量=在岗工人×每人产量，产物由定义 ProduceGoods 指定，
    /// 收成均分散落在占地格上（堆满装不下的烂在地里）。</summary>
    public void TickMonth(GameState gs)
    {
        var workersOf = BuildWorkerIndex(gs);
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.HarvestMonths <= 0)
                continue;
            b.MonthsSinceHarvest++;
            if (b.MonthsSinceHarvest < b.Def.HarvestMonths)
                continue;
            b.MonthsSinceHarvest = 0;

            int workers = workersOf.TryGetValue(b.Id, out var list) ? list.Count : 0;
            double yield = workers * b.Def.YieldPerWorker * gs.TechFactor("harvest"); // 农学科技加成
            if (yield <= 0)
                continue;

            // 产物数据驱动：空串默认产粮（采矿场产矿石、制盐厂产盐）
            string goodsId = string.IsNullOrEmpty(b.Def.ProduceGoods) ? Goods.Grain : b.Def.ProduceGoods;

            // 收成散落在占地格上（典型案例三：散落地图的物资）；
            // 1m 格下逐格散会生成上百小堆（拾运与渲染都遭殃），改为集中成限堆随机散在田面
            int cellCount = b.FootX * b.FootY;
            int dropSpots = Math.Min(EconomyConfig.HarvestMaxPiles, cellCount);
            double per = yield / dropSpots;
            for (int i = 0; i < dropSpots; i++)
            {
                int dx = _rng.Next(b.FootX), dy = _rng.Next(b.FootY); // 同格重复无妨，DropOnGround 自动并堆
                gs.DropOnGround(new Godot.Vector2I(b.Origin.X + dx, b.Origin.Y + dy), goodsId, per);
            }
        }
    }

    /// <summary>全部库存计龄一天（建筑/背包/地面堆）：本批次仅记录，变质效果后期在 Inventory 上挂接。</summary>
    private static void AgeAllInventories(GameState gs)
    {
        foreach (var b in gs.Buildings.Values)
            b.Inv.AgeOneDay();
        foreach (var c in gs.Citizens.Values)
            c.Pack.AgeOneDay();
        foreach (var p in gs.Piles.Values)
            p.Inv.AgeOneDay();
    }

    /// <summary>建筑 Id → 在岗雇工列表（分货款/算产量用）。</summary>
    private static Dictionary<int, List<Citizen>> BuildWorkerIndex(GameState gs)
    {
        var map = new Dictionary<int, List<Citizen>>();
        foreach (var c in gs.Citizens.Values)
        {
            if (c.JobKind != JobKind.Employed || c.WorkplaceId < 0)
                continue;
            if (!map.TryGetValue(c.WorkplaceId, out var list))
                map[c.WorkplaceId] = list = new List<Citizen>();
            list.Add(c);
        }
        return map;
    }

    /// <summary>吃饭：先掏家中存粮（粮→果→野味），不够再上市购买；返回是否吃饱。</summary>
    private bool ConsumeFood(GameState gs, Citizen c, BuildingInstance home, Dictionary<int, List<Citizen>> workersOf)
    {
        double need = FoodPerDay;
        if (home != null)
        {
            foreach (var id in FoodOrder)
            {
                need -= home.TakeGoods(id, need);
                if (need <= 0.0001)
                    return true;
            }
        }
        foreach (var id in FoodOrder)
        {
            need -= BuyGoods(gs, c, id, need, workersOf);
            if (need <= 0.0001)
                return true;
        }
        return false;
    }

    /// <summary>烧柴：先掏家中柴薪，不够再上市购买；返回是否够烧。</summary>
    private bool ConsumeFuel(GameState gs, Citizen c, BuildingInstance home, Dictionary<int, List<Citizen>> workersOf)
    {
        double need = FuelPerDay;
        if (home != null)
            need -= home.TakeGoods(Goods.Wood, need);
        if (need <= 0.0001)
            return true;
        need -= BuyGoods(gs, c, Goods.Wood, need, workersOf);
        return need <= 0.0001;
    }

    /// <summary>
    /// 上市购买：从有存货的专营铺面直接买走（当日即耗，不再入家库），
    /// 买价 = 基价 × 加价倍率；货款平分给铺面雇工，无雇工则入官库记「市易收入」。
    /// 返回实际买到的份数。
    /// </summary>
    private static double BuyGoods(GameState gs, Citizen c, string goodsId, double amount,
        Dictionary<int, List<Citizen>> workersOf)
    {
        if (amount <= 0)
            return 0;

        long unitPrice = (long)(Goods.PriceOf(goodsId) * Goods.BuyMarkup);
        if (unitPrice <= 0)
            return 0;
        long affordable = c.Money / unitPrice;
        double want = Math.Min(amount, affordable);
        if (want <= 0)
            return 0;

        double bought = 0;
        foreach (var b in gs.Buildings.Values)
        {
            // 专营该货的铺面或市集（市集通卖各货），且有存货才能买
            if ((b.Specialty != goodsId && b.Def.Id != "market") || b.Inv.AmountOf(goodsId) <= 0)
                continue;
            double got = b.TakeGoods(goodsId, want - bought);
            if (got <= 0)
                continue;

            long pay = (long)(got * unitPrice);
            c.Money -= pay;
            if (workersOf.TryGetValue(b.Id, out var staff) && staff.Count > 0)
            {
                foreach (var w in staff)
                    w.Money += pay / staff.Count;
            }
            else
            {
                gs.Money += pay;
                gs.Ledger.Add("市易收入", pay);
            }

            bought += got;
            if (bought >= want - 0.0001)
                break;
        }
        return bought;
    }
}
