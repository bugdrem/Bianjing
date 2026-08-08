using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 中央需求账本（第 9 项·阶段一）：城市级供需统计，作为全局可查阅的被动参考（上帝视角，暂不显示给玩家）。
/// 设计定位——账本相对静态（每日重算一次）；NPC 在各决策点（择业/建田/产业规划）主动查阅账本据此抉择，
/// 账本本身不主动指派/驱赶 NPC 转型（pull 参考，非 push 指派）。宏观叠加：各家日常口粮消耗仍走 GoodsSystem 家用逻辑，互不干扰。
/// 账本为派生数据，不入存档。
/// </summary>

/// <summary>单种货品的城市级供需快照（账本的一项）。</summary>
public class DemandEntry
{
    public string GoodsId = "";

    /// <summary>全城每日需求（份）。</summary>
    public double DailyDemand;

    /// <summary>当前全城库存（份）。</summary>
    public double Stock;

    /// <summary>库存可支撑天数（DailyDemand≤0 时记 double.PositiveInfinity）。</summary>
    public double DaysOfStock;

    /// <summary>是否短缺：有需求且可支撑天数低于 EconomyConfig.DemandShortDays。</summary>
    public bool IsShort;
}

/// <summary>中央需求账本：持有全城供需快照并提供查询 API（挂于 GameState.Demand，每日由 DemandSystem 重算）。</summary>
public class DemandLedger
{
    /// <summary>无需求货品的兜底空快照（可支撑天数无穷、不短缺）。</summary>
    private static readonly DemandEntry Empty = new() { DaysOfStock = double.PositiveInfinity };

    /// <summary>货品 id → 供需快照。</summary>
    public Dictionary<string, DemandEntry> Entries { get; } = new();

    /// <summary>取某货快照（未记账返回空快照：无需求、不短缺）。</summary>
    public DemandEntry Of(string goodsId) => Entries.GetValueOrDefault(goodsId) ?? Empty;

    /// <summary>某货库存可支撑天数（未记账返回无穷）。</summary>
    public double DaysOfStockOf(string goodsId) => Of(goodsId).DaysOfStock;

    /// <summary>某货是否短缺。</summary>
    public bool IsShort(string goodsId) => Of(goodsId).IsShort;

    /// <summary>最缺货品：短缺项中可支撑天数最小者的 id；无短缺返回空串。供后续转职/建田决策取首要缺口。</summary>
    public string MostShort()
    {
        string best = "";
        double bestDays = double.PositiveInfinity;
        foreach (var e in Entries.Values)
        {
            if (!e.IsShort || e.DaysOfStock >= bestDays)
                continue;
            bestDays = e.DaysOfStock;
            best = e.GoodsId;
        }
        return best;
    }

    /// <summary>每日重算前清空（账本全量重建，不保留昨日残值）。</summary>
    internal void Clear() => Entries.Clear();
}

/// <summary>需求账本系统（Main 持有，每日结算）：统计全城基础民生与分级需求、汇总库存、结算可支撑天数，
/// 写入 gs.Demand 供 NPC 决策时被动查阅（相对静态，不主动指派）。</summary>
public class DemandSystem
{
    public void TickDay(GameState gs)
    {
        var ledger = gs.Demand;
        ledger.Clear();

        int pop = gs.Population;
        int adults = 0;
        foreach (var c in gs.Citizens.Values)
            if (!c.IsChild)
                adults++;

        // 基础民生（全员）：早期核心即大米(grain)+柴火(wood)+水
        AddDemand(ledger, Goods.Grain, pop * EconomyConfig.FoodPerDay);
        AddDemand(ledger, Goods.Wood, pop * EconomyConfig.FuelPerDay);
        AddDemand(ledger, Goods.Water, pop * EconomyConfig.WaterPerDay);

        // 分级需求（仅成人，镜像 GoodsSystem.ConsumeTierNeeds 口径）：记于首选候选货
        foreach (var need in Milestones.TierNeeds)
        {
            if (gs.MilestoneLevel < need.MilestoneRequired)
                break; // TierNeeds 按里程碑升序排列，后面的更不满足
            if (need.GoodsIds.Length > 0)
                AddDemand(ledger, need.GoodsIds[0], adults * need.PerDay);
        }

        // 结算库存与可支撑天数
        foreach (var e in ledger.Entries.Values)
        {
            e.Stock = TotalStock(gs, e.GoodsId);
            e.DaysOfStock = e.DailyDemand > 0 ? e.Stock / e.DailyDemand : double.PositiveInfinity;
            e.IsShort = e.DailyDemand > 0 && e.DaysOfStock < EconomyConfig.DemandShortDays;
        }

        if (EconomyConfig.DemandDebugPrint)
            PrintSummary(ledger);
    }

    /// <summary>累加某货需求（无则建项）。</summary>
    private static void AddDemand(DemandLedger ledger, string goodsId, double amount)
    {
        if (amount <= 0)
            return;
        if (!ledger.Entries.TryGetValue(goodsId, out var e))
            ledger.Entries[goodsId] = e = new DemandEntry { GoodsId = goodsId };
        e.DailyDemand += amount;
    }

    /// <summary>全城库存：建筑库存 + 居民背包 + 地面堆。
    /// 批次八十七：官粮（gs.Food）不再计入——官粮是朝廷赈济储备（只进不出的单向池），
    /// 计入会使 grain 永不短缺，缺粮驱动的全部机制静默失效（开垦加速/升级折扣/缺粮招工/创业选品）。</summary>
    private static double TotalStock(GameState gs, string goodsId)
    {
        double s = 0;
        foreach (var b in gs.Buildings.Values)
            s += b.Inv.AmountOf(goodsId);
        foreach (var c in gs.Citizens.Values)
            s += c.Pack.AmountOf(goodsId);
        foreach (var p in gs.Piles.Values)
            s += p.Inv.AmountOf(goodsId);
        return s;
    }

    /// <summary>调试摘要（GD.Print，仅开发期排查用，由 EconomyConfig.DemandDebugPrint 开关）。</summary>
    private static void PrintSummary(DemandLedger ledger)
    {
        foreach (var e in ledger.Entries.Values)
        {
            string days = double.IsPositiveInfinity(e.DaysOfStock) ? "∞" : e.DaysOfStock.ToString("F1");
            GD.Print($"[需求账本] {Goods.NameOf(e.GoodsId)} 需求{e.DailyDemand:F2}/日 库存{e.Stock:F1} 可撑{days}天 {(e.IsShort ? "短缺" : "")}");
        }
        string most = ledger.MostShort();
        if (most != "")
            GD.Print($"[需求账本] 最缺：{Goods.NameOf(most)}");
    }
}
