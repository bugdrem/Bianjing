namespace Bianjing;

/// <summary>
/// 水系生成配置（业务归属：RiverGenerator 在侵蚀完成的成品地形上循坡走线，随存档保存）：
/// 河流不再由草图压谷决定——源自西北山区峰间鞍部，在最终高度场上沿最陡下降走线，
/// 水面高度沿程取「平滑地形的运行最小值」且不低于 MinWaterLevel（0）——
/// 水随地势逐级下降形成流向观感，河岸高差由地形自然涌现（平原浅滩、山区峡谷）。
/// 河湖只读地势不改地势，唯一例外是河床下压（把水格顶点压到本格水面之下）。
/// 湖泊坐落干流低平处：谐波湾汊湖缘按「地形低于湖面」着水，高于湖面的部分自然成岛。
/// 集中调参遵循 configs/*Config.cs 约定。
/// </summary>
public static class WaterConfig
{
    // ---- 水位模型（逐格水面高 Cell.WaterH）----

    /// <summary>全图最低水位（米）：沿程水面高的下限——「以 0 为最低点」，平原河段水面即 0。</summary>
    public const float MinWaterLevel = 0f;

    /// <summary>沿程水位平滑窗口（走线点数，奇数）：对走线上的地形高做滑动平均再取运行最小，
    /// 滤掉逐米噪声、保留地势台阶（急流/跌水观感）。</summary>
    public const int LevelSmoothWindow = 21;

    // ---- 河源与走线 ----

    /// <summary>河流条数范围（条）：源点取峰间鞍部，首条为干流，后续撞既有水体即汇流（原值×1.5 取整）。</summary>
    public const int RiverCountMin = 6;
    public const int RiverCountMax = 9;

    /// <summary>走线卡死（洼地无未访问低邻）时向东南强制滑行的最大连续步数：
    /// 超过即弃线（防在大盆地里无限爬行）。</summary>
    public const int MaxForcedSteps = 260;

    // ---- 河宽沿程（源头细、下游宽）----

    /// <summary>源头河宽（米）：山间溪流。</summary>
    public const float RiverWidthSource = 6f;

    /// <summary>干流河口河宽（米）：按走线程数从源头线性渐宽至此。</summary>
    public const float RiverWidthMouth = 20f;

    /// <summary>支线河口河宽（米）：汇流入干流处的最大宽度（支线短，封顶更细）。</summary>
    public const float BranchWidthMouth = 12f;

    /// <summary>水面渲染向岸外外扩量（米）：每格水面四边向外扩展此距嵌入邻格——
    /// 水面比岸地低，外扩后水平面从高岸下方穿过、被高出的岸地遮住，消除水陆交界的空隙与逐格锯齿。</summary>
    public const float WaterEdgeOverlap = 0.7f;

    // ---- 河床下压（唯一的地形修改：把水格顶点压到本格水面之下）----

    /// <summary>河床下压深度（米，本格水面以下）：水体边缘 / 深水中心，按离岸距离插值——
    /// 边缘浅压出浅滩带，中心深压出河槽/湖盆。</summary>
    public const float BedDepthEdge = 0.25f;
    public const float BedDepthCenter = 1.0f;

    /// <summary>达到满深度的离岸距离（米）：离岸超过此距的水格一律按中心深度下压。</summary>
    public const int BedFalloffDist = 5;

    // ---- 湖泊（坐落干流低平处，扭曲湖缘，岛屿自然涌现）----

    /// <summary>沿干流生成的湖泊数量范围（座）：入水口/出水口由干流走线天然连通（原值×1.5 取整）。</summary>
    public const int RiverLakeMin = 2;
    public const int RiverLakeMax = 3;

    /// <summary>成湖点的最高水位（米）：只在低平处成湖（山区不成湖，免湖面悬山腰）。</summary>
    public const float LakeMaxSiteLevel = 1.5f;

    /// <summary>湖盆并入容差（米）：地形高出湖面不超此值的格并入湖盆（由河床下压削到水下），
    /// 更高者才留作湖中岛/岬角——湖面取沿程运行最小水位，周围地势普遍略高，无容差则淹不成湖。</summary>
    public const float LakeFloodTolerance = 1.2f;

    /// <summary>湖泊基础半径范围（米）。</summary>
    public const int BigLakeRadiusMin = 30;
    public const int BigLakeRadiusMax = 52;

    /// <summary>湖岸扭曲幅度（0-1）：多正弦谐波叠加使湖缘呈不规则湾汊；
    /// 圈内只有「地形低于湖面」的格才着水——高地自然留成湖中岛/岬角，不再强制抠岛。</summary>
    public const double LakeEdgeWaviness = 0.42;
}
