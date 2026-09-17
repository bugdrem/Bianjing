using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 时效修正源注册表：所有环境/存储/设施因素都实现 <see cref="ITimedModifier"/> 并注册于此，
/// 由本表按 Order 顺序乘算累积成最终 <see cref="TimedRates"/>。
///
/// 设计意图（用户要求"单独的接口控制，包括但不限于露天室内季节、存储方式"）：
/// 核心衰减器只认倍率、不认具体因素，因此新增一种环境（地窖、熏房、冰鉴、地火炕）
/// 只需写一个实现类 + 一行 Register，衰减器与各实体接入处一行不改。
/// </summary>
public static class TimedRegistry
{
    /// <summary>已注册的修正源（按 Order 升序，小者先应用）。</summary>
    private static readonly List<ITimedModifier> Modifiers = new();

    static TimedRegistry()
    {
        RegisterBuiltins();
    }

    /// <summary>注册修正源（重复 Id 忽略，便于热重载时重复调用）。</summary>
    public static void Register(ITimedModifier modifier)
    {
        foreach (var m in Modifiers)
            if (m.Id == modifier.Id)
                return;
        // 按 Order 插入到有序位置（修正源数量极少，线性插入足够）
        int at = Modifiers.Count;
        for (int i = 0; i < Modifiers.Count; i++)
        {
            if (Modifiers[i].Order > modifier.Order)
            {
                at = i;
                break;
            }
        }
        Modifiers.Insert(at, modifier);
    }

    /// <summary>计算某上下文下的最终时效倍率（乘算累积，中性基准 1.0）。</summary>
    public static TimedRates Compute(in TimedContext ctx)
    {
        var rates = TimedRates.Neutral;
        if (!TimelinessConfig.Enabled)
            return rates;
        for (int i = 0; i < Modifiers.Count; i++)
        {
            var m = Modifiers[i];
            if (m.Applies(ctx))
                m.Apply(ctx, ref rates);
        }
        return rates;
    }

    /// <summary>注册全部内置修正源（位置 / 季节 / 临水）。</summary>
    private static void RegisterBuiltins()
    {
        Register(new PlaceModifier());
        Register(new SeasonModifier());
        Register(new NearWaterModifier());
    }

    /// <summary>
    /// 位置修正：露天为中性基准（不动），有顶室内大幅防腐并延长寿限，
    /// 无顶棚屋居中，随身携带略差于仓房。这就是"同一物品在露天与仓库中阈值不同"的来源。
    /// </summary>
    private sealed class PlaceModifier : ITimedModifier
    {
        public string Id => "place";

        public int Order => 10;

        public bool Applies(in TimedContext ctx) => ctx.Place != TimedPlace.Ground;

        public void Apply(in TimedContext ctx, ref TimedRates rates)
        {
            switch (ctx.Place)
            {
                case TimedPlace.Sheltered:
                    rates.MulRate(TimelinessConfig.ShelteredRateMul);
                    rates.MulSpan(TimelinessConfig.ShelteredSpanMul);
                    break;
                case TimedPlace.OpenShed:
                    rates.MulRate(TimelinessConfig.OpenShedRateMul);
                    rates.MulSpan(TimelinessConfig.OpenShedSpanMul);
                    break;
                case TimedPlace.Carried:
                    rates.MulRate(TimelinessConfig.CarriedRateMul);
                    rates.MulSpan(TimelinessConfig.CarriedSpanMul);
                    break;
            }
        }
    }

    /// <summary>
    /// 季节修正：只改速率不改寿限——总保质期由物品自身与仓储条件决定，
    /// 季节影响的是"多久走完这段路"。月份区间见 <see cref="TimelinessConfig.SeasonRateMul"/>。
    /// </summary>
    private sealed class SeasonModifier : ITimedModifier
    {
        public string Id => "season";

        public int Order => 20;

        public bool Applies(in TimedContext ctx) => true;

        public void Apply(in TimedContext ctx, ref TimedRates rates)
            => rates.MulRate(TimelinessConfig.SeasonRateMul(ctx.Month));
    }

    /// <summary>临水修正：四向邻格有水面即潮气重，腐坏加快、寿限缩短。</summary>
    private sealed class NearWaterModifier : ITimedModifier
    {
        public string Id => "nearwater";

        public int Order => 30;

        public bool Applies(in TimedContext ctx) => ctx.NearWater;

        public void Apply(in TimedContext ctx, ref TimedRates rates)
        {
            rates.MulRate(TimelinessConfig.NearWaterRateMul);
            rates.MulSpan(TimelinessConfig.NearWaterSpanMul);
        }
    }
}
