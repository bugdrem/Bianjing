using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>一局游戏的全部运行时状态与地图修改入口。</summary>
public class GameState
{
    public static GameState I { get; set; }

    public const int RoadCost = 10;
    public const int BridgeCost = 30;

    /// <summary>城市名（新建游戏时命名，随存档保存）。</summary>
    public string CityName = "汴京";

    public MapGrid Map { get; } = new();
    public RoadNetwork Roads { get; } = new();
    public Dictionary<string, BuildingDef> Defs { get; }
    public Dictionary<int, BuildingInstance> Buildings { get; } = new();
    public Dictionary<int, Citizen> Citizens { get; } = new();
    public Dictionary<int, Family> Families { get; } = new();

    /// <summary>植物实体，以格索引（y*Size+x）为键（一格至多一株）。</summary>
    public Dictionary<int, PlantObj> Plants { get; } = new();

    /// <summary>动物实体，以自增 Id 为键。</summary>
    public Dictionary<int, AnimalObj> Animals { get; } = new();

    public double Money = 5000;
    public double Food = 500;

    /// <summary>税收政策（四大税种档位，随存档保存）。</summary>
    public TaxPolicy Taxes = new();

    /// <summary>人口 = 存活居民数（真实个体模拟，不再是宏观计数器）。</summary>
    public int Population => Citizens.Count;

    // Id 自增器公开可写，供读档恢复
    public int NextBuildingId { get; set; } = 1;
    public int NextCitizenId { get; set; } = 1;
    public int NextFamilyId { get; set; } = 1;
    public int NextPlantId { get; set; } = 1;
    public int NextAnimalId { get; set; } = 1;

    /// <summary>格子坐标→一维索引（Plants 字典键）。</summary>
    public static int CellIndex(Vector2I c) => c.Y * MapGrid.Size + c.X;

    public GameState(Dictionary<string, BuildingDef> defs)
    {
        Defs = defs;
    }

    public void PlaceRoad(Vector2I c)
    {
        ref var cell = ref Map.CellAt(c);
        RemovePlantAt(c); // 施工砍伐
        cell.HasRoad = true;
        cell.Zone = ZoneType.None;
        Roads.SetRoad(c, true);
        Money -= RoadCost;
        EventBus.RaiseMapChanged();
        EventBus.RaiseStatsChanged();
    }

    /// <summary>在水面架桥：桥面等效道路接入路网。</summary>
    public void PlaceBridge(Vector2I c)
    {
        ref var cell = ref Map.CellAt(c);
        cell.HasBridge = true;
        cell.HasRoad = true;
        Roads.SetRoad(c, true);
        Money -= BridgeCost;
        EventBus.RaiseMapChanged();
        EventBus.RaiseStatsChanged();
    }

    /// <summary>放置建筑（已通过合法性校验）。official 扣钱，grown 免费。</summary>
    public BuildingInstance PlaceBuilding(BuildingDef def, Vector2I origin)
    {
        var b = new BuildingInstance { Id = NextBuildingId++, Def = def, Origin = origin };
        Buildings[b.Id] = b;

        for (int x = origin.X; x < origin.X + def.SizeX; x++)
        {
            for (int y = origin.Y; y < origin.Y + def.SizeY; y++)
            {
                RemovePlantAt(new Vector2I(x, y)); // 施工砍伐
                ref var cell = ref Map.CellAt(x, y);
                cell.BuildingId = b.Id;
                if (def.Category == "official")
                    cell.Zone = ZoneType.None; // 官方建筑覆盖坊区；grown 保留坊区便于拆后重生
            }
        }

        if (def.Category == "official")
            Money -= def.Cost;

        EventBus.RaiseMapChanged();
        EventBus.RaiseStatsChanged();
        return b;
    }

    /// <summary>拆除：桥梁 > 道路 > 建筑 > 坊区 > 树木，逐层清理；河水不可拆。</summary>
    public void DemolishAt(Vector2I c)
    {
        if (!MapGrid.InBounds(c))
            return;

        ref var cell = ref Map.CellAt(c);
        if (cell.HasBridge)
        {
            cell.HasBridge = false;
            cell.HasRoad = false;
            Roads.SetRoad(c, false);
            EventBus.RaiseMapChanged();
        }
        else if (cell.HasRoad)
        {
            cell.HasRoad = false;
            Roads.SetRoad(c, false);
            EventBus.RaiseMapChanged();
        }
        else if (cell.BuildingId >= 0 && Buildings.TryGetValue(cell.BuildingId, out var b))
        {
            DemolishBuilding(b);
        }
        else if (cell.Zone != ZoneType.None)
        {
            cell.Zone = ZoneType.None;
            EventBus.RaiseZonesChanged();
        }
        else if (cell.HasTree)
        {
            ChopTree(c);
        }
    }

    /// <summary>拆除建筑实例（手动拆除 / 老化坍塌共用）。</summary>
    public void DemolishBuilding(BuildingInstance b)
    {
        for (int x = b.Origin.X; x < b.Origin.X + b.Def.SizeX; x++)
            for (int y = b.Origin.Y; y < b.Origin.Y + b.Def.SizeY; y++)
                Map.CellAt(x, y).BuildingId = -1;
        Buildings.Remove(b.Id);
        EventBus.RaiseMapChanged();
    }

    // ---- 植物 / 动物 ----

    /// <summary>种植树木实体（growthMonths 为初始月龄），格子不可用时返回 null。</summary>
    public PlantObj AddPlant(Vector2I c, int growthMonths)
    {
        ref var cell = ref Map.CellAt(c);
        if (!cell.IsEmpty || cell.HasTree)
            return null;
        var p = new PlantObj { Id = NextPlantId++, X = c.X, Y = c.Y, GrowthMonths = growthMonths };
        Plants[CellIndex(c)] = p;
        cell.HasTree = true;
        return p;
    }

    /// <summary>手动种树（绘制树木工具）：直接种下成树。</summary>
    public void PlaceTree(Vector2I c)
    {
        if (AddPlant(c, PlantObj.MatureMonths) != null)
            EventBus.RaiseMapChanged();
    }

    /// <summary>砍伐树木（伐木/拆除/施工），返回是否砍到。</summary>
    public bool ChopTree(Vector2I c)
    {
        if (!MapGrid.InBounds(c))
            return false;
        ref var cell = ref Map.CellAt(c);
        if (!cell.HasTree)
            return false;
        cell.HasTree = false;
        Plants.Remove(CellIndex(c));
        EventBus.RaiseMapChanged();
        return true;
    }

    /// <summary>静默移除格上植物（施工砍伐，不单独广播事件）。</summary>
    private void RemovePlantAt(Vector2I c)
    {
        ref var cell = ref Map.CellAt(c);
        if (!cell.HasTree)
            return;
        cell.HasTree = false;
        Plants.Remove(CellIndex(c));
    }

    public AnimalObj AddAnimal(Vector2I c)
    {
        var a = new AnimalObj { Id = NextAnimalId++, X = c.X, Y = c.Y };
        Animals[a.Id] = a;
        return a;
    }

    /// <summary>捕获动物（打猎），返回是否成功。</summary>
    public bool HarvestAnimal(int id)
    {
        if (!Animals.Remove(id))
            return false;
        EventBus.RaiseWildlifeChanged();
        return true;
    }

    /// <summary>找最近的动物（线性扫描，动物数量有上限）。</summary>
    public AnimalObj FindNearestAnimal(Vector2I from, int maxRadius)
    {
        AnimalObj best = null;
        int bestDist = maxRadius + 1;
        foreach (var a in Animals.Values)
        {
            int d = Math.Max(Math.Abs(a.X - from.X), Math.Abs(a.Y - from.Y));
            if (d < bestDist)
            {
                bestDist = d;
                best = a;
            }
        }
        return best;
    }

    public int CountByDef(string defId)
    {
        int n = 0;
        foreach (var b in Buildings.Values)
            if (b.Def.Id == defId)
                n++;
        return n;
    }

    // ---- 居民 / 家庭 ----

    public Citizen AddCitizen(Citizen c)
    {
        c.Id = NextCitizenId++;
        Citizens[c.Id] = c;
        EventBus.RaiseCitizenAdded(c);
        return c;
    }

    public Family AddFamily(Family f)
    {
        f.Id = NextFamilyId++;
        Families[f.Id] = f;
        return f;
    }

    /// <summary>移除居民（死亡/迁出）并清理全部社会关系引用。</summary>
    public void RemoveCitizen(int id)
    {
        if (!Citizens.Remove(id, out var c))
            return;

        if (c.SpouseId >= 0 && Citizens.TryGetValue(c.SpouseId, out var spouse))
            spouse.SpouseId = -1;
        if (c.FatherId >= 0 && Citizens.TryGetValue(c.FatherId, out var father))
            father.ChildrenIds.Remove(id);
        if (c.MotherId >= 0 && Citizens.TryGetValue(c.MotherId, out var mother))
            mother.ChildrenIds.Remove(id);
        foreach (var childId in c.ChildrenIds)
            if (Citizens.TryGetValue(childId, out var child))
            {
                if (child.FatherId == id) child.FatherId = -1;
                if (child.MotherId == id) child.MotherId = -1;
            }
        foreach (var friendId in c.FriendIds)
            if (Citizens.TryGetValue(friendId, out var friend))
                friend.FriendIds.Remove(id);

        if (Families.TryGetValue(c.FamilyId, out var family))
        {
            family.MemberIds.Remove(id);
            if (family.MemberIds.Count == 0)
                Families.Remove(family.Id);
        }

        EventBus.RaiseCitizenRemoved(c);
    }

    /// <summary>住处剩余床位（按当前等级容量计）。occupancy 为预先汇总的 homeId-&gt;人数 字典。</summary>
    public int HouseVacancy(BuildingInstance house, Dictionary<int, int> occupancy)
    {
        int used = occupancy.GetValueOrDefault(house.Id);
        return house.HousingCapacity - used;
    }

    /// <summary>汇总当前各民居入住人数。</summary>
    public Dictionary<int, int> BuildHomeOccupancy()
    {
        var occ = new Dictionary<int, int>();
        foreach (var c in Citizens.Values)
            if (c.HomeId >= 0)
                occ[c.HomeId] = occ.GetValueOrDefault(c.HomeId) + 1;
        return occ;
    }
}
