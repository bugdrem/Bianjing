using Godot;

namespace Bianjing;

/// <summary>
/// 水系生成配置（业务归属：FlowRouter 流向路由 / RiverNetwork 河网提取 / LakeGenerator 湖泊选址 /
/// RiverGenerator 刻水与河床下压；成果随存档保存于 Cell.HasWater/WaterH/FlowDir）。
/// 批次六十九起重构：水系彻底脱离 128² 草图（草图只留山形，不再走线），改在**成品地形**
/// （上采样 + 细节 fBm + 水力侵蚀 + 热侵蚀之后）上按真实汇流求解：
/// ① FlowRouter：优先洪水填洼 → D8 流向 → 逆洪泛序汇流累积（面积=河宽依据、洼地=天然湖盆）；
/// ② RiverNetwork：按汇流面积提河网（树状汇流、剪短枝、蛇曲平滑、河口外推出图）；
/// ③ LakeGenerator：天然洼地塘 + 三档选址湖（最低前沿洪水填充，形状贴合地形）；
/// ④ RiverGenerator：沿折线刻水 → 按洪泛拓扑序统一水位（湖面冻结、支流回灌）→ 按水体尺寸缩放河床深度。
/// 水位模型：水面 = 平滑后的沿程地形 - RiverSurfaceDrop（河岸略高于水面，有岸坡观感），
/// 且沿流向单调不增（水位按拓扑序单趟求解），下限 MinWaterLevel。
/// </summary>
public static class WaterConfig
{
    // ---- 水位模型（逐格水面高 Cell.WaterH）----

    /// <summary>全图最低水位（米）：沿程水面高的下限——「以 0 为最低点」，平原河段水面即 0。</summary>
    public const float MinWaterLevel = 0f;

    /// <summary>河面低于岸顶的量（米）：水面 = 沿程地形 - 此值，岸坡露出水面而不与水齐平。</summary>
    public const float RiverSurfaceDrop = 0.35f;

    /// <summary>沿程水位平滑窗口（折线点数，奇数）：对折线上的地形高做滑动平均再取运行最小，
    /// 滤掉逐米噪声、保留地势台阶（急流/跌水观感）。</summary>
    public const int LevelSmoothWindow = 21;

    // ---- ① 流向路由（FlowRouter：优先洪水 + D8 + 汇流累积）----

    /// <summary>路由网格降采样倍率：成品 1m 格每 Downsample×Downsample 并成一个路由格。
    /// 2 → 512² 路由格（每格 2m）：既压住细节 fBm 造成的 D8 碎网，又保留河道走向精度。</summary>
    public const int RouteDownsample = 2;

    /// <summary>优先洪水的微量递增（米/格）：填充面每向外一格抬升此值，
    /// 保证「每格都有一条严格下降的出路」，同时近似保留原始地形（非洼地区差异仅累积微增）。</summary>
    public const float FloodEpsilon = 0.0005f;

    // ---- ② 河网提取（RiverNetwork）----

    /// <summary>成河的最小汇流面积（路由格）：汇流格数达此值才认定是河道而非坡面漫流。</summary>
    public const int ChannelMinDrainCells = 45;

    /// <summary>河网最多条数（条）：按汇流面积降序取前 N 个源头，其余并入已有河道。</summary>
    public const int ChannelMaxCount = 13;

    /// <summary>河道最短长度（路由格）：短于此的支流剪掉（防地图布满碎溪）。</summary>
    public const int ChannelMinLengthCells = 55;

    // ---- 河宽（∝ 汇流面积平方根，自然界 Hack 定律）----

    /// <summary>河宽系数：宽度 = WidthCoef × √汇流面积(m²)，再钳到 [WidthMin, WidthMax]。</summary>
    public const float WidthCoef = 0.075f;

    /// <summary>河宽下限 / 上限（米）：山间溪流不至于细到断流，大江不至于宽到淹城。</summary>
    public const float WidthMin = 5f;
    public const float WidthMax = 34f;

    /// <summary>河宽滑动平均窗口（折线点数）：消掉单点面积跳变造成的宽窄突变。</summary>
    public const int WidthSmoothWindow = 9;

    // ---- 蛇曲与平滑 ----

    /// <summary>Chaikin 圆角迭代次数：D8 阶梯走线 → 圆滑曲线（每轮点数约翻倍）。</summary>
    public const int ChaikinIterations = 2;

    /// <summary>蛇曲振幅 = 河宽 × 此系数（米）：宽河摆幅大、窄溪几乎笔直。</summary>
    public const float MeanderAmpFactor = 1.5f;

    /// <summary>蛇曲波长（米）：沿弧长的正弦摆动周期，越大越舒展。</summary>
    public const int MeanderWaveMeters = 210;

    /// <summary>蛇曲后向谷底最低点的吸附比例（0-1）：纯正弦可能爬上谷壁，
    /// 混一部分「垂向最低点」把河线按回谷底。</summary>
    public const float MeanderSnap = 0.5f;

    // ---- 河口（治「出图是个小点」）----

    /// <summary>河口中心线外推出图的距离（米）：中心线末端伸到图外，
    /// 刻盘时图缘处仍是满宽圆盘——河流以宽阔水面出图，而非贴边收成一个小点。</summary>
    public const float MouthExtendMeters = 56f;

    /// <summary>河口展宽倍率：末段河宽 ×此值（喇叭口）。</summary>
    public const float MouthWidthFactor = 2.1f;

    /// <summary>河口展宽的过渡弧长（米）：自末点回溯此弧长内宽度线性渐增至展宽倍率。</summary>
    public const float MouthTaperMeters = 170f;

    // ---- ③ 湖泊（LakeGenerator：天然洼地塘 + 三档选址湖）----

    /// <summary>湖泊总座数范围（座）：大小混排（大湖 1 座 + 中湖 1~2 座 + 小塘若干）。</summary>
    public const int LakeCountMin = 3;
    public const int LakeCountMax = 6;

    /// <summary>天然洼地塘的最小填充深度（米）：优先洪水的填洼量超此值才算天然盆底（滤掉数值噪声）。</summary>
    public const float PondMinFillDepth = 0.45f;

    /// <summary>天然洼地塘的格数下限 / 上限（1m 格）：太小弃之，太大视为地形缺陷不认作塘。</summary>
    public const int PondMinCells = 400;
    public const int PondMaxCells = 60_000;

    /// <summary>选址湖三档基础半径（米）：大 / 中 / 小。目标格数 = πr² × LakeFillRatio。</summary>
    public const float LakeRadiusLargeMin = 70f;
    public const float LakeRadiusLargeMax = 110f;
    public const float LakeRadiusMediumMin = 32f;
    public const float LakeRadiusMediumMax = 58f;
    public const float LakeRadiusSmallMin = 12f;
    public const float LakeRadiusSmallMax = 26f;

    /// <summary>选址湖的形状填充率（0-1）：目标格数 = πr² × 此值——
    /// 湖形贴合地形而非正圆，同样半径实际着水面积更小。</summary>
    public const float LakeFillRatio = 0.8f;

    /// <summary>选址湖离图缘的最小距离（米）：湖不贴边，留出岸线。</summary>
    public const float LakeEdgeMargin = 90f;

    /// <summary>选址湖之间的最小间距（米）：湖心距，防连成一片。</summary>
    public const float LakeMinSeparation = 170f;

    /// <summary>选址湖的地形起伏上限（米）：候选圈内最高-最低地形高差超此值即不平整，换点。</summary>
    public const float LakeSiteMaxRelief = 4.5f;

    /// <summary>选址湖的最大水深（米）：洪水填充时地形高出种子点超此值即停止扩张（防湖面爬上山腰）。</summary>
    public const float LakeMaxDepth = 3.2f;

    /// <summary>选址湖的最大伸展半径倍率（相对等效圆半径）：防湖沿河谷拉成细长条。</summary>
    public const float LakeMaxReachFactor = 2.4f;

    /// <summary>湖泊总面积占全图上限（0-1）：全部湖加起来不超过此比例，给城建留地。</summary>
    public const float LakeTotalAreaCap = 0.055f;

    // ---- ④ 河床下压（唯一的地形修改：把水格顶点压到本格水面之下）----

    /// <summary>河床下压深度（米，本格水面以下）：水体边缘浅滩，向深水中心插值。</summary>
    public const float BedDepthEdge = 0.25f;

    /// <summary>深水中心深度的下限 / 上限（米）：窄河取下限，大湖取上限。
    /// 按水体自身的最大离岸距离（即水体「胖瘦」）在两者间插值——大湖自然成深盆，小河仍是浅槽。</summary>
    public const float BedDepthCenterMin = 1.0f;
    public const float BedDepthCenterMax = 3.5f;

    /// <summary>深度插值的离岸距离区间（米）：最大离岸距离 ≤Shallow 取下限，≥Deep 取上限。</summary>
    public const float BedDepthShallowDist = 6f;
    public const float BedDepthDeepDist = 70f;

    /// <summary>深度沿离岸距离的衰减距离（米，按水体尺寸缩放）：中心满深 → 岸边浅滩的过渡带宽度。</summary>
    public const float BedFalloffMin = 2.5f;
    public const float BedFalloffMax = 26f;
    public const float BedFalloffRatio = 0.35f;

    // ---- 渲染 / 预览 ----

    /// <summary>水面渲染向岸外外扩量（米）：每格水面只朝「邻格非水」的方向外扩此距嵌入邻格——
    /// 水面比岸地低，外扩后水平面钻到高岸下方、被岸地遮住，消除水陆交界的空隙与逐格锯齿。
    /// （不可四向无差别外扩：相邻水格会互相重叠、半透明逐层叠加出规则网格。）</summary>
    public const float WaterEdgeOverlap = 0.7f;

    /// <summary>预览图河线颜色（新游戏 128×128 俯视预览上叠加的示意河网）。</summary>
    public static readonly Color PreviewRiverColor = new(0.45f, 0.72f, 0.90f);
}
