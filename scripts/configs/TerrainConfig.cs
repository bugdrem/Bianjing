using System;

namespace Bianjing;

/// <summary>
/// 地形配置：整数台地高度体系与坡度规则（业务归属：MountainGenerator 生成、GridRenderer 渲染、
/// CitizenAgent 通行、PlacementValidator 建造校验；山体仅世界生成期隆起，玩家暂不可改地形）。
/// 模型：每格一个整数高度层 Height（0=平地基准），相邻格层差即"台阶"；
/// 层差 ≤ StepClimb 的台阶村民可直接跨上（如门槛/矮坎）；更陡处按坡度规则限制通行与建造。
/// 为后续"连续平滑高度场/玩家塑形工具"（b 方案）预留：层高、最大坡度均参数化，换算集中于此。
/// </summary>
public static class TerrainConfig
{
    /// <summary>单个高度层的世界高度（米/层）：Height×此值 = 该格地面海拔。</summary>
    public const float LayerHeight = 0.5f;

    /// <summary>村民免坡度可直接跨越的最大层差（层）：≤此层差的台阶等同平地通行（题述"小于一定高度的台阶可直接上"）。</summary>
    public const int StepClimb = 1;

    /// <summary>可通行/可铺路的最大坡度（度）：相邻格层差换算成的坡角 ≤此值才准过人与铺路（题述 30°）。</summary>
    public const float MaxWalkSlopeDeg = 30f;

    /// <summary>世界生成的最高山体层数（0 基准往上）：控制山有多高。</summary>
    public const int MaxMountainLayer = 12;

    /// <summary>公式：层高 → 相邻格（水平 1 米）间的坡角（度）。用于把 LayerHeight 与 MaxWalkSlopeDeg 关联，
    /// 层差 d 层的台阶坡角 = atan(d×LayerHeight / 1m)。</summary>
    public static float SlopeDegForLayerDiff(int layerDiff) =>
        (float)(Math.Atan(Math.Abs(layerDiff) * LayerHeight / MapGrid.CellSize) * 180.0 / Math.PI);

    /// <summary>相邻两格之间能否供人通行/铺路：层差在免爬范围内，或坡角未超上限。</summary>
    public static bool Traversable(int fromLayer, int toLayer)
    {
        int d = Math.Abs(fromLayer - toLayer);
        return d <= StepClimb || SlopeDegForLayerDiff(d) <= MaxWalkSlopeDeg;
    }

    /// <summary>层数 → 世界海拔高度（米）。</summary>
    public static float LayerToWorldY(int layer) => layer * LayerHeight;
}
