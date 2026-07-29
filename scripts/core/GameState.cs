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

    /// <summary>三种道路造价：小路 6 / 辅路 10 / 主路 18。</summary>
    public static int RoadCostOf(RoadKind kind) => kind switch
    {
        RoadKind.Main => 18,
        RoadKind.Side => 10,
        _ => 6,
    };

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

    public double Money = 5000;
    public double Food = 500;

    /// <summary>官库收支账本（本月/上月分类流水，随存档保存）。</summary>
    public Ledger Ledger = new();

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

    public void PlaceRoad(Vector2I c, RoadKind kind = RoadKind.Side)
    {
        ref var cell = ref Map.CellAt(c);
        RemovePlantAt(c); // 施工砍伐
        cell.HasRoad = true;
        cell.RoadKind = kind;
        SetZone(c, ZoneType.None);
        Roads.SetRoad(c, true);
        RegisterRoadCell(c);
        int cost = RoadCostOf(kind);
        Money -= cost;
        Ledger.Add("营造道路", -cost);
        EventBus.RaiseCellChanged(c); // 单格变更：只重建所在分块
        EventBus.RaiseStatsChanged();
    }

    /// <summary>在水面架桥：桥面等效道路接入路网。</summary>
    public void PlaceBridge(Vector2I c)
    {
        ref var cell = ref Map.CellAt(c);
        cell.HasBridge = true;
        cell.HasRoad = true;
        Roads.SetRoad(c, true);
        RegisterRoadCell(c);
        Money -= BridgeCost;
        Ledger.Add("营造桥梁", -BridgeCost);
        EventBus.RaiseCellChanged(c);
        EventBus.RaiseStatsChanged();
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

        EventBus.RaiseMapChanged();
        EventBus.RaiseStatsChanged();
        return b;
    }

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

    /// <summary>把一格并入指定建筑占地（住宅扩建用）：砍除植物、收拾散落物资并登记占用。</summary>
    public void ClaimCellForBuilding(Vector2I c, int buildingId)
    {
        RemovePlantAt(c);
        // 扩地格上的地面物资堆并入建筑仓（装不下的散佚），免得永久压在房底下
        if (Piles.Remove(CellIndex(c), out var pile) && Buildings.TryGetValue(buildingId, out var owner))
            foreach (var s in pile.Inv.Stacks)
                owner.StoreGoods(s.GoodsId, s.Amount);
        Map.CellAt(c).BuildingId = buildingId;
        EventBus.RaiseCellChanged(c);
    }

    /// <summary>拆除建筑实例（手动拆除 / 老化坍塌共用）。</summary>
    public void DemolishBuilding(BuildingInstance b)
    {
        for (int x = b.Origin.X; x < b.Origin.X + b.FootX; x++)
            for (int y = b.Origin.Y; y < b.Origin.Y + b.FootY; y++)
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

    /// <summary>找最近的挂果成树（至少一份可摘；线性扫描，植物数有上限）。</summary>
    public PlantObj FindNearestFruitTree(Vector2I from, int maxRadius)
    {
        PlantObj best = null;
        int bestDist = maxRadius + 1;
        foreach (var p in Plants.Values)
        {
            if (!p.Mature || p.FruitStock < 1)
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

    // ---- 居民 / 家庭 ----

    public Citizen AddCitizen(Citizen c)
    {
        c.Id = NextCitizenId++;
        Citizens[c.Id] = c;
        EventBus.RaiseCitizenAdded(c);
        return c;
    }

    /// <summary>记一笔年龄履历（重大事件）：按当前游戏年月追加；上限 40 条，超出移除最旧，防长寿居民档案膨胀。</summary>
    public void LogLifeEvent(Citizen c, string text)
    {
        c.LifeEvents.Add(new LifeEvent { Year = CurYear, Month = CurMonth, Text = text });
        if (c.LifeEvents.Count > 40)
            c.LifeEvents.RemoveAt(0);
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
