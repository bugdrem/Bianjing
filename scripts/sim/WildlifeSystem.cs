using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 野生动物系统（每月结算）：树林边随机刷新 → 就近随机游走 → 繁育 → 自然减员。
/// 只操作数据层 AnimalObj，渲染由 AnimalRenderer 负责，捕猎由居民代理触发。
/// </summary>
public class WildlifeSystem
{
    private const int MaxAnimals = 40;
    private const int InitialAnimals = 12;
    private const float BreedChance = 0.08f;
    private const float NaturalDeathChance = 0.01f;

    private readonly Random _rng = new();

    /// <summary>新地图初始撒动物。</summary>
    public void SeedInitial(GameState gs)
    {
        for (int i = 0; i < InitialAnimals; i++)
            SpawnNearForest(gs);
        EventBus.RaiseWildlifeChanged();
    }

    public void Tick(GameState gs)
    {
        bool changed = false;

        // 随机刷新：种群未满时树林边缘补充新个体
        if (gs.Animals.Count < MaxAnimals && _rng.NextDouble() < 0.6)
            changed |= SpawnNearForest(gs);

        var deaths = new List<int>();
        var newborns = new List<Vector2I>();
        foreach (var a in gs.Animals.Values)
        {
            a.AgeMonths++;

            // 随机游走：就近挑一块贴着树林的空地
            var next = RandomNearbyCell(gs, new Vector2I(a.X, a.Y), 2);
            if (next != null)
            {
                a.X = next.Value.X;
                a.Y = next.Value.Y;
                changed = true;
            }

            // 繁育：成年个体在种群未满时就近产仔
            if (a.AgeMonths >= 6 && gs.Animals.Count + newborns.Count < MaxAnimals
                && _rng.NextDouble() < BreedChance)
            {
                var spot = RandomNearbyCell(gs, new Vector2I(a.X, a.Y), 1);
                if (spot != null)
                    newborns.Add(spot.Value);
            }

            if (_rng.NextDouble() < NaturalDeathChance)
                deaths.Add(a.Id);
        }

        foreach (int id in deaths)
            changed |= gs.Animals.Remove(id);
        foreach (var c in newborns)
        {
            gs.AddAnimal(c);
            changed = true;
        }

        if (changed)
            EventBus.RaiseWildlifeChanged();
    }

    private bool SpawnNearForest(GameState gs)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var c = new Vector2I(_rng.Next(MapGrid.Size), _rng.Next(MapGrid.Size));
            ref var cell = ref gs.Map.CellAt(c);
            if (!cell.IsEmpty || cell.HasTree)
                continue;
            // 栖息地约束：2 格内必须有树林
            if (gs.Map.FindNearestTree(c, 2) == null)
                continue;
            gs.AddAnimal(c);
            return true;
        }
        return false;
    }

    /// <summary>附近随机挑一块可落脚的空地（不离开树林 3 格范围，保持栖息地）。</summary>
    private Vector2I? RandomNearbyCell(GameState gs, Vector2I from, int radius)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var c = new Vector2I(
                from.X + _rng.Next(-radius, radius + 1),
                from.Y + _rng.Next(-radius, radius + 1));
            if (!MapGrid.InBounds(c))
                continue;
            ref var cell = ref gs.Map.CellAt(c);
            if (!cell.IsEmpty || cell.HasTree)
                continue;
            if (gs.Map.FindNearestTree(c, 3) == null)
                continue;
            return c;
        }
        return null;
    }
}
