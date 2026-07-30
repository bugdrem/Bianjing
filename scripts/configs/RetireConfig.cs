namespace Bianjing;

/// <summary>
/// 退休配置：致仕年龄与退休后的行为分流（业务归属：JobSystem 退休判定、CitizenAgent 退休行为）。
/// </summary>
public static class RetireConfig
{
    /// <summary>普通雇工退休年龄（岁）：达此退出当前岗位。</summary>
    public const int Age = 50;

    /// <summary>店主/家族产业内的人延迟退休年龄（岁）。</summary>
    public const int FamilyBusinessAge = 60;

    /// <summary>家庭人均资产高于此视为富裕（退休后闲逛而非采集）。</summary>
    public const double WealthyPerCapitaAssets = 200;
}
