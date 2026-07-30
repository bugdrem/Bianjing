namespace Bianjing;

/// <summary>
/// 修缮配置：建筑老化与两条修缮线（业务归属：MaintenanceSystem，各量按月值逐日 1/DaysPerMonth 结算）。
/// 公共设施由官府雇修缮匠维护；住宅/工商由居住者按人头集资自修（以税养屋）。
/// </summary>
public static class MaintenanceConfig
{
    /// <summary>建筑每月老化量（完好度，天然建筑不老化）。</summary>
    public const float AgingPerMonth = 0.7f;

    /// <summary>每名修缮匠每月修复量 / 每月官府料钱（贯）。</summary>
    public const float RepairPerWorker = 25f;
    public const double RepairWorkerCost = 1.0;

    /// <summary>居住者集资每月修复量 / 每位居住者每月修缮摊派（贯）。</summary>
    public const float ResidentRepairAmount = 5f;
    public const double RepairFeePerResident = 0.15;
}
