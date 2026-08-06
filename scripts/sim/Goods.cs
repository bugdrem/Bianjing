using System.Collections.Generic;

namespace Bianjing;

/// <summary>货品静态定义（批次五十六全面重写：新货品表含原料/中间品/成品共约 20 种，基价单位「文」）。
/// 产业链模型详见需求文档 §5-§6：官营上游 → 私营初级工坊 → 高级工坊 → 商铺零售 → NPC 消费。
/// </summary>
public static class Goods
{
    // ---- 食物与燃料 ----
    public const string Grain = "grain";       // 粮食
    public const string Wood  = "wood";        // 柴薪
    public const string Fruit = "fruit";       // 果品
    public const string Game  = "game";        // 野味
    public const string Water = "water";       // 水（仅家用，不买卖）
    public const string Flatbread = "flatbread"; // 烧饼（中期食品加工：粮食 → 烧饼）
    public const string Charcoal  = "charcoal";  // 木炭（中期燃料加工：柴薪 → 木炭）

    // ---- 采集/官营原料 ----
    public const string Log       = "log";        // 原木（樵夫/林场）
    public const string Hide      = "hide";       // 兽皮（猎户）
    public const string Herb      = "herb";       // 草药（采药人）
    public const string RawSalt   = "raw_salt";   // 粗盐（官营盐场）
    public const string IronOre   = "iron_ore";   // 铁矿石（官营冶铁所）
    public const string Stone     = "stone";      // 石料（官营采石场）
    public const string Yeast     = "yeast";      // 酒曲（官营酒曲司）

    // ---- 中间品（初级工坊产出）----
    public const string Planks      = "planks";       // 木板 ← 原木
    public const string Leather     = "leather";      // 皮革 ← 兽皮
    public const string RefinedSalt = "refined_salt"; // 精盐 ← 粗盐
    public const string IronIngot   = "iron_ingot";   // 铁锭 ← 铁矿石

    // ---- 成品（高级工坊产出）----
    public const string Timber   = "timber";    // 木器 ← 木板
    public const string Wine     = "wine";      // 酒 ← 粮食
    public const string Ironware = "ironware";  // 铁器 ← 铁锭
    public const string Cured    = "cured";     // 腌货 ← 野味+盐
    public const string Furniture = "furniture"; // 家具 ← 木板
    public const string Clothing  = "clothing";  // 成衣 ← 皮革
    public const string Medicine  = "medicine";  // 丸药 ← 草药

    // ---- 流民随身物（需求 §2.2：散勇携兵刃、客士携书籍；价值折入资产，可赴商铺变卖）----
    public const string Weapon = "weapon";   // 兵刃（散勇随身）
    public const string Book   = "book";     // 书籍（客士随身）

    /// <summary>一担几份（居民单次搬运量）：转发自 EconomyConfig。</summary>
    public static double LoadUnits => EconomyConfig.LoadUnits;

    /// <summary>食物类货品（家庭口粮储备按此合计；消耗优先级：粮→果→野味）。</summary>
    public static readonly string[] FoodKinds = { Grain, Fruit, Game };

    /// <summary>是否食物类货品。</summary>
    public static bool IsFood(string id) => id == Grain || id == Fruit || id == Game;

    /// <summary>商铺可专营的货品（三级商铺经营成品，一级可兼营原料）。</summary>
    public static readonly string[] ShopSpecialties = { Grain, Fruit, Game, Wood, Timber, Wine, Ironware, Cured, Furniture, Clothing, Medicine, Flatbread, Charcoal };

    /// <summary>加工配方：成品 id → 所需原料 id 列表（每产一份成品消耗每种原料各一份）。
    /// 初级工坊产中间品（木板/皮革/精盐/铁锭），高级工坊产成品（木器/家具/成衣/铁器/腌货/丸药）。
    /// 酒直接从粮食加工，不经过中间品。</summary>
    public static readonly Dictionary<string, string[]> Recipes = new()
    {
        // 初级工坊配方（原料 → 中间品）
        [Planks]      = new[] { Log },
        [Leather]     = new[] { Hide },
        [RefinedSalt] = new[] { RawSalt },
        [IronIngot]   = new[] { IronOre },
        // 高级工坊配方（中间品/原料 → 成品）
        [Timber]    = new[] { Planks },
        [Furniture] = new[] { Planks },
        [Clothing]  = new[] { Leather },
        [Ironware]  = new[] { IronIngot },
        [Cured]     = new[] { Game, RefinedSalt },
        [Medicine]  = new[] { Herb },
        [Wine]      = new[] { Grain },
        // 中期民生加工（基础品升级：粮→烧饼、柴→木炭）
        [Flatbread] = new[] { Grain },
        [Charcoal]  = new[] { Wood },
    };

    /// <summary>是否为初级工坊专营品（原料→中间品）。</summary>
    public static readonly HashSet<string> PrimaryWorkshopGoods = new() { Planks, Leather, RefinedSalt, IronIngot };

    /// <summary>是否为高级工坊专营品（中间品/原料→成品）。</summary>
    public static readonly HashSet<string> AdvancedWorkshopGoods = new() { Timber, Furniture, Clothing, Ironware, Cured, Medicine, Wine, Flatbread, Charcoal };

    /// <summary>可加工的成品种类（工坊/商铺各随机专营其一）。</summary>
    public static readonly string[] CraftSpecialties = { Timber, Wine, Ironware, Cured, Furniture, Clothing, Medicine, Planks, Leather, RefinedSalt, IronIngot, Flatbread, Charcoal };

    /// <summary>是否为可加工品（含中间品）。</summary>
    public static bool IsCraftable(string id) => Recipes.ContainsKey(id);

    /// <summary>为初级工坊品。</summary>
    public static bool IsPrimary(string id) => PrimaryWorkshopGoods.Contains(id);

    /// <summary>为高级工坊品。</summary>
    public static bool IsAdvanced(string id) => AdvancedWorkshopGoods.Contains(id);

    /// <summary>成品所需原料（非成品返回空数组）。</summary>
    public static string[] InputsOf(string id) => Recipes.GetValueOrDefault(id, System.Array.Empty<string>());

    /// <summary>每份基价（文，居民卖出价；买入价为基价 × BuyMarkup）。
    /// 物价锚点（需求 §9）：烧饼≈1文、柴薪 3文/捆、猪肉 10文/斤、工匠月薪 800~1200文。
    /// </summary>
    public static readonly Dictionary<string, long> BasePrice = new()
    {
        // 食物/燃料
        [Grain] = 10,
        [Wood]  = 3,
        [Fruit] = 6,
        [Game]  = 18,
        [Flatbread] = 15, // 烧饼（粮加工溢价）
        [Charcoal]  = 8,  // 木炭（柴加工溢价）
        // 原料
        [Log]     = 5,
        [Hide]    = 22,
        [Herb]    = 25,
        [RawSalt] = 15,
        [IronOre] = 20,
        [Stone]   = 8,
        [Yeast]   = 12,
        // 中间品
        [Planks]      = 18,
        [Leather]     = 40,
        [RefinedSalt] = 40,
        [IronIngot]   = 55,
        // 成品
        [Timber]    = 50,
        [Wine]      = 45,
        [Ironware]  = 140,
        [Cured]     = 60,
        [Furniture] = 100,
        [Clothing]  = 150,
        [Medicine]  = 80,
        // 流民随身物（非买卖品，仅价值折入资产）
        [Weapon] = 80,
        [Book]   = 30,
    };

    /// <summary>买入价倍率（去商铺购买比自产贵）：转发自 EconomyConfig。</summary>
    public static double BuyMarkup => EconomyConfig.BuyMarkup;

    /// <summary>库存联动定价（需求 §6.3）：根据库存占用率返回价格浮动倍率。</summary>
    public static double StockPriceFactor(double fillRate)
    {
        if (fillRate >= EconomyConfig.StockFullThreshold)
            return EconomyConfig.StockFullDiscount;
        if (fillRate >= EconomyConfig.StockHighThreshold)
            return EconomyConfig.StockHighDiscount;
        if (fillRate <= EconomyConfig.StockLowThreshold)
            return EconomyConfig.StockLowPremium;
        return 1.0;
    }

    public static readonly Dictionary<string, string> DisplayName = new()
    {
        [Grain]  = "粮食",
        [Wood]   = "柴薪",
        [Fruit]  = "果品",
        [Game]   = "野味",
        [Water]  = "水",
        [Flatbread] = "烧饼",
        [Charcoal]  = "木炭",
        [Log]    = "原木",
        [Hide]   = "兽皮",
        [Herb]   = "草药",
        [RawSalt] = "粗盐",
        [IronOre] = "铁矿石",
        [Stone]   = "石料",
        [Yeast]   = "酒曲",
        [Planks]      = "木板",
        [Leather]     = "皮革",
        [RefinedSalt] = "精盐",
        [IronIngot]   = "铁锭",
        [Timber]    = "木器",
        [Wine]      = "酒",
        [Ironware]  = "铁器",
        [Cured]     = "腌货",
        [Furniture] = "家具",
        [Clothing]  = "成衣",
        [Medicine]  = "丸药",
        [Weapon] = "兵刃",
        [Book]   = "书籍",
    };

    public static string NameOf(string id) => DisplayName.GetValueOrDefault(id, id);

    public static long PriceOf(string id) => BasePrice.GetValueOrDefault(id, EconomyConfig.DefaultPrice);

    /// <summary>货品所属城市等级（资源等级与城市等级挂钩的数据标记）：该货品在此里程碑起进入城市需求/产业视野。
    /// 本阶段仅作标记，需求由 TierNeeds 表驱动；阶段三 mod 化时外置为 JSON。</summary>
    public static readonly Dictionary<string, int> CityTier = new()
    {
        // 早期（村落 0）基础民生与山野自产
        [Grain]=0,[Wood]=0,[Water]=0,[Fruit]=0,[Game]=0,[Log]=0,[Hide]=0,[Herb]=0,[Stone]=0,[Weapon]=0,[Book]=0,
        // 中期（县城/郡城 3-4）加工新品与初级工坊品
        [Flatbread]=3,[Charcoal]=4,[Planks]=3,[Leather]=3,[RawSalt]=3,[Yeast]=3,[RefinedSalt]=4,[IronOre]=4,[IronIngot]=4,
        // 后期（州城~京城 5-7）成品
        [Wine]=5,[Cured]=5,[Timber]=6,[Furniture]=6,[Clothing]=6,[Medicine]=6,[Ironware]=7,
    };

    /// <summary>货品所属城市等级（未登记者返回 0）。</summary>
    public static int TierOf(string id) => CityTier.GetValueOrDefault(id, 0);
}
