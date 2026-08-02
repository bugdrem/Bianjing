namespace Bianjing;

/// <summary>
/// 王爷府配置（开局首建、全局唯一的核心官邸；业务归属：PlacementValidator 首建门槛/唯一校验、
/// Main 建成钩子拨款与安置、LifecycleSystem 随迁夫妻、ZoneGrowthSystem 选址倾向加成）。
/// 设定：开局须先建王爷府方能展开其它营造；建成时一次性拨给开基资源，并携三对富裕年轻夫妻暂居府中，
/// 待玩家划好可建设坊区后由现有「寄居→自建」逻辑迁出、在府邸周边自建新宅。
/// </summary>
public static class PrinceMansionConfig
{
    /// <summary>王爷府建筑定义 id（与 data/buildings.json 对应）。</summary>
    public const string DefId = "prince_mansion";

    // ---- 建成一次性开基资源 ----

    /// <summary>拨入官库的钱（文）与官粮（份）：开基家底（安家银，匹配 EconomyConfig.SettlementGrant）。</summary>
    public const long GrantMoney = 100_000;
    public const double GrantFood = 400;

    /// <summary>拨入王爷府库存的各类货品（份）：供市易/加工链启动的开基物资。</summary>
    public static readonly (string GoodsId, double Amount)[] GrantGoods =
    {
        (Goods.Grain, 120),
        (Goods.Wood, 80),
        (Goods.Fruit, 40),
        (Goods.RawSalt, 30),
        (Goods.IronOre, 30),
    };

    // ---- 随迁的富裕年轻夫妻（暂居府中，划区后自建新宅迁出）----

    /// <summary>随王爷入府的夫妻对数。</summary>
    public const int CoupleCount = 3;

    /// <summary>夫妻年龄区间（起始岁 + 随机跨度）：年轻当婚育之年。</summary>
    public const int AdultAgeMin = 20;
    public const int AdultAgeSpan = 7;

    /// <summary>每对夫妻的家庭公产（文，富裕：足以在好地段自建宅并有余）。</summary>
    public const long CoupleAssets = 40_000;

    /// <summary>每人随身私产（文）。</summary>
    public const long AdultMoney = 2_000;

    // ---- 村民建房选址倾向加成（用户需求：建房倾向叠加王爷府数值）----

    /// <summary>「近王爷府」的选址加成分（居首档，使民居优先聚于府邸周边；随距线性衰减）。</summary>
    public const float SiteScore = 6f;

    /// <summary>「近王爷府」判定半径（米，占地中心到府邸中心的切比雪夫距离内计分）。</summary>
    public const int SiteRadius = 24;
}
