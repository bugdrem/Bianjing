using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 外来访客系统配置（道路通边 → 四向邻城来人）：来访节奏、人数上限、NPC 类型比例、
/// 摆摊/停留时长、住宿费，以及四城配色与货类索引（供后期拓展互市/特产）。
/// </summary>
public static class VisitorConfig
{
    // ---- 来访节奏（实时生成，与人口成正比，不挂天数）----
    /// <summary>目标在场访客数 = 向下取整(城市总人口 × PopulationRatio)。与总人口成正比，
    /// 道路通边后才会有外来访客；该值即为「过来消费」的规模基准。</summary>
    public const float PopulationRatio = 0.03f;

    /// <summary>实时生成节奏：每经过该秒数尝试生成一名访客，直至达到人口比例目标或上限。</summary>
    public const float SpawnIntervalSec = 1.5f;

    /// <summary>每个生成周期实际生成的概率（&lt;1 引入到达抖动，避免机械感；仍为随机生成）。</summary>
    public const double SpawnChancePerInterval = 0.9;

    /// <summary>同屏外来访客硬上限（含商人/货郎/游客）。仅作性能保护，正常城市不触顶。</summary>
    public const int MaxConcurrentVisitors = 60;

    // ---- NPC 类型比例（每次 spawn 按此随机）----
    public const double MerchantRatio = 0.45; // 赴商铺/驿站批量交易
    public const double PeddlerRatio = 0.35;  // 路边摆摊零售
    public const double TouristRatio = 0.20;  // 进城住宿/逛铺

    // ---- 时长（秒，按游戏时钟倍速缩放；摆摊按"天"计在日结里递减）----
    /// <summary>商人/游客在交易场所驻留可见时长（秒）。</summary>
    public const float DwellSecondsMin = 12f;
    public const float DwellSecondsMax = 28f;
    /// <summary>货郎路边摆摊存活天数（日结递减，归零即收摊离城）。</summary>
    public const int StallMinDays = 2;
    public const int StallMaxDays = 4;

    /// <summary>住宿费（文）：游客在驿站下榻，城市收钱。</summary>
    public const long LodgeFee = 200;

    /// <summary>每日摆摊对外售货上限（份/天）：对照需求账本逐步卖出，防一夜清空。</summary>
    public const double StallDailySaleCap = 18;

    // ---- 互市供需闭环（道路通边 → 四向邻城来人买卖；访客行为响应城市真实供需 gs.Demand）----
    /// <summary>访客带货偏向「城市短缺货」的概率（城市有缺口时）。其余概率带邻城特产，维持多样性。[PLACEHOLDER·需 playtest]</summary>
    public const double ImportBias = 0.7;

    /// <summary>短缺货单次带货量区间（份）。参考：城市日口粮需求≈pop×EconomyConfig.FoodPerDay，取约 1–3 天缺口量。[PLACEHOLDER·需 playtest]</summary>
    public const double ShortageCargoMin = 20;
    public const double ShortageCargoMax = 60;

    /// <summary>出口收购阈值：城市某货「可支撑天数」高于此值才视为过剩、外城才收购（避免买走刚需）。[PLACEHOLDER·需 playtest]</summary>
    public const double SurplusDaysThreshold = 30;

    /// <summary>出口单次收购量上限（份）。[PLACEHOLDER·需 playtest]</summary>
    public const double ExportMaxQty = 80;

    /// <summary>出口收购占该过剩货城市库存的比例。[PLACEHOLDER·需 playtest]</summary>
    public const double ExportStockShare = 0.5;

    // ---- 四城配色（区分来客所属邻城，外观一眼可分）----
    /// <summary>北/东/南/西 四城外袍主色（按 MapDir 枚举序）。</summary>
    public static readonly Color[] CityRobe =
    {
        new(0.55f, 0.62f, 0.80f), // 北邙镇：青灰蓝
        new(0.62f, 0.78f, 0.55f), // 东津渡：竹青
        new(0.85f, 0.70f, 0.45f), // 南湖庄：暖黄
        new(0.80f, 0.55f, 0.55f), // 西山市：赭红
    };

    // ---- 货类索引（Goods.CategoryOf 取值 → 可贸易货品 id）----
    private static readonly string[] AllTradeGoods =
    {
        Goods.Grain, Goods.Fruit, Goods.Game, Goods.Flatbread, Goods.Charcoal,
        Goods.Log, Goods.Planks, Goods.Timber, Goods.Furniture,
        Goods.Hide, Goods.Leather, Goods.Clothing,
        Goods.Herb, Goods.Medicine,
        Goods.RawSalt, Goods.RefinedSalt,
        Goods.IronOre, Goods.IronIngot, Goods.Ironware,
        Goods.Yeast, Goods.Stone, Goods.Wine, Goods.Cured, Goods.Scrap,
    };

    private static readonly Dictionary<int, List<string>> GoodsByCategory = BuildGoodsIndex();

    private static Dictionary<int, List<string>> BuildGoodsIndex()
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var g in AllTradeGoods)
        {
            int cat = Goods.CategoryOf(g);
            if (!map.ContainsKey(cat))
                map[cat] = new List<string>();
            map[cat].Add(g);
        }
        return map;
    }

    /// <summary>取某货类下的全部可贸易货品 id（无则返回空列表）。</summary>
    public static IReadOnlyList<string> GoodsOfCategory(int category)
        => GoodsByCategory.GetValueOrDefault(category, new List<string>());

    /// <summary>全部存在的货类（用于随机选类）。</summary>
    public static IEnumerable<int> AllCategories()
    {
        foreach (var k in GoodsByCategory.Keys)
            yield return k;
    }

    /// <summary>取某邻城的主营货类（若存在），否则 -1。</summary>
    public static int PrimarySpecialty(NeighborCity nb)
        => nb.Specialties.Count > 0 ? nb.Specialties[0] : -1;
}
