using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>一局游戏的全部运行时状态与地图修改入口。</summary>
public class GameState
{
    public static GameState I { get; set; }

    /// <summary>桥梁每延米造价（调参见 configs/WorldConfig）。</summary>
    public const int BridgeCost = WorldConfig.BridgeCost;

    /// <summary>两种道路造价（每延米，不计宽度，调参见 configs/WorldConfig）。</summary>
    public static int RoadCostOf(RoadKind kind) => kind == RoadKind.Main ? WorldConfig.MainRoadCost : WorldConfig.SideRoadCost;

    /// <summary>道路占地宽度（米/格，调参见 configs/WorldConfig）。</summary>
    public static int RoadWidthOf(RoadKind kind) => kind == RoadKind.Main ? WorldConfig.MainRoadWidth : WorldConfig.SideRoadWidth;

    /// <summary>桥梁宽度（米/格，调参见 configs/WorldConfig）。</summary>
    public const int BridgeWidth = WorldConfig.BridgeWidth;

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

    /// <summary>地面物资堆，以格索引（y*Size+x）为键（一格至多一堆，拾空即消）。</summary>
    public Dictionary<int, ItemPileObj> Piles { get; } = new();

    public double Money = WorldConfig.StartMoney;
    public double Food = WorldConfig.StartFood;

    /// <summary>官库收支账本（本月/上月分类流水，随存档保存）。</summary>
    public Ledger Ledger = new();

    /// <summary>全城公告（新在前，上限 200 条；随存档保存，读档续接旧事）。</summary>
    public readonly List<NewsItem> News = new();

    /// <summary>当前游戏日期（由 Main 随时钟同步，供建造盖戳等数据层使用）。</summary>
    public int CurYear = 1;
    public int CurMonth = 1;

    /// <summary>税收政策（四大税种档位，随存档保存）。</summary>
    public TaxPolicy Taxes = new();

    /// <summary>城市里程碑等级（见 Milestones.Levels，随存档保存）：控制建筑解锁/居民需求/住宅限级。</summary>
    public int MilestoneLevel;

    /// <summary>已研成科技 id 集（随存档保存）。</summary>
    public HashSet<string> TechsUnlocked { get; } = new();

    /// <summary>当前主动研习中的科技 id（空串表示未在研）与已投入天数。</summary>
    public string ResearchTechId = "";
    public double ResearchDays;

    /// <summary>科技加成系数：1 + 已研成科技对该效果键的加成之和（如 "harvest"/"craft"/"tax"/"mint"）。</summary>
    public double TechFactor(string key)
    {
        double f = 1.0;
        foreach (var id in TechsUnlocked)
        {
            var def = TechDefs.Find(id);
            if (def != null)
                f += def.Effects.GetValueOrDefault(key);
        }
        return f;
    }

    /// <summary>人口 = 存活居民数（真实个体模拟，不再是宏观计数器）。</summary>
    public int Population => Citizens.Count;

    // Id 自增器公开可写，供读档恢复
    public int NextBuildingId { get; set; } = 1;
    public int NextCitizenId { get; set; } = 1;
    public int NextFamilyId { get; set; } = 1;
    public int NextPlantId { get; set; } = 1;
    public int NextAnimalId { get; set; } = 1;
    public int NextPileId { get; set; } = 1;

    /// <summary>格子坐标→一维索引（Plants 字典键）。</summary>
    public static int CellIndex(Vector2I c) => c.Y * MapGrid.Size + c.X;

    // ---- 增量维护的格子索引（免全图扫描，为大地图铺路）----

    /// <summary>全部道路格（含桥面）：随机选点/吸引力泼溅直接遍历此表。</summary>
    public List<Vector2I> RoadCells { get; } = new();
    private readonly Dictionary<Vector2I, int> _roadIndex = new();

    /// <summary>坐标→列表序号的反查，支撑 O(1) 尾交换删除。</summary>
    public void RegisterRoadCell(Vector2I c)
    {
        if (_roadIndex.ContainsKey(c))
            return;
        _roadIndex[c] = RoadCells.Count;
        RoadCells.Add(c);
    }

    public void UnregisterRoadCell(Vector2I c)
    {
        if (!_roadIndex.Remove(c, out int i))
            return;
        var last = RoadCells[^1];
        RoadCells.RemoveAt(RoadCells.Count - 1);
        if (i < RoadCells.Count)
        {
            RoadCells[i] = last;
            _roadIndex[last] = i;
        }
    }

    /// <summary>全部「可建设区」格：坊区生长只在此集内挑建房点，免每日全图扫描。</summary>
    public HashSet<Vector2I> BuildableCells { get; } = new();

    /// <summary>统一的坊区写入口：同步维护 BuildableCells 索引（绕过此方法直写 cell.Zone 会使索引失真）。</summary>
    public void SetZone(Vector2I c, ZoneType zone)
    {
        ref var cell = ref Map.CellAt(c);
        if (cell.Zone == zone)
            return;
        if (cell.Zone == ZoneType.Buildable)
            BuildableCells.Remove(c);
        cell.Zone = zone;
        if (zone == ZoneType.Buildable)
            BuildableCells.Add(c);
    }

    public GameState(Dictionary<string, BuildingDef> defs)
    {
        Defs = defs;
    }

    /// <summary>以 center 为中心的方形道路画笔（w×w，宽度随道路种类）：陆地空格铺路，
    /// 遇水面格自动架一座与路同宽的小桥（辅路w2、主路w4）——“和道路同步”，拖拉一次画成不断档；
    /// 岸上路段按道路单价、跨水桥段按桥梁单价，各按等效延米（新格数÷宽）计费（重叠盖戳不多扣）；
    /// 已占用格（路/已有桥/建筑）跳过；钱不够时不铺。返回是否铺下了新格。</summary>
    public bool PlaceRoadStamp(Vector2I center, RoadKind kind)
    {
        int roadCost = RoadCostOf(kind);
        if (!GameSettings.InfiniteMoney && Money < roadCost)
            return false;

        // 宽度为偶数，偏移范围 -(w-1)/2..w/2 两轴一致（宽 4 时 -1..2）
        int w = RoadWidthOf(kind);
        int newRoadCells = 0, newBridgeCells = 0;
        for (int ox = -((w - 1) / 2); ox <= w / 2; ox++)
        {
            for (int oy = -((w - 1) / 2); oy <= w / 2; oy++)
            {
                var c = center + new Vector2I(ox, oy);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref Map.CellAt(c);
                if (cell.HasWater)
                {
                    // 路跨水：在无桥的水面格架同宽小桥（已有桥则跳过）
                    if (cell.HasBridge)
                        continue;
                    LayBridgeCell(c);
                    newBridgeCells++;
                }
                else if (cell.IsEmpty)
                {
                    LayRoadCell(c, kind);
                    newRoadCells++;
                }
                // 其它（已有路/建筑）跳过
            }
        }
        if (newRoadCells > 0)
        {
            double charge = (double)roadCost * newRoadCells / w; // 新格数/宽 = 等效延米，斜拖/重叠不多扣
            Money -= charge;
            Ledger.Add("营造道路", -charge);
        }
        if (newBridgeCells > 0)
        {
            double charge = (double)BridgeCost * newBridgeCells / w; // 跨水段按桥梁单价
            Money -= charge;
            Ledger.Add("营造桥梁", -charge);
        }
        if (newRoadCells > 0 || newBridgeCells > 0)
            EventBus.RaiseStatsChanged();
        return newRoadCells > 0 || newBridgeCells > 0;
    }

    /// <summary>以 center 为中心的方形桥梁画笔（4×4 米）：只在无桥的水面格架设，
    /// 按实际新架格数折算延米计价（同道路，重叠盖戳不多扣）。</summary>
    public bool PlaceBridgeStamp(Vector2I center)
    {
        if (!GameSettings.InfiniteMoney && Money < BridgeCost)
            return false;

        int newCells = 0;
        for (int ox = -((BridgeWidth - 1) / 2); ox <= BridgeWidth / 2; ox++)
        {
            for (int oy = -((BridgeWidth - 1) / 2); oy <= BridgeWidth / 2; oy++)
            {
                var c = center + new Vector2I(ox, oy);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref Map.CellAt(c);
                if (!cell.HasWater || cell.HasBridge)
                    continue; // 岸上/已有桥的格跳过，桥只跨水
                LayBridgeCell(c);
                newCells++;
            }
        }
        if (newCells > 0)
        {
            double charge = (double)BridgeCost * newCells / BridgeWidth;
            Money -= charge;
            Ledger.Add("营造桥梁", -charge);
            EventBus.RaiseStatsChanged();
        }
        return newCells > 0;
    }

    /// <summary>架单格桥面（水面格，等效辅路可通行；条带内部用，不扣费不广播统计）。</summary>
    private void LayBridgeCell(Vector2I c)
    {
        ref var cell = ref Map.CellAt(c);
        cell.HasBridge = true;
        cell.HasRoad = true;
        Roads.SetRoad(c, true); // 桥面 kind=None：寻路权重同辅路
        RegisterRoadCell(c);
        EventBus.RaiseCellChanged(c);
    }

    /// <summary>铺单格道路（条带内部用，不扣费不广播统计）。</summary>
    private void LayRoadCell(Vector2I c, RoadKind kind)
    {
        ref var cell = ref Map.CellAt(c);
        RemovePlantAt(c); // 施工砍伐
        cell.HasRoad = true;
        cell.RoadKind = kind;
        SetZone(c, ZoneType.None);
        Roads.SetRoad(c, true, kind); // 同步寻路权重：主路代价低，居民偏好走主路
        RegisterRoadCell(c);
        EventBus.RaiseCellChanged(c); // 单格变更：只重建所在分块
    }

    /// <summary>放置建筑（已通过合法性校验）。official 扣钱，grown 免费。</summary>
    public BuildingInstance PlaceBuilding(BuildingDef def, Vector2I origin)
    {
        var b = new BuildingInstance
        {
            Id = NextBuildingId++,
            Def = def,
            Origin = origin,
            BuiltYear = CurYear,
            BuiltMonth = CurMonth,
            Specialty = DefaultSpecialty(def),
        };
        Buildings[b.Id] = b;

        // 自动整平垫基：占地顶点压平成台面（取占地顶点平均高），建筑立面天然水平；
        // 读档重建不经此方法（高度场随档恢复），不会二次整平
        Map.Height.FlattenRect(origin, def.SizeX, def.SizeY, Map.Height.FootprintAvgH(origin, def.SizeX, def.SizeY));

        for (int x = origin.X; x < origin.X + def.SizeX; x++)
        {
            for (int y = origin.Y; y < origin.Y + def.SizeY; y++)
            {
                RemovePlantAt(new Vector2I(x, y)); // 施工砍伐
                ref var cell = ref Map.CellAt(x, y);
                cell.BuildingId = b.Id;
                if (def.Category == "official")
                    SetZone(new Vector2I(x, y), ZoneType.None); // 官方建筑覆盖坊区；grown 保留坊区便于拆后重生
            }
        }

        if (def.Category == "official")
        {
            Money -= def.Cost;
            Ledger.Add("营造建筑", -def.Cost);
        }

        // 所有建筑（含玩家放置的官营）建成后四周环一圈小路（附属小路）：该侧已临任意路则不重铺
        LayLaneRing(origin, def.SizeX, def.SizeY);

        EventBus.RaiseMapChanged();
        EventBus.RaiseStatsChanged();
        EventBus.RaiseBuildingPlaced(b); // 实时放置钩子（如王爷府建成：拨款+安置夫妻）；读档重建不经此方法故不误触
        return b;
    }

    /// <summary>沿建筑 footprint 外一圈铺设小路环（附属小路）：空地→小路，
    /// 已有任意路（主/辅/桥/小路）保留不动（不重铺也不降级）。</summary>
    private void LayLaneRing(Vector2I origin, int sx, int sy)
    {
        int w = GrowthConfig.LaneRing;
        for (int x = origin.X - w; x < origin.X + sx + w; x++)
        {
            for (int y = origin.Y - w; y < origin.Y + sy + w; y++)
            {
                // 跳过 footprint 内部，只铺四周环
                if (x >= origin.X && x < origin.X + sx && y >= origin.Y && y < origin.Y + sy)
                    continue;
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref Map.CellAt(c);
                if (!cell.HasRoad && cell.IsEmpty) // 仅空地铺小路；已有主/辅/桥/小路保留
                    LayRoadCell(c, RoadKind.Lane);
            }
        }
    }

    /// <summary>扩建后对新 footprint 重新环一圈小路（被吞掉的环在新边界外补齐）：供 ZoneGrowthSystem 调用。</summary>
    public void LayBuildingLaneRing(BuildingInstance b) => LayLaneRing(b.Origin, b.FootX, b.FootY);

    /// <summary>村民自建住宅（兼容别名）：现与 PlaceBuilding 等价（小路环已并入 PlaceBuilding）。</summary>
    public BuildingInstance PlaceGrownWithLanes(BuildingDef def, Vector2I origin) => PlaceBuilding(def, origin);

    /// <summary>工商建筑的默认专营货品：商铺/工坊各随机专营一种可加工成品（后期支持多成品）。</summary>
    private static string DefaultSpecialty(BuildingDef def) => def.Id switch
    {
        "shop" => Goods.CraftSpecialties[Random.Shared.Next(Goods.CraftSpecialties.Length)],
        "workshop" => Goods.CraftSpecialties[Random.Shared.Next(Goods.CraftSpecialties.Length)],
        _ => "",
    };

    /// <summary>就地转业：把一座 grown 建筑（如住宅升级后）换成另一种 grown 定义，占地不变、居民保留、重置专营；
    /// 供 ZoneGrowthSystem 实现「住宅升级概率变商铺/工坊」。</summary>
    public void ConvertGrown(BuildingInstance b, string defId)
    {
        if (!Defs.TryGetValue(defId, out var def) || def.Category != "grown")
            return;
        // 先固化实例占地：换定义后 footprint 不随新 Def 尺寸突变（否则 mod 改尺寸会造成标格错位）
        b.SizeX = b.FootX;
        b.SizeY = b.FootY;
        b.Def = def;
        b.Specialty = DefaultSpecialty(def);
        b.Abandoned = false;
        b.Doors = null; // 转业后临路/用途可变，门失效待重算
        EventBus.RaiseMapChanged();
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
            UnregisterRoadCell(c);
            EventBus.RaiseCellChanged(c);
        }
        else if (cell.HasRoad)
        {
            cell.HasRoad = false;
            cell.RoadKind = RoadKind.None;
            Roads.SetRoad(c, false);
            UnregisterRoadCell(c);
            EventBus.RaiseCellChanged(c);
        }
        else if (cell.BuildingId >= 0 && Buildings.TryGetValue(cell.BuildingId, out var b))
        {
            DemolishBuilding(b);
        }
        else if (cell.Zone != ZoneType.None)
        {
            SetZone(c, ZoneType.None);
            EventBus.RaiseZonesChanged();
        }
        else if (cell.HasTree)
        {
            ChopTree(c);
        }
    }

    /// <summary>把一格并入指定建筑占地（住宅扩建用）：砍除植物、收拾散落物资、清除自家小路环并登记占用。</summary>
    public void ClaimCellForBuilding(Vector2I c, int buildingId)
    {
        RemovePlantAt(c);
        // 扩地格上的地面物资堆并入建筑仓（超限也全收，不散佚），免得永久压在房底下
        if (Piles.Remove(CellIndex(c), out var pile) && Buildings.TryGetValue(buildingId, out var owner))
            foreach (var s in pile.Inv.Stacks)
                owner.StoreGoodsForce(s.GoodsId, s.Amount);
        // 并入的若是自家小路环格：先清道路再纳入占地（避免占地格残留 HasRoad）
        ref var lane = ref Map.CellAt(c);
        if (lane.HasRoad)
        {
            lane.HasRoad = false;
            lane.RoadKind = RoadKind.None;
            Roads.SetRoad(c, false);
            UnregisterRoadCell(c);
        }
        Map.CellAt(c).BuildingId = buildingId;
        if (Buildings.TryGetValue(buildingId, out var host))
            host.Doors = null; // 扩地改变占地边界，门失效待重算
        EventBus.RaiseCellChanged(c);
    }

    /// <summary>拆除建筑实例（手动拆除 / 老化坍塌共用）：清空占地；顺带清理其附属小路环——
    /// 仅移除「不再紧贴任何其它建筑」的独占小路，共享小路保留以免切断邻居通路。</summary>
    public void DemolishBuilding(BuildingInstance b)
    {
        var origin = b.Origin;
        int fx = b.FootX, fy = b.FootY;
    
        for (int x = origin.X; x < origin.X + fx; x++)
            for (int y = origin.Y; y < origin.Y + fy; y++)
                Map.CellAt(x, y).BuildingId = -1;
        Buildings.Remove(b.Id);
    
        // footprint 已清空，此时判断小路格是否仍贴着「其它」建筑
        int w = GrowthConfig.LaneRing;
        for (int x = origin.X - w; x < origin.X + fx + w; x++)
        {
            for (int y = origin.Y - w; y < origin.Y + fy + w; y++)
            {
                if (x >= origin.X && x < origin.X + fx && y >= origin.Y && y < origin.Y + fy)
                    continue;
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c) || Map.CellAt(c).RoadKind != RoadKind.Lane)
                    continue;
                if (!TouchesAnyBuilding(c))
                    RemoveLaneCell(c);
            }
        }
    
        EventBus.RaiseMapChanged();
    }

    /// <summary>某格的 8 邻域内是否存在建筑占地（判断小路是否仍被邻居依赖）。</summary>
    private bool TouchesAnyBuilding(Vector2I c)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                var n = new Vector2I(c.X + dx, c.Y + dy);
                if (MapGrid.InBounds(n) && Map.CellAt(n).BuildingId >= 0)
                    return true;
            }
        return false;
    }

    /// <summary>移除一格小路（拆房清理专用）：还原为可建设区空地，便于日后重建。</summary>
    private void RemoveLaneCell(Vector2I c)
    {
        ref var cell = ref Map.CellAt(c);
        cell.HasRoad = false;
        cell.RoadKind = RoadKind.None;
        Roads.SetRoad(c, false);
        UnregisterRoadCell(c);
        SetZone(c, ZoneType.Buildable); // 小路原铺在可建设区空地上，拆后复原
        EventBus.RaiseCellChanged(c);
    }

    // ---- 建筑的门（懒算缓存，不入存档） ----

    /// <summary>确保建筑的门已计算：Doors 为空（新建/失效/读档）时按当前占地与临路重算并缓存。</summary>
    public void EnsureDoors(BuildingInstance b)
    {
        b.Doors ??= ComputeDoors(b);
    }

    /// <summary>某格作为门外停靠点的道路等级（大门朝最高等级路）：主路 3 / 辅路 2 / 小路 1 / 桥面 1 / 非路 0。</summary>
    private int RoadRank(Vector2I c)
    {
        if (!MapGrid.InBounds(c))
            return 0;
        ref var cell = ref Map.CellAt(c);
        if (!cell.HasRoad)
            return 0;
        return cell.RoadKind switch
        {
            RoadKind.Main => 3,
            RoadKind.Side => 2,
            RoadKind.Lane => 1,
            _ => 1, // 桥面（HasRoad 但 RoadKind.None）
        };
    }

    /// <summary>切比雪夫距离（格）：门间最小间距判定用。</summary>
    private static int Chebyshev(Vector2I a, Vector2I b)
        => System.Math.Max(System.Math.Abs(a.X - b.X), System.Math.Abs(a.Y - b.Y));

    /// <summary>收集一个门候选：仅当外侧格在界内且为可通行道路（含小路/桥面）时成立。</summary>
    private void AddDoorCand(List<Door> cands, Vector2I inside, Vector2I outside)
    {
        if (!MapGrid.InBounds(outside) || !Map.CellAt(outside).HasRoad)
            return;
        cands.Add(new Door(inside, outside, false));
    }

    /// <summary>计算建筑的门（按边分组）：大门在临路等级最高的边上**居中**；
    /// 后门优先开在大门对边（屋后）偏左或偏右（按建筑 Id 奇偶定侧），屋后无路则开在侧边偏后；
    /// 仅大门一边临路时不设后门；四面无路返回空列表（村民走邻路锚点 fallback）。</summary>
    private List<Door> ComputeDoors(BuildingInstance b)
    {
        var doors = new List<Door>();
        var origin = b.Origin;
        int fx = b.FootX, fy = b.FootY;

        // 边序：0=北(y-)、1=南(y+)、2=西(x-)、3=东(x+)；对边 0↔1、2↔3（异或 1）
        var edges = new List<Door>[4];
        for (int i = 0; i < 4; i++)
            edges[i] = new List<Door>();
        for (int x = origin.X; x < origin.X + fx; x++)
        {
            AddDoorCand(edges[0], new Vector2I(x, origin.Y), new Vector2I(x, origin.Y - 1));
            AddDoorCand(edges[1], new Vector2I(x, origin.Y + fy - 1), new Vector2I(x, origin.Y + fy));
        }
        for (int y = origin.Y; y < origin.Y + fy; y++)
        {
            AddDoorCand(edges[2], new Vector2I(origin.X, y), new Vector2I(origin.X - 1, y));
            AddDoorCand(edges[3], new Vector2I(origin.X + fx - 1, y), new Vector2I(origin.X + fx, y));
        }

        // 沿边参数位置选门：在指定边上取最靠近比例 t（0=低端、0.5=居中、1=高端）的候选
        Door PickAt(int edge, float t)
        {
            bool horizontal = edge <= 1; // 北/南边沿 X，西/东边沿 Y
            float target = horizontal ? origin.X + t * (fx - 1) : origin.Y + t * (fy - 1);
            Door best = edges[edge][0];
            float bestDiff = float.MaxValue;
            foreach (var d in edges[edge])
            {
                float diff = System.Math.Abs((horizontal ? d.Inside.X : d.Inside.Y) - target);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = d;
                }
            }
            return best;
        }

        // 大门边：临路等级最高者（并列取候选更多的边，出入更从容）
        int mainEdge = -1, mainRank = 0;
        for (int i = 0; i < 4; i++)
        {
            if (edges[i].Count == 0)
                continue;
            int rank = 0;
            foreach (var d in edges[i])
                rank = System.Math.Max(rank, RoadRank(d.Outside));
            if (mainEdge < 0 || rank > mainRank
                || (rank == mainRank && edges[i].Count > edges[mainEdge].Count))
            {
                mainEdge = i;
                mainRank = rank;
            }
        }
        if (mainEdge < 0)
            return doors; // 完全被围/无临路：无门

        // 大门：主路边居中
        var main = PickAt(mainEdge, 0.5f);
        main.IsMain = true;
        doors.Add(main);

        // 后门位次序列：屋后偏侧 → 两侧边偏后 → 屋后另一侧 → 居中兜底；
        // 偏左/偏右按建筑 Id 奇偶定（同城房屋左右错落，观感不呆板）
        int opposite = mainEdge ^ 1;
        int sideA = mainEdge <= 1 ? 2 : 0, sideB = sideA + 1;
        float sideT = (b.Id & 1) == 0 ? 0.25f : 0.75f;
        // 侧边“偏后”：靠大门对边那一端（大门在低端边则后方是高端）
        float backT = mainEdge % 2 == 0 ? 0.8f : 0.2f;
        var slots = new (int Edge, float T)[]
        {
            (opposite, sideT),
            (sideA, backT),
            (sideB, backT),
            (opposite, 1f - sideT),
            (opposite, 0.5f),
            (sideA, 0.5f),
            (sideB, 0.5f),
        };

        // 后门数随占地面积增长；仅大门边临路时 slot 全取不出，自然无后门
        int backDoors = System.Math.Max(1, fx * fy / GrowthConfig.CellsPerBackDoor);
        int gap = GrowthConfig.MinDoorGap;
        while (doors.Count < 1 + backDoors && gap >= 0)
        {
            int before = doors.Count;
            foreach (var (edge, t) in slots)
            {
                if (doors.Count >= 1 + backDoors)
                    break;
                if (edges[edge].Count == 0)
                    continue;
                var cand = PickAt(edge, t);
                bool ok = true;
                foreach (var d in doors)
                    if (cand.Inside == d.Inside || Chebyshev(cand.Inside, d.Inside) < gap)
                    {
                        ok = false; // 同格已开门或间距不够
                        break;
                    }
                if (ok)
                    doors.Add(cand);
            }
            if (doors.Count == before)
                gap--; // 本轮一个没选上：放宽间距再试，仍无候选则退出
        }
        return doors;
    }

    // ---- 植物 / 动物 ----

    /// <summary>种植树木实体（growthMonths 为初始月龄，isFruit 为果树），格子不可用时返回 null。</summary>
    public PlantObj AddPlant(Vector2I c, int growthMonths, bool isFruit = false)
    {
        ref var cell = ref Map.CellAt(c);
        if (!cell.IsEmpty || cell.HasTree)
            return null;
        var p = new PlantObj { Id = NextPlantId++, X = c.X, Y = c.Y, GrowthMonths = growthMonths, IsFruitTree = isFruit };
        p.Hp = p.MaxHp; // 新植满血（上限随树龄而定）
        Plants[CellIndex(c)] = p;
        cell.HasTree = true;
        return p;
    }

    /// <summary>手动种树（绘制树木工具）：直接种下成树。</summary>
    public void PlaceTree(Vector2I c)
    {
        if (AddPlant(c, PlantObj.MatureMonths) != null)
            EventBus.RaiseCellChanged(c);
    }

    /// <summary>对格上树木施加一斧砍伐伤害：返回实际扣除的血量（得柴按此折算，血量对应木材产量）；
    /// felled 输出是否砍倒（血尽即移除消失），并重置恢复计时。</summary>
    public float DamageTree(Vector2I c, float damage, out bool felled)
    {
        felled = false;
        if (!MapGrid.InBounds(c) || !Plants.TryGetValue(CellIndex(c), out var p))
            return 0f;
        p.IdleDays = 0; // 被砍过：恢复延迟重新计时
        float dealt = Math.Min(p.Hp, damage); // 残血不足一斧时只结算实际砍掉的部分
        p.Hp -= dealt;
        if (p.Hp <= 0f)
            felled = ChopTree(c);
        return dealt;
    }

    /// <summary>砍伐树木（伐木血尽/拆除/施工一击砍倒），返回是否砍到。</summary>
    public bool ChopTree(Vector2I c)
    {
        if (!MapGrid.InBounds(c))
            return false;
        ref var cell = ref Map.CellAt(c);
        if (!cell.HasTree)
            return false;
        cell.HasTree = false;
        Plants.Remove(CellIndex(c));
        EventBus.RaiseCellChanged(c); // 单格变更：分块重建 + 树层刷新
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

    /// <summary>捕获动物（打猎）：猎物在倒地处化为野味堆（一担），等待猎人拾取；返回是否成功。</summary>
    public bool HarvestAnimal(int id)
    {
        if (!Animals.Remove(id, out var prey))
            return false;
        DropOnGround(new Vector2I(prey.X, prey.Y), Goods.Game, Goods.LoadUnits);
        EventBus.RaiseWildlifeChanged();
        return true;
    }

    // ---- 地面物资堆 ----

    /// <summary>货品落地成堆（收获/猎杀/落果）：同格并堆，受堆容量限制；返回实际落地份数（装不下的烂掉）。</summary>
    public double DropOnGround(Vector2I c, string goodsId, double amount)
    {
        if (!MapGrid.InBounds(c) || amount <= 0)
            return 0;
        int key = CellIndex(c);
        if (!Piles.TryGetValue(key, out var pile))
        {
            pile = new ItemPileObj { Id = NextPileId++, X = c.X, Y = c.Y };
            Piles[key] = pile;
        }
        double dropped = pile.Inv.Store(goodsId, amount);
        if (pile.Inv.IsEmpty)
            Piles.Remove(key); // 一份没落下（满堆）：不留空堆
        return dropped;
    }

    /// <summary>可落堆的净地：无路无水无建筑（树下可堆，落果本就堆在树格）。</summary>
    private bool IsPileableCell(Vector2I c)
    {
        ref var cell = ref Map.CellAt(c);
        return !cell.HasRoad && !cell.HasWater && cell.BuildingId < 0;
    }

    /// <summary>就近落堆：卸货人常站在路边/路上，货不能落在路面/水面/房底——
    /// 站位不可堆时向外逐圈（半径≤6）找首个净地格再落，实在无净地才原地兑底。</summary>
    public double DropNearby(Vector2I c, string goodsId, double amount)
    {
        if (!MapGrid.InBounds(c) || amount <= 0)
            return 0;
        if (IsPileableCell(c))
            return DropOnGround(c, goodsId, amount);
        for (int r = 1; r <= 6; r++)
        {
            for (int ox = -r; ox <= r; ox++)
            {
                for (int oy = -r; oy <= r; oy++)
                {
                    if (Math.Max(Math.Abs(ox), Math.Abs(oy)) != r)
                        continue; // 只扫本圈环上的格
                    var n = c + new Vector2I(ox, oy);
                    if (MapGrid.InBounds(n) && IsPileableCell(n))
                        return DropOnGround(n, goodsId, amount);
                }
            }
        }
        return DropOnGround(c, goodsId, amount);
    }

    /// <summary>从格上物资堆拾货入目标库存（背包/后期载具），能装多少拾多少；拾空即删堆。</summary>
    public void PickupPile(Vector2I c, Inventory into)
    {
        if (!Piles.TryGetValue(CellIndex(c), out var pile))
            return;
        // 逐堆搬入（列表快照：搬空的堆会从原库存移除）
        foreach (var s in pile.Inv.Stacks.ToArray())
        {
            double got = pile.Inv.Take(s.GoodsId, into.Free);
            if (got > 0)
                into.Store(s.GoodsId, got);
        }
        if (pile.Inv.IsEmpty)
            Piles.Remove(CellIndex(c));
    }

    /// <summary>找最近的地面物资堆（goodsId 空串表示不限货品；切比雪夫距离）。</summary>
    public ItemPileObj FindNearestPile(Vector2I from, string goodsId, int maxRadius)
    {
        ItemPileObj best = null;
        int bestDist = maxRadius + 1;
        foreach (var p in Piles.Values)
        {
            if (goodsId != "" && p.Inv.AmountOf(goodsId) <= 0)
                continue;
            int d = Math.Max(Math.Abs(p.X - from.X), Math.Abs(p.Y - from.Y));
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        return best;
    }

    /// <summary>找最近的树木格（线性扫描植物实体，免大半径环扫全图；伐木选目标用）；
    /// 石峰上的景观树不入选（人不可攀，海拔高于 ForageMaxHeight 即跳过）。</summary>
    public Vector2I? FindNearestTreeCell(Vector2I from, int maxRadius)
    {
        PlantObj best = null;
        int bestDist = maxRadius + 1;
        foreach (var p in Plants.Values)
        {
            if (Map.GroundY(new Vector2I(p.X, p.Y)) > TerrainConfig.ForageMaxHeight)
                continue; // 峰上景观树不可及
            int d = Math.Max(Math.Abs(p.X - from.X), Math.Abs(p.Y - from.Y));
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        return best != null ? new Vector2I(best.X, best.Y) : null;
    }

    /// <summary>找最近的挂果果树（至少一份可摘；普通树不挂果，字段双重过滤防误摘）；峰上树同样豁免。</summary>
    public PlantObj FindNearestFruitTree(Vector2I from, int maxRadius)
    {
        PlantObj best = null;
        int bestDist = maxRadius + 1;
        foreach (var p in Plants.Values)
        {
            if (!p.IsFruitTree || !p.Mature || p.FruitStock < 1)
                continue;
            if (Map.GroundY(new Vector2I(p.X, p.Y)) > TerrainConfig.ForageMaxHeight)
                continue; // 峰上景观树不可及
            int d = Math.Max(Math.Abs(p.X - from.X), Math.Abs(p.Y - from.Y));
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        return best;
    }

    /// <summary>找最近的河岸格（水格的邻接陆格，打水站位）：逐圈环扫，找到即停；
    /// 仅在触发打水时偶发调用，城中有井时不走此路径。</summary>
    public Vector2I? FindNearestWaterShore(Vector2I from, int maxRadius)
    {
        Vector2I[] dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        for (int r = 0; r <= maxRadius; r++)
        {
            for (int ox = -r; ox <= r; ox++)
            {
                for (int oy = -r; oy <= r; oy++)
                {
                    if (Math.Max(Math.Abs(ox), Math.Abs(oy)) != r)
                        continue; // 只扫本圈环上的格
                    var c = from + new Vector2I(ox, oy);
                    if (!MapGrid.InBounds(c) || !Map.CellAt(c).HasWater)
                        continue;
                    foreach (var d in dirs)
                    {
                        var n = c + d;
                        if (MapGrid.InBounds(n) && !Map.CellAt(n).HasWater)
                            return n;
                    }
                }
            }
        }
        return null;
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

    /// <summary>建筑在岗雇工列表（交易分账用；小规模线性扫描）。</summary>
    public List<Citizen> StaffOf(BuildingInstance b)
    {
        var list = new List<Citizen>();
        foreach (var c in Citizens.Values)
            if (c.JobKind == JobKind.Employed && c.WorkplaceId == b.Id)
                list.Add(c);
        return list;
    }

    /// <summary>买方建筑向居民付款（收购居民背来的货）：官营走官库记账；
    /// 民营由在店雇工凑钱（钱不够只付到见底），返回实付金额。</summary>
    public double PayFromBuilding(BuildingInstance b, Citizen to, double amount)
    {
        if (amount <= 0)
            return 0;
        if (b.Def.Category == "official")
        {
            Money -= amount;
            Ledger.Add("市易采买", -amount);
            to.Money += amount;
            return amount;
        }
        var staff = StaffOf(b);
        if (staff.Count == 0)
            return 0; // 无人经营付不出钱
        double paid = 0;
        double share = amount / staff.Count;
        foreach (var w in staff)
        {
            double p = Math.Min(Math.Max(0, w.Money), share);
            w.Money -= p;
            paid += p;
        }
        to.Money += paid;
        return paid;
    }

    /// <summary>卖方建筑收款（居民向建筑买货）：有雇工平分、无雇工的官营入官库记账。</summary>
    public void PayToBuilding(BuildingInstance b, double amount)
    {
        if (amount <= 0)
            return;
        var staff = StaffOf(b);
        if (staff.Count > 0)
        {
            foreach (var w in staff)
                w.Money += amount / staff.Count;
        }
        else
        {
            Money += amount;
            Ledger.Add("市易收入", amount);
        }
    }

    public int CountByDef(string defId)
    {
        int n = 0;
        foreach (var b in Buildings.Values)
            if (b.Def.Id == defId)
                n++;
        return n;
    }

    /// <summary>是否已建成王爷府（开局首建门槛：未建成前锁定其它一切营造）。</summary>
    public bool PrinceMansionBuilt => CountByDef(PrinceMansionConfig.DefId) > 0;

    // ---- 居民 / 家庭 ----

    public Citizen AddCitizen(Citizen c)
    {
        c.Id = NextCitizenId++;
        Citizens[c.Id] = c;
        EventBus.RaiseCitizenAdded(c);
        return c;
    }

    /// <summary>记一笔年龄履历（重大事件）：按当前游戏年月追加；超出上限移除最旧，防长寿居民档案膨胀。</summary>
    public void LogLifeEvent(Citizen c, string text)
    {
        c.LifeEvents.Add(new LifeEvent { Year = CurYear, Month = CurMonth, Text = text });
        if (c.LifeEvents.Count > WorldConfig.LifeEventCap)
            c.LifeEvents.RemoveAt(0);
    }

    /// <summary>发一条全城公告（迁入迁出/生死等大事，新在前）并广播公告栏刷新；
    /// kind 为类别标签（见 NewsItem，后续新事件直接加标签即可拓展）。</summary>
    public void PostNews(string kind, string text)
    {
        News.Insert(0, new NewsItem { Year = CurYear, Month = CurMonth, Kind = kind, Text = text });
        if (News.Count > WorldConfig.NewsCap)
            News.RemoveAt(News.Count - 1); // 满则淘汰最旧一条
        EventBus.RaiseNewsPosted();
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
        {
            spouse.SpouseId = -1;
            LogLifeEvent(spouse, $"配偶 {c.Name} 亡故或迁离"); // 在世一方留丧偶记录（Name 已含姓）
        }
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

    /// <summary>住处剩余床位（按当前等级容量计）。occupancy 为预先汇总的 homeId-&gt;人数字典。</summary>
    public int HouseVacancy(BuildingInstance house, Dictionary<int, int> occupancy)
    {
        int used = occupancy.GetValueOrDefault(house.Id);
        return house.HousingCapacity - used;
    }
    
    /// <summary>户主（屋主）推导：住户中最年长成年男 → 最年长成年女 → 最年长者；
    /// 不入存档，户主亡故后下次查询自动换代；无人居住返回 null。</summary>
    public Citizen HouseholdHead(int homeId)
    {
        Citizen head = null;
        foreach (var c in Citizens.Values)
        {
            if (c.HomeId != homeId)
                continue;
            if (head == null)
            {
                head = c;
                continue;
            }
            // 成年优先；同为成年时男优先；再同比年龄
            bool cBetter =
                (!head.IsChild, head.Gender == Gender.Male, head.AgeMonths)
                    .CompareTo((!c.IsChild, c.Gender == Gender.Male, c.AgeMonths)) < 0;
            if (cBetter)
                head = c;
        }
        return head;
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

    /// <summary>住在指定民居的人数（家庭储备目标按人口计；居民数百量级线性扫尚廉）。</summary>
    public int HomeResidents(int homeId)
    {
        int n = 0;
        foreach (var c in Citizens.Values)
            if (c.HomeId == homeId)
                n++;
        return n;
    }

    /// <summary>建筑占用人数 = 本楼居民 + 外来雇工（HomeId≠b 且 WorkplaceId==b），同一人只占一格：
    /// 商铺/工坊居住与打工共用同一格池，招工/寄居均以此判空格（&lt; HousingCapacity 才接纳）。</summary>
    public int BuildingOccupancy(BuildingInstance b)
    {
        int n = 0;
        foreach (var c in Citizens.Values)
        {
            if (c.HomeId == b.Id)
                n++; // 本楼居民
            else if (c.JobKind == JobKind.Employed && c.WorkplaceId == b.Id)
                n++; // 外来雇工（住在别处，在此占一个工作格）
        }
        return n;
    }
}
