using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 货品日结算：各户消耗口粮/柴薪 → 家中无存自动上市购买（钱货当场两讫）→
/// 买不到则记短缺天数并扣兴致。货款优先分给铺面雇工，无雇工的官营铺面收入入官库。
/// 农田产粮不在此处——由表现层 CitizenAgent 在农夫下工时按动作即时结算入田仓。
/// 只动 Storage/Money/短缺计数，不做任何雇佣与生死决策。
/// </summary>
public class GoodsSystem
{
    /// <summary>农田每名工人每班产粮（份，下工动作结算时入田仓）。</summary>
    public const double GrainPerWorkerShift = 0.4;

    /// <summary>每人每日口粮 / 柴薪消耗（份）。</summary>
    private const double FoodPerDay = 0.1;
    private const double FuelPerDay = 0.03;

    /// <summary>口粮扣减优先级：先吃主粮，再果品，最后野味。</summary>
    private static readonly string[] FoodOrder = { Goods.Grain, Goods.Fruit, Goods.Game };

    public void TickDay(GameState gs)
    {
        var workersOf = BuildWorkerIndex(gs);

        foreach (var c in gs.Citizens.Values)
        {
            gs.Buildings.TryGetValue(c.HomeId, out var home);

            if (ConsumeFood(gs, c, home, workersOf))
                c.FoodShortDays = 0;
            else
            {
                c.FoodShortDays++;
                c.Fun = Math.Max(0, c.Fun - 1f); // 断炊：一天比一天丧气
            }

            if (ConsumeFuel(gs, c, home, workersOf))
                c.FuelShortDays = 0;
            else
            {
                c.FuelShortDays++;
                c.Fun = Math.Max(0, c.Fun - 0.5f); // 缺柴：冷灶伤神
            }
        }
    }

    /// <summary>建筑 Id → 在岗雇工列表（分货款用）。</summary>
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

        double unitPrice = Goods.PriceOf(goodsId) * Goods.BuyMarkup;
        double affordable = c.Money > 0 ? c.Money / unitPrice : 0;
        double want = Math.Min(amount, affordable);
        if (want <= 0)
            return 0;

        double bought = 0;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Specialty != goodsId || b.Storage.GetValueOrDefault(goodsId) <= 0)
                continue;
            double got = b.TakeGoods(goodsId, want - bought);
            if (got <= 0)
                continue;

            double pay = got * unitPrice;
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
