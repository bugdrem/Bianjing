using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 外来访客（独立 Node3D，不复用 CitizenAgent，故不污染居民生命周期/就业/存档）：
/// 从某方向地图边缘走入城内，赴商铺/驿站交易、或路边摆摊、或进城住宿，事毕从原方向离城。
/// 外观按所属邻城配色区分；携带 Inventory（1–2 种同类货）。
/// </summary>
public partial class ForeignVisitor : Node3D
{
    public enum VisitorKind { Merchant, Peddler, Tourist }
    private enum Phase { Entering, Running, Dwell, Leaving }

    // ---- 外部注入 ----
    public MapDir FromDir;
    public VisitorKind Kind;
    public NeighborCity Origin;
    public Inventory Inv = new();
    public Stall Stall;
    public Vector2I ExitCell;
    public bool IsDone;          // 离场后由 VisitorSystem 回收
    public bool HasStall;        // 到达后是否成功支摊（失败则改为有限驻留后离场）

    // ---- 个人信息（供点选面板展示）----
    public string VisitorName = "";
    public Gender Gender;
    public int AgeYears;

    public event Action<ForeignVisitor> Arrived;
    public event Action<ForeignVisitor> Departed;

    // ---- 内部 ----
    private GameState _gs;
    private GameClock _clock;
    private Node3D _body;
    private readonly Random _rng = new();
    private List<Vector3> _path;
    private int _pathIndex;
    private Phase _phase = Phase.Entering;
    private float _dwell;
    private float _bobPhase;
    private Vector3 _laneJitter;

    private static readonly BoxMesh SharedBox = new() { Size = Vector3.One };
    private static readonly SphereMesh SharedSphere = new() { Radius = 0.5f, Height = 1f };

    /// <summary>初始化并规划入城路径（在 AddChild 之前由 VisitorSystem 调用）。</summary>
    public void Init(GameState gs, GameClock clock, MapDir dir, NeighborCity origin,
        VisitorKind kind, Inventory cargo, Vector2I exitCell, Vector3 targetWorld)
    {
        _gs = gs; _clock = clock; FromDir = dir; Origin = origin; Kind = kind; Inv = cargo; ExitCell = exitCell;
        BuildBody();
        _laneJitter = new Vector3(
            ((float)_rng.NextDouble() * 2f - 1f) * VillagerConfig.LaneJitterRange, 0f,
            ((float)_rng.NextDouble() * 2f - 1f) * VillagerConfig.LaneJitterRange);
        _path = WalkerPathfinder.BuildPath(gs, Position, targetWorld);
        _pathIndex = 0;
        _phase = _path.Count > 0 ? Phase.Entering : Phase.Dwell;
        if (_phase == Phase.Dwell)
            _dwell = Kind == VisitorKind.Peddler ? float.MaxValue
                : VisitorConfig.DwellSecondsMin + (float)_rng.NextDouble() * (VisitorConfig.DwellSecondsMax - VisitorConfig.DwellSecondsMin);

        // 个人信息：姓名（与市民同风格）、性别、年龄
        Gender = _rng.Next(2) == 0 ? Gender.Male : Gender.Female;
        AgeYears = 18 + _rng.Next(0, 43); // 18–60 岁
        VisitorName = NameGenerator.NewName(Gender).Item2;
    }

    /// <summary>当前状态（面板展示用）。</summary>
    public string StateText => _phase switch
    {
        Phase.Entering => "入城途中",
        Phase.Dwell => HasStall ? "路边摆摊" : "在城内逗留",
        Phase.Leaving => "离城返程",
        _ => "途中",
    };

    /// <summary>系统强制其离场（摆摊到期 / 找不到摊位点等）。</summary>
    public void ForceLeave()
    {
        if (_phase == Phase.Leaving)
            return;
        BeginLeave();
    }

    public override void _Process(double delta)
    {
        if (_clock == null || _clock.Speed <= 0)
            return;
        float dt = (float)delta * _clock.Speed;

        if (_path != null && _path.Count > 0)
        {
            MoveAlongPath(dt);
        }
        else
        {
            if (_phase == Phase.Entering)
            {
                _phase = Phase.Dwell;
                if (Kind != VisitorKind.Peddler || !HasStall)
                    _dwell = Kind == VisitorKind.Peddler ? float.MaxValue
                        : VisitorConfig.DwellSecondsMin + (float)_rng.NextDouble() * (VisitorConfig.DwellSecondsMax - VisitorConfig.DwellSecondsMin);
                Arrived?.Invoke(this);
            }
            else if (_phase == Phase.Dwell)
            {
                if (Kind != VisitorKind.Peddler || !HasStall)
                {
                    _dwell -= dt;
                    if (_dwell <= 0f)
                        BeginLeave();
                }
            }
            else if (_phase == Phase.Leaving)
            {
                if (!IsDone)
                {
                    IsDone = true;
                    Departed?.Invoke(this);
                }
                return;
            }
        }

        // 地形贴合（桥面/地面）
        float surfaceY = SurfaceYAt(Position);
        if (Mathf.Abs(Position.Y - surfaceY) > 0.001f)
            Position = Position with { Y = Mathf.MoveToward(Position.Y, surfaceY, 1.5f * dt) };

        // 走路上下抖动（只动 _body 局部 Y，不碰 Position.Y 地形基准）
        bool moving = _path != null && _path.Count > 0;
        if (moving)
        {
            _bobPhase += dt * 10f;
            float bob = Mathf.Sin(_bobPhase * 2f) * 0.06f;
            _body.Position = new Vector3(_body.Position.X, bob, _body.Position.Z);
        }
        else if (Mathf.Abs(_body.Position.Y) > 0.001f)
        {
            _body.Position = new Vector3(_body.Position.X, Mathf.MoveToward(_body.Position.Y, 0f, 4f * dt), _body.Position.Z);
        }
    }

    private void BeginLeave()
    {
        _phase = Phase.Leaving;
        var exitWorld = MapGrid.CellToWorld(ExitCell) + Vector3.Up * (_gs.Map.GroundY(ExitCell) + 0.2f);
        _path = WalkerPathfinder.BuildPath(_gs, Position, exitWorld);
        _pathIndex = 0;
        if (_path.Count == 0)
        {
            IsDone = true;
            Departed?.Invoke(this);
        }
    }

    private void MoveAlongPath(float dt)
    {
        var cell = MapGrid.WorldToCell(Position);
        float speedFactor = MapGrid.InBounds(cell) && _gs.Map.CellAt(cell).HasRoad
            ? MovementConfig.RoadSpeedFactor(_gs.Map.CellAt(cell).RoadKind)
            : MovementConfig.OffRoadFactor;
        float step = MovementConfig.BaseSpeed * speedFactor * dt;
        var before = Position;
        while (step > 0f && _path != null)
        {
            var target = _path[_pathIndex];
            target.Y = Position.Y;
            float dist = Position.DistanceTo(target);
            if (dist > step)
            {
                Position += (target - Position).Normalized() * step;
                break;
            }
            Position = target;
            step -= dist;
            _pathIndex++;
            if (_pathIndex >= _path.Count) { _path = null; break; }
        }
        FaceMoveDirection(Position - before, dt);
    }

    private void FaceMoveDirection(Vector3 moved, float dt)
    {
        moved.Y = 0f;
        if (moved.LengthSquared() < 1e-8f)
            return;
        float desired = Mathf.Atan2(moved.X, moved.Z);
        float yaw = _body.Rotation.Y;
        float diff = Mathf.AngleDifference(yaw, desired);
        float maxStep = MovementConfig.TurnSpeedRadPerSec * dt;
        _body.Rotation = new Vector3(0f, yaw + Mathf.Clamp(diff, -maxStep, maxStep), 0f);
    }

    private float SurfaceYAt(Vector3 p)
    {
        var c = MapGrid.WorldToCell(p);
        return MapGrid.InBounds(c) ? _gs.Map.GroundY(c) : 0f;
    }

    private MeshInstance3D AddPart(Node parent, Mesh mesh, StandardMaterial3D mat)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
        parent.AddChild(mi);
        return mi;
    }

    private void BuildBody()
    {
        _body = new Node3D();
        AddChild(_body);
        _body.Scale = Vector3.One * VillagerConfig.ModelScale;

        var robeMat = new StandardMaterial3D();
        var headMat = new StandardMaterial3D();
        var hatMat = new StandardMaterial3D();
        var packMat = new StandardMaterial3D();

        var robe = AddPart(_body, SharedBox, robeMat);   // 长袍
        var head = AddPart(_body, SharedSphere, headMat); // 头（球）
        var hat = AddPart(_body, SharedBox, hatMat);      // 帽
        var pack = AddPart(_body, SharedBox, packMat);    // 背篓（带货）

        float gownH = 1.1f;
        robe.Scale = new Vector3(0.5f, gownH, 0.36f);
        robe.Position = Vector3.Up * (gownH / 2f);

        float headSize = 0.24f;
        head.Scale = Vector3.One * headSize;
        head.Position = new Vector3(0f, gownH + headSize / 2f, 0f);

        hat.Scale = new Vector3(0.26f, 0.10f, 0.26f);
        hat.Position = new Vector3(0f, gownH + headSize + 0.05f, 0f);

        pack.Scale = new Vector3(0.30f, 0.34f, 0.18f);
        pack.Position = new Vector3(0f, 0.7f, -0.22f); // 背在身后

        robeMat.AlbedoColor = VisitorConfig.CityRobe[(int)FromDir];
        headMat.AlbedoColor = new Color(0.91f, 0.76f, 0.62f); // 肤色
        hatMat.AlbedoColor = new Color(0.18f, 0.14f, 0.10f);
        packMat.AlbedoColor = new Color(0.55f, 0.42f, 0.28f);
    }
}
