using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>植物生长系统：月结——树木固定生长、成熟大树散播幼体；日结——成树逐日挂果，挂满过熟概率落果成地面果堆。</summary>
public class PlantGrowthSystem
{
    // 调参集中在 configs/PlantConfig，此处只留短名转发便于阅读
    private const int MaxPlants = PlantConfig.MaxPlants;
    private const float SeedChance = PlantConfig.SeedChance;
    private const double FruitPerDay = PlantConfig.FruitPerDay;
    private const double DropChance = PlantConfig.DropChance;
    private const int RegenDelayDays = PlantConfig.RegenDelayDays;
    private const float RegenPerDay = PlantConfig.RegenPerDay;

    private readonly Random _rng = new();

    /// <summary>日结：挂果生长与过熟落果（典型案例四→案例三的转化）；另处理砍伐伤的逐日恢复。</summary>
    public void TickDay(GameState gs)
    {
        foreach (var p in gs.Plants.Values)
        {
            // 砍伐伤恢复：一段时间没人砍才慢慢回血（被砍即重新计时，见 DamageTree）
            if (p.IdleDays < RegenDelayDays)
                p.IdleDays++;
            else if (p.Hp < p.MaxHp)
                p.Hp = Math.Min(p.MaxHp, p.Hp + RegenPerDay);

            if (!p.Mature || !p.IsFruitTree)
                continue; // 只有果树才挂果，普通树只出木材
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
        var seeds = new List<(Vector2I Cell, bool Fruit)>();

        foreach (var p in gs.Plants.Values)
        {
            // 树龄持续累积：成熟前驱动尺寸生长，成熟后驱动血量上限缓涨（越老涨得越慢）
            p.GrowthMonths++;

            if (p.Mature && gs.Plants.Count + seeds.Count < MaxPlants && _rng.NextDouble() < SeedChance)
            {
                // 成熟大树在 ±SeedRange 米内的空格散播一株幼体（不侵入坊区规划地）
                var c = new Vector2I(
                    p.X + _rng.Next(-PlantConfig.SeedRange, PlantConfig.SeedRange + 1),
                    p.Y + _rng.Next(-PlantConfig.SeedRange, PlantConfig.SeedRange + 1));
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.IsEmpty && !cell.HasTree && cell.Zone == ZoneType.None)
                    seeds.Add((c, p.IsFruitTree)); // 幼体继承母树类型：果树散果树
            }
        }

        foreach (var (c, fruit) in seeds)
            gs.AddPlant(c, 0, fruit);

        // 幼树逐月长大，需要重绘尺寸：只刷各分块树木 MultiMesh，不重建地形网格
        // （旧版这里全图 MapChanged，4x 下每月百万格网格重建是间歇卡顿主源之一）
        EventBus.RaiseTreesChanged();
    }
}
