using System.Collections.Generic;

namespace Bianjing;

/// <summary>货品静态定义：种类、名称、基价。单位「份」，居民一次可挑一担（5 份）。</summary>
public static class Goods
{
    public const string Grain = "grain";
    public const string Wood = "wood";
    public const string Fruit = "fruit";
    public const string Game = "game";

    /// <summary>一担 = 5 份（居民单次搬运量）。</summary>
    public const double LoadUnits = 5;

    /// <summary>商铺可专营的货品（工坊固定专营柴薪）。</summary>
    public static readonly string[] ShopSpecialties = { Grain, Fruit, Game };

    /// <summary>每份基价（居民卖出价；买入价为基价 × 1.5）。</summary>
    public static readonly Dictionary<string, double> BasePrice = new()
    {
        [Grain] = 0.2,
        [Wood] = 0.2,
        [Fruit] = 0.12,
        [Game] = 0.32,
    };

    /// <summary>买入价倍率（去商铺购买比自产贵）。</summary>
    public const double BuyMarkup = 1.5;

    public static readonly Dictionary<string, string> DisplayName = new()
    {
        [Grain] = "粮食",
        [Wood] = "柴薪",
        [Fruit] = "果品",
        [Game] = "野味",
    };

    public static string NameOf(string id) => DisplayName.GetValueOrDefault(id, id);

    public static double PriceOf(string id) => BasePrice.GetValueOrDefault(id, 0.2);
}
