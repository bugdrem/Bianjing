namespace Bianjing;

/// <summary>
/// 就业配置：家计开销与失业分流（业务归属：JobSystem）。
/// </summary>
public static class JobsConfig
{
    /// <summary>每人每月生活开销（贯，逐日按 1/DaysPerMonth 扣，先扣公产不足再成员分摊）。</summary>
    public const double LivingCostPerCapita = 0.8;

    /// <summary>无岗可寻时转入上山谋生（伐木/采摘/打猎）的概率。</summary>
    public const float JoblessForageChance = 0.6f;
}
