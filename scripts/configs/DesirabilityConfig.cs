namespace Bianjing;

/// <summary>
/// 吸引力配置：道路临街加成（业务归属：DesirabilitySystem；
/// 建筑自身的加成/污染在 buildings.json 的 desirabilityBonus/pollution 字段，数据驱动不在此）。
/// </summary>
public static class DesirabilityConfig
{
    /// <summary>主路 / 辅路每格的吸引力幅度（除以密度归一系数后泼溅；桥面不加成）。</summary>
    public const float MainRoadBonus = 1.0f;
    public const float SideRoadBonus = 0.4f;

    /// <summary>幅度归一系数（1m 格密度是旧版 4m 格的 16 倍，不除会把吸引力场吹胀十几倍）。</summary>
    public const float RoadBonusScale = 16f;

    /// <summary>道路吸引力泼溅半径（米，线性衰减）。</summary>
    public const float RoadRadius = 12f;
}
