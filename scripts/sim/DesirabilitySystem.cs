using Godot;

namespace Bianjing;

/// <summary>吸引力场：建筑变化后全图重算。吸引力 = 井/衙门/宫殿等正向覆盖（线性衰减）− 工坊污染。</summary>
public class DesirabilitySystem
{
    private bool _dirty = true;

    public DesirabilitySystem()
    {
        EventBus.MapChanged += MarkDirty;
        EventBus.CellChanged += MarkDirtyCell; // 铺路/拆路等单格变更同样影响吸引力场
    }

    private void MarkDirty() => _dirty = true;

    private void MarkDirtyCell(Vector2I _) => _dirty = true;

    public void EnsureUpdated(GameState gs)
    {
        if (!_dirty)
            return;
        _dirty = false;

        for (int x = 0; x < MapGrid.Size; x++)
            for (int y = 0; y < MapGrid.Size; y++)
                gs.Map.CellAt(x, y).Desirability = 0f;

        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.DesirabilityBonus > 0f)
                Splat(gs, b, b.Def.DesirabilityBonus, b.Def.DesirabilityRadius);
            if (b.Def.Pollution > 0f)
                Splat(gs, b, -b.Def.Pollution, b.Def.PollutionRadius);
        }

        // 道路也带来临街吸引力：主路 +1.0、辅路 +0.4、小路 0（仅对小范围叠加）；
        // 只遍历增量维护的道路格列表，大地图下不再全图扫描
        foreach (var rc in gs.RoadCells)
        {
            var cell = gs.Map.CellAt(rc);
            if (!cell.HasRoad)
                continue;
            float bonus = cell.RoadKind switch { RoadKind.Main => 1.0f, RoadKind.Side => 0.4f, _ => 0f };
            if (bonus <= 0f)
                continue;
            SplatCell(gs, rc.X, rc.Y, bonus, 3f);
        }
    }

    /// <summary>以某格为圆心线性衰减地叠加吸引力（道路用）。</summary>
    private static void SplatCell(GameState gs, int cx, int cy, float amount, float radius)
    {
        int r = Mathf.CeilToInt(radius);
        for (int x = Mathf.Max(0, cx - r); x <= Mathf.Min(MapGrid.Size - 1, cx + r); x++)
            for (int y = Mathf.Max(0, cy - r); y <= Mathf.Min(MapGrid.Size - 1, cy + r); y++)
            {
                float dist = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (dist > radius)
                    continue;
                gs.Map.CellAt(x, y).Desirability += amount * (1f - dist / radius);
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
