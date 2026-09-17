using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 建筑老化与修缮系统（每旬结算，各量按月值 1/3）：
/// 人造建筑逐旬老化，天然建筑固定不变；
/// 公共设施由修缮匠维护（官府出料钱）；住宅/工商建筑由居住者按人头集资修缮（以税养屋）；
/// 完好度归零则坍塌拆除。
/// </summary>
public class MaintenanceSystem
{
    // 全部调参转发自 EconomyConfig（调参集中在 configs 目录）
    private static float AgingPerMonth => EconomyConfig.BuildingAgingPerMonth;
    private static float RepairPerWorker => EconomyConfig.RepairPerWorker;
    private static long RepairWorkerCost => EconomyConfig.RepairWorkerCost;
    private static float ResidentRepairAmount => EconomyConfig.ResidentRepairAmount;
    private static long RepairFeePerResident => EconomyConfig.RepairFeePerResident;

    private static int Days => GameClock.DaysPerMonth;

    public void TickDay(GameState gs)
    {
        foreach (var b in gs.Buildings.Values)
            // 朝廷机构朝廷自理（批次七十七）：不老化不修缮不坍塌，与天然建筑同待遇；
            // 王爷府为开局地标（批次八十）：不设健康度，同样豁免老化
            if (!b.Def.Natural && b.Def.Category != "court" && b.Def.Id != PrinceMansionConfig.DefId)
                b.Condition = Math.Max(0f, b.Condition - AgingPerMonth / Days);

        RepairOfficial(gs);
        RepairPrivate(gs);
        Collapse(gs);

        // 批次八十七：完好度逐旬变化，每月广播一次供老化变暗渲染刷新（旧版改 Condition 从不广播，
        // 建筑变暗只靠其它事件顺带重建；逐日广播会全量重建建筑层，故取月频）
        if (++_daysSinceRefresh >= Days)
        {
            _daysSinceRefresh = 0;
            EventBus.RaiseBuildingsChanged();
        }
    }

    private static int _daysSinceRefresh;

    /// <summary>公共设施：修缮匠（受雇于修缮房）逐座抢修最破的官方建筑，直到当日工量用尽（料钱记账）。</summary>
    private static void RepairOfficial(GameState gs)
    {
        int repairers = 0;
        foreach (var c in gs.Citizens.Values)
            if (c.JobKind == JobKind.Employed && gs.Buildings.TryGetValue(c.WorkplaceId, out var wp)
                && wp.Def.Id == "repairhouse")
                repairers++;
        if (repairers == 0)
            return;

        long cost = Math.Max(1, repairers * RepairWorkerCost / Days);
        long paid = gs.PayBuildWages(cost); // 批次七十九：先发放、按实扣款（无人领则钱留官库）
        gs.Money -= paid;
        gs.Ledger.Add("修缮料钱", -paid);

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
            AccumRepair(worst, amount); // 批次九十五：累计大修量——修得越多，结构寿限折得越狠
            budget -= amount;
        }

        // 批次九十五：建筑修完后若还有余力，转去养路——此前道路只老化、无人养护。
        // 与"修最破的那一座"同策略：一次把余量全补到路况最差的一格上（均摊会让每条路都修不好）。
        if (budget > 0f)
            RepairWorstRoad(gs, budget);
    }

    /// <summary>
    /// 修缮匠养路：把剩余工量补到全城路况最差的一格。路况跨过阶段门槛时才广播重建
    /// （与 <c>TimelinessSystem.TickRoads</c> 同口径——逐日全标脏等于每天重建整张地图）。
    /// </summary>
    private static void RepairWorstRoad(GameState gs, float budget)
    {
        if (gs.RoadCells.Count == 0)
            return;

        Vector2I worst = default;
        float worstFresh = float.MaxValue;
        foreach (var c in gs.RoadCells)
        {
            float f = gs.RoadStateOf(c).Fresh;
            if (f >= 100f)
                continue;
            if (f < worstFresh)
            {
                worstFresh = f;
                worst = c;
            }
        }
        if (worstFresh >= 100f)
            return; // 全城路面都完好，工量省下

        var st = gs.RoadStateOf(worst);
        float amount = Math.Min(budget, 100f - st.Fresh);
        bool stageChanged = TimedRules.StageOf(st.Fresh) != TimedRules.StageOf(st.Fresh + amount);
        st.Fresh += amount;
        gs.RoadStates[GameState.CellIndex(worst)] = st;
        if (stageChanged)
            EventBus.RaiseCellChanged(worst); // 路面颜色随阶段变化，需要重建该分块
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

            long feeTotal = 0, paidTotal = 0;
            foreach (var c in list)
            {
                // 批次七十二：修缮摊派从家庭公产实扣（旧版扣在已停用的个人 Money 字段上=免费修缮，
                // 家庭不负担维修费，官库也失去一条回流渠道）；批次七十八：摊派款入官库（修缮服务官营），
                // 旧版扣款无收款方凭空消失
                long fee = Math.Max(1, RepairFeePerResident / Days);
                long paid = Math.Min(fee, gs.FamilyMoney(c));
                feeTotal += fee;
                paidTotal += paid;
                if (paid > 0)
                {
                    gs.TakeFromFamily(c, paid);
                    gs.Money += paid;
                    gs.Ledger.Add("修缮摊派", paid);
                }
            }
            // 批次八十七：回血按实收比例折算（旧版无条件全额回血——住户见底时等于免费维修，
            // 摊派收入与修缮服务脱钩；实收不足则建筑照常老化，终至坍塌回收）
            if (feeTotal > 0)
            {
                float before = b.Condition;
                b.Condition = Math.Min(100f, b.Condition + ResidentRepairAmount / Days * paidTotal / feeTotal);
                AccumRepair(b, b.Condition - before); // 批次九十五：住户集资修缮同样计入大修量
            }
        }
    }

    /// <summary>
    /// 累计修复量：每满 <see cref="TimelinessConfig.BuildingRepairPerRenew"/> 记为一次"大修"。
    /// 大修次数越高，结构寿限被 ×0.65^n 折得越狠——与货品"回锅/烘干"是同一套翻新机制：
    /// <b>每次翻新都透支未来</b>，所以老房子会越修越难维持，终须重建。
    /// 用"累计量"而非"每次修缮都计数"，是因为日常养护是逐旬小额回血，逐次计数会瞬间爆表。
    /// </summary>
    private static void AccumRepair(BuildingInstance b, float amount)
    {
        if (amount <= 0f)
            return;
        b.RepairAccum += amount;
        while (b.RepairAccum >= TimelinessConfig.BuildingRepairPerRenew)
        {
            b.RepairAccum -= TimelinessConfig.BuildingRepairPerRenew;
            b.RenewCount++;
        }
    }

    /// <summary>
    /// 结构寿限是否已耗尽（批次九十五）：与 <see cref="BuildingInstance.RenewCount"/> 的复合折扣联算。
    /// 天然建筑、朝廷机构、王爷府豁免（与既有"不老化"口径一致）。
    /// </summary>
    private static bool LifespanSpent(BuildingInstance b)
    {
        if (b.Def.Natural || b.Def.Category == "court" || b.Def.Id == PrinceMansionConfig.DefId)
            return false;
        float span = TimelinessConfig.BuildingSpanDays(b.Def.Category);
        if (span <= 0f)
            return false;
        int n = Math.Min(b.RenewCount, TimelinessConfig.RenewMaxCount);
        return b.UsedLifespan >= span * MathF.Pow(TimelinessConfig.RenewSpanPenalty, n);
    }

    private static void Collapse(GameState gs)
    {
        List<BuildingInstance> fallen = null;
        foreach (var b in gs.Buildings.Values)
            // 两条坍塌路径（批次九十五）：软轴破败归零（无人修缮）或硬轴寿限耗尽（结构到寿）
            if (b.Condition <= 0f || LifespanSpent(b))
                (fallen ??= new List<BuildingInstance>()).Add(b);
        if (fallen == null)
            return;

        // 坍塌拆除：居民失所由 LifecycleSystem 的无家处理流程接管
        foreach (var b in fallen)
            gs.DemolishBuilding(b);
    }
}
