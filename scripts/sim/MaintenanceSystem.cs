using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 建筑老化与修缮系统（每日结算，各量按月值 1/30）：
/// 人造建筑逐日老化，天然建筑固定不变；
/// 公共设施由修缮匠维护（官府出料钱）；住宅/工商建筑由居住者按人头集资修缮（以税养屋）；
/// 完好度归零则坍塌拆除。
/// </summary>
public class MaintenanceSystem
{
    private const float AgingPerMonth = 0.7f;
    /// <summary>每名修缮匠每月修复量。</summary>
    private const float RepairPerWorker = 25f;
    /// <summary>每名修缮匠每月官府料钱。</summary>
    private const double RepairWorkerCost = 1.0;
    /// <summary>居住者集资每月修复量。</summary>
    private const float ResidentRepairAmount = 5f;
    /// <summary>每位居住者每月修缮摊派。</summary>
    private const double RepairFeePerResident = 0.15;

    private const int Days = GameClock.DaysPerMonth;

    public void TickDay(GameState gs)
    {
        foreach (var b in gs.Buildings.Values)
            if (!b.Def.Natural)
                b.Condition = Math.Max(0f, b.Condition - AgingPerMonth / Days);

        RepairOfficial(gs);
        RepairPrivate(gs);
        Collapse(gs);
    }

    /// <summary>公共设施：修缮匠逐座抢修最破的官方建筑，直到当日工量用尽（料钱记账）。</summary>
    private static void RepairOfficial(GameState gs)
    {
        int repairers = 0;
        foreach (var c in gs.Citizens.Values)
            if (c.JobKind == JobKind.Repairer)
                repairers++;
        if (repairers == 0)
            return;

        double cost = repairers * RepairWorkerCost / Days;
        gs.Money -= cost;
        gs.Ledger.Add("修缮料钱", -cost);

        float budget = repairers * RepairPerWorker / Days;
        while (budget > 0f)
        {
            BuildingInstance worst = null;
            foreach (var b in gs.Buildings.Values)
                if (b.Def.Category == "official" && b.Condition < 100f
                    && (worst == null || b.Condition < worst.Condition))
                    worst = b;
            if (worst == null)
                break;

            float amount = Math.Min(budget, 100f - worst.Condition);
            worst.Condition += amount;
            budget -= amount;
        }
    }

    /// <summary>住宅/工商：居住者按人头出修缮钱，无人居住则任其荒废。</summary>
    private static void RepairPrivate(GameState gs)
    {
        var residents = new Dictionary<int, List<Citizen>>();
        foreach (var c in gs.Citizens.Values)
        {
            if (c.HomeId < 0)
                continue;
            if (!residents.TryGetValue(c.HomeId, out var list))
                residents[c.HomeId] = list = new List<Citizen>();
            list.Add(c);
        }

        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "grown" || b.Condition >= 100f)
                continue;
            if (!residents.TryGetValue(b.Id, out var list))
                continue;

            foreach (var c in list)
                c.Money = Math.Max(0, c.Money - RepairFeePerResident / Days);
            b.Condition = Math.Min(100f, b.Condition + ResidentRepairAmount / Days);
        }
    }

    private static void Collapse(GameState gs)
    {
        List<BuildingInstance> fallen = null;
        foreach (var b in gs.Buildings.Values)
            if (b.Condition <= 0f)
                (fallen ??= new List<BuildingInstance>()).Add(b);
        if (fallen == null)
            return;

        // 坍塌拆除：居民失所由 LifecycleSystem 的无家处理流程接管
        foreach (var b in fallen)
            gs.DemolishBuilding(b);
    }
}
