using System;

namespace Bianjing;

/// <summary>
/// 地形配置：顶点级 float 高度场（灰度地图）与坡度规则（业务归属：WorldSketch 草图规划、
/// HydraulicEroder 水力侵蚀、WorldGenerator 生成管线、GridRenderer 渲染、CitizenAgent 通行、
/// PlacementValidator 建造校验、HeightField 数据）。
/// 生成模型（批次四十九起）：128² 内存草图先定宏观大势——西北高东南低、峰点半包围、
/// 谷线河湖、山脊连接——再上采样映射 1025² 顶点并做水力侵蚀 + fBm 细节。
/// 地形为纯地貌生成，不为通行让步；村民由 Traversable/垫基/采集豁免等机制就地适配。
/// 地形仅世界生成期成形 + 垫基修改，后期玩家升降地形复用 HeightField 顶点写接口。
/// </summary>
public static class TerrainConfig
{
    // ---- 通行 / 坡度 / 建造规则 ----

    /// <summary>免坡度判定可直接跨越的相邻格高差（米）：小台阶等同平地通行。</summary>
    public const float MaxStepHeight = 0.5f;

    /// <summary>可通行/可铺路的最大坡度（度）：相邻格高差换算成的坡角 ≤此值才准过人与铺路。</summary>
    public const float MaxWalkSlopeDeg = 30f;

    /// <summary>整平垫基允许的占地最大高差（米）：占地内最高-最低顶点超过此值不可落建筑。</summary>
    public const float MaxBuildFlattenDiff = 1.0f;

    /// <summary>世界最高海拔（米）：峰顶上限（映射/侵蚀后统一 clamp）。</summary>
    public const float MaxTerrainHeight = 64f;

    /// <summary>世界最低海拔（米）：侵蚀/噪声下掘的统一下限；卷轴画布/图缘裙板垫在其下。</summary>
    public const float MinTerrainHeight = -3f;

    // ---- 草图规划（SketchSize² 内存小图：先宏观后细节）----

    /// <summary>草图边长（格）：128 → 映射比 8（1024/128），一草图格 = 8 米。</summary>
    public const int SketchSize = 128;

    /// <summary>草图格对应的世界米数（映射比）。</summary>
    public const int SketchScale = MapGrid.Size / SketchSize;

    /// <summary>全图大势落差（米）：西北角基准高 → 东南角 0 的对角线性趋势，
    /// 保证「西北高、东南低」，河流走线天然流向东南。</summary>
    public const float TrendHeight = 6f;

    /// <summary>平原缓起伏 fBm：幅度（米）与波长（米）——替代旧缓丘，铺满全图的低频地貌。</summary>
    public const float PlainFbmAmp = 1.5f;
    public const int PlainFbmWaveMeters = 96;

    // ---- 峰点（西北半包围结构）----

    /// <summary>峰点数量范围（个）：撒在西北半包围带内，构成群山骨架（原值×1.5 取整，山地占比上调）。</summary>
    public const int PeakCountMin = 15;
    public const int PeakCountMax = 21;

    /// <summary>峰顶高度范围（米）：随机抽取（趋势与细节叠加后统一 clamp MaxTerrainHeight）。</summary>
    public const float PeakHeightMin = 30f;
    public const float PeakHeightMax = 62f;

    /// <summary>单峰高斯锥半径范围（米）：越大山体越浑厚。</summary>
    public const float PeakRadiusMin = 60f;
    public const float PeakRadiusMax = 120f;

    /// <summary>山区带深度（米）：顶点到西缘/北缘的较小距离小于此值即属半包围山区带。</summary>
    public const float MountainBandDepth = 440f;

    /// <summary>山体离图缘的最小边距系数：峰心距任一图缘 ≥ 峰半径×此值（高斯尾到图缘已衰至 ~5%），
    /// 落实「山体尽量不贴地图边缘」。</summary>
    public const float PeakEdgeMarginFactor = 1.0f;

    /// <summary>地图中心避让半径（米）：峰点/独立山不落在中心圆内，给城建留开阔腹地。</summary>
    public const float CenterExclusionRadius = 280f;

    // ---- 低矮独立山（中部/东南平原上的零星山包，不连脊）----

    /// <summary>独立山数量范围（座）：撒在山区带之外（中部/东南），避中心圆与图缘（原值×1.5 取整）。</summary>
    public const int LowHillCountMin = 5;
    public const int LowHillCountMax = 9;

    /// <summary>独立山高度范围（米）：低矮可见但不成屏障（高处仍可能超行走坡限成景观）。</summary>
    public const float LowHillHeightMin = 3f;
    public const float LowHillHeightMax = 7f;

    /// <summary>独立山高斯锥半径范围（米）。</summary>
    public const float LowHillRadiusMin = 30f;
    public const float LowHillRadiusMax = 80f;

    // ---- 山脊连接（峰对之间未被河湖拦截才连）----

    /// <summary>每峰尝试连接的最近邻峰数：连线成脊，群山连绵不成孤包。</summary>
    public const int RidgeNeighborLinks = 2;

    /// <summary>脊中部鞍部系数：脊线高度 = 两端峰高插值 × 此系数托底（中段下凹成鞍）。</summary>
    public const float RidgeSaddleFactor = 0.62f;

    /// <summary>山脊余弦横截面半宽（米）。</summary>
    public const float RidgeHalfWidth = 46f;

    /// <summary>沿脊高低起伏：幅度比例与波长（米），令山脊连绵起伏而非等高。</summary>
    public const float RidgeUndulateAmp = 0.18f;
    public const int RidgeUndulateWaveMeters = 180;

    // ---- 谷线河湖已删（批次五十）：水系改为在侵蚀完成的成品地形上循坡走线（参数见 WaterConfig），
    // 草图不再压谷/压湖盆——地形生成保持纯粹，河湖只读地势不改地势（唯一例外：河床下压）----

    // ---- fBm 细节（映射后全图叠加，消上采样平滑感）----

    /// <summary>全图高频细节 fBm：幅度（米）与波长（米）。</summary>
    public const float DetailFbmAmp = 1.1f;
    public const int DetailFbmWaveMeters = 26;

    /// <summary>细节噪声的坡度削减系数：幅度 ×= 1/(1+坡×此值)——陡坡（山腰/山脚）少叠噪声，
    /// 专治山脚毛刺；平地细节不受影响。</summary>
    public const float DetailSlopeDamp = 2f;

    // ---- 水力侵蚀（droplet 水滴模型，草图与全图两级复用）----

    /// <summary>侵蚀水滴数：全图级（1025²）/ 草图级（128²）。滴数与耗时线性，低端机可降档。</summary>
    public const int ErodeDropletsFull = 250_000;
    public const int ErodeDropletsSketch = 6_000;

    /// <summary>方向惯性（0-1）：越大水流越倾向保持原方向，冲沟越顺直。</summary>
    public const float ErodeInertia = 0.08f;

    /// <summary>携沙容量系数：容量 = max(坡度, MinSlope) × 流速 × 水量 × 此系数。</summary>
    public const float ErodeCapacityFactor = 3.5f;

    /// <summary>最小坡度下限：防止平地容量归零导致除零/停滞。</summary>
    public const float ErodeMinSlope = 0.01f;

    /// <summary>侵蚀速率（欠容时挖取比例）与沉积速率（超容时卸沙比例），均 0-1。</summary>
    public const float ErodeSpeed = 0.35f;
    public const float DepositSpeed = 0.3f;

    /// <summary>每步水量蒸发比例（0-1）：水尽则滴终止。</summary>
    public const float ErodeEvaporate = 0.02f;

    /// <summary>重力加速度系数：决定流速随落差的增长。</summary>
    public const float ErodeGravity = 4f;

    /// <summary>单滴最大步数（步长 1 格）：防洼地死循环。</summary>
    public const int ErodeMaxLifetime = 50;

    /// <summary>侵蚀笔刷半径（格）：挖沙/卸沙摊到邻域，防单点尖坑。全图级用此值，草图级用 1。</summary>
    public const int ErodeBrushRadius = 3;

    // ---- 热侵蚀（塌方松弛，水力侵蚀后收尾）----

    /// <summary>安息高差（米/邻格）：相邻顶点高差超此值才塌方（≈0.65 → 33°），
    /// 缓地形不动土——只磨平山脚/山腰毛刺，保留侵蚀冲沟纹理。</summary>
    public const float ThermalTalusDh = 0.65f;

    /// <summary>塌方迭代轮数与每轮搬运比例（0-1）。</summary>
    public const int ThermalIterations = 3;
    public const float ThermalRate = 0.5f;

    // ---- 采集豁免 ----

    /// <summary>村民可采集目标的最高海拔（米）：高于此的树视为高山景观树，不派人去砍/摘，
    /// 野物也不上（平原区可及，西北山区深处为景观区）。</summary>
    public const float ForageMaxHeight = 4.5f;

    // ---- 公式 ----

    /// <summary>公式：相邻格（水平 1 米）高差 → 坡角（度）= atan(dh / 1m)。</summary>
    public static float SlopeDegForDrop(float dh) =>
        (float)(Math.Atan(Math.Abs(dh) / MapGrid.CellSize) * 180.0 / Math.PI);

    /// <summary>相邻两格之间能否供人通行/铺路：高差在免爬范围内，或坡角未超上限。
    /// 陡岸/陡坡处坡角超上限 → 天然不可攀（对"山体挡通行"的落实）。</summary>
    public static bool Traversable(float hFrom, float hTo)
    {
        float d = Math.Abs(hFrom - hTo);
        return d <= MaxStepHeight || SlopeDegForDrop(d) <= MaxWalkSlopeDeg;
    }
}
