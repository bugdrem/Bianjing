namespace Bianjing;

/// <summary>
/// 植物配置：树林消长、挂果落果与血量制砍伐（业务归属：PlantGrowthSystem 生长驱动、PlantObj 派生属性）。
/// 血量模型：满血上限随树龄按米氏式渐进增长（永不超过 BaseHp+HpGainCap），
/// 0月=20，1年≈47，2年=60，5年≈77，20年≈93。
/// </summary>
public static class PlantConfig
{
    /// <summary>全图植物上限，防树林无限蔓延吞噬地图（世界面积扩大四倍后同比上调）。</summary>
    public const int MaxPlants = 8800;

    /// <summary>成熟大树每月散播幼体概率与散播范围（±米，幼体继承母树类型）。</summary>
    public const float SeedChance = 0.03f;
    public const int SeedRange = 4;

    /// <summary>成树每日挂果增量（份）与挂满后每日落果概率（只有果树挂果）。</summary>
    public const double FruitPerDay = 0.1;
    public const double DropChance = 0.1;

    /// <summary>砍伐伤恢复：连续无人砍伐达到延迟天数后，每日回血量。</summary>
    public const int RegenDelayDays = 3;
    public const float RegenPerDay = 2f;

    /// <summary>长成大树所需月数。</summary>
    public const int MatureMonths = 12;

    /// <summary>挂果上限（份）：树上未掉落的果实也是一类仓储。</summary>
    public const double FruitCap = 3;

    /// <summary>新芽基础血量 / 随龄增量上限（渐进上界 BaseHp+HpGainCap）/ 半饱和树龄（月）。</summary>
    public const float BaseHp = 20f;
    public const float HpGainCap = 80f;
    public const float HpAgeHalfMonths = 24f;

    /// <summary>公式：树龄（月）→ 满血上限（米氏式渐进：增速随龄递减，永不超过 BaseHp+HpGainCap）。</summary>
    public static float MaxHpAt(int growthMonths) =>
        BaseHp + HpGainCap * growthMonths / (growthMonths + HpAgeHalfMonths);
}
