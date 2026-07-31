using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>吸引力场：建筑/道路变化后重算。吸引力 = 井/衙门/宫殿等正向覆盖（线性衰减）− 工坊污染
/// + 道路临街加成。道路项数量大（数千格 × 半径12圆盘）且逐格增量变化，
/// 用独立场缓存增量维护（只泼溅新增/移除/变种的路格），重算时整场拷入再叠建筑项——
/// 免每次全量重泼全部路格（4x 下村民频繁铺小路/建房触发重算，全量重泼是间歇卡顿源之一）。</summary>
public class DesirabilitySystem
{
    private bool _dirty = true;

    /// <summary>道路吸引力场缓存（与地图同尺寸一维数组）：增量维护，重算时整场拷入。</summary>
    private readonly float[] _roadField = new float[MapGrid.Size * MapGrid.Size];

    /// <summary>已泼溅进 _roadField 的路格 → 当时的泼溅幅度（变种/拆除时按差额补泼）。</summary>
    private readonly Dictionary<Vector2I, float> _roadSplat = new();

    public DesirabilitySystem()
    {
        EventBus.MapChanged += MarkDirty;          // 读档/新局：全量重算（含道路场重建）
        EventBus.CellChanged += MarkDirtyCell;     // 铺路/拆路等单格变更同样影响吸引力场
        EventBus.RectChanged += MarkDirtyRect;     // 建筑落成/拆除/扩建（不再走全图 MapChanged）
        EventBus.BuildingsChanged += MarkDirty;    // 转业改变污染/加成来源
    }

    private void MarkDirty() => _dirty = true;

    private void MarkDirtyCell(Vector2I _) => _dirty = true;

    private void MarkDirtyRect(Vector2I _, Vector2I __) => _dirty = true;

    public void EnsureUpdated(GameState gs)
    {
        if (!_dirty)
            return;
        _dirty = false;

        // 1) 增量同步道路场：只泼溅「新增/变种/拆除」的路格差额，存量路格零开销
        SyncRoadField(gs);

        // 2) 道路场整场拷入作底，其上叠加建筑正负覆盖
        for (int y = 0; y < MapGrid.Size; y++)
            for (int x = 0; x < MapGrid.Size; x++)
                gs.Map.CellAt(x, y).Desirability = _roadField[y * MapGrid.Size + x];

        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.DesirabilityBonus > 0f)
                Splat(gs, b, b.Def.DesirabilityBonus, b.Def.DesirabilityRadius);
            if (b.Def.Pollution > 0f)
                Splat(gs, b, -b.Def.Pollution, b.Def.PollutionRadius);
        }
    }

    /// <summary>某路格当前应有的泼溅幅度：主路/辅路小量加成，小路与桥面（RoadKind.None）不加成；
    /// 幅度/归一系数/泼溅半径均见 configs/GrowthConfig 吸引力段。</summary>
    private static float RoadBonusOf(in Cell cell) => !cell.HasRoad ? 0f : cell.RoadKind switch
    {
        RoadKind.Main => GrowthConfig.DesirMainRoadBonus / GrowthConfig.DesirRoadScale,
        RoadKind.Side => GrowthConfig.DesirSideRoadBonus / GrowthConfig.DesirRoadScale,
        _ => 0f,
    };

    /// <summary>把道路场与当前路网增量对齐：新增路格正泼、拆除负泼、变种（辅路升主路）补差额。
    /// 只遍历增量维护的道路格列表与既有泼溅记录，典型帧内差额为个位数路格。</summary>
    private void SyncRoadField(GameState gs)
    {
        // 新增/变种：现值与记录不符则按差额补泼
        foreach (var rc in gs.RoadCells)
        {
            float want = RoadBonusOf(gs.Map.CellAt(rc));
            _roadSplat.TryGetValue(rc, out float has);
            if (Mathf.Abs(want - has) < 0.0001f)
                continue;
            SplatRoad(rc.X, rc.Y, want - has);
            if (want == 0f)
                _roadSplat.Remove(rc);
            else
                _roadSplat[rc] = want;
        }

        // 拆除：记录里还在、格上已无路（或已出 RoadCells）→ 负泼回收；
        // 移除项攒列表后删，避免遍历中改字典
        List<Vector2I> gone = null;
        foreach (var (c, has) in _roadSplat)
        {
            if (RoadBonusOf(gs.Map.CellAt(c)) != 0f)
                continue;
            SplatRoad(c.X, c.Y, -has);
            (gone ??= new List<Vector2I>()).Add(c);
        }
        if (gone != null)
            foreach (var c in gone)
                _roadSplat.Remove(c);
    }

    /// <summary>以某路格为圆心线性衰减地叠加进道路场缓存（幅度可负，用于回收）。</summary>
    private void SplatRoad(int cx, int cy, float amount)
    {
        float radius = GrowthConfig.DesirRoadRadius;
        int r = Mathf.CeilToInt(radius);
        for (int x = Mathf.Max(0, cx - r); x <= Mathf.Min(MapGrid.Size - 1, cx + r); x++)
            for (int y = Mathf.Max(0, cy - r); y <= Mathf.Min(MapGrid.Size - 1, cy + r); y++)
            {
                float dist = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (dist > radius)
                    continue;
                _roadField[y * MapGrid.Size + x] += amount * (1f - dist / radius);
            }
    }

    /// <summary>以建筑 footprint 中心为圆心，线性衰减地叠加吸引力。</summary>
    private static void Splat(GameState gs, BuildingInstance b, float amount, float radius)
    {
        if (radius <= 0f)
            return;

        float cx = b.Origin.X + (b.Def.SizeX - 1) / 2f;
        float cy = b.Origin.Y + (b.Def.SizeY - 1) / 2f;
        int r = Mathf.CeilToInt(radius);

        int x0 = Mathf.Max(0, Mathf.FloorToInt(cx) - r);
        int x1 = Mathf.Min(MapGrid.Size - 1, Mathf.CeilToInt(cx) + r);
        int y0 = Mathf.Max(0, Mathf.FloorToInt(cy) - r);
        int y1 = Mathf.Min(MapGrid.Size - 1, Mathf.CeilToInt(cy) + r);

        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                float dist = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (dist > radius)
                    continue;
                gs.Map.CellAt(x, y).Desirability += amount * (1f - dist / radius);
            }
        }
    }
}
