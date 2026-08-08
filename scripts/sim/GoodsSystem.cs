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

            // 季节窗口（批次七十四一年两熟）：农田只在收获窗口内产出——窗口外（含冬季 10-12 月）
            // 归零重新播种（冬季休整不产出）；矿/盐场等非农田产业不受季节限制
            if (b.Def.Category == "field"
                && (gs.CurMonth < FarmlandConfig.HarvestStartMonth || gs.CurMonth > FarmlandConfig.HarvestEndMonth))
                continue;

            int workers = workersOf.TryGetValue(b.Id, out var list) ? list.Count : 0;
            double yield = workers * b.Def.YieldPerWorker * gs.TechFactor("harvest"); // 农学科技加成
            if (yield <= 0)
                continue;

            // 田主与技能加成（批次七十四）：田主亲自下地多收两成；在岗农夫平均经验越高收成越多
            // （平均达 SkillYieldFullExp 时封顶 +SkillYieldMaxBonus）；粮田专享（yield>0 保证 list 非空）
            if (b.Def.Category == "field")
            {
                double bonus = 0;
                if (b.OwnerCitizenId >= 0 && list.Exists(w => w.Id == b.OwnerCitizenId))
                    bonus += FarmlandConfig.OwnerYieldBonus;
                float avgExp = 0;
                foreach (var w in list)
                    avgExp += w.SkillExp;
                avgExp /= list.Count;
                bonus += FarmlandConfig.SkillYieldMaxBonus * Math.Min(1f, avgExp / FarmlandConfig.SkillYieldFullExp);
                yield *= 1 + bonus;
            }

            // 产物数据驱动：空串默认产粮（采矿场产矿石、制盐厂产盐）
            string goodsId = string.IsNullOrEmpty(b.Def.ProduceGoods) ? Goods.Grain : b.Def.ProduceGoods;

            // 田赋（批次七十三）：农田收成按比例入官粮（官粮唯一产出渠道，防饥荒永久开启致全民早亡），
            // 余下才散落田面归村民；非粮/非农田建筑不收田赋。
            if (goodsId == Goods.Grain && b.Def.Category == "field")
            {
                double tithe = yield * EconomyConfig.GrainTaxShare;
                gs.Food += tithe;
                yield -= tithe;
            }

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

        // 朝廷衙门库容月清空（批次七十七）：朝廷漕运按月拉走收购物资，衙门不积库存——
        // 朝廷收购不设上限，只受月内库容自然限制（StorageAtCap 闸门），下月恢复满额收购
        foreach (var b in gs.Buildings.Values)
            if (b.Def.Category == "court")
                b.Inv.Stacks.Clear();
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

    /// <summary>烧柴：先掏家中柴薪，不足烧废料（批次六十七：废料价低可当柴烧，燃料链 wood→scrap），
    /// 还不够再上市购买；返回是否够烧。</summary>
    private bool ConsumeFuel(GameState gs, Citizen c, BuildingInstance home, Dictionary<int, List<Citizen>> workersOf)
    {
        double need = FuelPerDay;
        if (home != null)
        {
            need -= home.TakeGoods(Goods.Wood, need);
            if (need > 0.0001)
                need -= home.TakeGoods(Goods.Scrap, need); // 柴薪不足烧废料
        }
        if (need <= 0.0001)
            return true;
        need -= BuyGoods(gs, c, Goods.Wood, need, workersOf);
        if (need <= 0.0001)
            return true;
        need -= BuyGoods(gs, c, Goods.Scrap, need, workersOf);
        return need <= 0.0001;
    }

    /// <summary>
    /// 上市购买：从有存货的专营铺面直接买走（当日即耗，不再入家库），
    /// 买价 = 基价 × 加价倍率；货款从买家家庭公产扣（另按商税率交税入官库），
    /// 卖方收款：官营入官库（批次七十五，俸禄制不回分账）、民营平分给铺面雇工家庭，无雇工则折入官库。
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
        // 商税（批次七十五）：买家按成交额另付税入官库，可买量按含税价估算防超支
        double taxRate = gs.Taxes.TradeTaxRate;
        long affordable = gs.FamilyMoney(c) / (long)(unitPrice * (1 + taxRate));
        double want = Math.Min(amount, affordable);
        if (want <= 0)
            return 0;

        double bought = 0;
        foreach (var b in gs.Buildings.Values)
        {
            // 专营/兼营该货的铺面可卖；官营生产产业（批次七十六：市集撤除后官营直售，
            // 盐/矿/木/石/曲等有产出即上柜；朝廷衙门只进不出不售），且有存货才能买
            if (b.Specialty != goodsId && !b.ExtraGoods.Contains(goodsId)
                && !(b.Def.Category == "official" && !b.Def.IsCourtBuyer && b.Def.ProduceGoods != ""))
                continue;
            double got = b.TakeGoods(goodsId, want - bought);
            if (got <= 0)
                continue;

            long pay = (long)(got * unitPrice);
            long tax = (long)(pay * taxRate);
            gs.TakeFromFamily(c, pay + tax); // 货款 + 商税由家庭公产支付
            if (tax > 0)
            {
                gs.Money += tax;
                gs.Ledger.Add("商税", tax);
            }
            // 卖方收款：官营一律入官库（批次七十五，与 PayToBuilding 对齐——官营产业收购走官库、售货也回官库，
            // 吃差价保官库平衡；官营员工为俸禄制不再分账）；民营按有雇工平分、无雇工折入官库
            if (b.Def.Category == "official")
            {
                gs.Money += pay;
                gs.Ledger.Add("市易收入", pay);
            }
            else if (workersOf.TryGetValue(b.Id, out var staff) && staff.Count > 0)
            {
                foreach (var w in staff)
                    gs.PayToFamily(w, pay / staff.Count);
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
