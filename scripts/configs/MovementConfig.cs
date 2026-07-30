namespace Bianjing;

/// <summary>
/// 移动配置：基础速度、各路面系数与寻路权重公式（业务归属：CitizenAgent 移动、RoadNetwork 寻路）。
/// 米/秒制（1m 格）；脱路减速由 OffRoadFactor 惩罚，故居民自发沿路行走。
/// </summary>
public static class MovementConfig
{
    /// <summary>基础步行速度（米/秒，路面系数 1.0 时）。</summary>
    public const float BaseSpeed = 5f;

    /// <summary>脱离道路的减速惩罚系数（越小惩罚越重）。</summary>
    public const float OffRoadFactor = 0.35f;

    /// <summary>各道路种类的移速系数：主路最快、小路最慢；桥面（RoadKind.None 但 HasRoad）同辅路。</summary>
    public const float SpeedMain = 1.2f;
    public const float SpeedSide = 1.0f;
    public const float SpeedLane = 0.7f;
    public const float SpeedBridge = 1.0f;

    /// <summary>公式：脚下道路种类 → 移速系数（仅路面调用；脱路由调用方按 OffRoadFactor 处理）。</summary>
    public static float RoadSpeedFactor(RoadKind kind) => kind switch
    {
        RoadKind.Main => SpeedMain,
        RoadKind.Side => SpeedSide,
        RoadKind.Lane => SpeedLane,
        _ => SpeedBridge, // None 且 HasRoad（桥面）
    };

    /// <summary>公式：寻路权重 = 主路速度 ÷ 该路面速度（以主路为 1.0 基准，越慢代价越高，
    /// 使 AStar 最小化实际旅行时间而非几何距离）。</summary>
    public static float RoadWeight(RoadKind kind) => SpeedMain / RoadSpeedFactor(kind);
}
