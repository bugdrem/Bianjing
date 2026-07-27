using Godot;

namespace Bianjing;

/// <summary>吸引力场：建筑变化后全图重算。吸引力 = 井/衙门/宫殿等正向覆盖（线性衰减）− 工坊污染。</summary>
public class DesirabilitySystem
{
    private bool _dirty = true;

    public DesirabilitySystem()
    {
        EventBus.MapChanged += MarkDirty;
    }

    private void MarkDirty() => _dirty = true;

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
