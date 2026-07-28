using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>植物生长系统：月结——树木固定生长、成熟大树散播幼体；日结——成树逐日挂果，挂满过熟概率落果成地面果堆。</summary>
public class PlantGrowthSystem
{
    /// <summary>全图植物上限，防止树林无限蔓延吞噬地图。</summary>
    private const int MaxPlants = 2200;
    private const float SeedChance = 0.03f;

    /// <summary>成树每日挂果增量（份）与挂满后每日落果概率。</summary>
    private const double FruitPerDay = 0.1;
    private const double DropChance = 0.1;

    private readonly Random _rng = new();

    /// <summary>日结：挂果生长与过熟落果（典型案例四→案例三的转化）。</summary>
    public void TickDay(GameState gs)
    {
        foreach (var p in gs.Plants.Values)
        {
            if (!p.Mature)
                continue;
            if (p.FruitStock < PlantObj.FruitCap)
            {
                // 树上果实逐日缓慢增长（未掉落前属于树上仓储）
                p.FruitStock = Math.Min(PlantObj.FruitCap, p.FruitStock + FruitPerDay);
            }
            else if (_rng.NextDouble() < DropChance)
            {
                // 挂满过熟：掉一份成地面果堆，谁都能拾
                p.FruitStock -= 1;
                gs.DropOnGround(new Vector2I(p.X, p.Y), Goods.Fruit, 1);
            }
        }
    }

    /// <summary>月结：幼树长大、成熟大树散播幼体。</summary>
    public void TickMonth(GameState gs)
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
