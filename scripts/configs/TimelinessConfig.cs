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
        { Goods.Game,      new TimedBaseline(10f,   30f) },   // 野味：生肉最易腐
        { Goods.Water,     new TimedBaseline(6f,    18f) },   // 饮水：隔日即馊
        { Goods.Flatbread, new TimedBaseline(15f,   45f) },   // 烧饼：回锅的主力对象
        { Goods.Fruit,     new TimedBaseline(24f,   60f) },   // 果品
        { Goods.Cured,     new TimedBaseline(90f,  360f) },   // 腌货：加工换来耐放
        { Goods.Grain,     new TimedBaseline(120f, 480f) },   // 粮食：谷物耐放（中期囤积主体）
        { Goods.Wine,      new TimedBaseline(300f, 1800f) },  // 酒：越陈越耐

        // ---- 燃料：柴薪的"鲜度"即燥度，湿柴热量低（烘干可救）----
        { Goods.Wood,      new TimedBaseline(240f,   0f) },   // 柴薪：只受潮、不消亡
        { Goods.Charcoal,  new TimedBaseline(600f,   0f) },   // 木炭：经烧制，极稳

        // ---- 木作：木材缓慢朽坏 ----
        { Goods.Log,       new TimedBaseline(300f, 1800f) },
        { Goods.Planks,    new TimedBaseline(600f,   0f) },
        { Goods.Timber,    new TimedBaseline(600f,   0f) },
        { Goods.Furniture, new TimedBaseline(900f,   0f) },

        // ---- 金工：锈蚀极慢 ----
        { Goods.IronIngot, new TimedBaseline(1200f,  0f) },
        { Goods.Ironware,  new TimedBaseline(1800f,  0f) },
        { Goods.Weapon,    new TimedBaseline(1800f,  0f) },

        // ---- 皮纺：生皮最急（鞣制后即稳）----
        { Goods.Hide,      new TimedBaseline(20f,   60f) },
        { Goods.Leather,   new TimedBaseline(900f,   0f) },
        { Goods.Clothing,  new TimedBaseline(1200f,  0f) },

        // ---- 医药：药材会失药效，丸药有明确保质期 ----
        { Goods.Herb,      new TimedBaseline(45f,  150f) },
        { Goods.Medicine,  new TimedBaseline(300f, 900f) },

        // ---- 盐业：结晶物只受潮 ----
        { Goods.RawSalt,      new TimedBaseline(900f,  0f) },
        { Goods.RefinedSalt,  new TimedBaseline(1800f, 0f) },

        // ---- 杂项：酒曲是活物、会失活 ----
        { Goods.Yeast,     new TimedBaseline(90f,  270f) },
    };
}

/// <summary>
/// 单种货品的时效基线（游戏日）。两个字段独立：软轴先耗尽只是"不好用"，硬轴耗尽才真正消亡。
/// 以 <see cref="TimelinessConfig.Baselines"/> 登记，未登记者不参与时效。
/// </summary>
public readonly struct TimedBaseline
{
    /// <summary>鲜度从满值（100）衰减到 0 所需游戏日。</summary>
    public readonly float FreshDays;

    /// <summary>硬轴总寿限（游戏日）。<b>0 表示不设硬轴</b>（只衰减、永不消亡）。</summary>
    public readonly float LifespanDays;

    public TimedBaseline(float freshDays, float lifespanDays)
    {
        FreshDays = freshDays;
        LifespanDays = lifespanDays;
    }

    /// <summary>是否只参与软轴（无硬轴寿限）。</summary>
    public bool SoftOnly => LifespanDays <= 0f;
}
