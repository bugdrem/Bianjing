using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 野生动物系统：每日小范围游走（倾向树林、远离人口区，避免同格堆叠）；
/// 每月在没有动物的树林边按比例随机刷新 → 繁育 → 自然减员。
/// 动物无性别：只要全图在世两只以上即可繁育；种群总数上限与周围树林量成正比。
/// 只操作数据层 AnimalObj，渲染由 AnimalRenderer 负责，捕猎由居民代理触发。
/// </summary>
public class WildlifeSystem
{
    // 调参集中在 configs/WildlifeConfig，此处只留短名转发便于阅读
    private const int TreesPerAnimal = WildlifeConfig.TreesPerAnimal;
    private const int HardCap = WildlifeConfig.HardCap;
    private const float SpawnChancePerMonth = WildlifeConfig.SpawnChancePerMonth;
    private const int LonelyRadius = WildlifeConfig.LonelyRadius;
    private const float BreedChance = WildlifeConfig.BreedChance;
    private const float NaturalDeathChance = WildlifeConfig.NaturalDeathChance;

    private readonly Random _rng = new();

    /// <summary>种群上限：随当前树林总量（Plants 即树木）按比例推算，封顶硬上限。</summary>
    private static int MaxAnimals(GameState gs) => Math.Min(HardCap, gs.Plants.Count / TreesPerAnimal);

    /// <summary>新地图初始撒动物（约半数上限，至少一只以便后续繁育）。</summary>
    public void SeedInitial(GameState gs)
    {
        int target = Math.Max(1, MaxAnimals(gs) / 2);
        for (int i = 0; i < target; i++)
            SpawnNearForest(gs);
        EventBus.RaiseWildlifeChanged();
    }

    /// <summary>每日游走：每只动物在 ±4 米内挑最宜栖落脚点，已被同类占用的格子不去（防堆叠）。</summary>
    public void TickDay(GameState gs)
    {
        if (gs.Animals.Count == 0)
            return;

        var occupied = BuildOccupied(gs);
        bool changed = false;

        foreach (var a in gs.Animals.Values)
        {
            var cur = new Vector2I(a.X, a.Y);
            var next = BestNearbyCell(gs, cur, WildlifeConfig.WanderRadius, occupied);
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

    /// <summary>每月大事：无动物的树林边按比例刷新 → 增龄 → 繁育（≥两只、无性别）→ 自然减员；总数不超林地上限。</summary>
    public void TickMonth(GameState gs)
    {
        bool changed = false;
        int max = MaxAnimals(gs);

        // 随机刷新：种群未满时，在“附近没有动物”的树林边缘按比例补充新个体
        if (gs.Animals.Count < max && _rng.NextDouble() < SpawnChancePerMonth)
            changed |= SpawnNearForest(gs);

        var occupied = BuildOccupied(gs);
        var deaths = new List<int>();
        var newborns = new List<Vector2I>();
        bool canBreed = gs.Animals.Count >= 2; // 两只以上才可繁育（无性别）

        foreach (var a in gs.Animals.Values)
        {
            a.AgeMonths++;

            // 繁育：总数未到林地上限时就近产仔（不依赖个体性别/性状）
            if (canBreed && gs.Animals.Count + newborns.Count < max
                && _rng.NextDouble() < BreedChance)
            {
                var spot = BestNearbyCell(gs, new Vector2I(a.X, a.Y), 4, occupied);
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
        for (int attempt = 0; attempt < 24; attempt++)
        {
            var c = new Vector2I(_rng.Next(MapGrid.Size), _rng.Next(MapGrid.Size));
            ref var cell = ref gs.Map.CellAt(c);
            if (!cell.IsEmpty || cell.HasTree)
                continue;
            // 栖息地约束：8 米内有树林、避开人口区，且此处附近暂无动物（动物数 <1）
            if (gs.Map.FindNearestTree(c, 8) == null || CrowdScore(gs, c, 16) > 0)
                continue;
            if (HasAnimalNear(gs, c, LonelyRadius))
                continue;
            gs.AddAnimal(c);
            return true;
        }
        return false;
    }

    /// <summary>该格该半径内是否已有动物（判定“此处动物<1”）。</summary>
    private static bool HasAnimalNear(GameState gs, Vector2I c, int radius)
    {
        foreach (var a in gs.Animals.Values)
            if (Math.Max(Math.Abs(a.X - c.X), Math.Abs(a.Y - c.Y)) <= radius)
                return true;
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
    /// 附近挑最宜栖的落脚点：候选中优先选人烟少、离树林近的格子（不离开树林 12 米，保持栖息地）；
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
            var tree = gs.Map.FindNearestTree(c, 12);
            if (tree == null)
                continue;
            int treeDist = Math.Max(Math.Abs(tree.Value.X - c.X), Math.Abs(tree.Value.Y - c.Y));
            int score = CrowdScore(gs, c, 12) * 4 + treeDist; // 人烟惩罚权重远大于树林距离
            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }
}
