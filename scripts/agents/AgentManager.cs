using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 居民代理管理器：数据层 Citizen 与场景中 CitizenAgent 的同步桥。
/// 监听居民增减与读档事件；维护道路格缓存供代理随机选点；
/// 每帧重建空间哈希桶，为代理提供邻居分离推力（轻量碰撞，免物理引擎）。
/// </summary>
public partial class AgentManager : Node
{
    /// <summary>表现层代理上限（超出的居民只参与数据模拟，不上屏；调参见 configs/AgentConfig）。</summary>
    private const int MaxAgents = AgentConfig.MaxAgents;

    /// <summary>分离半径/强度：小于邻桶覆盖距离（1m 格桶，查 3×3 邻桶保证覆盖 1m 内邻居）。</summary>
    private const float SeparationRadius = AgentConfig.SeparationRadius;
    private const float SeparationStrength = AgentConfig.SeparationStrength;

    private readonly GameClock _clock;
    private readonly Dictionary<int, CitizenAgent> _agents = new();
    private readonly Dictionary<Vector2I, List<CitizenAgent>> _buckets = new();

    // ---- 选中居民目标路线（浅绿色线，随移动实时缩短）----
    private int _selectedId = -1;
    private MeshInstance3D _pathLine;
    private ImmediateMesh _pathMesh;
    private StandardMaterial3D _pathMat;

    /// <summary>全部在场代理（点选拾取用）。</summary>
    public IEnumerable<CitizenAgent> Agents => _agents.Values;

    public AgentManager(GameClock clock)
    {
        _clock = clock;
    }

    public override void _Ready()
    {
        EventBus.CitizenAdded += OnCitizenAdded;
        EventBus.CitizenRemoved += OnCitizenRemoved;
        EventBus.GameLoaded += RebuildAll;
        EventBus.CitizenSelected += OnCitizenSelected;

        // 路线网格常驻，无选中/无路径时隐藏；关深度测试使指引线穿透建筑/树木也可见
        _pathMesh = new ImmediateMesh();
        _pathMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.55f, 1f, 0.65f, 0.85f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            RenderPriority = 10,
        };
        _pathLine = new MeshInstance3D { Mesh = _pathMesh, Visible = false };
        AddChild(_pathLine);

        RebuildAll();
    }

    public override void _ExitTree()
    {
        EventBus.CitizenAdded -= OnCitizenAdded;
        EventBus.CitizenRemoved -= OnCitizenRemoved;
        EventBus.GameLoaded -= RebuildAll;
        EventBus.CitizenSelected -= OnCitizenSelected;
    }

    /// <summary>每帧重建空间哈希桶（父节点 _Process 先于子节点执行，代理拿到的是本帧数据）；末尾重绘选中居民路线。</summary>
    public override void _Process(double delta)
    {
        _buckets.Clear();
        foreach (var agent in _agents.Values)
        {
            var cell = MapGrid.WorldToCell(agent.Position);
            if (!_buckets.TryGetValue(cell, out var list))
                _buckets[cell] = list = new List<CitizenAgent>();
            list.Add(agent);
        }

        UpdatePathLine();
    }

    private void OnCitizenSelected(int id) => _selectedId = id;

    /// <summary>重绘选中居民的剩余目标路线：从代理当前位置依次连到尚未走过的路径点；
    /// 无选中、代理不在场（超上限只模拟不上屏）或无路径时隐藏。</summary>
    private void UpdatePathLine()
    {
        if (_selectedId < 0 || !_agents.TryGetValue(_selectedId, out var agent)
            || agent.PathPoints == null || agent.PathIndex >= agent.PathPoints.Count)
        {
            _pathMesh.ClearSurfaces();
            _pathLine.Visible = false;
            return;
        }

        const float y = 0.5f; // 抬到路面（顶 0.2）与桥面（顶约 0.43）之上，否则沿路的线会被路面埋没
        _pathMesh.ClearSurfaces();
        _pathMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip, _pathMat);
        var start = agent.Position;
        _pathMesh.SurfaceAddVertex(new Vector3(start.X, y, start.Z));
        for (int i = agent.PathIndex; i < agent.PathPoints.Count; i++)
        {
            var p = agent.PathPoints[i];
            _pathMesh.SurfaceAddVertex(new Vector3(p.X, y, p.Z));
        }
        _pathMesh.SurfaceEnd();
        _pathLine.Visible = true;
    }

    /// <summary>
    /// 邻居分离推力：查 3×3 邻桶内半径内的其他代理，越近推力越大；
    /// 完全重叠时按 Id 伪随机方向错开，避免死锁。
    /// </summary>
    public Vector3 SeparationPush(CitizenAgent agent)
    {
        var push = Vector3.Zero;
        var cell = MapGrid.WorldToCell(agent.Position);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (!_buckets.TryGetValue(new Vector2I(cell.X + dx, cell.Y + dy), out var list))
                    continue;
                foreach (var other in list)
                {
                    if (other == agent)
                        continue;
                    var delta = agent.Position - other.Position;
                    delta.Y = 0f;
                    float d = delta.Length();
                    if (d >= SeparationRadius)
                        continue;
                    if (d < 0.01f)
                    {
                        float a = agent.C.Id * 2.399f;
                        push += new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * SeparationStrength;
                    }
                    else
                    {
                        push += delta / d * ((SeparationRadius - d) / SeparationRadius) * SeparationStrength;
                    }
                }
            }
        }
        return push;
    }

    private void OnCitizenAdded(Citizen c)
    {
        if (_agents.Count >= MaxAgents || _agents.ContainsKey(c.Id))
            return;
        var agent = new CitizenAgent(c, _clock, this);
        _agents[c.Id] = agent;
        AddChild(agent);
    }

    private void OnCitizenRemoved(Citizen c)
    {
        if (_agents.Remove(c.Id, out var agent))
            agent.QueueFree();
    }

    private void RebuildAll()
    {
        foreach (var agent in _agents.Values)
            agent.QueueFree();
        _agents.Clear();

        foreach (var c in GameState.I.Citizens.Values)
            OnCitizenAdded(c);
    }

    /// <summary>随机道路格：直接取自 GameState 增量维护的道路格列表，无需全图扫描重建缓存。</summary>
    public Vector2I? RandomRoadCell(Random rng)
    {
        var roads = GameState.I.RoadCells;
        if (roads.Count == 0)
            return null;
        return roads[rng.Next(roads.Count)];
    }
}
