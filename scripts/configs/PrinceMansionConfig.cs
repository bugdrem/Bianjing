namespace Bianjing;

/// <summary>
/// 王爷府配置（开局首建、全局唯一的核心官邸；业务归属：Main 开局选位放置/落成后解锁首建门槛、
/// PlacementValidator 首建门槛/唯一校验、BuildController 落成自动退出建造模式、
/// Main 建成钩子拨款与安置、LifecycleSystem 随迁夫妻、ZoneGrowthSystem 选址倾向加成）。
/// 设定：开局即进入王爷府选位放置（批次八十一，预览跟随鼠标、点击地图落成），落成前一切营造锁定；
/// 建成时一次性拨给开基资源，并携三对富裕年轻夫妻暂居府中，
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

    // ---- 村民建房选址倾向加成（批次七十：在既有近府邸加成上强化参数，民居更明显优先靠王府）----

    /// <summary>「近王爷府」的选址加成分（居首档，使民居优先聚于府邸周边；随距线性衰减）。
    /// 批次七十：8（原 6）——加权抽签幂放大后府邸周边中签率更高。</summary>
    public const float SiteScore = 8f;

    /// <summary>「近王爷府」判定半径（米，占地中心到府邸中心的切比雪夫距离内计分）。
    /// 批次七十：32（原 24）——府邸周边影响范围扩大。</summary>
    public const int SiteRadius = 32;
}
