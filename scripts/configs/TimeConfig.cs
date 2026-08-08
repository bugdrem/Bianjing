namespace Bianjing;

/// <summary>
/// 时间与作息配置：日历换算、流速与居民上下工时刻
/// （业务归属：GameClock 时钟驱动、各系统“月值/7 逐日结算”的分母、CitizenAgent 日常决策）。
/// 一天 24 小时、一月 7 天、一年 12 月（批次七十五：日历由 10 天/月改 7 天/月，
/// 面板仍 30 天制——后台 7 天显示 1/5/10/15/20/25/30 日；流速保持 1 游戏年 = 1 现实小时）。
/// 调整 DaysPerMonth 即缩放每日的真实时长（日长 = SecondsPerGameHour × 24，年长恒为 3600 秒）。
/// </summary>
public static class TimeConfig
{
    /// <summary>每天小时数（24 小时，白天/夜晚各 12 时，见 DayStartHour/NightStartHour）。</summary>
    public const int HoursPerDay = 24;

    /// <summary>每月天数（压缩日历：7 天为一月，一游戏日 ≈ 43 现实秒、一游戏月 ≈ 5 现实分钟）。</summary>
    public const int DaysPerMonth = 7;

    /// <summary>每年月数。</summary>
    public const int MonthsPerYear = 12;

    /// <summary>1x 速度下一个游戏小时对应的真实秒数（速度主控旋钮）。
    /// 基准：1 游戏年（84 天） = 1 现实小时 = 3600 秒（公式随 DaysPerMonth 自动重算）。</summary>
    public const float SecondsPerGameHour = 3600f / (24 * DaysPerMonth * MonthsPerYear);

    /// <summary>白天/夜晚分界（时）：白天 = [DayStartHour, NightStartHour)，其余为夜晚。
    /// 批次七十四：去除十二时辰，昼夜两态（显示与作息判定共用，光照夜间联动变暗）。</summary>
    public const int DayStartHour = 6;
    public const int NightStartHour = 18;

    // ---- 作息（原 ScheduleConfig 并入）：有职居民的上下工时刻与轮休周期 ----

    /// <summary>上班时段起止时（含起不含止）：早晨上工、下午收工（与昼夜边界一致）。</summary>
    public const int WorkStartHour = 6;
    public const int WorkEndHour = 18;

    /// <summary>轮休周期（天）：每满此天数休息一天（按个体错峰，不全城同日停工）。</summary>
    public const int RestCycleDays = 5;
}
