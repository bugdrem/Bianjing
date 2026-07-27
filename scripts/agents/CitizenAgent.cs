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

    private MeshInstance3D _mesh;
    private StandardMaterial3D _mat;

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
        _mat = new StandardMaterial3D();
        _mesh = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.45f, Height = 1.7f },
            MaterialOverride = _mat,
        };
        AddChild(_mesh);
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
            Position = HomePosition();
            _dwell = (float)_rng.NextDouble() * 3f;
        }
    }

    public override void _Process(double delta)
    {
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

        // 背着山货（柴/野果/猎物）：先去市集卖掉
        if (C.Carrying != "")
        {
            StartActivity(ActivityType.Trading, ShoppingAnchor(gs), 2.5f);
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

    /// <summary>驻留结束时的活动结算（砍树/采摘/打猎/卖货）。</summary>
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
                    C.Money += TradeIncome(C.Carrying);
                    C.Carrying = "";
                }
                break;
        }
        _activityCell = null;
        _activityAnimalId = -1;
    }

    /// <summary>山货市集售价（月薪另由 JobSystem 结算）。</summary>
    private static double TradeIncome(string goods) => goods switch
    {
        "wood" => 1.0,
        "fruit" => 0.6,
        "game" => 1.6,
        _ => 0,
    };

    // ---- 寻路与移动 ----

    /// <summary>
    /// 混合寻路：就近上路 → 路网 AStar → 末段脱路直线接近目标。
    /// 找不到路网可达时全程直线慢行（脱路惩罚），不再瞬移。
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
            foreach (var c in cells)
                points.Add(MapGrid.CellToWorld(c) + Vector3.Up * 0.2f + _laneJitter); // 车道偏移：各走各道
        }

        // 末段：脱路直线走向目标（也覆盖无路可走的情形）
        points.Add(new Vector3(targetWorld.X, 0.2f, targetWorld.Z));

        _path = points;
        _pathIndex = 0;
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
        if (GameState.I.Buildings.TryGetValue(C.HomeId, out var home))
            return MapGrid.CellToWorld(home.Origin) + Vector3.Up * 0.2f;
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

    /// <summary>外观：体型随年龄、颜色随性别/年龄。</summary>
    private void ApplyLook()
    {
        _lookAgeYears = C.AgeYears;
        float bodyScale = C.IsChild ? 0.45f + 0.35f * (C.AgeYears / 16f) : 1f;
        _mesh.Scale = Vector3.One * bodyScale;
        _mesh.Position = Vector3.Up * (0.85f * bodyScale);

        Color color;
        if (C.IsElder)
            color = new Color(0.75f, 0.75f, 0.72f);
        else if (C.IsChild)
            color = new Color(0.95f, 0.85f, 0.45f);
        else
            color = C.Gender == Gender.Male ? new Color(0.35f, 0.45f, 0.6f) : new Color(0.75f, 0.4f, 0.45f);
        _mat.AlbedoColor = color;
    }
}
