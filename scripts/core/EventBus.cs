using System;
using Godot;

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

    /// <summary>点选居民变化（-1 表示取消选中，供目标路线绘制等表现层响应）。</summary>
    public static event Action<int> CitizenSelected;

    /// <summary>单格地表变化（铺路/砍树/拆除等局部变更）：渲染层只重建所在分块，免全图重建。</summary>
    public static event Action<Vector2I> CellChanged;

    /// <summary>矩形区域地表变化（建筑落成/拆除/扩建的垫基整平）：渲染层只重建覆盖分块，
    /// 取代旧版全图 MapChanged（村民 4x 下频繁建房时全图重建是间歇卡顿主源）。</summary>
    public static event Action<Vector2I, Vector2I> RectChanged;

    /// <summary>仅建筑外观变化（升级楼高/转业换色）：只重建建筑层，不碰地形/水面/树木分块。</summary>
    public static event Action BuildingsChanged;

    /// <summary>仅树木变化（月度生长/散播幼体）：只刷新各分块树木 MultiMesh，不重建地形网格。</summary>
    public static event Action TreesChanged;

    /// <summary>城市晋级新里程碑（参数为新等级）：菜单刷新解锁项、HUD 弹报。</summary>
    public static event Action<int> MilestoneReached;

    /// <summary>科技研成（参数为科技 id）：HUD 弹报。</summary>
    public static event Action<string> TechUnlocked;

    /// <summary>新公告入栏（迁入迁出/生死等全城大事，见 GameState.PostNews）：公告栏实时刷新。</summary>
    public static event Action NewsPosted;

    /// <summary>玩家/村民新放置一座建筑（仅实时放置触发，读档重建不发）：供王爷府建成钩子、菜单刷新等响应。</summary>
    public static event Action<BuildingInstance> BuildingPlaced;

    /// <summary>某方向道路首次通到地图边缘（参数 = 该边对应的邻城方向）：供「通边即播报」响应。</summary>
    public static event Action<MapDir> RoadReachedEdge;

    public static void RaiseMapChanged() => MapChanged?.Invoke();
    public static void RaiseZonesChanged() => ZonesChanged?.Invoke();
    public static void RaiseStatsChanged() => StatsChanged?.Invoke();
    public static void RaiseCitizenAdded(Citizen c) => CitizenAdded?.Invoke(c);
    public static void RaiseCitizenRemoved(Citizen c) => CitizenRemoved?.Invoke(c);
    public static void RaiseGameLoaded() => GameLoaded?.Invoke();
    public static void RaiseWildlifeChanged() => WildlifeChanged?.Invoke();
    public static void RaiseCitizenSelected(int id) => CitizenSelected?.Invoke(id);
    public static void RaiseCellChanged(Vector2I c) => CellChanged?.Invoke(c);
    public static void RaiseRectChanged(Vector2I origin, Vector2I size) => RectChanged?.Invoke(origin, size);
    public static void RaiseBuildingsChanged() => BuildingsChanged?.Invoke();
    public static void RaiseTreesChanged() => TreesChanged?.Invoke();
    public static void RaiseMilestoneReached(int level) => MilestoneReached?.Invoke(level);
    public static void RaiseTechUnlocked(string techId) => TechUnlocked?.Invoke(techId);
    public static void RaiseNewsPosted() => NewsPosted?.Invoke();
    public static void RaiseBuildingPlaced(BuildingInstance b) => BuildingPlaced?.Invoke(b);
    public static void RaiseRoadReachedEdge(MapDir dir) => RoadReachedEdge?.Invoke(dir);

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
        CitizenSelected = null;
        CellChanged = null;
        RectChanged = null;
        BuildingsChanged = null;
        TreesChanged = null;
        MilestoneReached = null;
        TechUnlocked = null;
        NewsPosted = null;
        BuildingPlaced = null;
        RoadReachedEdge = null;
    }
}
