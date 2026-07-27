using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 居民 3D 代理：按身份/家庭角色驱动日常作息——
/// 受雇者到工作地驻留直至下班；山民伐木/采摘/打猎后去市集交易；
/// 修缮匠巡修公共设施；主妇采购、孩童玩耍、老人闲逛。
/// 出行优先走道路（路网 AStar，带随机车道偏移避免排成一线），可脱离道路直线行走但有大幅减速惩罚；
/// 每帧受邻居分离推力，避免人群叠在一起。
/// 只读写 Citizen 的状态值，不做任何生死/雇佣决策（那是数据层系统的职责）。
/// </summary>
public partial class CitizenAgent : Node3D
{
    private const float BaseSpeed = 5f;
    private const float OffRoadFactor = 0.35f; // 脱离道路的减速惩罚
    private const float TiredThreshold = 80f;
    private const float BoredThreshold = 25f;
    private const float StayUntilDone = 9999f; // 驻留到条件触发（如下班）而非计时
    private const float LaneJitterRange = 1.2f; // 车道偏移幅度（路格宽 4m，偏移后仍在路面内）

    public Citizen C { get; }

    private readonly GameClock _clock;
    private readonly AgentManager _manager;
    private readonly Random _rng = new();

    private MeshInstance3D _lower, _upper, _head, _hat;
    private StandardMaterial3D _lowerMat, _upperMat, _headMat, _hatMat;

    // 宋人占位模型：头/上身/下身 + 冠发，共享网格资源省内存，材质各自持有
    private static readonly BoxMesh SharedBox = new() { Size = Vector3.One };
    private static readonly SphereMesh SharedSphere = new() { Radius = 0.5f, Height = 1f };

    private Node3D _body;
    private bool _spawnPending;

    private List<Vector3> _path;
    private int _pathIndex;
    private float _dwell;
    private Vector2I? _activityCell; // 当前活动目标格（伐木/采摘结算用）
    private int _activityAnimalId = -1; // 打猎目标动物 Id
    private Vector3 _laneJitter; // 个人固定车道偏移，避免全员踩路心叠成一条线
    private int _lookAgeYears = -1;

    public CitizenAgent(Citizen citizen, GameClock clock, AgentManager manager)
    {
        C = citizen;
        _clock = clock;
        _manager = manager;
    }

    public override void _Ready()
    {
        _body = new Node3D();
        AddChild(_body);
        _lower = AddPart(SharedBox, _lowerMat = new StandardMaterial3D());
        _upper = AddPart(SharedBox, _upperMat = new StandardMaterial3D());
        _head = AddPart(SharedSphere, _headMat = new StandardMaterial3D());
        _hat = AddPart(SharedBox, _hatMat = new StandardMaterial3D());
        ApplyLook();

        _laneJitter = new Vector3(
            ((float)_rng.NextDouble() * 2f - 1f) * LaneJitterRange,
            0f,
            ((float)_rng.NextDouble() * 2f - 1f) * LaneJitterRange);

        if (C.PosValid)
        {
            // 从存档恢复位置，稍作停留后按当前活动重新决策衔接
            Position = new Vector3(C.PosX, 0.2f, C.PosZ);
            _dwell = 0.5f;
        }
        else
        {
            // 迁入者从住所/工商建筑门前出现；HomeId 在同次月结算稍后才指定，首帧再校正一次
            Position = HomePosition();
            _spawnPending = true;
            _dwell = (float)_rng.NextDouble() * 3f;
        }
    }

    private MeshInstance3D AddPart(Mesh mesh, StandardMaterial3D mat)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
        _body.AddChild(mi);
        return mi;
    }

    public override void _Process(double delta)
    {
        if (_spawnPending)
        {
            // 出生/迁入时住所在代理创建之后才分配，首帧从住所建筑重新定位
            _spawnPending = false;
            Position = HomePosition();
        }

        if (_clock.Speed <= 0)
            return;
        float dt = (float)delta * _clock.Speed;

        if (_path != null)
        {
            MoveAlongPath(dt);
        }
        else
        {
            ApplyActivityNeeds(dt);
            _dwell -= dt;
            if (_dwell <= 0f)
            {
                CompleteActivity();
                DecideNext();
            }
        }

        // 碰撞分离：被邻居推开，避免人群重叠
        var push = _manager.SeparationPush(this);
        if (push != Vector3.Zero)
            Position += push * Mathf.Min(dt, 0.1f);

        // 位置回写数据层，随存档保存
        C.PosX = Position.X;
        C.PosZ = Position.Z;
        C.PosValid = true;
    }

    // ---- 决策 ----

    private void DecideNext()
    {
        var gs = GameState.I;

        if (C.AgeYears != _lookAgeYears)
            ApplyLook();

        // 背着山货（粮/柴/野果/猎物）：家里放得下先搬回家，否则挑去专营铺子卖掉
        if (C.Carrying != "")
        {
            if (gs.Buildings.TryGetValue(C.HomeId, out var home) && home.StorageFree >= 1)
                StartActivity(ActivityType.Hauling, HomeAnchor(), 2f);
            else
                StartActivity(ActivityType.Trading, TradeAnchor(gs), 2.5f);
            return;
        }

        if (C.IsChild)
        {
            // 孩童：在家附近玩耍（后续接入学堂后改为上学）
            StartActivity(ActivityType.Playing, NearbyRoadCell(HomeCell(), 4), 3f);
            return;
        }

        if (C.Fatigue >= TiredThreshold)
        {
            // 累了：兴趣太低先闲逛散心，否则回家歇息
            if (C.Fun < BoredThreshold)
                StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            else
                StartActivity(ActivityType.RestHome, HomeAnchor(), 5f);
            return;
        }

        if (C.JobKind == JobKind.Employed && gs.Buildings.TryGetValue(C.WorkplaceId, out var wp))
        {
            // 受雇者/店主：进工作地驻留，疲劳攒满才下班
            StartWorkAt(wp);
            return;
        }

        if (C.JobKind == JobKind.Logger)
        {
            StartForaging();
            return;
        }

        if (C.JobKind == JobKind.Repairer)
        {
            StartRepairing(gs);
            return;
        }

        if (C.Gender == Gender.Female && C.IsMarried && C.Activity != ActivityType.Shopping
            && _rng.NextDouble() < 0.6)
        {
            // 主妇：外出采购，回程自然衔接下次决策
            StartActivity(ActivityType.Shopping, ShoppingAnchor(gs), 2.5f);
            return;
        }

        if (C.IsElder)
        {
            if (_rng.NextDouble() < 0.5)
                StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            else
                StartActivity(ActivityType.RestHome, HomeAnchor(), 4f);
            return;
        }

        // 无业成年人：上山谋生换钱，再去市集交易
        StartForaging();
    }

    private void StartActivity(ActivityType activity, Vector2I? targetCell, float dwellSeconds)
    {
        C.Activity = activity;
        _dwell = dwellSeconds;
        _activityCell = targetCell;
        _activityAnimalId = -1;

        if (targetCell == null)
        {
            _path = null; // 无处可去：原地待着
            return;
        }
        BuildPathTo(MapGrid.CellToWorld(targetCell.Value));
    }

    /// <summary>受雇者上班/修缮匠巡修：站进目标建筑门口（锚点与建筑中心之间），驻留至疲劳攻顶。</summary>
    private void StartWorkAt(BuildingInstance wp, ActivityType activity = ActivityType.Working)
    {
        C.Activity = activity;
        _dwell = StayUntilDone;
        _activityCell = null;
        _activityAnimalId = -1;

        var anchor = GameState.I.Map.FindAdjacentRoad(wp.Origin, wp.Def.SizeX, wp.Def.SizeY);
        var anchorWorld = anchor != null ? MapGrid.CellToWorld(anchor.Value) : BuildingCenter(wp);
        var stand = anchorWorld.Lerp(BuildingCenter(wp), 0.45f);
        BuildPathTo(stand);
    }

    /// <summary>山民谋生三选一：伐木 / 林中采摘 / 打猎。</summary>
    private void StartForaging()
    {
        double roll = _rng.NextDouble();
        if (roll < 0.4)
            StartLogging();
        else if (roll < 0.7)
            StartGathering();
        else
            StartHunting();
    }

    /// <summary>伐木：找最近的树走过去砍；无树可砍则闲逛等待。</summary>
    private void StartLogging()
    {
        var tree = GameState.I.Map.FindNearestTree(MapGrid.WorldToCell(Position), 48);
        if (tree == null)
        {
            StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            return;
        }
        StartActivity(ActivityType.Logging, tree, 4f);
    }

    /// <summary>采摘：到林中采野果，不砸树不伐木；无树则闲逛等待。</summary>
    private void StartGathering()
    {
        var tree = GameState.I.Map.FindNearestTree(MapGrid.WorldToCell(Position), 48);
        if (tree == null)
        {
            StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            return;
        }
        StartActivity(ActivityType.Gathering, tree, 3f);
    }

    /// <summary>打猎：锁定最近的野物赶过去；没有猎物则改伐木。</summary>
    private void StartHunting()
    {
        var prey = GameState.I.FindNearestAnimal(MapGrid.WorldToCell(Position), 48);
        if (prey == null)
        {
            StartLogging();
            return;
        }
        StartActivity(ActivityType.Hunting, new Vector2I(prey.X, prey.Y), 4f);
        _activityAnimalId = prey.Id;
    }

    /// <summary>修缮匠：去最破旧的公共建筑驻留巡修（实际修复量由 MaintenanceSystem 月结）。</summary>
    private void StartRepairing(GameState gs)
    {
        BuildingInstance target = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "official" || b.Def.Natural)
                continue;
            if (target == null || b.Condition < target.Condition)
                target = b;
        }
        if (target == null)
        {
            StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            return;
        }
        StartWorkAt(target, ActivityType.Repairing);
    }

    /// <summary>驻留结束时的活动结算（砍树/采摘/打猎/卖货/下工工钱）：钱与货品一律按动作完成即时结算。</summary>
    private void CompleteActivity()
    {
        switch (C.Activity)
        {
            case ActivityType.Logging:
                if (_activityCell != null && GameState.I.ChopTree(_activityCell.Value))
                    C.Carrying = "wood";
                break;
            case ActivityType.Gathering:
                // 采摘不破坏树木，只要树还在就有收获
                if (_activityCell != null && MapGrid.InBounds(_activityCell.Value)
                    && GameState.I.Map.CellAt(_activityCell.Value).HasTree)
                    C.Carrying = "fruit";
                break;
            case ActivityType.Hunting:
                if (_activityAnimalId >= 0 && GameState.I.HarvestAnimal(_activityAnimalId))
                    C.Carrying = "game";
                break;
            case ActivityType.Trading:
                if (C.Carrying != "")
                {
                    SellCarrying(GameState.I);
                    C.Carrying = "";
                }
                break;
            case ActivityType.Hauling:
                // 搬回家入库；家里已装不下整担则下次决策转去市集
                if (C.Carrying != "" && GameState.I.Buildings.TryGetValue(C.HomeId, out var home)
                    && home.StoreGoods(C.Carrying, Goods.LoadUnits) > 0)
                    C.Carrying = "";
                break;
            case ActivityType.Working:
                // 下工即结：当班工钱（月俸/30）入账；农夫另将当班产粮入田仓，田仓有存就挑一担带走
                if (GameState.I.Buildings.TryGetValue(C.WorkplaceId, out var work))
                {
                    C.Money += work.Def.Salary / GameClock.DaysPerMonth;
                    if (work.Def.Id == "farm")
                    {
                        work.StoreGoods(Goods.Grain, GoodsSystem.GrainPerWorkerShift);
                        if (C.Carrying == "" && work.TakeGoods(Goods.Grain, Goods.LoadUnits) > 0)
                            C.Carrying = Goods.Grain;
                    }
                }
                break;
            case ActivityType.Repairing:
                // 修缮匠下工即结：官库发当班俸禄并记账
                double pay = JobSystem.RepairerIncome / GameClock.DaysPerMonth;
                C.Money += pay;
                GameState.I.Money -= pay;
                GameState.I.Ledger.Add("修缮匠俸禄", -pay);
                break;
        }
        _activityCell = null;
        _activityAnimalId = -1;
    }

    /// <summary>把肩上一担货卖进专营铺面：入库多少收多少钱；铺面全满则按基价散卖给行商。</summary>
    private void SellCarrying(GameState gs)
    {
        var shop = FindTradeShop(gs, C.Carrying, needFree: true);
        double sold = shop?.StoreGoods(C.Carrying, Goods.LoadUnits) ?? 0;
        if (sold <= 0)
            sold = Goods.LoadUnits; // 散卖：货物离场，不入任何库
        C.Money += Goods.PriceOf(C.Carrying) * sold;
    }

    /// <summary>找专营该货品的商铺/工坊（needFree 时要求还有库容），取余仓最大者。</summary>
    private static BuildingInstance FindTradeShop(GameState gs, string goodsId, bool needFree)
    {
        BuildingInstance best = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Specialty != goodsId)
                continue;
            if (needFree && b.StorageFree < 1)
                continue;
            if (best == null || b.StorageFree > best.StorageFree)
                best = b;
        }
        return best;
    }

    /// <summary>交易目的地：优先有库容的专营铺，其次任意专营铺，再退化到随机商铺。</summary>
    private Vector2I? TradeAnchor(GameState gs)
    {
        var shop = FindTradeShop(gs, C.Carrying, needFree: true)
                   ?? FindTradeShop(gs, C.Carrying, needFree: false);
        return shop != null ? BuildingAnchor(shop) ?? shop.Origin : ShoppingAnchor(gs);
    }

    // ---- 寻路与移动 ----

    /// <summary>
    /// 混合寻路：就近上路 → 路网 AStar → 末段脱路直线接近目标。
    /// 脱路段若直线会蹚水，先用网格 BFS 找旱路绕行（岸上/桥/路可走）；
    /// 只有真正无旱路可绕时才允许直线过河，不再瞬移。
    /// </summary>
    private void BuildPathTo(Vector3 targetWorld)
    {
        var gs = GameState.I;
        var startCell = MapGrid.WorldToCell(Position);
        var targetCell = MapGrid.WorldToCell(targetWorld);

        var points = new List<Vector3>();

        var entry = gs.Map.FindNearestRoad(startCell, 8);
        var exit = gs.Map.FindNearestRoad(targetCell, 8);
        if (entry != null && exit != null && entry.Value != exit.Value)
        {
            var cells = gs.Roads.FindPath(entry.Value, exit.Value);
            if (cells.Count > 0)
                AppendDrySegment(points, Position, MapGrid.CellToWorld(cells[0])); // 上路前的脱路段也不蹚水
            foreach (var c in cells)
                points.Add(MapGrid.CellToWorld(c) + Vector3.Up * 0.2f + _laneJitter); // 车道偏移：各走各道
        }

        // 末段：脱路走向目标（先绕开水面，无旱路才直线过河）
        var dest = new Vector3(targetWorld.X, 0.2f, targetWorld.Z);
        AppendDrySegment(points, points.Count > 0 ? points[^1] : Position, dest);
        points.Add(dest);

        _path = points;
        _pathIndex = 0;
    }

    /// <summary>脱路段避水：直线会蹚水时插入 BFS 旱路绕行点；找不到旱路则保持直线（无路可走才过河）。</summary>
    private static void AppendDrySegment(List<Vector3> points, Vector3 from, Vector3 to)
    {
        if (!LineCrossesWater(from, to))
            return;
        var detour = FindDryDetour(MapGrid.WorldToCell(from), MapGrid.WorldToCell(to));
        if (detour == null)
            return;
        for (int i = 1; i < detour.Count; i++) // 跳过脚下第一格，免得原地折返
            points.Add(MapGrid.CellToWorld(detour[i]) + Vector3.Up * 0.2f);
    }

    /// <summary>直线途经是否会蹚水（桥面/路面上的水不算），沿线每 2m 采样一次。</summary>
    private static bool LineCrossesWater(Vector3 from, Vector3 to)
    {
        float dist = new Vector2(to.X - from.X, to.Z - from.Z).Length();
        int steps = Mathf.CeilToInt(dist / 2f);
        for (int i = 1; i <= steps; i++)
        {
            var c = MapGrid.WorldToCell(from.Lerp(to, i / (float)steps));
            if (MapGrid.InBounds(c) && !IsDryCell(c))
                return true;
        }
        return false;
    }

    /// <summary>旱路格：岸上、桥面或路面。</summary>
    private static bool IsDryCell(Vector2I c)
    {
        var cell = GameState.I.Map.CellAt(c);
        return !cell.HasWater || cell.HasBridge || cell.HasRoad;
    }

    /// <summary>四向 BFS 找旱路（含起终格）；搜索量封顶防卡帧，找不到返回 null。</summary>
    private static List<Vector2I> FindDryDetour(Vector2I from, Vector2I to)
    {
        if (!MapGrid.InBounds(from) || !MapGrid.InBounds(to) || !IsDryCell(to))
            return null;

        var prev = new Dictionary<Vector2I, Vector2I> { [from] = from };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(from);
        Vector2I[] dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

        while (queue.Count > 0 && prev.Count < 2000)
        {
            var cur = queue.Dequeue();
            if (cur == to)
            {
                var path = new List<Vector2I>();
                for (var c = to; ; c = prev[c])
                {
                    path.Add(c);
                    if (c == from)
                        break;
                }
                path.Reverse();
                return path;
            }
            foreach (var d in dirs)
            {
                var n = cur + d;
                if (!prev.ContainsKey(n) && MapGrid.InBounds(n) && IsDryCell(n))
                {
                    prev[n] = cur;
                    queue.Enqueue(n);
                }
            }
        }
        return null;
    }

    private void MoveAlongPath(float dt)
    {
        // 道路优先：脚下无路时大幅减速（脱路惩罚）
        var cell = MapGrid.WorldToCell(Position);
        bool onRoad = MapGrid.InBounds(cell) && GameState.I.Map.CellAt(cell).HasRoad;
        float step = BaseSpeed * (onRoad ? 1f : OffRoadFactor) * dt;

        while (step > 0f && _path != null)
        {
            var target = _path[_pathIndex];
            float dist = Position.DistanceTo(target);
            if (dist > step)
            {
                Position += (target - Position).Normalized() * step;
                return;
            }
            Position = target;
            step -= dist;
            _pathIndex++;
            if (_pathIndex >= _path.Count)
                _path = null; // 到达，进入活动驻留
        }
    }

    /// <summary>驻留期间按活动更新疲劳/兴趣；上班攒满疲劳即下班。</summary>
    private void ApplyActivityNeeds(float dt)
    {
        switch (C.Activity)
        {
            case ActivityType.Working:
            case ActivityType.Repairing:
                C.Fatigue += 3f * dt;
                C.Fun -= 0.5f * dt;
                if (C.Fatigue >= TiredThreshold)
                    _dwell = 0f; // 下班
                break;
            case ActivityType.RestHome:
                C.Fatigue -= 5f * dt;
                C.Fun += 0.5f * dt;
                break;
            case ActivityType.Shopping:
                C.Fatigue += 0.5f * dt;
                C.Fun += 1.5f * dt;
                break;
            case ActivityType.Playing:
                C.Fun += 4f * dt;
                break;
            case ActivityType.Strolling:
                C.Fatigue -= 2f * dt;
                C.Fun += 3f * dt;
                break;
            case ActivityType.Logging:
                C.Fatigue += 4f * dt;
                C.Fun -= 0.5f * dt;
                break;
            case ActivityType.Gathering:
                C.Fatigue += 2f * dt;
                C.Fun += 0.5f * dt;
                break;
            case ActivityType.Hunting:
                C.Fatigue += 4f * dt;
                C.Fun += 1f * dt;
                break;
            case ActivityType.Trading:
                C.Fatigue += 0.5f * dt;
                C.Fun += 1f * dt;
                break;
            case ActivityType.Hauling:
                C.Fatigue += 1f * dt; // 挑担回家：略耗体力
                break;
        }
        C.Fatigue = Mathf.Clamp(C.Fatigue, 0f, 100f);
        C.Fun = Mathf.Clamp(C.Fun, 0f, 100f);
    }

    // ---- 位置工具 ----

    private Vector2I? HomeCell()
    {
        if (GameState.I.Buildings.TryGetValue(C.HomeId, out var home))
            return home.Origin;
        return null;
    }

    private Vector2I? HomeAnchor()
    {
        if (GameState.I.Buildings.TryGetValue(C.HomeId, out var home))
            return BuildingAnchor(home);
        return null;
    }

    private static Vector2I? BuildingAnchor(BuildingInstance b) =>
        GameState.I.Map.FindAdjacentRoad(b.Origin, b.Def.SizeX, b.Def.SizeY);

    private static Vector3 BuildingCenter(BuildingInstance b)
    {
        var a = MapGrid.CellToWorld(b.Origin);
        var c = MapGrid.CellToWorld(new Vector2I(b.Origin.X + b.Def.SizeX - 1, b.Origin.Y + b.Def.SizeY - 1));
        return (a + c) * 0.5f;
    }

    private Vector3 HomePosition()
    {
        var gs = GameState.I;
        if (gs.Buildings.TryGetValue(C.HomeId, out var home))
            return MapGrid.CellToWorld(home.Origin) + Vector3.Up * 0.2f;
        // 无住所也从住宅/工商建筑出现，不在地图上凭空刷新
        foreach (var b in gs.Buildings.Values)
            if (b.Def.Category == "grown")
                return MapGrid.CellToWorld(b.Origin) + Vector3.Up * 0.2f;
        var road = _manager.RandomRoadCell(_rng);
        return road != null ? MapGrid.CellToWorld(road.Value) + Vector3.Up * 0.2f : Vector3.Up * 0.2f;
    }

    private Vector2I? NearbyRoadCell(Vector2I? center, int radius)
    {
        if (center == null)
            return _manager.RandomRoadCell(_rng);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var c = new Vector2I(
                center.Value.X + _rng.Next(-radius, radius + 1),
                center.Value.Y + _rng.Next(-radius, radius + 1));
            if (MapGrid.InBounds(c) && GameState.I.Map.CellAt(c).HasRoad)
                return c;
        }
        return _manager.RandomRoadCell(_rng);
    }

    private Vector2I? ShoppingAnchor(GameState gs)
    {
        var shops = new List<BuildingInstance>();
        foreach (var b in gs.Buildings.Values)
            if (b.Def.Id == "shop")
                shops.Add(b);

        var target = shops.Count > 0 ? shops[_rng.Next(shops.Count)] : null;
        return target != null ? BuildingAnchor(target) : _manager.RandomRoadCell(_rng);
    }

    /// <summary>外观：宋人三段式（头/上身/下身）+ 冠发。体型随年龄，配色分男女，老人白发。</summary>
    private void ApplyLook()
    {
        _lookAgeYears = C.AgeYears;
        float bodyScale = C.IsChild ? 0.45f + 0.35f * (C.AgeYears / 16f) : 1f;
        _body.Scale = Vector3.One * bodyScale;

        bool female = C.Gender == Gender.Female;

        // 下身：男着裤褌收窄，女着长裙放宽
        _lower.Scale = female ? new Vector3(0.6f, 0.8f, 0.42f) : new Vector3(0.48f, 0.75f, 0.32f);
        _lower.Position = Vector3.Up * (_lower.Scale.Y / 2f);
        float waist = _lower.Scale.Y;

        // 上身：男肩宽、女肩窄
        _upper.Scale = new Vector3(female ? 0.5f : 0.56f, 0.55f, 0.34f);
        _upper.Position = Vector3.Up * (waist + 0.275f);
        float neck = waist + 0.55f;

        // 头 + 冠发（男戴幞头、女绾发髻，老人白发）
        _head.Scale = Vector3.One * 0.34f;
        _head.Position = Vector3.Up * (neck + 0.19f);
        _hat.Scale = female ? new Vector3(0.18f, 0.14f, 0.18f) : new Vector3(0.3f, 0.1f, 0.3f);
        _hat.Position = Vector3.Up * (neck + 0.38f);

        _headMat.AlbedoColor = new Color(0.91f, 0.76f, 0.62f); // 肤色
        _hatMat.AlbedoColor = C.IsElder ? new Color(0.92f, 0.92f, 0.9f) : new Color(0.09f, 0.08f, 0.08f);

        Color upperCol, lowerCol;
        if (C.IsChild)
        {
            upperCol = new Color(0.95f, 0.85f, 0.45f);
            lowerCol = new Color(0.75f, 0.55f, 0.35f);
        }
        else if (C.IsElder)
        {
            upperCol = new Color(0.62f, 0.62f, 0.58f);
            lowerCol = new Color(0.46f, 0.46f, 0.43f);
        }
        else if (female)
        {
            upperCol = new Color(0.76f, 0.42f, 0.47f); // 粉色襦衫
            lowerCol = new Color(0.5f, 0.62f, 0.5f);   // 青绿罗裙
        }
        else
        {
            upperCol = new Color(0.34f, 0.4f, 0.5f);   // 青灰襕衫
            lowerCol = new Color(0.26f, 0.3f, 0.36f);
        }
        _upperMat.AlbedoColor = upperCol;
        _lowerMat.AlbedoColor = lowerCol;
    }
}
