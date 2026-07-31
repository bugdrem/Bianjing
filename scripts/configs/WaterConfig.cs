namespace Bianjing;

/// <summary>
/// 水系生成配置（业务归属：WorldSketch 谷线走线 + WorldGenerator 河湖落地，随存档保存）：
/// 河流由草图谷线决定——源于西北山区峰间鞍部，沿最陡下降流向东南，撞上既有河即汇流（树状水系天成）；
/// 湖泊坐落谷线途中（局部凹陷 + 谐波湖缘，可含湖中岛）。
/// 顶点高度场下水系不只标水面格，还按深度把河床顶点下压（中心深、边缘浅），
/// 岸形由地势自然涌现：平原岸缓入水成浅滩，山区河谷两侧高耸成峡谷。
/// 集中调参遵循 configs/*Config.cs 约定。
/// </summary>
public static class WaterConfig
{
    // ---- 水位与河床深度（顶点高度场）----

    /// <summary>全图统一水面高度（米，低于平原基准 0）。后期水源模块/分段水位接入时，
    /// 换掉 WaterLevelAt 的实现即可，调用方无需改动。</summary>
    public const float WaterLevel = -0.5f;

    /// <summary>某格的水面高度：本版全图统一返回 WaterLevel（分段水位预留收口）。</summary>
    public static float WaterLevelAt(Godot.Vector2I c) => WaterLevel;

    /// <summary>河床下压深度（米，水面以下）：水体边缘 / 深水中心，按离岸距离插值——
    /// 边缘浅压出浅滩带，中心深压出河槽/湖盆。</summary>
    public const float BedDepthEdge = 0.3f;
    public const float BedDepthCenter = 1.6f;

    /// <summary>达到满深度的离岸距离（米）：离岸超过此距的水格一律按中心深度下压。</summary>
    public const int BedFalloffDist = 6;

    // ---- 河宽沿程（谷线河流：源头细、下游宽）----

    /// <summary>源头河宽（米）：山间溪流。</summary>
    public const float RiverWidthSource = 4f;

    /// <summary>干流河口河宽（米）：按走线程数从源头线性渐宽至此。</summary>
    public const float RiverWidthMouth = 14f;

    /// <summary>支线河口河宽（米）：汇流入干流处的最大宽度（支线短，封顶更细）。</summary>
    public const float BranchWidthMouth = 8f;

    // ---- 湖泊（坐落谷线途中，扭曲边缘，可含湖中岛）----

    /// <summary>沿谷线生成的湖泊数量范围（座）：入水口/出水口由谷线天然连通。</summary>
    public const int RiverLakeMin = 1;
    public const int RiverLakeMax = 2;

    /// <summary>湖泊基础半径范围（米）。</summary>
    public const int BigLakeRadiusMin = 30;
    public const int BigLakeRadiusMax = 52;

    /// <summary>湖岸扭曲幅度（0-1）：多正弦谐波叠加使湖缘呈不规则湾汊。</summary>
    public const double LakeEdgeWaviness = 0.42;

    /// <summary>湖泊生成湖中岛的概率，以及每湖最多岛数。</summary>
    public const float IslandChance = 0.7f;
    public const int IslandMaxPerLake = 2;

    /// <summary>湖中岛半径相对湖半径的比例范围。</summary>
    public const float IslandRadiusMin = 0.12f;
    public const float IslandRadiusMax = 0.24f;
}
