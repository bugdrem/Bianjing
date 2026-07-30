namespace Bianjing;

/// <summary>
/// 迁入配置：流民自带资产与自建门槛（业务归属：LifecycleSystem.Immigration，
/// 控制「靠迁入+分家建房」的人口增长节奏）。
/// </summary>
public static class ImmigrationConfig
{
    /// <summary>迁入者随机自带资产区间（家庭公产初值，扣除建房地价后余额入公产）。</summary>
    public const double AssetsMin = 20;
    public const double AssetsMax = 120;

    /// <summary>单身自建住宅门槛：资产达此值且有合法落位才自建，否则寄居店坊当暂住雇工。</summary>
    public const double SelfBuildAssets = 80;

    /// <summary>迁入成人的年龄区间（起始岁数 + 随机跨度）。</summary>
    public const int ArriveAgeMin = 18;
    public const int ArriveAgeSpan = 18;

    /// <summary>迁入成人的随身私产区间（起始 + 随机跨度，贯）。</summary>
    public const double ArriveMoneyMin = 10;
    public const double ArriveMoneySpan = 20;
}
