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

    /// <summary>朝廷收购价倍率（批次七十七全场最低价）：朝廷出价低于城内收购价（城内铺面/工坊按基价收），
    /// 村民富余资源先供城内、卖不完的才卖给朝廷兜底；0.8 = 基价八折。朝廷收购不设配额上限，
    /// 衙门库容每月清空（朝廷漕运拉走，见 GoodsSystem.TickMonth），只受月内库容自然限制。</summary>
    public const double CourtProcurementPriceFactor = 0.8;

    // ===== 货担与价差 =====

    /// <summary>一担几份（居民单次搬运量，Goods.LoadUnits 转发于此）。</summary>
    public const double LoadUnits = 5;

    /// <summary>买入价倍率（去商铺购买比自产贵）。</summary>
    public const double BuyMarkup = 1.5;

    /// <summary>未登记基价的货品兜底单价（文，Goods.PriceOf 用）。</summary>
    public const long DefaultPrice = 10;

    // ===== 消耗 =====

    /// <summary>人均日耗官粮（份，官库赈济储备，区别于家中口粮；批次七十八：0.2→0.05——
    /// 官粮不再是人人每天的口粮，而是官府赈济/公务用度，补给靠朝廷粮饷 + 农田田赋）。</summary>
    public const double OfficialFoodPerCapita = 0.05;

    /// <summary>朝廷粮饷（批次七十八）：朝廷按人口每月拨入官仓的官粮（份/人/月，凭空生成）——
    /// 官粮从此有稳定补给，不再只靠开局存量耗尽即饥荒；农田田赋为额外增收。</summary>
    public const double CourtFoodAmmoPerCapitaMonth = 3;

    /// <summary>田赋（批次七十三/八十五）：农田 grain 收成按此比例入官粮，余下散落田面归村民——
    /// 官粮此前只有开局存量、无任何产出（buildings.json 无 foodOutput 定义），耗尽即永久饥荒、全民早亡；
    /// 批次八十五 0.2→0.1：农田改发固定工资后，种田家庭现金收入大头是工钱而非卖粮，田赋减半补农户收成。</summary>
    public const double GrainTaxShare = 0.1;

    /// <summary>每人每日口粮 / 柴薪 / 饮水消耗（份，家中库存）。</summary>
    public const double FoodPerDay = 0.1;
    public const double FuelPerDay = 0.03;
    public const double WaterPerDay = 0.1;

    /// <summary>断炊 / 缺柴时每日兴致扣减。</summary>
    public const float HungerFunPenalty = 1f;
    public const float ColdFunPenalty = 0.5f;

    // ===== 中央需求账本（第 9 项·阶段一）=====

    /// <summary>需求账本：库存可支撑天数低于此值判为短缺（对齐家用「低于半月存量就补」口径）。</summary>
    public const double DemandShortDays = 15.0;

    /// <summary>需求账本调试摘要开关（GD.Print，仅开发期排查用，默认关）。
    /// 用 static readonly 而非 const：const false 会使调用处 if 分支被判为不可达代码（CS0162）；改运行时判定即可开发期改 true 重编开启。</summary>
    public static readonly bool DemandDebugPrint = false;

    // ===== 产能 =====

    /// <summary>田面收成最多集中成几堆（防 1m 格下散出上百小堆拖垮拾运与渲染）。</summary>
    public const int HarvestMaxPiles = 8;

    /// <summary>每名在岗工人每日加工产量（份，工坊/商铺）。</summary>
    public const double CraftPerWorkerDay = 0.8;

    /// <summary>地面物资堆单堆容量（份），满堆后多余收成烂在地里。</summary>
    public const double PileCapacity = 40;

    // ===== 商铺/工坊 =====

    /// <summary>采买判定半径（米）：此范围内有备货的铺面/官营产业就去买，否则自主采集。</summary>
    public const int BuySearchRadius = 160;

    /// <summary>商铺/工坊升级后的副营种类上限（批次六十七：基本专营，升级才增补同大类，防垄断）。</summary>
    public const int MaxSpecialtiesPerShop = 3;

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

    // ===== 自主创业（批次六十四：技能+家庭资金+市场缺口三条件，缺货越狠门槛越低）=====

    /// <summary>创业基础日概率（无缺口时也保留小额创业可能）。</summary>
    public const double StartupChancePerDay = 0.01;

    /// <summary>缺口加成系数：缺口每多一分（可支撑天数逼近 0），日概率额外加此值×缺口度。</summary>
    public const double StartupScarcityBonus = 0.02;

    /// <summary>创业技能经验基础门槛（文/经验点，随缺口打折，下限 = 基础×（1-MaxDiscount））。</summary>
    public const double StartupSkillExpReq = 120;

    /// <summary>创业家庭公产基础门槛（文，随缺口打折）。</summary>
    public const double StartupAssetsReq = 8_000;

    /// <summary>缺口折扣上限：可支撑天数 0（最缺）时门槛最低打此折扣（0.5 = 五折）。</summary>
    public const double StartupMaxDiscount = 0.5;

    /// <summary>全城专营同货的铺面数上限（防垄断撞车；选品/转业均受此限）。</summary>
    public const int ShopSameGoodsCap = 3;

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
