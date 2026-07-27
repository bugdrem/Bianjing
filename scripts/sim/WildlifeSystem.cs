using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 野生动物系统：每日小范围游走（倾向树林、远离人口区，避免同格堆叠）；
/// 每月树林边随机刷新 → 繁育 → 自然减员。
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

    /// <summary>每日游走：每只动物在 ±1 格内挑最宜栖落脚点，已被同类占用的格子不去（防堆叠）。</summary>
    public void TickDay(GameState gs)
    {
        if (gs.Animals.Count == 0)
            return;

        var occupied = BuildOccupied(gs);
        bool changed = false;

        foreach (var a in gs.Animals.Values)
        {
            var cur = new Vector2I(a.X, a.Y);
            var next = BestNearbyCell(gs, cur, 1, occupied);
            if (next == null || next.Value == cur)
                continue;
            occupied.Remove(cur);
            occupied.Add(next.Value);
            a.X = next.Value.X;
            a.Y = next.Value.Y;
            changed = true;
        }

        if (changed)
            EventBus.RaiseWildlifeChanged();
    }

    /// <summary>每月大事：刷新补充 → 增龄 → 繁育 → 自然减员。</summary>
    public void TickMonth(GameState gs)
    {
        bool changed = false;

        // 随机刷新：种群未满时树林边缘补充新个体
        if (gs.Animals.Count < MaxAnimals && _rng.NextDouble() < 0.6)
            changed |= SpawnNearForest(gs);

        var occupied = BuildOccupied(gs);
        var deaths = new List<int>();
        var newborns = new List<Vector2I>();
        foreach (var a in gs.Animals.Values)
        {
            a.AgeMonths++;

            // 繁育：成年个体在种群未满时就近产仔
            if (a.AgeMonths >= 6 && gs.Animals.Count + newborns.Count < MaxAnimals
                && _rng.NextDouble() < BreedChance)
            {
                var spot = BestNearbyCell(gs, new Vector2I(a.X, a.Y), 1, occupied);
                if (spot != null)
                {
                    newborns.Add(spot.Value);
                    occupied.Add(spot.Value);
                }
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

    /// <summary>当前全部动物占用的格子集合。</summary>
    private static HashSet<Vector2I> BuildOccupied(GameState gs)
    {
        var set = new HashSet<Vector2I>();
        foreach (var a in gs.Animals.Values)
            set.Add(new Vector2I(a.X, a.Y));
        return set;
    }

    private bool SpawnNearForest(GameState gs)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var c = new Vector2I(_rng.Next(MapGrid.Size), _rng.Next(MapGrid.Size));
            ref var cell = ref gs.Map.CellAt(c);
            if (!cell.IsEmpty || cell.HasTree)
                continue;
            // 栖息地约束：2 格内有树林，且避开人口区刷新
            if (gs.Map.FindNearestTree(c, 2) == null || CrowdScore(gs, c, 4) > 0)
                continue;
            gs.AddAnimal(c);
            return true;
        }
        return false;
    }

    /// <summary>人口密集度：周围道路/建筑格数（动物避开人烟）。</summary>
    private static int CrowdScore(GameState gs, Vector2I c, int radius)
    {
        int score = 0;
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                var p = new Vector2I(c.X + dx, c.Y + dy);
                if (!MapGrid.InBounds(p))
                    continue;
                ref var cell = ref gs.Map.CellAt(p);
                if (cell.HasRoad || cell.BuildingId >= 0)
                    score++;
            }
        }
        return score;
    }

    /// <summary>
    /// 附近挑最宜栖的落脚点：候选中优先选人烟少、离树林近的格子（不离开树林 3 格，保持栖息地）；
    /// 已被其他动物占用的格子跳过，避免全群收敛堆叠。
    /// </summary>
    private Vector2I? BestNearbyCell(GameState gs, Vector2I from, int radius, HashSet<Vector2I> occupied)
    {
        Vector2I? best = null;
        int bestScore = int.MaxValue;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var c = new Vector2I(
                from.X + _rng.Next(-radius, radius + 1),
                from.Y + _rng.Next(-radius, radius + 1));
            if (!MapGrid.InBounds(c) || (c != from && occupied.Contains(c)))
                continue;
            ref var cell = ref gs.Map.CellAt(c);
            if (!cell.IsEmpty || cell.HasTree)
                continue;
            var tree = gs.Map.FindNearestTree(c, 3);
            if (tree == null)
                continue;
            int treeDist = Math.Max(Math.Abs(tree.Value.X - c.X), Math.Abs(tree.Value.Y - c.Y));
            int score = CrowdScore(gs, c, 3) * 4 + treeDist; // 人烟惩罚权重远大于树林距离
            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }
}
