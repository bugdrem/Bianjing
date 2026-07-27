using System;

namespace Bianjing;

/// <summary>全局事件总线：地图/坊区/统计数据变化时广播，供渲染与UI刷新。</summary>
public static class EventBus
{
    public static event Action MapChanged;
    public static event Action ZonesChanged;
    public static event Action StatsChanged;
    public static event Action<Citizen> CitizenAdded;
    public static event Action<Citizen> CitizenRemoved;
    public static event Action GameLoaded;

    /// <summary>野生动物增减/移动（动物渲染层刷新）。</summary>
    public static event Action WildlifeChanged;

    public static void RaiseMapChanged() => MapChanged?.Invoke();
    public static void RaiseZonesChanged() => ZonesChanged?.Invoke();
    public static void RaiseStatsChanged() => StatsChanged?.Invoke();
    public static void RaiseCitizenAdded(Citizen c) => CitizenAdded?.Invoke(c);
    public static void RaiseCitizenRemoved(Citizen c) => CitizenRemoved?.Invoke(c);
    public static void RaiseGameLoaded() => GameLoaded?.Invoke();
    public static void RaiseWildlifeChanged() => WildlifeChanged?.Invoke();

    /// <summary>重开一局时清空所有订阅，避免编辑器内重启后重复订阅。</summary>
    public static void Reset()
    {
        MapChanged = null;
        ZonesChanged = null;
        StatsChanged = null;
        CitizenAdded = null;
        CitizenRemoved = null;
        GameLoaded = null;
        WildlifeChanged = null;
    }
}
