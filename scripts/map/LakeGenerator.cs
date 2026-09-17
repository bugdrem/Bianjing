using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 一处湖泊：着水格集合 + 统一湖面高（米）。尚未写入网格，由 RiverGenerator 落盘。
/// </summary>
public sealed class LakeShape
{
    /// <summary>湖面覆盖的 1m 格（4-连通，形状贴合地形而非圆形）。</summary>
    public List<Vector2I> Cells = new();

    /// <summary>湖面海拔（米）：= 湖内最高地形（最低前沿洪水填充的终止高程）。</summary>
    public float Level;

    /// <summary>种子格（选址湖的生长起点 / 洼地塘的最低点）：湖间最小间距以它为基准。</summary>
    public Vector2I Seed;

    /// <summary>是否为天然洼地塘（FlowRouter 填洼量揭示的天然盆底）。</summary>
    public bool NaturalPond;
}

/// <summary>
/// 湖泊生成（批次六十九新增）：两类湖，共同构成「大小不一、随机分布」的湖群。
/// ① <b>天然洼地塘</b>：FlowRouter 的填洼量 Depression &gt; PondMinFillDepth 的连通块就是天然盆底，
///    湖面取该块的溢出高程（块内最小填充高），再在 1m 分辨率上按「地形低于湖面」漫灌成塘——
///    洼地塘是侵蚀自己挖出来的，位置天然合理。
/// ② <b>选址湖</b>：大/中/小三档随机选址，拒绝采样要求「离图缘、彼此间隔、避开城心、局部低洼平坦」，
///    再以<b>最低前沿洪水填充</b>从种子点向外生长到目标格数——湖面随前沿抬升，
///    形状贴着地形长（沿谷地伸出湾汊、遇高地绕开），而不是一个正圆。
/// ③ 总面积上限 LakeTotalAreaCap：湖再多也不淹掉城建腹地。
/// 纯数据运算，可在后台线程运行。
/// </summary>
public static class LakeGenerator
{
    /// <summary>生成湖群。</summary>
    /// <param name="map">地图（读地形与既有水面）。</param>
    /// <param name="flow">FlowRouter 流向场（提供天然洼地）。</param>
    /// <param name="rng">随机源。</param>
    public static List<LakeShape> Build(MapGrid map, FlowField flow, Random rng)
    {
        var lakes = new List<LakeShape>();
        long budget = (long)(MapGrid.Size * MapGrid.Size * WaterConfig.LakeTotalAreaCap);
        long used = 0;

        // ① 天然洼地塘（先放：不占选址预算之外的名额，但计入总面积）
        foreach (var pond in FindPonds(map, flow))
        {
            if (used + pond.Cells.Count > budget)
                break;
            used += pond.Cells.Count;
            lakes.Add(pond);
        }

        // ② 选址湖：1 座大湖 + 1~2 座中湖 + 若干小塘
        int count = WaterConfig.LakeCountMin
            + rng.Next(WaterConfig.LakeCountMax - WaterConfig.LakeCountMin + 1);
        var mark = new int[MapGrid.Size * MapGrid.Size];
        int stamp = 0;

        for (int i = 0; i < count; i++)
        {
            (float rMin, float rMax) = i == 0
                ? (WaterConfig.LakeRadiusLargeMin, WaterConfig.LakeRadiusLargeMax)
                : i <= 2
                    ? (WaterConfig.LakeRadiusMediumMin, WaterConfig.LakeRadiusMediumMax)
                    : (WaterConfig.LakeRadiusSmallMin, WaterConfig.LakeRadiusSmallMax);

            var lake = TrySite(map, lakes, rMin, rMax, mark, ++stamp, rng, budget - used);
            if (lake == null)
                continue;
            used += lake.Cells.Count;
            lakes.Add(lake);
        }

        return lakes;
    }

    // ---- ① 天然洼地塘 ----

    /// <summary>在路由分辨率上找填洼量超阈值的连通块（4-连通），按块内最小填充高（溢出高程）漫灌成塘。</summary>
    private static List<LakeShape> FindPonds(MapGrid map, FlowField flow)
    {
        var result = new List<LakeShape>();
        int n = flow.Size * flow.Size;
        int minRouteCells = Math.Max(4, WaterConfig.PondMinCells / Math.Max(1, flow.Downsample * flow.Downsample));
        var seen = new bool[n];
        var comp = new List<int>();
        var stack = new Stack<int>();

        for (int seed = 0; seed < n; seed++)
        {
            if (seen[seed] || flow.Depression[seed] <= WaterConfig.PondMinFillDepth)
                continue;

            comp.Clear();
            stack.Clear();
            stack.Push(seed);
            seen[seed] = true;
            float spill = float.MaxValue;
            int lowCell = seed;
            float lowBase = float.MaxValue;

            while (stack.Count > 0)
            {
                int c = stack.Pop();
                comp.Add(c);
                if (flow.Filled[c] < spill)
                    spill = flow.Filled[c];
                if (flow.Base[c] < lowBase)
                {
                    lowBase = flow.Base[c];
                    lowCell = c;
                }
                int cx = flow.XOf(c), cy = flow.YOf(c);
                for (int k = 0; k < 4; k++)
                {
                    int dx = k == 0 ? 1 : k == 1 ? -1 : 0;
                    int dy = k == 2 ? 1 : k == 3 ? -1 : 0;
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= flow.Size || ny >= flow.Size)
                        continue;
                    int ni = ny * flow.Size + nx;
                    if (seen[ni] || flow.Depression[ni] <= WaterConfig.PondMinFillDepth)
                        continue;
                    seen[ni] = true;
                    stack.Push(ni);
                }
            }

            if (comp.Count < minRouteCells)
                continue;

            // 1m 分辨率漫灌：从块内最低点出发，地形低于溢出高程的连通格
            int sx = (int)((flow.XOf(lowCell) + 0.5f) * flow.Downsample);
            int sy = (int)((flow.YOf(lowCell) + 0.5f) * flow.Downsample);
            var seedCell = new Vector2I(
                Math.Clamp(sx, 0, MapGrid.Size - 1),
                Math.Clamp(sy, 0, MapGrid.Size - 1));
            var cells = FloodBelow(map, seedCell, spill);
            if (cells.Count < WaterConfig.PondMinCells || cells.Count > WaterConfig.PondMaxCells)
                continue;

            result.Add(new LakeShape { Cells = cells, Level = spill, Seed = seedCell, NaturalPond = true });
        }
        return result;
    }

    /// <summary>从种子格 4-连通漫灌：只收「地形中心高 &lt; level」的格（自然岸线，不强制成圆）。</summary>
    private static List<Vector2I> FloodBelow(MapGrid map, Vector2I seed, float level)
    {
        var cells = new List<Vector2I>();
        var seen = new HashSet<int>();
        var stack = new Stack<Vector2I>();
        stack.Push(seed);
        seen.Add(seed.Y * MapGrid.Size + seed.X);

        while (stack.Count > 0 && cells.Count < WaterConfig.PondMaxCells)
        {
            var c = stack.Pop();
            if (map.Height.CellCenterH(c) >= level)
                continue;
            cells.Add(c);
            for (int k = 0; k < 4; k++)
            {
                int dx = k == 0 ? 1 : k == 1 ? -1 : 0;
                int dy = k == 2 ? 1 : k == 3 ? -1 : 0;
                var n = new Vector2I(c.X + dx, c.Y + dy);
                if (!MapGrid.InBounds(n))
                    continue;
                int ni = n.Y * MapGrid.Size + n.X;
                if (!seen.Add(ni))
                    continue;
                stack.Push(n);
            }
        }
        return cells;
    }

    // ---- ② 选址湖 ----

    /// <summary>拒绝采样选点 + 最低前沿洪水填充成湖。采不中/填不出即放弃该座。</summary>
    private static LakeShape TrySite(MapGrid map, List<LakeShape> existing,
        float rMin, float rMax, int[] mark, int stamp, Random rng, long budget)
    {
        var hf = map.Height;
        float centerExcl = TerrainConfig.CenterExclusionRadius * 0.7f;
        var center = new Vector2(MapGrid.Size / 2f, MapGrid.Size / 2f);

        for (int tries = 0; tries < 90; tries++)
        {
            float r = Mathf.Lerp(rMin, rMax, (float)rng.NextDouble());
            var pos = new Vector2(
                WaterConfig.LakeEdgeMargin + (float)rng.NextDouble() * (MapGrid.Size - 2 * WaterConfig.LakeEdgeMargin),
                WaterConfig.LakeEdgeMargin + (float)rng.NextDouble() * (MapGrid.Size - 2 * WaterConfig.LakeEdgeMargin));

            if (pos.DistanceTo(center) < centerExcl)
                continue; // 不淹城心

            bool tooClose = false;
            foreach (var lk in existing)
            {
                if (pos.DistanceTo(new Vector2(lk.Seed.X, lk.Seed.Y)) < WaterConfig.LakeMinSeparation)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose)
                continue;

            if (!IsBasinLike(hf, pos, r, out float seedH))
                continue; // 局部不够低洼平坦：换点

            var seed = new Vector2I((int)pos.X, (int)pos.Y);
            int target = (int)(Math.PI * r * r * WaterConfig.LakeFillRatio);
            long allow = Math.Min(budget, target);
            if (allow < WaterConfig.PondMinCells)
                return null; // 总面积预算已尽

            var (cells, level) = FloodLowestFrontier(map, seed, (int)allow, r * WaterConfig.LakeMaxReachFactor, mark, stamp);
            if (cells.Count < WaterConfig.PondMinCells)
                continue;

            return new LakeShape { Cells = cells, Level = level, Seed = seed, NaturalPond = false };
        }
        return null;
    }

    /// <summary>局部是否「低洼平坦」：环上 16 点 + 中心的高差 ≤ LakeSiteMaxRelief，
    /// 且中心不高于环上最高点（是洼地而非坡面/山头）。</summary>
    private static bool IsBasinLike(HeightField hf, Vector2 pos, float r, out float seedH)
    {
        seedH = hf.SampleCell(pos.X, pos.Y);
        float min = seedH, max = seedH;
        for (int k = 0; k < 16; k++)
        {
            double a = k * Math.PI * 2 / 16;
            float h = hf.SampleCell(pos.X + r * (float)Math.Cos(a), pos.Y + r * (float)Math.Sin(a));
            if (h < min) min = h;
            if (h > max) max = h;
        }
        return max - min <= WaterConfig.LakeSiteMaxRelief && seedH <= max;
    }

    /// <summary>最低前沿洪水填充：优先队列按地形高升序出队，每次把当前最低的前沿格收进湖里，
    /// 湖面随之抬升到已收格的最高地形。结果 = 种子周围最低洼的 target 个连通格，
    /// 形状贴着地形长（遇高地绕开、沿谷地伸湾汊），而不是一个正圆。
    /// 两道闸门：地形高出种子点 LakeMaxDepth 即停（防湖面爬上山腰）；伸展超 maxReach 的格跳过。</summary>
    private static (List<Vector2I> cells, float level) FloodLowestFrontier(
        MapGrid map, Vector2I seed, int target, float maxReach, int[] mark, int stamp)
    {
        var hf = map.Height;
        var cells = new List<Vector2I>(target);
        var open = new PriorityQueue<int, float>(Math.Min(target * 4, 1 << 16));
        float seedH = hf.CellCenterH(seed);
        float maxH = seedH + WaterConfig.LakeMaxDepth;
        float reachSq = maxReach * maxReach;
        float level = seedH;

        int si = seed.Y * MapGrid.Size + seed.X;
        mark[si] = stamp;
        open.Enqueue(si, seedH);

        while (open.Count > 0 && cells.Count < target)
        {
            int idx = open.Dequeue();
            int cx = idx % MapGrid.Size, cy = idx / MapGrid.Size;
            float h = hf.CellCenterH(new Vector2I(cx, cy));
            if (h > maxH)
                break; // 前沿已爬到最大水深以上：湖面不能再涨，停
            float dx = cx - seed.X, dy = cy - seed.Y;
            if (dx * dx + dy * dy > reachSq)
                continue; // 伸太远：跳过此格（其邻格仍可能回头向内生长）

            cells.Add(new Vector2I(cx, cy));
            if (h > level)
                level = h;

            for (int k = 0; k < 4; k++)
            {
                int ox = k == 0 ? 1 : k == 1 ? -1 : 0;
                int oy = k == 2 ? 1 : k == 3 ? -1 : 0;
                int nx = cx + ox, ny = cy + oy;
                if (nx < 0 || ny < 0 || nx >= MapGrid.Size || ny >= MapGrid.Size)
                    continue;
                int ni = ny * MapGrid.Size + nx;
                if (mark[ni] == stamp)
                    continue;
                mark[ni] = stamp;
                open.Enqueue(ni, hf.CellCenterH(new Vector2I(nx, ny)));
            }
        }

        return (cells, level);
    }
}
