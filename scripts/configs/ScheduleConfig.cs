namespace Bianjing;

/// <summary>
/// 作息配置：有职居民的上下工时刻与轮休周期（业务归属：CitizenAgent 日常决策）。
/// </summary>
public static class ScheduleConfig
{
    /// <summary>上班时段起止时（含起不含止）：早晨上工、下午收工。</summary>
    public const int WorkStartHour = 6;
    public const int WorkEndHour = 18;

    /// <summary>轮休周期（天）：每满此天数休息一天（按个体错峰，不全城同日停工）。</summary>
    public const int RestCycleDays = 5;
}
