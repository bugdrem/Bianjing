using System.Collections.Generic;

namespace Bianjing;

/// <summary>货品静态定义：种类、名称、基价。单位「份」，居民一次可挑一担（5 份）。</summary>
public static class Goods
{
    public const string Grain = "grain";
    public const string Wood = "wood";
    public const string Fruit = "fruit";
    public const string Game = "game";
    public const string Ore = "ore";
    public const string Salt = "salt";

    /// <summary>水：仅供家庭自用（井/河边打水背回家），不设市价、不入铺面备货、不参与买卖。</summary>
    public const string Water = "water";

    // ---- 加工成品（工坊/商铺由原料加工而成，价高于原料）----
    public const string Timber = "timber";     // 木器←柴薪
    public const string Wine = "wine";         // 酒←粮食
    public const string Ironware = "ironware"; // 铁器←矿石
    public const string Cured = "cured";       // 腌货←野味+盐

    /// <summary>一担 = 5 份（居民单次搬运量）。</summary>
    public const double LoadUnits = 5;

    /// <summary>食物类货品（家庭口粮储备按此合计；消耗优先级：粮→果→野味）。</summary>
    public static readonly string[] FoodKinds = { Grain, Fruit, Game };

    /// <summary>是否食物类货品。</summary>
    public static bool IsFood(string id) => id == Grain || id == Fruit || id == Game;

    /// <summary>商铺可专营的货品（工坊固定专营柴薪）。</summary>
    public static readonly string[] ShopSpecialties = { Grain, Fruit, Game };

    /// <summary>加工配方：成品 id → 所需原料 id 列表（每产一份成品消耗每种原料各一份）。
    /// 后期支持一座工坊多成品，此表即可扩充。</summary>
    public static readonly Dictionary<string, string[]> Recipes = new()
    {
        [Timber] = new[] { Wood },
        [Wine] = new[] { Grain },
        [Ironware] = new[] { Ore },
        [Cured] = new[] { Game, Salt },
    };

    /// <summary>可加工的成品种类（工坊/商铺各随机专营其一）。</summary>
    public static readonly string[] CraftSpecialties = { Timber, Wine, Ironware, Cured };

    /// <summary>是否为可加工成品。</summary>
    public static bool IsCraftable(string id) => Recipes.ContainsKey(id);

    /// <summary>成品所需原料（非成品返回空数组）。</summary>
    public static string[] InputsOf(string id) => Recipes.GetValueOrDefault(id, System.Array.Empty<string>());

    /// <summary>每份基价（居民卖出价；买入价为基价 × 1.5）。</summary>
    public static readonly Dictionary<string, double> BasePrice = new()
    {
        [Grain] = 0.2,
        [Wood] = 0.2,
        [Fruit] = 0.12,
        [Game] = 0.32,
        [Ore] = 0.5,
        [Salt] = 0.6,
        [Timber] = 0.6,
        [Wine] = 0.7,
        [Ironware] = 1.2,
        [Cured] = 1.0,
    };

    /// <summary>买入价倍率（去商铺购买比自产贵）。</summary>
    public const double BuyMarkup = 1.5;

    public static readonly Dictionary<string, string> DisplayName = new()
    {
        [Grain] = "粮食",
        [Wood] = "柴薪",
        [Fruit] = "果品",
        [Game] = "野味",
        [Ore] = "矿石",
        [Salt] = "盐",
        [Timber] = "木器",
        [Wine] = "酒",
        [Ironware] = "铁器",
        [Cured] = "腌货",
        [Water] = "水",
    };

    public static string NameOf(string id) => DisplayName.GetValueOrDefault(id, id);

    public static double PriceOf(string id) => BasePrice.GetValueOrDefault(id, 0.2);
}
