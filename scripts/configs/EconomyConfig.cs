namespace Bianjing;

/// <summary>
/// 经济配置：货担、价差、人均日耗、加工产能与物资堆容量
/// （业务归属：Goods 定价、GoodsSystem 消耗、CraftingSystem 加工、EconomySystem 官粮、ItemPileObj 堆容量）。
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
}
