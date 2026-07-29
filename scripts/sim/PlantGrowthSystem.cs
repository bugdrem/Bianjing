using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>植物生长系统：月结——树木固定生长、成熟大树散播幼体；日结——成树逐日挂果，挂满过熟概率落果成地面果堆。</summary>
public class PlantGrowthSystem
{
    /// <summary>全图植物上限（世界面积扩大四倍后同比上调），防止树林无限蔓延吞噬地图。</summary>
    private const int MaxPlants = 8800;
    private const float SeedChance = 0.03f;

    /// <summary>成树每日挂果增量（份）与挂满后每日落果概率。</summary>
    private const double FruitPerDay = 0.1;
    private const double DropChance = 0.1;

    /// <summary>砍伐伤恢复：连续无人砍伐达到延迟天数后，每日回血至满。</summary>
    private const int RegenDelayDays = 3;
    private const float RegenPerDay = 2f;

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
                // 成熟大树在 ±4 米内的空格散播一株幼体（与旧版 ±1 格同距；不侵入坊区规划地）
                var c = new Vector2I(p.X + _rng.Next(-4, 5), p.Y + _rng.Next(-4, 5));
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.IsEmpty && !cell.HasTree && cell.Zone == ZoneType.None)
                    seeds.Add((c, p.IsFruitTree)); // 幼体继承母树类型：果树散果树
            }
        }

        foreach (var (c, fruit) in seeds)
            gs.AddPlant(c, 0, fruit);

        // 幼树逐月长大，需要重绘尺寸
        EventBus.RaiseMapChanged();
    }
}
