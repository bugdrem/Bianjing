namespace Bianjing;

/// <summary>
/// 经济配置：货担、价差、人均日耗、加工产能、物资堆容量，及家计/修缮/税制三块民生财政
/// （业务归属：Goods 定价、GoodsSystem 消耗、CraftingSystem 加工、EconomySystem 官粮、ItemPileObj 堆容量、
/// CitizenAgent 供货/采买、JobSystem 家计、MaintenanceSystem 修缮、TaxPolicy/TaxSystem 税制）。
/// </summary>
public static class EconomyConfig
{
    /// <summary>一担几份（居民单次搬运量，Goods.LoadUnits 转发于此）。</summary>
    public const double LoadUnits = 5;

    /// <summary>买入价倍率（去商铺购买比自产贵）。</summary>
    public const double BuyMarkup = 1.5;

    /// <summary>未登记基价的货品兜底单价（Goods.PriceOf 用）。</summary>
    public const double DefaultPrice = 0.2;

    /// <summary>人均日耗官粮（官库口粮，区别于家中口粮）。</summary>
    public const double OfficialFoodPerCapita = 0.2;

    /// <summary>每人每日口粮 / 柴薪 / 饮水消耗（份，家中库存）。</summary>
    public const double FoodPerDay = 0.1;
    public const double FuelPerDay = 0.03;
    public const double WaterPerDay = 0.1;

    /// <summary>断炊 / 缺柴时每日兴致扣减。</summary>
    public const float HungerFunPenalty = 1f;
    public const float ColdFunPenalty = 0.5f;

    /// <summary>田面收成最多集中成几堆（防 1m 格下散出上百小堆拖垮拾运与渲染）。</summary>
    public const int HarvestMaxPiles = 8;

    /// <summary>每名在岗工人每日加工产量（份，工坊/商铺）。</summary>
    public const double CraftPerWorkerDay = 0.8;

    /// <summary>地面物资堆单堆容量（份），满堆后多余收成烂在地里。</summary>
    public const double PileCapacity = 40;

    /// <summary>市集每货备货线（份）：市集存量低于此线即构成收购需求（CitizenAgent 供货派单）。</summary>
    public const double MarketStockLine = 20;

    /// <summary>采买判定半径（米）：此范围内有备货的市集/铺面就去买，否则自主采集。</summary>
    public const int BuySearchRadius = 160;

    // ---- 家计与就业（原 JobsConfig 并入）----

    /// <summary>每人每月生活开销（贯，逐日按 1/DaysPerMonth 扣，先扣公产不足再成员分摊）。</summary>
    public const double LivingCostPerCapita = 0.8;

    /// <summary>无岗可寻时转入上山谋生（伐木/采摘/打猎）的概率。</summary>
    public const float JoblessForageChance = 0.6f;

    // ---- 修缮（原 MaintenanceConfig 并入）：建筑老化与两条修缮线，各量按月值逐日 1/DaysPerMonth 结算；
    // 公共设施由官府雇修缮匠维护，住宅/工商由居住者按人头集资自修（以税养屋）----

    /// <summary>建筑每月老化量（完好度，天然建筑不老化）。</summary>
    public const float BuildingAgingPerMonth = 0.7f;

    /// <summary>每名修缮匠每月修复量 / 每月官府料钱（贯）。</summary>
    public const float RepairPerWorker = 25f;
    public const double RepairWorkerCost = 1.0;

    /// <summary>居住者集资每月修复量 / 每位居住者每月修缮摊派（贯）。</summary>
    public const float ResidentRepairAmount = 5f;
    public const double RepairFeePerResident = 0.15;

    // ---- 税制（原 TaxConfig 并入）：税率步长与各税基公式系数；
    // 税种注册表本体在 TaxDefs（数据驱动，mod 可追加），此处只放数值系数 ----

    /// <summary>档位税率步长：税率 = 档位 × 此值（免征0 / 轻0.5 / 中1.0 / 重1.5）。</summary>
    public const double TaxRatePerLevel = 0.5;

    /// <summary>田赋税基：每块粮田 / 每户在籍的月计税额（贯）。</summary>
    public const double FarmBasePerFarm = 4.0;
    public const double FarmBasePerFamily = 0.5;

    /// <summary>市舶税基：每座港口的月计税额（贯，另加建筑 TaxBonus）。</summary>
    public const double PortBasePerPort = 8.0;

    /// <summary>每项重税每月造成的民怨（成人兴趣值扣减）。</summary>
    public const float HeavyTaxFunPenalty = 2f;
}
