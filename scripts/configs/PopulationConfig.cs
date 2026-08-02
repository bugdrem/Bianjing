using System;

namespace Bianjing;

/// <summary>
/// 人口配置：迁入/婚配/生育/交友/迁出的日月频概率、生育概率公式与流民自带资产
/// （业务归属：LifecycleSystem——概率均为「日频」直接取值，1x 下一游戏日 ≈ 20 现实秒；
/// 人口增长靠「迁入+分家建房」驱动，迁入细则见文中「迁入」段）。
/// </summary>
public static class PopulationConfig
{
    /// <summary>每日迁入事件概率（四类流民按权重抽一，成行还需流民营/店坊有寄居空位；见 LifecycleSystem.Immigration）。</summary>
    public const float ImmigrationChancePerDay = 0.1f;

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

    /// <summary>富裕度对高胎次生育的抑制尺度（人均资产 40,000 文时降至下限）。</summary>
    public const double WealthEase = 40_000;
    public const double BirthWealthFactorMin = 0.3;

    /// <summary>朋友数上限（社交预留）。</summary>
    public const int MaxFriends = 5;

    /// <summary>成年分家新立家庭的初始公产（文）。</summary>
    public const long SplitFamilyAssets = 1_500;

    // ---- 迁入（需求 §2.2 四类流民模型：归民/寓商/散勇/客士，权重与资产区间见下；
    // 流民现金买不起地（§8.2），须先寄居流民营/店坊就业攒钱，再由 BuildUpFromLodging 自建迁出）----

    /// <summary>四类流民权重（归一化抽签：归民最多，客士极少）。</summary>
    public const double ImmigrantWeightSettler = 0.50;
    public const double ImmigrantWeightMerchant = 0.25;
    public const double ImmigrantWeightSoldier = 0.20;
    public const double ImmigrantWeightScholar = 0.05;

    /// <summary>各类流民随身现金区间（文，寓商最富，客士最穷；携带有价物者另加变卖价值）。</summary>
    public const long SettlerAssetsMin = 5;
    public const long SettlerAssetsMax = 15;
    public const long MerchantAssetsMin = 2_000;
    public const long MerchantAssetsMax = 5_000;
    public const long SoldierAssetsMin = 100;
    public const long SoldierAssetsMax = 300;
    public const long ScholarAssetsMin = 0;
    public const long ScholarAssetsMax = 50;

    /// <summary>寄居者攒够自建住宅的门槛（文）：对齐普通宅基地地价（减半后 5,000），预算达此值且有落位才自建迁出。</summary>
    public const long SelfBuildAssets = 5_000;

    /// <summary>迁入成人的年龄区间（起始岁数 + 随机跨度）。</summary>
    public const int ArriveAgeMin = 18;
    public const int ArriveAgeSpan = 18;

    /// <summary>迁入成人的随身私产区间（起始 + 随机跨度，文）。</summary>
    public const long ArriveMoneyMin = 5;
    public const long ArriveMoneySpan = 5000;

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

    /// <summary>公式：家庭人均资产（文） → 高胎次生育系数（越富越不易再生，钳制在下限与 1 之间）。</summary>
    public static double BirthWealthFactor(long perCapitaAssets) =>
        Math.Clamp(1.0 - perCapitaAssets / 40_000.0, BirthWealthFactorMin, 1.0);

    /// <summary>流民类型（需求 §2.2）：归民（务农无技能）/ 寓商（经商）/ 散勇（携兵刃）/ 客士（携书籍）。</summary>
    public enum ImmigrantType
    {
        Settler,   // 归民：无技能，占比高
        Merchant,  // 寓商：商业技能，占比低
        Soldier,   // 散勇：战斗技能 + 兵刃，占比低
        Scholar,   // 客士：文化技能 + 书籍，占比极低
    }

    /// <summary>类型 → 随身现金下限（文）。</summary>
    public static long AssetsMinOf(ImmigrantType t) => t switch
    {
        ImmigrantType.Merchant => MerchantAssetsMin,
        ImmigrantType.Soldier => SoldierAssetsMin,
        ImmigrantType.Scholar => ScholarAssetsMin,
        _ => SettlerAssetsMin,
    };

    /// <summary>类型 → 随身现金上限（文）。</summary>
    public static long AssetsMaxOf(ImmigrantType t) => t switch
    {
        ImmigrantType.Merchant => MerchantAssetsMax,
        ImmigrantType.Soldier => SoldierAssetsMax,
        ImmigrantType.Scholar => ScholarAssetsMax,
        _ => SettlerAssetsMax,
    };

    /// <summary>类型 → 技能（需求 §2.2：归民无技能，寓商商业，散勇战斗，客士文化）。</summary>
    public static SkillType SkillOf(ImmigrantType t) => t switch
    {
        ImmigrantType.Merchant => SkillType.Commerce,
        ImmigrantType.Soldier => SkillType.Combat,
        ImmigrantType.Scholar => SkillType.Scholarship,
        _ => SkillType.None,
    };

    /// <summary>类型 → 随身携带物（需求 §2.2：散勇携兵刃、客士携书籍，可变卖折入资产；无则 null）。</summary>
    public static string CarriedOf(ImmigrantType t) => t switch
    {
        ImmigrantType.Soldier => Goods.Weapon,
        ImmigrantType.Scholar => Goods.Book,
        _ => null,
    };
}
