using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 128² 内存草图（世界生成第一步，仅存在于生成期）：先在低分辨率上定宏观大势，
/// 再由 WorldGenerator 上采样映射到 1025² 顶点高度场。步骤：
/// ① 趋势场——西北高东南低的对角线性梯度 + 低幅 fBm 平原缓起伏；
/// ② 峰点——西北半包围带撒随机高度峰点（峰心距任一图缘 ≥ 峰半径，山体不贴图缘；避中心圆），高斯锥取高叠加；
/// ③ 山脊——近邻峰对之间连线抬脊（鞍部下凹 + 沿脊起伏），群山连绵不成孤包；
/// ④ 低矮独立山——山区带之外（中部/东南）撒零星山包，不连脊；
/// ⑤ 草图级水力侵蚀收尾。
/// 批次五十起草图不再规划河湖（不压谷/不压湖盆）——地形生成保持纯粹，
/// 水系由 RiverGenerator 在侵蚀完成的成品地形上循坡走线（只读地势）。
/// 坐标单位=草图格（1 格 = SketchScale 米），高度单位=米。
/// </summary>
public class WorldSketch
{
    private const int S = TerrainConfig.SketchSize;
    private const float Scale = TerrainConfig.SketchScale; // 草图格 → 米

    /// <summary>草图高度（米，行主序 y*S+x）。</summary>
    public float[] H;

    /// <summary>峰点（草图坐标 + 峰高）：供 RiverGenerator 在成品地形上取河源（峰间鞍部）。</summary>
    public List<(Vector2 pos, float h)> Peaks = new();

    /// <summary>构建草图：按 ①→⑤ 顺序执行（纯内存数据，可在后台线程运行）。</summary>
    public static WorldSketch Build(Random rng)
    {
        var sk = new WorldSketch { H = new float[S * S] };
        sk.LayTrendAndPlain(rng);
        sk.ScatterPeaks(rng);
        sk.LinkRidges(rng);
        sk.ScatterLowHills(rng);
        // 草图级侵蚀：小规模水滴冲刷宏观形态（笔刷半径 1，分辨率低无需摊开）
        HydraulicEroder.Erode(sk.H, S, TerrainConfig.ErodeDropletsSketch, 1, rng);
        return sk;
    }

    // ---- ① 趋势场 + 平原缓起伏 ----

    /// <summary>对角线性趋势（西北角 TrendHeight → 东南角 0）+ fBm 平原起伏（替代旧缓丘）。</summary>
    private void LayTrendAndPlain(Random rng)
    {
        var fbm = new ValueNoise(rng, TerrainConfig.PlainFbmWaveMeters / (int)Scale, 2, S);
        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                // t：西北角 1 → 东南角 0（对角归一）
                float t = 1f - (x + y) / (2f * (S - 1));
                float plain = (fbm.Sample(x, y) - 0.5f) * 2f * TerrainConfig.PlainFbmAmp;
                H[y * S + x] = t * TerrainConfig.TrendHeight + plain;
            }
        }
    }

    // ---- ② 峰点撒布（西北半包围带，离图缘留边）----

    /// <summary>在山区带内撒峰点（拒绝采样：贴西/北缘的带内、峰心距任一图缘 ≥ 峰半径×系数、
    /// 避中心圆、峰间留距），高斯锥取高叠加——「山体尽量不贴地图边缘」由边距保证。</summary>
    private void ScatterPeaks(Random rng)
    {
        int count = TerrainConfig.PeakCountMin + rng.Next(TerrainConfig.PeakCountMax - TerrainConfig.PeakCountMin + 1);
        float bandCells = TerrainConfig.MountainBandDepth / Scale;
        float exclCells = TerrainConfig.CenterExclusionRadius / Scale;
        var center = new Vector2(S / 2f, S / 2f);

        for (int i = 0; i < count; i++)
        {
            // 先抽半径再采位置：边距随半径走（高斯尾到图缘已衰至 ~5%）
            float rCells = Mathf.Lerp(TerrainConfig.PeakRadiusMin, TerrainConfig.PeakRadiusMax, (float)rng.NextDouble()) / Scale;
            float margin = rCells * TerrainConfig.PeakEdgeMarginFactor;

            Vector2 pos = default;
            bool ok = false;
            for (int tries = 0; tries < 60 && !ok; tries++)
            {
                pos = new Vector2(
                    margin + (float)rng.NextDouble() * (S - 2 * margin),
                    margin + (float)rng.NextDouble() * (S - 2 * margin));
                // 半包围带：到西缘或北缘的较小距离在带内；且避开地图中心圆、峰间留距
                ok = Math.Min(pos.X, pos.Y) < bandCells
                    && pos.DistanceTo(center) > exclCells
                    && NearestPeakDist(pos) > 6f; // 峰间至少 6 草图格（48m），防扎堆成一坨
            }
            if (!ok)
                continue; // 采不中即放弃该峰（数量随缘，不硬凑）

            float peakH = Mathf.Lerp(TerrainConfig.PeakHeightMin, TerrainConfig.PeakHeightMax, (float)rng.NextDouble());
            Peaks.Add((pos, peakH));
            RaiseGaussianCone(pos, peakH, rCells);
        }
    }

    /// <summary>高斯锥取高叠加：exp(-3(d/r)²)，r 处衰减到 ~5%，峰脚自然融入平原（峰/独立山共用）。</summary>
    private void RaiseGaussianCone(Vector2 pos, float peakH, float rCells)
    {
        int r = Mathf.CeilToInt(rCells);
        for (int oy = -r; oy <= r; oy++)
        {
            for (int ox = -r; ox <= r; ox++)
            {
                int px = (int)pos.X + ox, py = (int)pos.Y + oy;
                if (px < 0 || py < 0 || px >= S || py >= S)
                    continue;
                float d = new Vector2(ox, oy).Length() / rCells;
                if (d > 1f)
                    continue;
                float hh = peakH * MathF.Exp(-3f * d * d);
                if (H[py * S + px] < hh)
                    H[py * S + px] = hh;
            }
        }
    }

    private float NearestPeakDist(Vector2 pos)
    {
        float best = float.MaxValue;
        foreach (var (p, _) in Peaks)
            best = Math.Min(best, p.DistanceTo(pos));
        return best;
    }

    // ---- ③ 山脊连接 ----

    /// <summary>近邻峰对之间连脊：脊高两端峰高插值、中段鞍部下凹、沿脊正弦起伏；
    /// 余弦横截面取高叠加——峰点由脊串联成连绵山脉，不再是孤立土包。
    /// （草图已无河湖，不再做水域拦截判定。）</summary>
    private void LinkRidges(Random rng)
    {
        var linked = new HashSet<(int, int)>();
        for (int i = 0; i < Peaks.Count; i++)
        {
            // 距离升序取最近的 RidgeNeighborLinks 个峰
            var order = new List<int>();
            for (int j = 0; j < Peaks.Count; j++)
                if (j != i) order.Add(j);
            int self = i;
            order.Sort((a, b) => Peaks[a].pos.DistanceTo(Peaks[self].pos)
                .CompareTo(Peaks[b].pos.DistanceTo(Peaks[self].pos)));

            for (int k = 0; k < Math.Min(TerrainConfig.RidgeNeighborLinks, order.Count); k++)
            {
                int j = order[k];
                var key = (Math.Min(i, j), Math.Max(i, j));
                if (linked.Contains(key))
                    continue;
                linked.Add(key);
                RaiseRidge(Peaks[i], Peaks[j], rng);
            }
        }
    }

    /// <summary>沿峰对连线抬脊：逐点余弦横截面取高（脊心高 → 缘 0），
    /// 脊高 = 两端峰高插值 × 鞍部包络（两端 1 → 中点 SaddleFactor）× 正弦起伏。</summary>
    private void RaiseRidge((Vector2 pos, float h) a, (Vector2 pos, float h) b, Random rng)
    {
        float hwCells = TerrainConfig.RidgeHalfWidth / Scale;
        int hw = Mathf.CeilToInt(hwCells);
        float waveCells = TerrainConfig.RidgeUndulateWaveMeters / Scale;
        double phase = rng.NextDouble() * Math.PI * 2;
        int steps = Mathf.CeilToInt(a.pos.DistanceTo(b.pos) * 2); // 半格步进防漏点

        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)Math.Max(1, steps);
            var p = a.pos.Lerp(b.pos, t);
            // 鞍部包络：两端 1、中点 SaddleFactor（二次曲线）
            float saddle = TerrainConfig.RidgeSaddleFactor
                + (1 - TerrainConfig.RidgeSaddleFactor) * (2 * t - 1) * (2 * t - 1);
            float undulate = 1 + TerrainConfig.RidgeUndulateAmp
                * (float)Math.Sin(s * 0.5f * 2 * Math.PI / waveCells + phase);
            float ridgeH = Mathf.Lerp(a.h, b.h, t) * saddle * undulate;

            for (int oy = -hw; oy <= hw; oy++)
            {
                for (int ox = -hw; ox <= hw; ox++)
                {
                    int px = (int)p.X + ox, py = (int)p.Y + oy;
                    if (px < 0 || py < 0 || px >= S || py >= S)
                        continue;
                    float d = new Vector2(ox, oy).Length() / hwCells;
                    if (d > 1f)
                        continue;
                    // 余弦截面：脊心满高 → 缘 0；取高不叠加，与峰体/邻脊平滑衔接
                    float hh = ridgeH * (0.5f + 0.5f * MathF.Cos(MathF.PI * d));
                    if (H[py * S + px] < hh)
                        H[py * S + px] = hh;
                }
            }
        }
    }

    // ---- ④ 低矮独立山（中部/东南平原上的零星山包）----

    /// <summary>山区带之外撒低矮独立山：不入 Peaks（不连脊、不作河源），
    /// 拒绝采样避开山区带/中心圆/图缘，高斯锥叠加——平原不再一马平川，又不挡城建大局。</summary>
    private void ScatterLowHills(Random rng)
    {
        int count = TerrainConfig.LowHillCountMin + rng.Next(TerrainConfig.LowHillCountMax - TerrainConfig.LowHillCountMin + 1);
        float bandCells = TerrainConfig.MountainBandDepth / Scale;
        float exclCells = TerrainConfig.CenterExclusionRadius / Scale;
        var center = new Vector2(S / 2f, S / 2f);

        for (int i = 0; i < count; i++)
        {
            float rCells = Mathf.Lerp(TerrainConfig.LowHillRadiusMin, TerrainConfig.LowHillRadiusMax, (float)rng.NextDouble()) / Scale;
            float margin = rCells * TerrainConfig.PeakEdgeMarginFactor;

            Vector2 pos = default;
            bool ok = false;
            for (int tries = 0; tries < 60 && !ok; tries++)
            {
                pos = new Vector2(
                    margin + (float)rng.NextDouble() * (S - 2 * margin),
                    margin + (float)rng.NextDouble() * (S - 2 * margin));
                // 山区带之外（中部/东南才落）、避中心圆
                ok = Math.Min(pos.X, pos.Y) >= bandCells
                    && pos.DistanceTo(center) > exclCells;
            }
            if (!ok)
                continue;

            float hillH = Mathf.Lerp(TerrainConfig.LowHillHeightMin, TerrainConfig.LowHillHeightMax, (float)rng.NextDouble());
            RaiseGaussianCone(pos, hillH, rCells);
        }
    }
}
