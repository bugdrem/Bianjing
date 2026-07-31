using System;

namespace Bianjing;

/// <summary>
/// 寿命与死亡配置：年龄门槛（含致仕）与 Gompertz 死亡率曲线
/// （业务归属：LifecycleSystem 老化/死亡，Citizen 派生属性，JobSystem 退休判定，CitizenAgent 退休行为）。
/// 死亡率模型：年死亡率 = 基础底噪 + 幅值 A × e^((age-陡增起点)/尺度)，主要死亡区间约 55-65 岁；
/// 健康值经放大系数影响死亡率（当前健康恒满为中性，健康系统接入后自动生效）。
/// </summary>
public static class LifeConfig
{
    /// <summary>成年门槛（岁）：达到即为成年，可打工/婚嫁/繁育/立户。</summary>
    public const int AdultAgeYears = 16;

    /// <summary>老年线（岁）：达到即为老人（行为层闲逛/在家为主）。</summary>
    public const int ElderAgeYears = 60;

    /// <summary>最大寿数（岁）：达到必亡，任何个体不超过此龄。</summary>
    public const int MaxAgeYears = 120;

    /// <summary>婚配年龄上限（岁）。</summary>
    public const int MarriageMaxAgeYears = 50;

    /// <summary>生育年龄区间（岁）：下限同成年门槛，上限见此。</summary>
    public const int FertileMinAgeYears = AdultAgeYears;
    public const int FertileMaxAgeYears = 45;

    // ---- 致仕（原 RetireConfig 并入）：退休年龄与退休后的行为分流 ----

    /// <summary>普通雇工退休年龄（岁）：达此退出当前岗位。</summary>
    public const int RetireAge = 50;

    /// <summary>店主/家族产业内的人延迟退休年龄（岁）。</summary>
    public const int FamilyBusinessRetireAge = 60;

    /// <summary>家庭人均资产高于此视为富裕（退休后闲逛而非采集）。</summary>
    public const double WealthyPerCapitaAssets = 200;

    /// <summary>任何年龄的基础年死亡率（意外/疾病等与龄无关的底噪）。</summary>
    public const float BaseAnnualMortality = 0.005f;

    /// <summary>死亡率陡增起点（岁）：主要死亡区间由此展开（约 55-65）。</summary>
    public const int MortalityRampAgeYears = 55;

    /// <summary>陡增起点处的年死亡率系数（Gompertz 幅值 A）。</summary>
    public const float MortalityAtRamp = 0.03f;

    /// <summary>Gompertz 尺度（岁）：越小死亡率随龄上升越陡。</summary>
    public const float MortalityScaleYears = 8f;

    /// <summary>饥荒（官粮见底）时的月死亡率附加值。</summary>
    public const float FamineMonthlyDeathBonus = 0.03f;

    /// <summary>健康值对死亡率的放大系数上限（健康 0 时最多放大到此倍数）。</summary>
    public const float HealthMortalityCap = 4f;

    /// <summary>公式：某年龄的年死亡率（未含健康放大与钳制）= 底噪 + A × e^((age-起点)/尺度)。</summary>
    public static double AnnualMortalityAt(int ageYears) =>
        BaseAnnualMortality
        + MortalityAtRamp * Math.Exp((ageYears - MortalityRampAgeYears) / (double)MortalityScaleYears);

    /// <summary>公式：健康值 → 死亡率放大系数（满值 100 时为 1.0，越低越易亡，封顶 HealthMortalityCap）。</summary>
    public static double HealthMortalityFactor(float health) =>
        Math.Clamp(1.0 + (100.0 - health) / 100.0, 1.0, HealthMortalityCap);

    /// <summary>公式：年死亡率 → 月死亡率（复利换算：月率 = 1 - (1-年率)^(1/12)）。</summary>
    public static double MonthlyFromAnnual(double annual) =>
        1.0 - Math.Pow(1.0 - Math.Clamp(annual, 0.0, 1.0), 1.0 / 12.0);
}
