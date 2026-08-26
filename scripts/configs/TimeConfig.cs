namespace Bianjing;

/// <summary>
/// 时间与作息配置：日历换算、流速与居民上下工时刻
/// （业务归属：GameClock 时钟驱动、各系统“月值/3 逐旬结算”的分母、CitizenAgent 日常决策）。
/// 一天 24 小时、一月 3 旬、一年 12 月（批次九十一：日历由 7 天/月改 3 旬/月，
/// 显示上旬/中旬/下旬；流速改为 1 游戏旬 = 1 现实分钟，一游戏年 = 36 现实分钟）。
/// 调整 DaysPerMonth 即缩放每旬的真实时长（旬长 = SecondsPerGameHour × 24）。
/// </summary>
public static class TimeConfig
{
    /// <summary>每天小时数（24 小时，白天 14 时 [5–19) / 夜晚 10 时 [19–次日5)，见 DayStartHour/NightStartHour）。</summary>
    public const int HoursPerDay = 24;

    /// <summary>每月旬数（3 旬为一月：上旬/中旬/下旬，一游戏旬 = 1 现实分钟、一游戏月 ≈ 3 现实分钟）。</summary>
    public const int DaysPerMonth = 3;

    /// <summary>每年月数。</summary>
    public const int MonthsPerYear = 12;

    /// <summary>1x 速度下一个游戏小时对应的真实秒数（速度主控旋钮）。
    /// 基准：1 游戏旬 = 1 现实分钟 = 60 秒（旬 24 时 ÷ 60 秒 = 2.5 秒/游戏时，年 36 旬 = 36 分钟）。</summary>
    public const float SecondsPerGameHour = 60f / 24;

    /// <summary>白天/夜晚分界（时）：白天 = [DayStartHour, NightStartHour)，其余为夜晚。
    /// 昼夜两态（显示与作息判定共用，光照夜间联动变暗）。
    /// 与一日六时对齐：白天 平旦→晡时（5–19，14 时），夜晚 黄昏→夜半（19–次日5，10 时）。</summary>
    public const int DayStartHour = 5;
    public const int NightStartHour = 19;

    // ---- 作息（原 ScheduleConfig 并入）：有职居民的上下工时刻与轮休周期 ----

    /// <summary>上班时段起止时（含起不含止）：清晨上工、傍晚收工（工作时段为白天的一部分，
    /// 不与昼夜边界完全重合；19–次日5 为夜、18–19 视作居民傍晚闲暇）。</summary>
    public const int WorkStartHour = 6;
    public const int WorkEndHour = 18;

    /// <summary>轮休周期（旬）：每满此旬数休息一天（按个体错峰，不全城同日停工；旧 5 日×3/7≈2 旬）。</summary>
    public const int RestCycleDays = 2;
}
