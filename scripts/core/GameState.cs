using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>地图四向（ batches：道路通边 → 外城来人）。每条边对应一座邻城，
/// 后期可据 Specialties 拓展互市/特产/任务。</summary>
public enum MapDir { North, East, South, West }

/// <summary>邻城信息（道路延伸到对应边即解锁）：供外来访客系统选出生点、取主营货类，
/// 并记录连通状态供后期拓展。</summary>
public sealed class NeighborCity
{
    public string Name = "";
    public bool Connected;
    /// <summary>该方向已连通的道路边格（出生/离场锚点）。</summary>
    public readonly List<Vector2I> EdgeCells = new();
    /// <summary>该城主营货类（Goods.CategoryOf 取值）；既作 NPC 带货偏向，也留待后期拓展。</summary>
    public List<int> Specialties = new();
}

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

    /// <summary>道路等级（批次八十：高级可覆盖低级）：主路 2 &gt; 辅路 1 &gt; 小路 0；None/桥面视作 0。</summary>
    public static int RoadRank(RoadKind kind) => kind switch
    {
        RoadKind.Main => 2,
        RoadKind.Side => 1,
        _ => 0,
    };

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

    public long Money = WorldConfig.StartMoney;
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

    /// <summary>中央需求账本（城市级供需快照，每日由 DemandSystem 重算；派生数据，不随存档保存）。</summary>
    public DemandLedger Demand { get; } = new();

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
        RegisterEdgeCell(c);
    }

    public void UnregisterRoadCell(Vector2I c)
    {
        UnregisterEdgeCell(c);
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

    // ---- 四向邻城：道路边格增量维护（O(1)，免全图扫描）----

    /// <summary>四向邻城（索引对应 MapDir 枚举序：0 北 / 1 东 / 2 南 / 3 西）。</summary>
    public readonly NeighborCity[] Neighbors =
    {
        new() { Name = "北邙镇", Specialties = { 0 } },  // 北：粮作（食物类）
        new() { Name = "东津渡", Specialties = { 2 } },  // 东：木作
        new() { Name = "南湖庄", Specialties = { 0, 5 } },// 南：果品+药材
        new() { Name = "西山市", Specialties = { 3 } },  // 西：金工
    };

    /// <summary>是否任一邻城已连通（道路延伸到地图边缘）。</summary>
    public bool AnyNeighborConnected
    {
        get { foreach (var n in Neighbors) if (n.Connected) return true; return false; }
    }

    /// <summary>取某格所属的边向（x/y 落在最外圈）；非边格返回 null。</summary>
    private static MapDir? EdgeDirOf(Vector2I c)
    {
        int s = MapGrid.Size - 1;
        if (c.X == 0) return MapDir.West;
        if (c.X == s) return MapDir.East;
        if (c.Y == 0) return MapDir.North;
        if (c.Y == s) return MapDir.South;
        return null;
    }

    private void RegisterEdgeCell(Vector2I c)
    {
        var dir = EdgeDirOf(c);
        if (dir == null)
            return;
        var nb = Neighbors[(int)dir.Value];
        bool wasConnected = nb.Connected;
        nb.EdgeCells.Add(c);
        if (!wasConnected)
        {
            nb.Connected = true;
            EventBus.RaiseRoadReachedEdge(dir.Value);
        }
    }

    private void UnregisterEdgeCell(Vector2I c)
    {
        var dir = EdgeDirOf(c);
        if (dir == null)
            return;
        var nb = Neighbors[(int)dir.Value];
        nb.EdgeCells.Remove(c);
        if (nb.EdgeCells.Count == 0 && nb.Connected)
            nb.Connected = false;
    }

    /// <summary>按建筑 id 取城内全部该类建筑实例（外来访客选交易场所用）。</summary>
    public IEnumerable<BuildingInstance> BuildingsOfType(string id)
    {
        foreach (var b in Buildings.Values)
            if (b.Def.Id == id)
                yield return b;
    }

    /// <summary>全部「可建设区」格：坊区生长只在此集内挑建房点，免每日全图扫描。</summary>
    public HashSet<Vector2I> BuildableCells { get; } = new();

    /// <summary>全部「耕种区」格：农场系统在此集内开垦农田（连通块分组），免每日全图扫描。</summary>
    public HashSet<Vector2I> FarmlandCells { get; } = new();

    /// <summary>统一的坊区写入口：同步维护 BuildableCells/FarmlandCells 索引（绕过此方法直写 cell.Zone 会使索引失真）。</summary>
    public void SetZone(Vector2I c, ZoneType zone)
    {
        ref var cell = ref Map.CellAt(c);
        if (cell.Zone == zone)
            return;
        if (cell.Zone == ZoneType.Buildable)
            BuildableCells.Remove(c);
        else if (cell.Zone == ZoneType.Farmland)
            FarmlandCells.Remove(c);
        cell.Zone = zone;
        if (zone == ZoneType.Buildable)
            BuildableCells.Add(c);
        else if (zone == ZoneType.Farmland)
            FarmlandCells.Add(c);
    }

    public GameState(Dictionary<string, BuildingDef> defs)
    {
        Defs = defs;
    }

    /// <summary>以 center 为中心的方形道路画笔（w×w，宽度随道路种类）：陆地空格铺路，
    /// 遇水面格自动架一座与路同宽的小桥（辅路w2、主路w4）——“和道路同步”，拖拉一次画成不断档；
    /// 岸上路段按道路单价、跨水桥段按桥梁单价，各按等效延米（新格数÷宽）计费（重叠盖戳不多扣）；
    /// 已占用格（路/已有桥/建筑）跳过；钱不够时不铺。返回是否铺下了新格。
    /// 批次八十七：先扫描统计总价再一次性资金校验（旧版只校验单格成本，一印多格时总价可超官库，
    /// 默认模式下官库被扣成负数——与 GameSettings 注释“无限钱才允许扣至负数”不符）。</summary>
    public bool PlaceRoadStamp(Vector2I center, RoadKind kind)
    {
        // 宽度为偶数，偏移范围 -(w-1)/2..w/2 两轴一致（宽 4 时 -1..2）
        int w = RoadWidthOf(kind);

        // 第一遍：扫描统计可铺/可升级/可架桥格（不落地）
        var pending = new List<(Vector2I c, bool bridge, bool upgrade)>();
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
                    pending.Add((c, true, false));
                }
                else if (cell.IsEmpty)
                {
                    pending.Add((c, false, false));
                }
                else if (cell.HasRoad && !cell.HasBridge && RoadRank(kind) > RoadRank(cell.RoadKind))
                {
                    // 高级覆盖低级（批次八十）：主路压辅路/小路、辅路压小路；同级/低级不覆盖，桥面不升级
                    pending.Add((c, false, true));
                }
                // 其它（已有同级/高级路、建筑）跳过
            }
        }
        if (pending.Count == 0)
            return false;

        int newRoadCells = 0, newBridgeCells = 0;
        foreach (var (_, bridge, _) in pending)
        {
            if (bridge) newBridgeCells++;
            else newRoadCells++;
        }
        long roadCharge = (long)RoadCostOf(kind) * newRoadCells / w; // 新格数/宽 = 等效延米，斜拖/重叠不多扣
        long bridgeCharge = (long)BridgeCost * newBridgeCells / w;    // 跨水段按桥梁单价
        if (!GameSettings.InfiniteMoney && Money < roadCharge + bridgeCharge)
            return false;

        // 第二遍：统一落地（先校验后动地图，钱不够时一张格都不铺）
        foreach (var (c, bridge, upgrade) in pending)
        {
            if (bridge) LayBridgeCell(c);
            else if (upgrade) UpgradeRoadCell(c, kind);
            else LayRoadCell(c, kind);
        }
        if (newRoadCells > 0)
        {
            long paid = PayBuildWages(roadCharge); // 批次七十九：先发放、按实扣款（无人领则钱留官库）
            Money -= paid;
            Ledger.Add("营造道路", -paid);
        }
        if (newBridgeCells > 0)
        {
            long paid = PayBuildWages(bridgeCharge);
            Money -= paid;
            Ledger.Add("营造桥梁", -paid);
        }
        EventBus.RaiseStatsChanged();
        return true;
    }

    /// <summary>以 center 为中心的方形桥梁画笔（4×4 米）：只在无桥的水面格架设，
    /// 按实际新架格数折算延米计价（同道路，重叠盖戳不多扣）。
    /// 批次八十七：先统计总价再一次性资金校验（旧版只校验单格成本，一印多格时官库可被扣成负数）。</summary>
    public bool PlaceBridgeStamp(Vector2I center)
    {
        var pending = new List<Vector2I>();
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
                pending.Add(c);
            }
        }
        if (pending.Count == 0)
            return false;

        long charge = (long)BridgeCost * pending.Count / BridgeWidth;
        if (!GameSettings.InfiniteMoney && Money < charge)
            return false;

        foreach (var c in pending)
            LayBridgeCell(c);
        long paid = PayBuildWages(charge); // 批次七十九：同道路营造，按实扣款
        Money -= paid;
        Ledger.Add("营造桥梁", -paid);
        EventBus.RaiseStatsChanged();
        return true;
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

    /// <summary>升级已有路面（高级覆盖低级，批次八十）：只换道路种类与寻路权重，
    /// 不重登记索引、不砍树（有路格无树）、不清坊区（铺路时已清）。
    /// 批次八十七：覆盖时解除有主小路归属（旧版 LaneOwnerId 残留——屋主拆迁时也不再清除，成永久死数据）。</summary>
    private void UpgradeRoadCell(Vector2I c, RoadKind kind)
    {
        ref var cell = ref Map.CellAt(c);
        cell.RoadKind = kind;
        cell.LaneOwnerId = -1;
        Roads.SetRoad(c, true, kind); // 同步寻路权重：主路代价低，居民偏好走主路
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

    /// <summary>放置建筑（已通过合法性校验）。official 扣钱，grown 免费；
    /// sizeX/sizeY 可覆写定义占地（村民初始大宅用，默认取定义值）。</summary>
    public BuildingInstance PlaceBuilding(BuildingDef def, Vector2I origin, int sizeX = -1, int sizeY = -1)
    {
        int sx = sizeX > 0 ? sizeX : def.SizeX;
        int sy = sizeY > 0 ? sizeY : def.SizeY;
        var b = new BuildingInstance
        {
            Id = NextBuildingId++,
            Def = def,
            Origin = origin,
            SizeX = sx,
            SizeY = sy,
            BuiltYear = CurYear,
            BuiltMonth = CurMonth,
            Specialty = DefaultSpecialty(def),
        };
        Buildings[b.Id] = b;

        // 自动整平垫基：占地顶点压平成台面（取占地顶点平均高），建筑立面天然水平；
        // 读档重建不经此方法（高度场随档恢复），不会二次整平
        Map.Height.FlattenRect(origin, sx, sy, Map.Height.FootprintAvgH(origin, sx, sy));

        for (int x = origin.X; x < origin.X + sx; x++)
        {
            for (int y = origin.Y; y < origin.Y + sy; y++)
            {
                RemovePlantAt(new Vector2I(x, y)); // 施工砍伐
                ref var cell = ref Map.CellAt(x, y);
                if (cell.HasRoad) // 占小路建房（批次六十六：村民可直接在小路上盖房）：清路并入占地
                {
                    cell.HasRoad = false;
                    cell.RoadKind = RoadKind.None;
                    Roads.SetRoad(new Vector2I(x, y), false);
                    UnregisterRoadCell(new Vector2I(x, y));
                }
                cell.LaneOwnerId = -1; // 占用的有主小路已在选址时补偿转无主，此处兜底解除
                cell.BuildingId = b.Id;
                if (def.Category is "official" or "court")
                    SetZone(new Vector2I(x, y), ZoneType.None); // 官方/朝廷建筑覆盖坊区；grown 保留坊区便于拆后重生
            }
        }

        if (def.Category == "official")
        {
            long paid = PayBuildWages(def.Cost); // 批次七十九：先发放、按实扣款（无人领则钱留官库）
            Money -= paid;
            Ledger.Add("营造建筑", -paid);
        }
        else if (def.Category == "court")
        {
            // 朝廷机构朝廷拨款营造（批次七十七）：官库不扣钱，营造工钱由朝廷凭空生成发给无业者
            long courtPaid = PayBuildWages(def.Cost);
            if (courtPaid > 0)
                Ledger.Add("朝廷营造", courtPaid); // 朝廷拨款流水，账本可查
        }

        // 所有建筑（含玩家放置的官营）建成后四周环一圈小路（附属小路）：该侧已临任意路则不重铺
        LayLaneRing(origin, sx, sy, b.Id);

        // 局部重建：垫基整平只动占地矩形内顶点，只标脏覆盖分块（小路环已逐格 CellChanged）；
        // 旧版这里全图 MapChanged，村民 4x 下频繁建房时百万格重建是间歇卡顿主源
        EventBus.RaiseRectChanged(origin, new Vector2I(sx, sy));
        EventBus.RaiseStatsChanged();
        EventBus.RaiseBuildingPlaced(b); // 实时放置钩子（如王爷府建成：拨款+安置夫妻）；读档重建不经此方法故不误触
        return b;
    }

    /// <summary>沿建筑 footprint 外一圈铺设小路环（附属小路）：空地→小路并登记归属，
    /// 已有任意路（主/辅/桥/小路）保留不动（不重铺也不降级、不夺归属）。</summary>
    private void LayLaneRing(Vector2I origin, int sx, int sy, int ownerId)
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
                if (!cell.HasRoad && cell.IsEmpty) // 仅空地铺小路；已有主/辅/桥/小路保留（含其归属）
                {
                    LayRoadCell(c, RoadKind.Lane);
                    cell.LaneOwnerId = ownerId; // 小路独立个体：登记归属本宅
                }
            }
        }
    }

    /// <summary>扩建后对新 footprint 重新环一圈小路（被吞掉的环在新边界外补齐）：供 ZoneGrowthSystem 调用。</summary>
    public void LayBuildingLaneRing(BuildingInstance b) => LayLaneRing(b.Origin, b.FootX, b.FootY, b.Id);

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
    /// specialty 非空按指定货品专营（创业选品），空串随机（DefaultSpecialty）；
    /// 供 ZoneGrowthSystem 实现「住宅升级概率变商铺/工坊」。</summary>
    public void ConvertGrown(BuildingInstance b, string defId, string specialty = "")
    {
        if (!Defs.TryGetValue(defId, out var def) || def.Category != "grown")
            return;
        // 先固化实例占地：换定义后 footprint 不随新 Def 尺寸突变（否则 mod 改尺寸会造成标格错位）
        b.SizeX = b.FootX;
        b.SizeY = b.FootY;
        b.Def = def;
        b.Specialty = specialty != "" ? specialty : DefaultSpecialty(def);
        b.Abandoned = false;
        b.Doors = null; // 转业后临路/用途可变，门失效待重算
        EventBus.RaiseBuildingsChanged(); // 只换定义/颜色不动地表：仅重建建筑层
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
            cell.LaneOwnerId = -1; // 批次八十七：拆桥清路时同步解归属（旧版残留死数据）
            Roads.SetRoad(c, false);
            UnregisterRoadCell(c);
            EventBus.RaiseCellChanged(c);
        }
        else if (cell.HasRoad)
        {
            cell.HasRoad = false;
            cell.RoadKind = RoadKind.None;
            cell.LaneOwnerId = -1; // 批次八十七：同拆桥，拆路同步解归属
            Roads.SetRoad(c, false);
            UnregisterRoadCell(c);
            EventBus.RaiseCellChanged(c);
        }
        else if (cell.BuildingId >= 0 && Buildings.TryGetValue(cell.BuildingId, out var b))
        {
            // 王爷府为开局地标（批次八十）：不设健康度、不进建造栏，拆了无法重建，禁止拆除
            if (b.Def.Id == PrinceMansionConfig.DefId)
                return;
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

    /// <summary>把一格并入指定建筑占地（住宅扩建用）：砍除植物、收拾散落物资、清除占用格上的道路并登记占用。</summary>
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
        lane.LaneOwnerId = -1; // 并入的小路不再归属任何建筑
        Map.CellAt(c).BuildingId = buildingId;
        if (Buildings.TryGetValue(buildingId, out var host))
            host.Doors = null; // 扩地改变占地边界，门失效待重算
        EventBus.RaiseCellChanged(c);
    }

    /// <summary>拆除建筑实例（手动拆除 / 老化坍塌共用）：清空占地；
    /// 附属小路独立存续（批次六十六）：不再随房清除，只把本宅名下的小路格转无主——
    /// 新村民可免费贴路建房或直接占路重建，无需再付半价。</summary>
    public void DemolishBuilding(BuildingInstance b)
    {
        var origin = b.Origin;
        int fx = b.FootX, fy = b.FootY;
    
        for (int x = origin.X; x < origin.X + fx; x++)
            for (int y = origin.Y; y < origin.Y + fy; y++)
                Map.CellAt(x, y).BuildingId = -1;
        Buildings.Remove(b.Id);
    
        // footprint 已清空，把本宅名下的小路格转为无主（小路本体保留）
        int w = GrowthConfig.LaneRing;
        for (int x = origin.X - w; x < origin.X + fx + w; x++)
        {
            for (int y = origin.Y - w; y < origin.Y + fy + w; y++)
            {
                if (x >= origin.X && x < origin.X + fx && y >= origin.Y && y < origin.Y + fy)
                    continue;
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref Map.CellAt(c);
                if (cell.RoadKind == RoadKind.Lane && cell.LaneOwnerId == b.Id)
                    cell.LaneOwnerId = -1; // 屋主已去，小路留作无主道路
            }
        }
    
        // 局部重建：占地矩形（含外圈小路已逐格 CellChanged）覆盖分块重建即可，免全图重建
        EventBus.RaiseRectChanged(origin, new Vector2I(fx, fy));
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

    /// <summary>可落堆的净地：无路无水无建筑（树下可堆，落果本就堆在树格；与 Cell.IsEmpty 同口径）。</summary>
    private bool IsPileableCell(Vector2I c)
    {
        ref var cell = ref Map.CellAt(c);
        return cell.IsEmpty;
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
    /// 高山上的景观树不入选（海拔高于 ForageMaxHeight 即跳过）。</summary>
    public Vector2I? FindNearestTreeCell(Vector2I from, int maxRadius)
    {
        PlantObj best = null;
        int bestDist = maxRadius + 1;
        foreach (var p in Plants.Values)
        {
            if (Map.GroundY(new Vector2I(p.X, p.Y)) > TerrainConfig.ForageMaxHeight)
                continue; // 高山景观树不可及
            int d = Math.Max(Math.Abs(p.X - from.X), Math.Abs(p.Y - from.Y));
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        return best != null ? new Vector2I(best.X, best.Y) : null;
    }

    /// <summary>找最近的挂果果树（至少一份可摘；普通树不挂果，字段双重过滤防误摘）；高山树同样豁免。</summary>
    public PlantObj FindNearestFruitTree(Vector2I from, int maxRadius)
    {
        PlantObj best = null;
        int bestDist = maxRadius + 1;
        foreach (var p in Plants.Values)
        {
            if (!p.IsFruitTree || !p.Mature || p.FruitStock < 1)
                continue;
            if (Map.GroundY(new Vector2I(p.X, p.Y)) > TerrainConfig.ForageMaxHeight)
                continue; // 高山景观树不可及
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

    // ---- 家庭资金（批次六十八：资金挂家庭公产，个人 Money 停止流通）----

    /// <summary>给居民所属家庭入账（文）：家庭不存在（理论不会）则折入官库。</summary>
    public void PayToFamily(Citizen to, long amount)
    {
        if (amount <= 0)
            return;
        if (Families.TryGetValue(to.FamilyId, out var fam))
            fam.SharedAssets += amount;
        else
            Money += amount;
    }

    /// <summary>营造工钱（批次七十六）：建造费/建房提成发给当日无业成年人（均分）——
    /// 除朝廷直属机构外所有钱都在玩家↔村民间循环，建造费不是凭空消失而是发成工资；
    /// 批次七十九：返回实际发出额——先发放后按实扣款，无人可领时钱留在官库，
    /// 金额小于无业者数时按序每人 1 文发完（小额营造/料钱也全数发出），杜绝「扣了款却发不出」的凭空消失。
    /// 朝廷采买不走此路（凭空生成，见 CitizenAgent）。</summary>
    public long PayBuildWages(long amount)
    {
        if (amount <= 0)
            return 0;
        int count = 0;
        foreach (var c in Citizens.Values)
            if (!c.IsChild && c.JobKind == JobKind.None)
                count++;
        if (count <= 0)
            return 0; // 全城无无业者：无人领工钱，钱留在官库（调用方按实扣款）
        long share = amount / count;
        if (share <= 0)
        {
            // 金额小于无业者数：按序每人发 1 文，发完即止
            long left = amount;
            foreach (var c in Citizens.Values)
                if (!c.IsChild && c.JobKind == JobKind.None && left > 0)
                {
                    PayToFamily(c, 1);
                    left--;
                }
            return amount - left;
        }
        long paid = 0;
        foreach (var c in Citizens.Values)
            if (!c.IsChild && c.JobKind == JobKind.None)
            {
                PayToFamily(c, share);
                paid += share;
            }
        return paid; // 除不尽余数留在官库（按实扣款，不凭空消失）
    }

    /// <summary>从居民所属家庭公产扣款（文，不为负）：家庭不存在则不扣（理论不会）。</summary>
    public void TakeFromFamily(Citizen c, long amount)
    {
        if (amount <= 0)
            return;
        if (Families.TryGetValue(c.FamilyId, out var fam))
            fam.SharedAssets = Math.Max(0, fam.SharedAssets - amount);
    }

    /// <summary>土地税征收（批次七十二）：从建筑住户/店主家庭公产实扣入官库，家庭无钱则免收；
    /// 返回实收金额（文，账本按实记账——批次八十七：旧版返回 bool 且记账记全额，公产不足时账实不符）。
    /// 民营店坊按店主家庭（OwnerCitizenId）征收，民居按户主家庭（HouseholdHead）。</summary>
    public long TakeLandTax(BuildingInstance b, long amount)
    {
        Citizen payer = null;
        if (b.OwnerCitizenId >= 0 && Citizens.TryGetValue(b.OwnerCitizenId, out var owner))
            payer = owner;
        else
            payer = HouseholdHead(b.Id);
        if (payer == null || !Families.TryGetValue(payer.FamilyId, out var fam) || fam.SharedAssets <= 0)
            return 0;
        long paid = Math.Min(amount, fam.SharedAssets);
        fam.SharedAssets -= paid;
        Money += paid;
        return paid;
    }

    /// <summary>居民所属家庭公产余额（文，家庭不存在返回 0）。</summary>
    public long FamilyMoney(Citizen c) =>
        Families.TryGetValue(c.FamilyId, out var fam) ? Math.Max(0, fam.SharedAssets) : 0;

    /// <summary>买方建筑向居民付款（收购居民背来的货）：官营走官库记账；
    /// 民营由在店雇工家庭凑钱（钱不够只付到见底），返回实付金额（文）。</summary>
    public long PayFromBuilding(BuildingInstance b, Citizen to, long amount)
    {
        if (amount <= 0)
            return 0;
        if (b.Def.Category == "official")
        {
            Money -= amount;
            Ledger.Add("市易采买", -amount);
            PayToFamily(to, amount);
            return amount;
        }
        var staff = StaffOf(b);
        if (staff.Count == 0)
            return 0; // 无人经营付不出钱
        long paid = 0;
        long share = amount / staff.Count;
        for (int i = 0; i < staff.Count; i++)
        {
            // 批次八十七：末位员工承担除不尽余数（旧版只付 amount/人数，余数凭空消失）
            long p = Math.Min(FamilyMoney(staff[i]), i == staff.Count - 1 ? amount - paid : share);
            TakeFromFamily(staff[i], p);
            paid += p;
        }
        PayToFamily(to, paid);
        return paid;
    }

    /// <summary>卖方建筑收款（居民向建筑买货）：官营一律入官库记账（批次七十二：此前有员工时钱全分给员工家庭，
    /// 官库只收无员工官营，官营设施只出不进——俸禄/收购流出，售货款不回官库，是官库持续失血主因）；
    /// 民营按有雇工平分、无雇工折入官库。朝廷衙门不售货（只进不出），不参与本方法。</summary>
    public void PayToBuilding(BuildingInstance b, long amount)
    {
        if (amount <= 0)
            return;
        if (b.Def.Category == "official")
        {
            Money += amount;
            Ledger.Add("市易收入", amount);
            return;
        }
        var staff = StaffOf(b);
        if (staff.Count > 0)
        {
            long share = amount / staff.Count;
            // 批次八十七：末位员工收除不尽余数（旧版除不尽余数无人接收、凭空消失）
            for (int i = 0; i < staff.Count; i++)
                PayToFamily(staff[i], i == staff.Count - 1 ? amount - share * (staff.Count - 1) : share);
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
            {
                // 批次七十八：绝户（全家亡故）或最后一人迁出时，公产折入官库——
                // 旧版随家庭删除凭空消失，是总资产持续流失的黑洞之一
                if (family.SharedAssets > 0)
                {
                    Money += family.SharedAssets;
                    Ledger.Add("绝户充公", family.SharedAssets);
                }
                Families.Remove(family.Id);
            }
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
