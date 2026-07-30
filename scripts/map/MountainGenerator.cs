using System;
using Godot;

namespace Bianjing;

/// <summary>
/// 新地图地形成形（河后树前运行一次，高度随存档保存）：
/// ① 基准抬升——全部陆地抬到 TerrainConfig.BaseLayers（1 米），水面/河床保持 0 层（最低水面 0 米）；
/// ② 平原缓丘——value noise 高于阈值处隆起 0~HillAmplitudeLayers 层，再削壁保证缓丘处处可走（展示平原高低差）；
/// ③ 桂林石峰——若干孤峰柱（超椭圆剖面：顶平壁陡），随机高度/半径，陡壁天然不可攀（Traversable 拦截），
///    峰上由 TreeGenerator 保底落树。避水：石峰与缓丘都不占水面。
/// </summary>
public static class MountainGenerator
{
    public static void Raise(MapGrid map, Random rng)
    {
        RaiseBaseline(map);
        RaiseHills(map, rng);
        SmoothCliffs(map);      // 只平滑到此为止的"可走地形"（基准+缓丘），石峰在其后叠加、保留陡壁
        RaisePillars(map, rng);
    }

    /// <summary>① 基准抬升：陆地一律抬到基准层，水面/河床保持 0 层（水面即全图最低处）。</summary>
    private static void RaiseBaseline(MapGrid map)
    {
        for (int x = 0; x < MapGrid.Size; x++)
            for (int y = 0; y < MapGrid.Size; y++)
            {
                ref var cell = ref map.CellAt(x, y);
                if (!cell.HasWater)
                    cell.Height = TerrainConfig.BaseLayers;
            }
    }

    /// <summary>② 平原缓丘：双八度 value noise（与树木密度场同款手法），阈上部分映射为 1~HillAmplitudeLayers 层附加高度；
    /// 随机高低差由噪声天然提供，后续削壁把偶发陡沿压回可走坡度。</summary>
    private static void RaiseHills(MapGrid map, Random rng)
    {
        int wave = TerrainConfig.HillWavelength;
        var coarse = MakeLattice(rng, wave);
        var fine = MakeLattice(rng, wave / 2);

        for (int x = 0; x < MapGrid.Size; x++)
        {
            for (int y = 0; y < MapGrid.Size; y++)
            {
                ref var cell = ref map.CellAt(x, y);
                if (cell.HasWater)
                    continue; // 缓丘不占水面
                float v = 0.65f * SampleLattice(coarse, wave, x, y) + 0.35f * SampleLattice(fine, wave / 2, x, y);
                if (v <= TerrainConfig.HillThreshold)
                    continue;
                float t = (v - TerrainConfig.HillThreshold) / (1f - TerrainConfig.HillThreshold);
                int extra = Mathf.Clamp(Mathf.CeilToInt(t * TerrainConfig.HillAmplitudeLayers), 1, TerrainConfig.HillAmplitudeLayers);
                cell.Height = Math.Max(cell.Height, TerrainConfig.BaseLayers + extra);
            }
        }
    }

    /// <summary>③ 桂林石峰：孤峰柱散布平原（避水、避图缘），超椭圆剖面 1-(d/r)^k——k 越大顶越平、壁越陡；
    /// 峰壁层差远超坡度上限，村民不可攀（也不削壁），成为纯景观地标。</summary>
    private static void RaisePillars(MapGrid map, Random rng)
    {
        int count = TerrainConfig.MinPillars + rng.Next(TerrainConfig.MaxPillars - TerrainConfig.MinPillars + 1);
        for (int i = 0; i < count; i++)
        {
            int radius = TerrainConfig.PillarMinRadius + rng.Next(TerrainConfig.PillarMaxRadius - TerrainConfig.PillarMinRadius + 1);
            // 峰心避开图缘一圈（半径+8米），落在水面上则本座作罢（不强求，座数本就随机）
            int cx = radius + 8 + rng.Next(MapGrid.Size - 2 * (radius + 8));
            int cy = radius + 8 + rng.Next(MapGrid.Size - 2 * (radius + 8));
            if (map.CellAt(cx, cy).HasWater)
                continue;
            int peak = TerrainConfig.PillarMinLayers + rng.Next(TerrainConfig.PillarMaxLayers - TerrainConfig.PillarMinLayers + 1);

            for (int x = cx - radius; x <= cx + radius; x++)
            {
                for (int y = cy - radius; y <= cy + radius; y++)
                {
                    var c = new Vector2I(x, y);
                    if (!MapGrid.InBounds(c))
                        continue;
                    ref var cell = ref map.CellAt(c);
                    if (cell.HasWater)
                        continue; // 石峰不压水面
                    float d = new Vector2(x - cx, y - cy).Length() / radius;
                    if (d > 1f)
                        continue;
                    // 超椭圆剖面：中心 1 → 缘 0，PillarShapePower 越大顶部越平坦
                    float t = 1f - Mathf.Pow(d, TerrainConfig.PillarShapePower);
                    // 微随机起伏：峰顶不是死平的一块（±1 层噪声，保留"顶平"大观感）
                    int jitter = rng.Next(3) - 1;
                    int layer = cell.Height + Mathf.RoundToInt(peak * t) + jitter;
                    if (layer > cell.Height)
                        cell.Height = Math.Min(layer, TerrainConfig.MaxMountainLayer);
                }
            }
        }
    }

    /// <summary>削壁（只针对基准+缓丘阶段）：把「比最矮邻居高出超过可走层差」的格降到刚好可走，
    /// 保证平原缓丘处处可上；石峰其后叠加，不受削壁影响、保留陡壁。水面 0 层不动。</summary>
    private static void SmoothCliffs(MapGrid map)
    {
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
                    ref var cell = ref map.CellAt(x, y);
                    if (cell.HasWater || cell.Height <= TerrainConfig.BaseLayers)
                        continue; // 水面与基准平地无壁可削
                    int minN = MinNeighborHeight(map, x, y);
                    if (cell.Height - minN > maxDiff)
                    {
                        cell.Height = minN + maxDiff; // 削到与最矮邻居恰好可走
                        changed = true;
                    }
                }
            }
        }
    }

    /// <summary>四邻中的最低"陆地"高度：水面 0 层不参与（岸沿本就靠桥/渡衔接，不因河边削平缓丘）；
    /// 四邻全为水的孤格按基准层处理。</summary>
    private static int MinNeighborHeight(MapGrid map, int x, int y)
    {
        int min = int.MaxValue;
        Span<Vector2I> dirs = stackalloc Vector2I[]
        {
            new(x - 1, y), new(x + 1, y), new(x, y - 1), new(x, y + 1),
        };
        foreach (var n in dirs)
        {
            if (!MapGrid.InBounds(n))
                continue;
            ref var cell = ref map.CellAt(n);
            if (cell.HasWater)
                continue;
            min = Math.Min(min, cell.Height);
        }
        return min == int.MaxValue ? TerrainConfig.BaseLayers : min;
    }

    /// <summary>最大可走层差：从免爬层差起逐层放大，直到坡角超上限的前一层。</summary>
    private static int MaxTraversableLayerDiff()
    {
        int d = TerrainConfig.StepClimb;
        while (TerrainConfig.SlopeDegForLayerDiff(d + 1) <= TerrainConfig.MaxWalkSlopeDeg)
            d++;
        return d;
    }

    // ---- value noise 工具（与 TreeGenerator 同款手法，本文件自持一份免跨类耦合）----

    /// <summary>噪声格点阵（间距 cellSize 米，多留一圈防插值越界）。</summary>
    private static float[,] MakeLattice(Random rng, int cellSize)
    {
        int n = MapGrid.Size / cellSize + 2;
        var lattice = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                lattice[i, j] = (float)rng.NextDouble();
        return lattice;
    }

    /// <summary>平滑双线性采样（smoothstep 缓和格点棱线），返回 0-1。</summary>
    private static float SampleLattice(float[,] lattice, int cellSize, int x, int y)
    {
        float fx = (float)x / cellSize;
        float fy = (float)y / cellSize;
        int ix = (int)fx, iy = (int)fy;
        float tx = fx - ix, ty = fy - iy;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);

        float a = lattice[ix, iy], b = lattice[ix + 1, iy];
        float c = lattice[ix, iy + 1], d = lattice[ix + 1, iy + 1];
        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
    }
}
