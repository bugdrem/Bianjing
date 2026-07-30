using System;
using Godot;

namespace Bianjing;

/// <summary>
/// 新地图山体生成：在若干随机山心处按径向衰减隆起整数台地（0 基准往上，最高 TerrainConfig.MaxMountainLayer 层）。
/// 山丘避开水面（河床不抬升）；生成后做一遍相邻层差平滑，避免出现无法通行的陡壁。
/// 只在新图时运行一次，位于河流之后、树木之前（山形定了才决定哪长树），高度随存档保存。
/// </summary>
public static class MountainGenerator
{
    // 山丘数量与半径（米）：几座缓丘散布图上，半径大则坡缓
    private const int MinPeaks = 3;
    private const int MaxPeaks = 6;
    private const int MinRadius = 60;
    private const int MaxRadius = 140;

    public static void Raise(MapGrid map, Random rng)
    {
        int peaks = MinPeaks + rng.Next(MaxPeaks - MinPeaks + 1);
        for (int i = 0; i < peaks; i++)
            RaisePeak(map, rng);

        SmoothCliffs(map); // 削平无法攀爬的陡壁，保证山坡整体可走/可依坡铺路
    }

    /// <summary>单座山丘：随机山心 + 半径，格到山心距离越近层数越高（余弦钟形衰减），叠加到现有高度取较大值。</summary>
    private static void RaisePeak(MapGrid map, Random rng)
    {
        int cx = rng.Next(MapGrid.Size);
        int cy = rng.Next(MapGrid.Size);
        int radius = MinRadius + rng.Next(MaxRadius - MinRadius + 1);
        int peakLayer = 4 + rng.Next(TerrainConfig.MaxMountainLayer - 3); // 4~MaxMountainLayer 层高

        for (int x = cx - radius; x <= cx + radius; x++)
        {
            for (int y = cy - radius; y <= cy + radius; y++)
            {
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref map.CellAt(c);
                if (cell.HasWater)
                    continue; // 河床不抬升，保持水系连通

                float dist = new Vector2(x - cx, y - cy).Length();
                if (dist > radius)
                    continue;
                // 余弦钟形：山心 1.0 → 山脚 0，边缘坡缓不突兀
                float t = 0.5f * (1f + Mathf.Cos(dist / radius * Mathf.Pi));
                int layer = Mathf.RoundToInt(peakLayer * t);
                if (layer > cell.Height)
                    cell.Height = layer;
            }
        }
    }

    /// <summary>削壁：反复把「比最矮邻居高出超过免爬层差」的格降到刚好可攀爬，直到全图无陡壁
    /// （每格层差 ≤ StepClimb 或坡角 ≤ MaxWalkSlopeDeg），使山坡处处可上，不会出现悬崖挡路。</summary>
    private static void SmoothCliffs(MapGrid map)
    {
        // 每格与四邻的最大容许层差：优先按免爬层差，坡度换算更宽松时用坡度上限对应的层差
        int maxDiff = MaxTraversableLayerDiff();
        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 64) // 收敛保护：整数场逐轮下降，必在有限轮内平息
        {
            changed = false;
            for (int x = 0; x < MapGrid.Size; x++)
            {
                for (int y = 0; y < MapGrid.Size; y++)
                {
                    var c = new Vector2I(x, y);
                    ref var cell = ref map.CellAt(c);
                    if (cell.Height == 0)
                        continue;
                    int minN = MinNeighborHeight(map, x, y);
                    if (cell.Height - minN > maxDiff)
                    {
                        cell.Height = minN + maxDiff; // 削到与最矮邻居恰好可攀爬
                        changed = true;
                    }
                }
            }
        }
    }

    /// <summary>四邻中的最低高度（含越界按 0），用于判断本格是否为陡壁。</summary>
    private static int MinNeighborHeight(MapGrid map, int x, int y)
    {
        int min = int.MaxValue;
        Span<Vector2I> dirs = stackalloc Vector2I[]
        {
            new(x - 1, y), new(x + 1, y), new(x, y - 1), new(x, y + 1),
        };
        foreach (var n in dirs)
            min = Math.Min(min, MapGrid.InBounds(n) ? map.CellAt(n).Height : 0);
        return min == int.MaxValue ? 0 : min;
    }

    /// <summary>最大可通行层差：从免爬层差起逐层放大，直到坡角超过上限的前一层。</summary>
    private static int MaxTraversableLayerDiff()
    {
        int d = TerrainConfig.StepClimb;
        while (TerrainConfig.SlopeDegForLayerDiff(d + 1) <= TerrainConfig.MaxWalkSlopeDeg)
            d++;
        return d;
    }
}
