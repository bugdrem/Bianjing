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
    /// <summary>表现层代理上限（超出的居民只参与数据模拟，不上屏）。</summary>
    private const int MaxAgents = 300;

    /// <summary>分离半径/强度：小于格宽 4m，查 3×3 邻桶即可覆盖。</summary>
    private const float SeparationRadius = 1.1f;
    private const float SeparationStrength = 3f;

    private readonly GameClock _clock;
    private readonly Dictionary<int, CitizenAgent> _agents = new();
    private readonly Dictionary<Vector2I, List<CitizenAgent>> _buckets = new();

    private List<Vector2I> _roadCells;

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
        EventBus.MapChanged += InvalidateRoads;

        RebuildAll();
    }

    public override void _ExitTree()
    {
        EventBus.CitizenAdded -= OnCitizenAdded;
        EventBus.CitizenRemoved -= OnCitizenRemoved;
        EventBus.GameLoaded -= RebuildAll;
        EventBus.MapChanged -= InvalidateRoads;
    }

    /// <summary>每帧重建空间哈希桶（父节点 _Process 先于子节点执行，代理拿到的是本帧数据）。</summary>
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
        InvalidateRoads();

        foreach (var c in GameState.I.Citizens.Values)
            OnCitizenAdded(c);
    }

    private void InvalidateRoads() => _roadCells = null;

    /// <summary>随机道路格（缓存，道路变化时失效重建）。</summary>
    public Vector2I? RandomRoadCell(Random rng)
    {
        if (_roadCells == null)
        {
            _roadCells = new List<Vector2I>();
            var gs = GameState.I;
            for (int x = 0; x < MapGrid.Size; x++)
                for (int y = 0; y < MapGrid.Size; y++)
                    if (gs.Map.CellAt(x, y).HasRoad)
                        _roadCells.Add(new Vector2I(x, y));
        }

        if (_roadCells.Count == 0)
            return null;
        return _roadCells[rng.Next(_roadCells.Count)];
    }
}
