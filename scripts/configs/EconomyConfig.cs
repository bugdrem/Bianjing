namespace Bianjing;

/// <summary>
/// 经济配置（批次五十六全面重写：单位由「贯」切换为「文」，增月俸/安家银/朝廷采购/技能/新税制）。
/// 货币：铜钱（文）为唯一内部单位；白银/黄金仅用于 UI 展示（见 CurrencyConfig/CurrencyHelper）。
/// （业务归属：Goods 定价、GoodsSystem 消耗、CraftingSystem 加工、EconomySystem 官粮/月俸/采购、
/// ItemPileObj 堆容量、CitizenAgent 供货/采买、JobSystem 家计/技能匹配、MaintenanceSystem 修缮、
/// TaxSystem 三税种、ZoneGrowthSystem 建筑生长）。
/// </summary>
public static class EconomyConfig
{
    // ===== 货币注入（外部输入） =====

    /// <summary>开府安家银：开局一次性发放（文）。</summary>
    public const long SettlementGrant = 100_000;

    /// <summary>王爷月俸：每月按时入国库（文/月），前期核心现金流。</summary>
    public const long PrinceMonthlySalary = 8_000;

    /// <summary>朝廷赏赐区间（文/次），任务触发时随机浮动。</summary>
    public const long CourtRewardMin = 5_000;
    public const long CourtRewardMax = 50_000;

    // ===== 朝廷采购 =====
    /// <summary>朝廷机构（柴炭司等）从工坊批量收购的固定单价倍率：支付价 = 货品基价 × 本倍率（1.0=平价收购）。</summary>
    public const double CourtProcurementPriceFactor = 1.2;

    /// <summary>朝廷每月采购配额上限（份）。</summary>
    public const long CourtProcurementQuota = 200;

    // ===== 货担与价差 =====

    /// <summary>一担几份（居民单次搬运量，Goods.LoadUnits 转发于此）。</summary>
    public const double LoadUnits = 5;

    /// <summary>买入价倍率（去商铺购买比自产贵）。</summary>
    public const double BuyMarkup = 1.5;

    /// <summary>未登记基价的货品兜底单价（文，Goods.PriceOf 用）。</summary>
    public const long DefaultPrice = 10;

    // ===== 消耗 =====

    /// <summary>人均日耗官粮（份，官库口粮，区别于家中口粮）。</summary>
    public const double OfficialFoodPerCapita = 0.2;

    /// <summary>每人每日口粮 / 柴薪 / 饮水消耗（份，家中库存）。</summary>
    public const double FoodPerDay = 0.1;
    public const double FuelPerDay = 0.03;
    public const double WaterPerDay = 0.1;

    /// <summary>断炊 / 缺柴时每日兴致扣减。</summary>
    public const float HungerFunPenalty = 1f;
    public const float ColdFunPenalty = 0.5f;

    // ===== 产能 =====

    /// <summary>田面收成最多集中成几堆（防 1m 格下散出上百小堆拖垮拾运与渲染）。</summary>
    public const int HarvestMaxPiles = 8;

    /// <summary>每名在岗工人每日加工产量（份，工坊/商铺）。</summary>
    public const double CraftPerWorkerDay = 0.8;

    /// <summary>地面物资堆单堆容量（份），满堆后多余收成烂在地里。</summary>
    public const double PileCapacity = 40;

    // ===== 商铺/市集 =====

    /// <summary>市集每货备货线（份）：市集存量低于此线即构成收购需求（CitizenAgent 供货派单）。</summary>
    public const double MarketStockLine = 40;

    /// <summary>采买判定半径（米）：此范围内有备货的市集/铺面就去买，否则自主采集。</summary>
    public const int BuySearchRadius = 160;

    // ===== 库存联动定价（需求 §6.3）=====
    /// <summary>库存 / 容量各档阈值与对应的价格浮动倍率（售出价 = 基价 × 倍率）。</summary>
    public const double StockHighThreshold = 0.8;   // ≥80% 开始降价
    public const double StockHighDiscount = 0.9;     // ×0.9（降价 10%）
    public const double StockFullThreshold = 0.95;  // ≥95% 更大折扣
    public const double StockFullDiscount = 0.7;     // ×0.7（降价 30%）
    public const double StockLowThreshold = 0.2;    // ≤20% 涨价
    public const double StockLowPremium = 1.1;       // ×1.1（涨价 10%）

    // ===== 家计与就业（原 JobsConfig 并入）=====

    /// <summary>每人每月生活开销（文，逐日按 1/DaysPerMonth 扣，先扣公产不足再成员分摊）。</summary>
    public const long LivingCostPerCapita = 200;

    /// <summary>无岗可寻时转入上山谋生（伐木/采摘/打猎）的概率。</summary>
    public const float JoblessForageChance = 0.6f;

    // ===== 技能系统（需求 §3.3）=====
    /// <summary>学徒每日经验累积量 / 熟练工匠所需经验 / 高级技工所需经验。</summary>
    public const float SkillExpPerDay = 2f;
    public const float SkillExpSkilled = 200f;
    public const float SkillExpExpert = 600f;

    // ===== 修缮（原 MaintenanceConfig 并入）：建筑老化与两条修缮线，各量按月值逐日 1/DaysPerMonth 结算 =====

    /// <summary>建筑每月老化量（完好度，天然建筑不老化）。</summary>
    public const float BuildingAgingPerMonth = 0.7f;

    /// <summary>每名修缮匠每月修复量 / 每月官府料钱（文）。</summary>
    public const float RepairPerWorker = 25f;
    public const long RepairWorkerCost = 100;

    /// <summary>居住者集资每月修复量 / 每位居住者每月修缮摊派（文）。</summary>
    public const float ResidentRepairAmount = 5f;
    public const long RepairFeePerResident = 15;

    // ===== 税制（批次五十六重写：三税种定额+浮动模型，详见 TaxSystem）=====

    /// <summary>土地税：默认税率（1%~10% 可调，默认 3%），作用于每栋建筑的定额税基（见 TaxSystem.BuildingTaxBase）。</summary>
    public const double LandTaxRateDefault = 0.03;
    public const double LandTaxRateMin = 0.01;
    public const double LandTaxRateMax = 0.10;

    /// <summary>商税：默认税率（2%~15% 可调，默认 5%），交易发生时自动扣除。</summary>
    public const double TradeTaxRateDefault = 0.05;
    public const double TradeTaxRateMin = 0.02;
    public const double TradeTaxRateMax = 0.15;

    /// <summary>人口税：默认关闭（-1 = 关闭），开启时 20% 从工资扣。</summary>
    public const double PollTaxRate = 0.20;

    /// <summary>每项重税每月造成的民怨（成人兴趣值扣减）。</summary>
    public const float HeavyTaxFunPenalty = 2f;

    /// <summary>人口税时每月幸福度下降值 / 关闭后每月恢复值。</summary>
    public const float PollTaxMoraleDrop = 1.5f;
    public const float PollTaxMoraleRecover = 0.5f;

    // ===== 建筑生长（需求 §7）=====
    /// <summary>民居三级名称：茅草屋 / 木瓦房 / 砖木院落。</summary>
    public static readonly string[] HouseLevelNames = { "茅草屋", "木瓦房", "砖木院落" };

    /// <summary>宅邸三级名称。</summary>
    public static readonly string[] MansionLevelNames = { "小宅", "中宅", "大院" };

    /// <summary>耕地价格（文/牛）：与宅基地同档。</summary>
    public const long CattlePrice = 10_000;
}
