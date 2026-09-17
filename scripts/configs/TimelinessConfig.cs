using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 时效系统调参（批次九十五）：<see cref="TimedState"/> 的两根时间轴共用同一套阈值与倍率，
/// 货品 / 房屋 / 道路 / 植物 / 动物 / 人 六类实体全部复用（命名与规则统一）。
///
/// 时间口径：本文件所有「天数」均为**游戏日**——1 游戏日 = 20 秒现实时间
/// （1 旬 = 3 日 = 60 秒，1 月 = 3 旬，1 年 = 36 旬 = 36 分钟，见 TimeConfig）。
/// 故"易腐品 15 日"≈ 现实 5 分钟，是玩家来得及反应、也够紧迫的窗口。
///
/// 两条轴：
///   软轴 Fresh（0~100）：全效果 → 渐变衰减 → 失效（原用途效果归零，物品仍在、可作他用）
///   硬轴 Lifespan      ：已耗天数累积到 Span 即消亡或降级为废料
///
/// 阈值本身可被环境放大/缩小（仓库寿限 ×1.6 等），详见 sim/timeliness/TimedRegistry。
/// 调整本文件即可全局改味，衰减器逻辑在 sim/timeliness/ 下。
/// </summary>
public static class TimelinessConfig
{
    /// <summary>系统总开关：关闭则货品不再计时效（房屋老化仍由 MaintenanceSystem 独立驱动）。
    /// 用 static readonly 而非 const——const 会被编译器折叠，使关闭分支被报"不可达代码"（CS0162）。</summary>
    public static readonly bool Enabled = true;

    // ===== 软轴（新鲜度）三阶段：以 100 为满值 =====

    /// <summary>全效果段下限：Fresh ≥ 此值 → 效果不打折。</summary>
    public const float FreshFullThreshold = 60f;

    /// <summary>渐变衰减段下限：Fresh ≥ 此值但 &lt; 全效果线 → 效果按比例打折。</summary>
    public const float FreshWaneThreshold = 15f;

    /// <summary>合并分档粒度：鲜度按此值向下取整分档，同档才并堆（10 → 10 档）。</summary>
    public const float FreshBandSize = 10f;

    /// <summary>硬轴剩余寿限的分档粒度（游戏日）：同档才并堆。</summary>
    public const float LifespanBandDays = 30f;

    // ===== 翻新（回锅 / 烘干 / 腌制）：每次恢复都透支未来，惩罚复合叠加 =====

    /// <summary>每次翻新对硬轴寿限的惩罚倍率（0.65 → 翻新两次只剩 42%）。</summary>
    public const float RenewSpanPenalty = 0.65f;

    /// <summary>每次翻新对软轴衰减速率的惩罚倍率（1.8 → 翻新两次已是 3.24 倍速）。</summary>
    public const float RenewRatePenalty = 1.8f;

    /// <summary>翻新次数上限（保险：防倍率下溢到 0 或溢出）。</summary>
    public const int RenewMaxCount = 6;

    // ===== 环境·位置（基准 = 露天堆放，Rate 1.0 / Span 1.0）=====

    /// <summary>有顶室内（建筑覆盖且非露棚）：衰减速率倍率——仓房防腐的主要来源。</summary>
    public const float ShelteredRateMul = 0.45f;

    /// <summary>有顶室内：硬轴寿限倍率——入库即"续命"（阈值回滚，见设计文档）。</summary>
    public const float ShelteredSpanMul = 1.6f;

    /// <summary>无顶棚屋（建筑覆盖但 NoRoof）：遮阳不挡湿，介于露天与仓房之间。</summary>
    public const float OpenShedRateMul = 0.8f;
    public const float OpenShedSpanMul = 1.15f;

    /// <summary>随身携带（村民/访客背包）：颠簸日晒，略差于仓房。</summary>
    public const float CarriedRateMul = 1.15f;
    public const float CarriedSpanMul = 0.95f;

    /// <summary>临水（四向邻格有水面）：潮气重，腐坏加快。</summary>
    public const float NearWaterRateMul = 1.35f;
    public const float NearWaterSpanMul = 0.9f;

    // ===== 环境·季节（月份区间：春 1-3、夏 4-6、秋 7-9、冬 10-12）=====

    /// <summary>夏季速率倍率：高温易腐。</summary>
    public const float SummerRateMul = 1.6f;

    /// <summary>秋季速率倍率：凉爽。</summary>
    public const float AutumnRateMul = 0.85f;

    /// <summary>冬季速率倍率：低温防腐（但农田休耕，见 FarmlandConfig）。</summary>
    public const float WinterRateMul = 0.55f;

    // ===== 硬轴归零的处理 =====

    /// <summary>硬轴归零时是否降级为废料（true）还是直接消失（false）。
    /// static readonly 而非 const：同 <see cref="Enabled"/>，避免关闭分支被判为不可达。</summary>
    public static readonly bool ExpiryToScrap = true;

    /// <summary>降级为废料的产出比例（份废料 / 份原物）。</summary>
    public const float ExpiryScrapRatio = 0.3f;

    // ===== 房屋（建筑）：软轴沿用既有 Condition，硬轴为新增的结构寿命 =====

    /// <summary>
    /// 房屋硬轴基础寿限（游戏日，按 category）。
    /// 房屋寿限刻意设得远长于一局游戏——它是"长期存档"的压力而非即时惩罚；
    /// 真正让玩家感受到的是软轴（Condition 失修，无人修缮约 432 日破败）。
    /// 但修缮会累积"大修次数"从而折损寿限（见 <see cref="BuildingRepairPerRenew"/>），
    /// 常用"翻新透支未来"的同一套机制：老房子会越修越难维持。
    /// </summary>
    public static float BuildingSpanDays(string category) => category switch
    {
        "official" => 14400f,  // 官署宫殿：400 游戏年
        "grown" => 7200f,      // 民居/商铺/工坊：200 游戏年
        "field" => 3600f,      // 田块：100 游戏年
        "court" => 0f,         // 朝廷机构：豁免（与既有"朝廷自理"一致）
        _ => 7200f,
    };

    /// <summary>累计修复量达到此值即记为一次"大修"（驱动翻新惩罚：寿限 ×0.65^n）。
    /// 逐旬小额修缮累加到 100 才计一次，避免"每日养护"被当成天天翻新。</summary>
    public const float BuildingRepairPerRenew = 100f;

    // ===== 恢复动作（翻新）：回锅 / 烘干 / 重腌 =====

    /// <summary>
    /// 各货品的恢复手段。<b>核心不是"回血"，而是"用总寿命换当下品质"</b>——
    /// 每次执行都会让 <c>RenewCount</c> +1，于是寿限 ×0.65^n、衰减速率 ×1.8^n 复合叠加
    /// （见 <see cref="RenewSpanPenalty"/> / <see cref="RenewRatePenalty"/>）。
    /// 递减收益自然涌现，不需要额外规则去禁止无限回锅。
    ///
    /// 未登记在此的货品没有恢复手段（铁器锈了就是锈了、盐受潮只能认）。
    /// </summary>
    public static readonly Dictionary<string, RenewSpec> RenewSpecs = new()
    {
        // 熟食回锅：烧柴薪再热一遍（用户举例的原型）
        { Goods.Flatbread, new RenewSpec("回锅", 85f, Goods.Wood, 0.20) },
        // 腌货重腌：补盐回鲜
        { Goods.Cured, new RenewSpec("重腌", 80f, Goods.RefinedSalt, 0.10) },
        // 酒澄清：不需辅料，但同样透支（越澄清越易酸败）
        { Goods.Wine, new RenewSpec("澄清", 85f, "", 0f) },
        // 柴薪烘干：湿柴回燥（用户举例的第二个原型）
        { Goods.Wood, new RenewSpec("烘干", 90f, Goods.Charcoal, 0.08) },
        // 兽皮晾晒：受潮的生皮可晾回
        { Goods.Hide, new RenewSpec("晾晒", 85f, "", 0f) },
        // 草药复晒
        { Goods.Herb, new RenewSpec("复晒", 80f, "", 0f) },
    };

    /// <summary>NPC 自动抢救的触发线：鲜度低于此值（即已进入渐变衰减段过半）才值得动手。
    /// 设得太高会让居民不停地翻新，反而把物品的寿限迅速耗光。</summary>
    public const float AutoRenewBelowFresh = 40f;

    /// <summary>NPC 自动抢救的次数上限（低于 <see cref="RenewMaxCount"/>）：
    /// 居民不会不计后果地反复翻新——把寿限留给真正需要的时候。</summary>
    public const int AutoRenewMaxCount = 2;

    // ===== 道路：软轴=路况（影响移速），硬轴=寿限（耗尽降一级）=====

    /// <summary>道路软轴天数：路况从满值衰减到 0 所需游戏日（按道路种类）。</summary>
    public static float RoadSoftDays(RoadKind kind) => kind switch
    {
        RoadKind.Main => 360f,  // 主路有专人维护性铺装，最耐久
        RoadKind.Side => 240f,
        RoadKind.Lane => 120f,  // 土路小道最易被踩烂
        _ => 240f,              // 桥面（RoadKind.None 但 HasRoad）
    };

    /// <summary>道路硬轴寿限（游戏日）：耗尽则降一级（主→辅→小路→冻结最差路况）。
    /// <b>道路只降级不消失</b>——拆除会破坏玩家路网与建筑临路判定，代价远大于收益。</summary>
    public static float RoadSpanDays(RoadKind kind) => kind switch
    {
        RoadKind.Main => 720f,
        RoadKind.Side => 480f,
        RoadKind.Lane => 240f,
        _ => 720f,
    };

    /// <summary>最差路况下的移速倍率下限：路况归零也只是"走得慢"，不会让通行瘫痪。</summary>
    public const float RoadMinSpeedFactor = 0.55f;

    // ===== 植物（树木）：软轴沿用既有 Hp（归一化为生机百分比），硬轴为新增的自然寿限 =====

    /// <summary>树木硬轴寿限（游戏日）：到寿枯死倒伏（掉落木材）。
    /// 设得极长（300 游戏年）——一局游戏通常看不到树老死，它是长期存档的机制；
    /// 玩家能感受到的是软轴（砍伐伤 + 闲置自愈）。</summary>
    public const float PlantLifespanDays = 10800f;

    /// <summary>枯死倒伏的木材产出比例（相对正常砍伐满血的产出）：死木质次，出材减半。
    /// 实际份数 = MaxHp × VillagerConfig.WoodPerHp × 本比例。</summary>
    public const float PlantDeathWoodRatio = 0.5f;

    // ===== 动物：软轴=体况（季节驱动），硬轴=寿限（老死）=====

    /// <summary>动物寿限（月，15 游戏年）：到寿老死，与既有"随机自然减员"并存
    /// （后者代表意外、天敌与捕猎未遂，不改）。</summary>
    public const int AnimalMaxAgeMonths = 180;

    /// <summary>
    /// 冬季每日体况变化（掉膘，负数）与其余季节的每日回复。
    ///
    /// 两值刻意配成"略微入不敷出"：一游戏年是 36 日（冬 10-12 月 = 9 日），
    /// 年净变化 ≈ 9×(-1.8) + 27×(0.42) ≈ <b>-4.9 点</b>，
    /// 于是体况从满值滑到下限约需 15 游戏年——恰好与
    /// <see cref="AnimalMaxAgeMonths"/>（15 游戏年寿限）吻合：
    /// 动物一生就是从肥壮走到羸弱，繁育与出肉随之递减，不需要额外规则。
    /// 若回复值调大（如 +0.9），体况会在春季就回满、全年贴着上限，季节波动形同虚设。
    /// </summary>
    public const float AnimalVigorWinterDelta = -1.8f;

    /// <summary>非冬季每日体况回复（见 <see cref="AnimalVigorWinterDelta"/> 的配比说明）。</summary>
    public const float AnimalVigorGrowDelta = 0.42f;

    /// <summary>体况下限：再瘦也不会归零（归零等于绝育，会把种群玩死）。</summary>
    public const float AnimalVigorMin = 25f;

    /// <summary>体况对繁育率的折算下限：体况最差时仍有此比例，避免种群自我灭绝。</summary>
    public const float AnimalBreedVigorFloor = 0.25f;

    /// <summary>体况对猎获野味量的折算下限：瘦猎物仍能出一部分肉。</summary>
    public const float AnimalYieldVigorFloor = 0.45f;

    // ===== 人：软轴=健康 Health（既有字段，此前恒满未生效），硬轴=年龄（既有 Gompertz 死亡曲线）=====

    /// <summary>断炊时每日健康损耗。</summary>
    public const float PersonHealthHungerLoss = 3f;

    /// <summary>缺柴受冻时每日健康损耗。</summary>
    public const float PersonHealthColdLoss = 2f;

    /// <summary>温饱时每日健康恢复（约 20 游戏日回满）。</summary>
    public const float PersonHealthRegen = 5f;

    /// <summary>健康对劳动效率（加工产量、田间收获）的折算下限：重病仍能勉强干活。</summary>
    public const float PersonLaborFloor = 0.35f;

    /// <summary>季节速率倍率（按游戏月查表，春季与未列月份为基准 1.0）。</summary>
    public static float SeasonRateMul(int month) => month switch
    {
        >= 4 and <= 6 => SummerRateMul,
        >= 7 and <= 9 => AutumnRateMul,
        >= 10 => WinterRateMul,
        _ => 1f,
    };

    /// <summary>
    /// 货品时效基线。<b>未登记在册的货品一律不参与时效</b>（矿石、书籍、废料等不会腐坏），
    /// 这比逐项写"永不腐坏"更省事，也避免新增货品时忘记登记导致莫名腐烂。
    /// </summary>
    public static readonly Dictionary<string, TimedBaseline> Baselines = new()
    {
        // ---- 食物：易腐，短期（10~24 日 ≈ 现实 3~8 分钟）----
        // ---- 食物 ----
        // 生鲜：无稳定期——一离枝、一离火就开始劣变
        { Goods.Game,      new TimedBaseline(10f,   30f,   0f) },   // 野味：生肉最易腐
        { Goods.Water,     new TimedBaseline(6f,    18f,   0f) },   // 饮水：隔日即馊
        { Goods.Flatbread, new TimedBaseline(15f,   45f,   0f) },   // 烧饼：回锅的主力对象
        { Goods.Fruit,     new TimedBaseline(24f,   60f,   0f) },   // 果品
        // 耐储：有稳定期——腌过、酿过、晒过的东西才存得住
        { Goods.Cured,     new TimedBaseline(90f,  360f,  30f) },   // 腌货：仓房约 2 年后才开始变味
        // ★ 粮食（谷物）：本表的基准案例——仓房 2.96 年转入打折、6.32 年不可食用
        //   平台 48 ÷ 0.45 = 107 日 = 2.96 年（此前完全不变质）
        //   鲜度 15 落在 (48 + 0.85×64) ÷ 0.45 = 228 日 = 6.32 年（此后不可食，可作饲料/堆肥）
        //   鲜度 0 落在 112 ÷ 0.45 = 249 日 = 6.91 年；硬轴 480÷0.45 = 21.3 年才彻底消亡
        { Goods.Grain,     new TimedBaseline(112f, 480f,  48f) },
        { Goods.Wine,      new TimedBaseline(300f, 1800f, 90f) },   // 酒：越陈越耐，稳定期最长

        // ---- 燃料：柴薪的"鲜度"即燥度，湿柴热量低（烘干可救）----
        { Goods.Wood,      new TimedBaseline(240f,   0f,   0f) },   // 柴薪：受潮是渐进的，无稳定期
        { Goods.Charcoal,  new TimedBaseline(600f,   0f, 120f) },   // 木炭：经烧制，极稳

        // ---- 木作：木材缓慢朽坏 ----
        { Goods.Log,       new TimedBaseline(300f, 1800f,  60f) },
        { Goods.Planks,    new TimedBaseline(600f,   0f,  150f) },
        { Goods.Timber,    new TimedBaseline(600f,   0f,  150f) },
        { Goods.Furniture, new TimedBaseline(900f,   0f,  240f) },

        // ---- 金工：锈蚀极慢 ----
        { Goods.IronIngot, new TimedBaseline(1200f,  0f,  480f) },
        { Goods.Ironware,  new TimedBaseline(1800f,  0f,  720f) },
        { Goods.Weapon,    new TimedBaseline(1800f,  0f,  720f) },

        // ---- 皮纺：生皮最急（鞣制后即稳）----
        { Goods.Hide,      new TimedBaseline(20f,   60f,   0f) },   // 生皮：立即开始腐
        { Goods.Leather,   new TimedBaseline(900f,   0f, 360f) },
        { Goods.Clothing,  new TimedBaseline(1200f,  0f, 480f) },

        // ---- 医药：药材会失药效，丸药有明确保质期 ----
        { Goods.Herb,      new TimedBaseline(45f,  150f,   0f) },   // 药材：开始失药效便一路下滑
        { Goods.Medicine,  new TimedBaseline(300f, 900f,  60f) },

        // ---- 盐业：结晶物只受潮 ----
        { Goods.RawSalt,      new TimedBaseline(900f,  0f, 360f) },
        { Goods.RefinedSalt,  new TimedBaseline(1800f, 0f, 720f) },

        // ---- 杂项：酒曲是活物、会失活 ----
        { Goods.Yeast,     new TimedBaseline(90f,  270f,   0f) },
    };
}

/// <summary>
/// 单种货品的时效基线（游戏日）。两个字段独立：软轴先耗尽只是"不好用"，硬轴耗尽才真正消亡。
/// 以 <see cref="TimelinessConfig.Baselines"/> 登记，未登记者不参与时效。
/// </summary>
public readonly struct TimedBaseline
{
    /// <summary>鲜度从满值（100）衰减到 0 所需游戏日（<b>含稳定期</b>）。</summary>
    public readonly float FreshDays;

    /// <summary>硬轴总寿限（游戏日）。<b>0 表示不设硬轴</b>（只衰减、永不消亡）。</summary>
    public readonly float LifespanDays;

    /// <summary>
    /// 稳定期（游戏日，<b>露天基准</b>）：此前鲜度<b>完全不衰减</b>，之后才开始线性下滑。
    ///
    /// 这是"耐储物"的真实形态——稻谷是"三年不变质"，而不是"三年里慢慢掉到六成"，
    /// 两者对玩家的体验完全不同（前者可以放心囤，后者要天天盯着）。
    /// 生鲜类（野味、烧饼、生皮）没有稳定期，故默认 0。
    ///
    /// ⚠️ 单位是<b>露天基准日</b>：仓房内实际可存 = 本值 ÷ <see cref="ShelteredRateMul"/>(0.45)。
    /// 例如粮食平台 48 → 仓房约 107 日 = <b>2.96 年</b>、露天 48 日 = 1.33 年。
    /// </summary>
    public readonly float PlateauDays;

    public TimedBaseline(float freshDays, float lifespanDays, float plateauDays = 0f)
    {
        FreshDays = freshDays;
        LifespanDays = lifespanDays;
        PlateauDays = plateauDays;
    }

    /// <summary>是否只参与软轴（无硬轴寿限）。</summary>
    public bool SoftOnly => LifespanDays <= 0f;

    /// <summary>稳定期后用于衰减的时长（游戏日）：平台期不参与衰减，故须扣除。</summary>
    public float DecayDays => System.MathF.Max(0.0001f, FreshDays - PlateauDays);
}

/// <summary>
/// 单种货品的恢复手段（"翻新"）：一次动作把鲜度拉回 <see cref="RestoreTo"/>，
/// 代价是 <c>RenewCount</c> +1 —— 寿限与衰减速率此后按复合惩罚恶化。
///
/// 刻意<b>不按比例恢复</b>（不是"+30 点"而是"拉回到 85%"）：
/// 这样连续使用会迅速失效——第二次回锅时鲜度本就没掉多少，
/// 拉到同一个值等于白费一次寿限折扣，玩家自然会挑时机。
/// </summary>
public readonly struct RenewSpec
{
    /// <summary>动作名（UI 显示用："回锅"、"烘干"）。</summary>
    public readonly string Verb;

    /// <summary>恢复到的新鲜度（取 max：已高于此值则不降）。</summary>
    public readonly float RestoreTo;

    /// <summary>每次消耗的辅料货品 id（空串表示不需辅料）。</summary>
    public readonly string FuelGoodsId;

    /// <summary>每份货品消耗的辅料份数。</summary>
    public readonly double FuelPerUnit;

    public RenewSpec(string verb, float restoreTo, string fuelGoodsId, double fuelPerUnit)
    {
        Verb = verb;
        RestoreTo = restoreTo;
        FuelGoodsId = fuelGoodsId;
        FuelPerUnit = fuelPerUnit;
    }

    /// <summary>是否需要辅料。</summary>
    public bool NeedsFuel => !string.IsNullOrEmpty(FuelGoodsId) && FuelPerUnit > 0;
}
