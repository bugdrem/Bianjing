namespace Bianjing;

/// <summary>
/// 时间配置：日历换算与流速（业务归属：GameClock 时钟驱动、各系统"月值/30 逐日结算"的分母）。
/// 一天 24 小时（= 12 时辰）、一月 12 天、一年 12 月；
/// 调整 DaysPerMonth 即缩放一年的真实时长（日/年长度 = 日历天数 × SecondsPerGameHour × 24）。
/// </summary>
public static class TimeConfig
{
    /// <summary>每天小时数（24 小时 = 十二时辰，每时辰 2 小时）。</summary>
    public const int HoursPerDay = 24;

    /// <summary>每月天数（压缩日历：12 天为一月，使一游戏月 ≈ 10 现实分钟）。</summary>
    public const int DaysPerMonth = 12;

    /// <summary>每年月数。</summary>
    public const int MonthsPerYear = 12;

    /// <summary>1x 速度下一个游戏小时对应的真实秒数（速度主控旋钮）。
    /// 值取自旧版基准 7200 秒/年 ÷ (24×30×12) ≈ 0.833 秒/时。</summary>
    public const float SecondsPerGameHour = 7200f / (24 * 30 * 12);
}
