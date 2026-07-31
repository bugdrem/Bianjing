using System;

namespace Bianjing;

/// <summary>
/// 地形配置：顶点级 float 高度场（灰度地图）与坡度规则（业务归属：MountainGenerator 生成、
/// GridRenderer 三角网格渲染、CitizenAgent 通行、PlacementValidator 建造校验、HeightField 数据）。
/// 模型：全图 (Size+1)² 顶点高度，y=0 为平原基准；水面统一 WaterConfig.WaterLevel（-0.5m），
/// 河床由水系生成按深度下压顶点（岸形随地势自然呈浅滩或陡岸）。
/// 图缘山带铺连绵群山（约半图），平原上另散布可走缓丘；
/// 建筑落位自动整平垫基（占地高差在限内即可建，放置时压平成台面）。
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

    /// <summary>世界最高海拔（米）：山脊顶上限（脊高 10 取整余量）。</summary>
    public const float MaxTerrainHeight = 11f;

    // ---- 图缘山带（两条相邻图缘的连绵群山，L 形覆盖约半图，另半为平原）----

    /// <summary>山带深度（米）：从所依两条图缘向内延伸此距——(1-300/1024)²≈0.5，恰好半图群山半图平原。</summary>
    public const float BeltDepth = 300f;

    /// <summary>山带基底最高附加高度（米）：向图缘平滑渐升，300m 爬 6m 坡度极缓可走。</summary>
    public const float BeltBaseHeight = 6f;

    /// <summary>山带边界扭曲噪声：波长（米）与推拉幅度（米），令山缘蜿蜒不成直线。</summary>
    public const int BeltNoiseWave = 160;
    public const float BeltNoiseAmp = 70f;

    // ---- 平原缓丘（可走的高低差）----

    /// <summary>缓丘最大附加高度（米）：噪声阈上平滑隆起，全部在可走坡度内。</summary>
    public const float HillAmplitude = 1.5f;

    /// <summary>缓丘噪声阈值（0-1）：噪声高于此才隆起，控制丘陵覆盖率（越高丘越稀、平地越多）。</summary>
    public const float HillThreshold = 0.62f;

    /// <summary>缓丘噪声波长（米）：越大丘体越宽缓。</summary>
    public const int HillWavelength = 48;

    // ---- 连绵山脉（起伏的山脊线，比缓丘高、坡缓可走）----

    /// <summary>山脉数量范围（条脊线，集中生在图缘山带内叠在基底上）。</summary>
    public const int MinRanges = 5;
    public const int MaxRanges = 8;

    /// <summary>山脊单条长度范围（米）：逐米蠕蜒推进。</summary>
    public const int RangeLenMin = 120;
    public const int RangeLenMax = 300;

    /// <summary>山脊半宽（米）：脊线两侧隔此距离内隆起，越远越低（余弦剖面连续无台阶）。</summary>
    public const int RangeHalfWidth = 28;

    /// <summary>山脊顶高范围（米，绝对海拔，与山带基底取高）：余弦剖面最大坡
    /// ≈ π×10/(2×28) ≈ 0.56 → 29°，恰在可走坡度上限内，群山高而仍可翻越。</summary>
    public const float RangeExtraMin = 7f;
    public const float RangeExtraMax = 10f;

    /// <summary>山脊推进方向每步抖动幅度（弧度）：越大脊线越曲折。</summary>
    public const double RangeWaver = 0.28;

    /// <summary>山脊沿脊线的高低起伏波长（米）：令山脊"连绵起伏"而非等高。</summary>
    public const int RangeUndulateWave = 40;

    /// <summary>村民可采集目标的最高海拔（米）：高于此的树视为高山景观树，不派人去砍/摘，
    /// 野物也不上（平原缓丘顶 1.5 可及，图缘山带深处为景观区）。</summary>
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
