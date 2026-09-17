using System;

namespace Bianjing;

/// <summary>
/// 时效规则查询：把「客观状态 + 当前环境 + 货品基线」折算成当下可用的时效参数。
///
/// 本类是纯函数集合（无状态），六类实体共用同一套阶段判定、效果折算与分档规则——
/// 这就是"复用同一套机制"的落点：换实体只需换 <see cref="TimedBaseline"/>，规则不动。
/// </summary>
public static class TimedRules
{
    /// <summary>该货品是否参与时效（未登记基线的货品永不腐坏，如矿石、书籍、废料）。</summary>
    public static bool Applies(string goodsId)
        => !string.IsNullOrEmpty(goodsId) && TimelinessConfig.Baselines.ContainsKey(goodsId);

    /// <summary>取货品基线；未登记时返回"不参与"的零基线。</summary>
    public static TimedBaseline BaselineOf(string goodsId)
        => TimelinessConfig.Baselines.TryGetValue(goodsId, out var b) ? b : default;

    /// <summary>
    /// 软轴阶段判定：全效果 → 渐变衰减 → 已失效。
    /// 阈值见 <see cref="TimelinessConfig"/>（默认 60 / 15）。
    /// </summary>
    public static FreshStage StageOf(float fresh)
    {
        if (fresh >= TimelinessConfig.FreshFullThreshold)
            return FreshStage.Full;
        return fresh >= TimelinessConfig.FreshWaneThreshold ? FreshStage.Waning : FreshStage.Spent;
    }

    /// <summary>
    /// 软轴效果折算系数 0~1（连续、单调）：
    /// 全效果段恒为 1；渐变段从 1 线性降到 0.25（"还能吃但很差"）；
    /// 失效段从 0.25 继续滑到 0（归零即完全无用）。
    /// 消耗时以「需要有效量 ÷ 系数」反推实际取用量——鲜度 50% 的粮要吃双倍才饱。
    /// </summary>
    public static float EffectOf(float fresh)
    {
        float full = TimelinessConfig.FreshFullThreshold;
        float wane = TimelinessConfig.FreshWaneThreshold;
        if (fresh >= full)
            return 1f;
        if (fresh >= wane)
            return Lerp(0.25f, 1f, (fresh - wane) / MathF.Max(0.0001f, full - wane));
        return Lerp(0f, 0.25f, MathF.Max(0f, fresh) / MathF.Max(0.0001f, wane));
    }

    /// <summary>
    /// 解析当前时效参数：阈值与速率都现算——
    /// 环境（露天/仓库/季节）实时的倍率、以及翻新次数带来的复合惩罚都在此生效。
    /// </summary>
    public static TimedResolved Resolve(in TimedState st, in TimedRates rates, string goodsId)
    {
        var baseline = BaselineOf(goodsId);
        // 未登记基线的货品：不参与时效，效果恒满、无硬轴
        if (baseline.FreshDays <= 0f && baseline.LifespanDays <= 0f)
            return new TimedResolved(0f, 0f, -1f, 1f, FreshStage.Full, true);

        // 翻新惩罚复合叠加：每多翻新一次，寿限再打一次折、老化再加速一次
        int n = Math.Min(st.RenewCount, TimelinessConfig.RenewMaxCount);
        float renewSpan = MathF.Pow(TimelinessConfig.RenewSpanPenalty, n);
        float renewRate = MathF.Pow(TimelinessConfig.RenewRatePenalty, n);

        bool softOnly = baseline.LifespanDays <= 0f;
        float span = softOnly ? 0f : baseline.LifespanDays * rates.LifespanSpan * renewSpan;
        // 老化速率倍率：环境（仓房 0.45 / 露天 1.0）× 翻新惩罚
        float agingRate = rates.FreshRate * renewRate;
        // 剩余寿限必须夹紧：环境会实时改变 span，未夹紧时可能出现负数
        float remain = softOnly ? -1f : MathF.Max(0f, span - st.UsedLifespan);
        // 鲜度由累积龄期派生（稳定期内恒为 100），而非逐日扣减
        float fresh = FreshOf(st.FreshAgeDays, baseline);

        return new TimedResolved(span, agingRate, remain, EffectOf(fresh), StageOf(fresh), softOnly);
    }

    /// <summary>
    /// 由累积老化龄期算鲜度：<b>稳定期内恒为 100，之后线性下降到 0</b>。
    ///
    /// 这就是"稻谷三年不变质"的落点——谷物的真实储存曲线是"先长期稳定、之后加速劣变"，
    /// 而不是从入库第一天就线性下滑（后者对玩家意味着"要天天盯着"，
    /// 前者意味着"可以放心囤三年"）。两者的数值可以调成一样，体验却完全不同。
    /// </summary>
    public static float FreshOf(float freshAgeDays, in TimedBaseline baseline)
    {
        if (baseline.FreshDays <= 0f)
            return 100f;
        float decayAge = MathF.Max(0f, freshAgeDays - baseline.PlateauDays);
        return Math.Clamp(100f * (1f - decayAge / baseline.DecayDays), 0f, 100f);
    }

    /// <summary>由目标鲜度反算对应的老化龄期——翻新动作"把鲜度拉回某个值"时用。</summary>
    public static float AgeForFresh(float targetFresh, in TimedBaseline baseline)
    {
        if (baseline.FreshDays <= 0f)
            return 0f;
        float decay = (1f - Math.Clamp(targetFresh, 0f, 100f) / 100f) * baseline.DecayDays;
        return baseline.PlateauDays + decay;
    }

    /// <summary>软轴分档（用于合并判定）：按 <see cref="TimelinessConfig.FreshBandSize"/> 向下取整。</summary>
    public static int FreshBandOf(float fresh)
        => (int)MathF.Floor(MathF.Max(0f, fresh) / TimelinessConfig.FreshBandSize);

    /// <summary>
    /// 硬轴分档（用于合并判定）：按<b>已耗寿限</b>向下取整。
    ///
    /// 刻意用"已耗"而非"剩余"——剩余寿限是派生量（受环境 Span 倍率影响，会随搬家而变），
    /// 拿它当合并键会导致"同一批货物因环境变化被拆成两批"。已耗寿限是客观事实，只增不减。
    /// </summary>
    public static int UsedBandOf(float usedLifespan)
        => (int)MathF.Floor(MathF.Max(0f, usedLifespan) / TimelinessConfig.LifespanBandDays);

    /// <summary>
    /// 两批货物是否可合并：<b>客观量同档</b>（鲜度档 + 已耗寿限档）+ 翻新次数相同。
    ///
    /// 合并键刻意不含环境标签：同一储存点内的所有物品共享同一套环境倍率，
    /// 因此"露天坏得快、仓库坏得慢"这个差异会自然沉淀到各自的 Fresh 数值里——
    /// 两天后它们自动落到不同鲜度档而分成两批，无需任何环境标记，也不需要季节变化时重新分批。
    /// </summary>
    public static bool SameBand(in TimedState a, in TimedState b)
        => FreshBandOf(a.Fresh) == FreshBandOf(b.Fresh)
        && UsedBandOf(a.UsedLifespan) == UsedBandOf(b.UsedLifespan)
        && a.RenewCount == b.RenewCount;

    /// <summary>
    /// 加权平均合并两批的状态（同档内合并）：份数作权重，翻新次数取较大者（保守）。
    /// 同档内差异上限即档宽（鲜度 10 点、寿限 30 日），平均后误差可接受。
    /// </summary>
    public static TimedState Blend(in TimedState a, double wa, in TimedState b, double wb)
    {
        double total = wa + wb;
        if (total <= 0.0001)
            return a;
        return new TimedState
        {
            Fresh = (float)((a.Fresh * wa + b.Fresh * wb) / total),
            UsedLifespan = (float)((a.UsedLifespan * wa + b.UsedLifespan * wb) / total),
            RenewCount = System.Math.Max(a.RenewCount, b.RenewCount),
        };
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}
