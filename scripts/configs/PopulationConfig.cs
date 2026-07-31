using System;

namespace Bianjing;

/// <summary>
/// 人口配置：迁入/婚配/生育/交友/迁出的日月频概率、生育概率公式与流民自带资产
/// （业务归属：LifecycleSystem——概率均为「日频」直接取值，1x 下一游戏日 ≈ 20 现实秒；
/// 人口增长靠「迁入+分家建房」驱动，迁入细则见文中「迁入」段）。
/// </summary>
public static class PopulationConfig
{
    /// <summary>每日迁入概率：夫妻户 / 单身流民（有合法落位/寄居空位才成行）。</summary>
    public const float CoupleChancePerDay = 0.1f;
    public const float SingleChancePerDay = 0.05f;

    /// <summary>单身迁入者为男性的概率。</summary>
    public const float SingleMaleChance = 0.6f;

    /// <summary>每日概率：单身男婚配 / 生育基础值 / 成年人结交新友。</summary>
    public const float MarriageChancePerDay = 0.01f;
    public const float BirthChancePerDay = 0.003f;
    public const float FriendChancePerDay = 0.01f;

    /// <summary>婚配每次抽样的候选人数（近亲跳过，抽满未果则本日作罢）。</summary>
    public const int MarriageTryCandidates = 8;

    /// <summary>无家可归累计多少月后携幼迁出。</summary>
    public const int EmigrateAfterHomelessMonths = 6;

    /// <summary>满员住户每月触发拥挤事件（扩建/分家疏解）的概率。</summary>
    public const float CrowdEventChance = 0.15f;

    /// <summary>生育的住房容量封顶倍率（住户数超容量×此倍即停生，超员由拥挤事件疏解）。</summary>
    public const double BirthOverCapFactor = 1.5;

    /// <summary>胎次递减：第 3/4 胎系数、第 5 胎起基数与每多一胎的衰减倍率（永不归零）。</summary>
    public const double BirthFactor3rd = 0.6;
    public const double BirthFactor4th = 0.3;
    public const double BirthFactor5thBase = 0.12;
    public const double BirthDecayPerExtra = 0.5;

    /// <summary>高胎次的母亲年龄抑制：起始岁数、每岁递减、系数下限。</summary>
    public const int BirthAgeSlowStart = 30;
    public const double BirthAgeSlowPerYear = 0.05;
    public const double BirthAgeFactorMin = 0.2;

    /// <summary>富裕度对第五胎后生育的抑制尺度（家庭人均资产达此值时降至下限）与系数下限。</summary>
    public const double WealthEase = 400;
    public const double BirthWealthFactorMin = 0.3;

    /// <summary>朋友数上限（社交预留）。</summary>
    public const int MaxFriends = 5;

    /// <summary>成年分家新立家庭的初始公产（贯）。</summary>
    public const double SplitFamilyAssets = 15;

    // ---- 迁入（原 ImmigrationConfig 并入）：流民自带资产与自建门槛 ----

    /// <summary>迁入者随机自带资产区间（家庭公产初值，扣除建房地价后余额入公产）。</summary>
    public const double ArriveAssetsMin = 20;
    public const double ArriveAssetsMax = 120;

    /// <summary>单身自建住宅门槛：资产达此值且有合法落位才自建，否则寄居店坊当暂住雇工。</summary>
    public const double SelfBuildAssets = 80;

    /// <summary>迁入成人的年龄区间（起始岁数 + 随机跨度）。</summary>
    public const int ArriveAgeMin = 18;
    public const int ArriveAgeSpan = 18;

    /// <summary>迁入成人的随身私产区间（起始 + 随机跨度，贯）。</summary>
    public const double ArriveMoneyMin = 10;
    public const double ArriveMoneySpan = 20;

    /// <summary>公式：胎次 → 生育系数（1~3 胎最大，之后递减，第六胎起指数衰减永不归零）。</summary>
    public static double BirthCountFactor(int kids) =>
        kids <= 2 ? 1.0
        : kids == 3 ? BirthFactor3rd
        : kids == 4 ? BirthFactor4th
        : BirthFactor5thBase * Math.Pow(BirthDecayPerExtra, kids - 5);

    /// <summary>公式：母亲年龄 → 高胎次生育系数（起始岁前不减，之后线性下降到下限）。</summary>
    public static double BirthAgeFactor(int ageYears) =>
        ageYears <= BirthAgeSlowStart ? 1.0
        : Math.Max(BirthAgeFactorMin, 1.0 - (ageYears - BirthAgeSlowStart) * BirthAgeSlowPerYear);

    /// <summary>公式：家庭人均资产 → 高胎次生育系数（越富越不易再生，钳制在下限与 1 之间）。</summary>
    public static double BirthWealthFactor(double perCapitaAssets) =>
        Math.Clamp(1.0 - perCapitaAssets / WealthEase, BirthWealthFactorMin, 1.0);
}
