using System;

namespace Bianjing;

/// <summary>
/// 地形配置：整数台地高度体系与坡度规则（业务归属：MountainGenerator 生成、GridRenderer 渲染、
/// CitizenAgent 通行、PlacementValidator 建造校验；地形仅世界生成期成形，玩家暂不可改）。
/// 模型：每格一个整数高度层 Height——水面/河床恒 0 层，陆地基准 BaseLayers；
/// 竖直原点对齐到陆地基准（平原 = y 0 米），故水面在陆地之下 BaseLayers×LayerHeight = 0.5 米（水面 y=-0.5）。
/// 平原上散布可走缓丘，另立桂林石峰式孤峰（平顶陡壁，人不可攀，峰上长树）。
/// 相邻格层差即"台阶"：层差 ≤ StepClimb 可直接跨上；更陡处按坡度规则限制通行与建造。
/// </summary>
public static class TerrainConfig
{
    /// <summary>单个高度层的世界高度（米/层）：Height×此值 = 该格地面海拔。</summary>
    public const float LayerHeight = 0.5f;

    /// <summary>陆地基准层数：平原地面即竖直原点 y=0；水面/河床保持 0 层，落在陆地之下 BaseLayers 层
    /// （= 0.5 米，即水面 y=-0.5，暂不考虑水体流动）。</summary>
    public const int BaseLayers = 1;

    // ---- 平原缓丘（可走的高低差）----

    /// <summary>缓丘最大附加层数（丘顶 = 基准 + 此值）：全部在可走坡度内。</summary>
    public const int HillAmplitudeLayers = 3;

    /// <summary>缓丘噪声阈值（0-1）：噪声高于此才隆起，控制丘陵覆盖率（越高丘越稀，渲染实例越少）。</summary>
    public const float HillThreshold = 0.62f;

    /// <summary>缓丘噪声波长（米）：越大丘体越宽缓。</summary>
    public const int HillWavelength = 48;

    // ---- 连绵山脉（起伏的山脊线，比缓丘高、削壁后仍可走）----

    /// <summary>山脉数量范围（条脊线）。</summary>
    public const int MinRanges = 2;
    public const int MaxRanges = 4;

    /// <summary>山脊单条长度范围（米）：逐米蠕蜒推进。</summary>
    public const int RangeLenMin = 120;
    public const int RangeLenMax = 300;

    /// <summary>山脊半宽（米）：脊线两侧隔此距离内隆起，越远越低。</summary>
    public const int RangeHalfWidth = 14;

    /// <summary>山脊附加高度范围（层，叠在基准之上）：峰高 = 基准 + 此值；上限低于 PillarLayerMin 以保留可侵蚀空间。</summary>
    public const int RangeExtraMin = 3;
    public const int RangeExtraMax = 5;

    /// <summary>山脊推进方向每步抑动幅度（弧度）：越大脊线越曲折。</summary>
    public const double RangeWaver = 0.28;

    /// <summary>山脊沿脊线的高低起伏波长（米）：令山脊“连绵起伏”而非等高。</summary>
    public const int RangeUndulateWave = 40;

    // ---- 桂林石峰（孤峰柱，陡壁不可攀）----

    /// <summary>石峰数量范围（座）。</summary>
    public const int MinPillars = 10;
    public const int MaxPillars = 18;

    /// <summary>石峰半径范围（米）。</summary>
    public const int PillarMinRadius = 5;
    public const int PillarMaxRadius = 14;

    /// <summary>石峰相对地面的高度范围（层）：16~26 层即 8~13 米。</summary>
    public const int PillarMinLayers = 16;
    public const int PillarMaxLayers = 26;

    /// <summary>石峰截面幂次：越大顶越平、壁越陡（超椭圆剖面 1-(d/r)^k）。</summary>
    public const float PillarShapePower = 5f;

    /// <summary>视为"峰域"的最低层数（缓丘最高 BaseLayers+HillAmplitudeLayers=5，取 7 与之留隙）：
    /// 峰域格保底落树（TreeGenerator）、且其上树木不作村民采伐目标（人不可攀）。</summary>
    public const int PillarLayerMin = 7;

    /// <summary>峰域格保底落树概率（棵/格）：保证"山上会生成树木"，不受林区噪声左右。</summary>
    public const float PillarTreeChance = 0.06f;

    /// <summary>村民可采集目标的最高地形层：高于此层的树视为峰上景观树，不派人去砍/摘。</summary>
    public const int ForageMaxLayer = BaseLayers + HillAmplitudeLayers;

    // ---- 通行/坡度规则 ----

    /// <summary>村民免坡度可直接跨越的最大层差（层）：≤此层差的台阶等同平地通行。</summary>
    public const int StepClimb = 1;

    /// <summary>可通行/可铺路的最大坡度（度）：相邻格层差换算成的坡角 ≤此值才准过人与铺路。</summary>
    public const float MaxWalkSlopeDeg = 30f;

    /// <summary>世界最高层数（石峰顶上限 = BaseLayers+HillAmplitudeLayers+PillarMaxLayers 取整余量）。</summary>
    public const int MaxMountainLayer = 30;

    /// <summary>公式：层差 → 相邻格（水平 1 米）间的坡角（度）= atan(d×LayerHeight / 1m)。</summary>
    public static float SlopeDegForLayerDiff(int layerDiff) =>
        (float)(Math.Atan(Math.Abs(layerDiff) * LayerHeight / MapGrid.CellSize) * 180.0 / Math.PI);

    /// <summary>相邻两格之间能否供人通行/铺路：层差在免爬范围内，或坡角未超上限。
    /// 石峰边缘层差十余层，坡角远超上限 → 天然不可攀（对"山体挡通行"的落实）。</summary>
    public static bool Traversable(int fromLayer, int toLayer)
    {
        int d = Math.Abs(fromLayer - toLayer);
        return d <= StepClimb || SlopeDegForLayerDiff(d) <= MaxWalkSlopeDeg;
    }

    /// <summary>层数 → 世界海拔高度（米）：以陆地基准层为原点——平原(BaseLayers)=0 米，水面(0 层)=-0.5 米，缓丘/石峰在其上。</summary>
    public static float LayerToWorldY(int layer) => (layer - BaseLayers) * LayerHeight;

    // ---- 平地占比保障 ----

    /// <summary>平地（非水、高度=基准层）占全图的最低比例：生成收尾若不足此值，按“从山缘逐层侵蚀”削回到达标（MountainGenerator.EnforceFlatRatio）。</summary>
    public const float FlatLandTarget = 0.5f;
}
