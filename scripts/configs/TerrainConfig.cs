using System;

namespace Bianjing;

/// <summary>
/// 地形配置：顶点级 float 高度场（灰度地图）与坡度规则（业务归属：MountainGenerator 生成、
/// GridRenderer 三角网格渲染、CitizenAgent 通行、PlacementValidator 建造校验、HeightField 数据）。
/// 模型：全图 (Size+1)² 顶点高度，y=0 为平原基准；水面统一 WaterConfig.WaterLevel（-0.5m），
/// 河床由水系生成按深度下压顶点（岸形随地势自然呈浅滩或陡岸）。
/// 平原上散布可走缓丘与连绵山脉，另立桂林石峰（陡壁人不可攀，峰上长树）；
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

    /// <summary>世界最高海拔（米）：石峰顶上限（缓丘 1.5 + 石峰 13 取整余量）。</summary>
    public const float MaxTerrainHeight = 14.5f;

    // ---- 平原缓丘（可走的高低差）----

    /// <summary>缓丘最大附加高度（米）：噪声阈上平滑隆起，全部在可走坡度内。</summary>
    public const float HillAmplitude = 1.5f;

    /// <summary>缓丘噪声阈值（0-1）：噪声高于此才隆起，控制丘陵覆盖率（越高丘越稀、平地越多）。</summary>
    public const float HillThreshold = 0.62f;

    /// <summary>缓丘噪声波长（米）：越大丘体越宽缓。</summary>
    public const int HillWavelength = 48;

    // ---- 连绵山脉（起伏的山脊线，比缓丘高、坡缓可走）----

    /// <summary>山脉数量范围（条脊线）。</summary>
    public const int MinRanges = 2;
    public const int MaxRanges = 4;

    /// <summary>山脊单条长度范围（米）：逐米蠕蜒推进。</summary>
    public const int RangeLenMin = 120;
    public const int RangeLenMax = 300;

    /// <summary>山脊半宽（米）：脊线两侧隔此距离内隆起，越远越低（二次 falloff 连续无台阶）。</summary>
    public const int RangeHalfWidth = 14;

    /// <summary>山脊附加高度范围（米，叠在平原之上）：半宽 14m 内爬升 ≤2.5m，坡度天然可走。</summary>
    public const float RangeExtraMin = 1.5f;
    public const float RangeExtraMax = 2.5f;

    /// <summary>山脊推进方向每步抖动幅度（弧度）：越大脊线越曲折。</summary>
    public const double RangeWaver = 0.28;

    /// <summary>山脊沿脊线的高低起伏波长（米）：令山脊"连绵起伏"而非等高。</summary>
    public const int RangeUndulateWave = 40;

    // ---- 桂林石峰（孤峰，陡壁不可攀）----

    /// <summary>石峰数量范围（座）。</summary>
    public const int MinPillars = 10;
    public const int MaxPillars = 18;

    /// <summary>石峰半径范围（米）。</summary>
    public const int PillarMinRadius = 5;
    public const int PillarMaxRadius = 14;

    /// <summary>石峰相对地面的高度范围（米）。</summary>
    public const float PillarMinHeight = 8f;
    public const float PillarMaxHeight = 13f;

    /// <summary>石峰截面幂次：越大顶越平、壁越陡（超椭圆剖面 1-(d/r)^k）。</summary>
    public const float PillarShapePower = 5f;

    /// <summary>视为"峰域"的最低海拔（米，缓丘+山脉最高 ≈4，取 5 与之留隙）：
    /// 峰域格保底落树（TreeGenerator），保证"山上会生成树木"。</summary>
    public const float PillarZoneMinHeight = 5f;

    /// <summary>峰域格保底落树概率（棵/格）：不受林区噪声左右。</summary>
    public const float PillarTreeChance = 0.06f;

    /// <summary>村民可采集目标的最高海拔（米）：高于此的树视为峰上/高山景观树，不派人去砍/摘，
    /// 野物也不上（缓丘顶 1.5 + 山脉顶 2.5 皆可及，仅石峰域被隔离）。</summary>
    public const float ForageMaxHeight = 4.5f;

    // ---- 公式 ----

    /// <summary>公式：相邻格（水平 1 米）高差 → 坡角（度）= atan(dh / 1m)。</summary>
    public static float SlopeDegForDrop(float dh) =>
        (float)(Math.Atan(Math.Abs(dh) / MapGrid.CellSize) * 180.0 / Math.PI);

    /// <summary>相邻两格之间能否供人通行/铺路：高差在免爬范围内，或坡角未超上限。
    /// 石峰边缘一格落差数米，坡角远超上限 → 天然不可攀（对"山体挡通行"的落实）。</summary>
    public static bool Traversable(float hFrom, float hTo)
    {
        float d = Math.Abs(hFrom - hTo);
        return d <= MaxStepHeight || SlopeDegForDrop(d) <= MaxWalkSlopeDeg;
    }
}
