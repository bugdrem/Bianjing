using System;

namespace Bianjing;

/// <summary>软轴（新鲜度）三阶段：全效果 → 渐变衰减 → 已失效（原用途失效，仍可作他用）。</summary>
public enum FreshStage
{
    /// <summary>全效果：Fresh ≥ <see cref="TimelinessConfig.FreshFullThreshold"/>。</summary>
    Full = 0,

    /// <summary>渐变衰减：效果按比例打折，仍可作原用途。</summary>
    Waning = 1,

    /// <summary>已失效：原用途效果趋近于零，只能转化、降级或丢弃。</summary>
    Spent = 2,
}

/// <summary>
/// 时效状态：两根时间轴的统一载体，六类实体（货品/房屋/道路/植物/动物/人）共用同一套字段名与规则。
///
/// <b>只存客观事实</b>——当前鲜度、已耗寿限、翻新次数。
/// "当前阈值是多少、还剩几天"一律现算（<see cref="TimedRules.Resolve"/>）：
/// 因为环境（露天/仓库/季节）会实时改变阈值，把它存进数据里就会在环境变化时失真。
///
/// <b>已耗寿限单调递增</b>，任何"回滚"都只能通过显式翻新动作（回锅/烘干/修缮）减少翻新次数之外的量，
/// 不接受由环境切换自动回退——否则会出现"来回搬家刷寿命"的漏洞。
/// </summary>
public struct TimedState
{
    /// <summary>软轴：当前新鲜度 0~100（100 = 刚产出）。</summary>
    public float Fresh;

    /// <summary>硬轴：已耗用的寿限（游戏日，单调累积，与软轴独立计时）。</summary>
    public float UsedLifespan;

    /// <summary>翻新次数：驱动寿限与衰减速率的复合惩罚（每次翻新都透支未来）。</summary>
    public int RenewCount;

    /// <summary>全新状态（刚产出/刚建成的物品）。</summary>
    public static TimedState New => new() { Fresh = 100f, UsedLifespan = 0f, RenewCount = 0 };

    /// <summary>软轴当前阶段。</summary>
    public readonly FreshStage Stage => TimedRules.StageOf(Fresh);

    /// <summary>软轴效果折算系数 0~1（消耗时按此折算有效量）。</summary>
    public readonly float Effect => TimedRules.EffectOf(Fresh);
}

/// <summary>
/// <see cref="TimedRules.Resolve"/> 的解析结果：把"客观事实 + 当前环境"折算成当下可用的时效参数。
/// 环境一变这些值立刻变，故绝不入存档。
/// </summary>
public readonly struct TimedResolved
{
    /// <summary>当前硬轴寿限阈值（游戏日）；<b>≤0 表示该物不设硬轴</b>（永不消亡）。</summary>
    public readonly float Span;

    /// <summary>当前软轴衰减速率（鲜度点 / 游戏日）。</summary>
    public readonly float FreshRate;

    /// <summary>当前硬轴剩余寿限（游戏日，已夹紧到 ≥0）；&lt;0 表示无硬轴。</summary>
    public readonly float Remain;

    /// <summary>软轴效果折算系数 0~1。</summary>
    public readonly float Effect;

    /// <summary>软轴阶段。</summary>
    public readonly FreshStage Stage;

    /// <summary>是否无硬轴（永不消亡）。</summary>
    public readonly bool SoftOnly;

    public TimedResolved(float span, float freshRate, float remain, float effect, FreshStage stage, bool softOnly)
    {
        Span = span;
        FreshRate = freshRate;
        Remain = remain;
        Effect = effect;
        Stage = stage;
        SoftOnly = softOnly;
    }

    /// <summary>硬轴是否已耗尽（该消亡或降级）。</summary>
    public readonly bool Expired => !SoftOnly && Remain <= 0f;
}
