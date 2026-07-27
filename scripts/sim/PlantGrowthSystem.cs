using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>植物生长系统（每月结算）：树木固定生长；成熟大树向周围空地散播幼体。</summary>
public class PlantGrowthSystem
{
    /// <summary>全图植物上限，防止树林无限蔓延吞噬地图。</summary>
    private const int MaxPlants = 2200;
    private const float SeedChance = 0.03f;

    private readonly Random _rng = new();

    public void Tick(GameState gs)
    {
        var seeds = new List<Vector2I>();

        foreach (var p in gs.Plants.Values)
        {
            if (p.GrowthMonths < PlantObj.MatureMonths)
            {
                p.GrowthMonths++;
            }
            else if (gs.Plants.Count + seeds.Count < MaxPlants && _rng.NextDouble() < SeedChance)
            {
                // 成熟大树在紧邻空格散播一株幼体（不侵入坊区规划地）
                var c = new Vector2I(p.X + _rng.Next(-1, 2), p.Y + _rng.Next(-1, 2));
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.IsEmpty && !cell.HasTree && cell.Zone == ZoneType.None)
                    seeds.Add(c);
            }
        }

        foreach (var c in seeds)
            gs.AddPlant(c, 0);

        // 幼树逐月长大，需要重绘尺寸
        EventBus.RaiseMapChanged();
    }
}
