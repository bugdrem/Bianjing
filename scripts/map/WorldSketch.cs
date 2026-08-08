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
/// ⑤ 草图级水力侵蚀收尾；
/// ⑥ 河流定线（批次六十一起）：侵蚀后沿最陡下降走线，只存路径不压地形——
///    预览画线所见即所得，RiverGenerator 把定线放大为引导线在成品地形上循坡细化。
/// 批次五十~六十草图不规划河湖（不压谷/不压湖盆）——地形生成保持纯粹，
/// 水系走线只记录路径，不写高度场。
/// 坐标单位=草图格（1 格 = SketchScale 米），高度单位=米。
/// </summary>
public class WorldSketch
{
    private const int S = TerrainConfig.SketchSize;
    private const float Scale = TerrainConfig.SketchScale; // 草图格 → 米

    /// <summary>草图高度（米，行主序 y*S+x）。</summary>
    public float[] H;

    /// <summary>峰点（草图坐标 + 峰高）：山脊连接与河源定位（峰间鞍部）共用。</summary>
    public List<(Vector2 pos, float h)> Peaks = new();

    /// <summary>河流定线（草图坐标点列，8 邻连续）：供预览画线与全图循坡细化（×8 放大为引导线）。</summary>
    public List<List<Vector2I>> Rivers = new();

    /// <summary>已定河流路径格（全部河的并集）：后定之河踩线即汇流终止，防路径交叉。</summary>
    private readonly HashSet<int> _riverCells = new();

    /// <summary>构建草图：按 ①→⑥ 顺序执行（纯内存数据，可在后台线程运行）。</summary>
    public static WorldSketch Build(Random rng)
    {
        var sk = new WorldSketch { H = new float[S * S] };
        sk.LayTrendAndPlain(rng);
        sk.ScatterPeaks(rng);
        sk.LinkRidges(rng);
        sk.ScatterLowHills(rng);
        // 草图级侵蚀：小规模水滴冲刷宏观形态（笔刷半径 1，分辨率低无需摊开）
        HydraulicEroder.Erode(sk.H, S, TerrainConfig.ErodeDropletsSketch, 1, rng);
        sk.WalkRivers(rng); // ⑥ 河流定线：侵蚀完成后循坡走线（只存路径，不压地形）
        return sk;
    }

    // ---- ⑥ 河流定线（批次六十一：预览所见即所得，全图循坡细化）----

    /// <summary>河流定线：峰间鞍部取源（海拔降序，RiverCount 条），逐条循坡走线至图缘；
    /// 路径格互相视为水体（后河撞前河即汇流）。只存路径不压地形。</summary>
    private void WalkRivers(Random rng)
    {
        var sources = PickSources(rng);
        foreach (var src in sources)
        {
            var path = TracePath(src);
            if (path.Count < WaterConfig.MinRiverPathCells / (int)Scale)
                continue; // 过短弃线（同全图语义：不足 MinRiverPathCells 世界格）
            Rivers.Add(path);
            foreach (var p in path)
                _riverCells.Add(p.Y * S + p.X);
        }
    }

    /// <summary>河源候选：每峰与最近邻峰的中点（鞍部），按草图海拔降序取 RiverCount 条。</summary>
    private List<Vector2I> PickSources(Random rng)
    {
        var candidates = new List<Vector2>();
        for (int i = 0; i < Peaks.Count; i++)
        {
            float bestD = float.MaxValue;
            Vector2 mid = default;
            for (int j = 0; j < Peaks.Count; j++)
            {
                if (j == i) continue;
                float d = Peaks[j].pos.DistanceTo(Peaks[i].pos);
                if (d < bestD)
                {
                    bestD = d;
                    mid = (Peaks[i].pos + Peaks[j].pos) / 2f;
                }
            }
            // 候选间距 ≥ 40 世界格（同全图语义），防源点扎堆
            if (bestD < float.MaxValue && candidates.TrueForAll(s => s.DistanceTo(mid) > 40f / Scale))
                candidates.Add(mid);
        }
        candidates.Sort((a, b) =>
            H[(int)b.Y * S + (int)b.X].CompareTo(H[(int)a.Y * S + (int)a.X])); // 海拔高者先走（成干流）

        int count = Math.Min(candidates.Count,
            WaterConfig.RiverCountMin + rng.Next(WaterConfig.RiverCountMax - WaterConfig.RiverCountMin + 1));
        var sources = new List<Vector2I>();
        for (int i = 0; i < count; i++)
            sources.Add(new Vector2I(
                Math.Clamp((int)candidates[i].X, 1, S - 2),
                Math.Clamp((int)candidates[i].Y, 1, S - 2)));
        return sources;
    }

    /// <summary>单条走线（草图格中心高上循坡）：8 邻取未访问的最低格；洼地/平地向东南强制滑行
    /// （维持西北→东南大势，不设上限——必达图缘，河流不在中途断流）；踩到既有河线即汇流终止；出图缘终止。</summary>
    private List<Vector2I> TracePath(Vector2I source)
    {
        var path = new List<Vector2I>();
        var visited = new HashSet<int>();
        int x = source.X, y = source.Y;

        for (int step = 0; step < S * 4; step++)
        {
            if (x < 1 || y < 1 || x >= S - 1 || y >= S - 1)
            {
                path.Add(new Vector2I(x, y));
                break; // 出图缘：河口
            }
            var cur = new Vector2I(x, y);
            path.Add(cur);
            visited.Add(y * S + x);

            // 汇流检测（离源 4 步后才检——4 草图格≈32m，同全图 32 格语义，防源头自撞）
            if (step > 4 && _riverCells.Contains(y * S + x))
                break;

            // 8 邻中选未走过的最低格（前河路径格视为水体，不入候选）
            int bx = 0, by = 0;
            float bestH = float.MaxValue;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oy == 0) continue;
                    int nx = x + ox, ny = y + oy;
                    if (nx < 0 || ny < 0 || nx >= S || ny >= S
                        || visited.Contains(ny * S + nx) || _riverCells.Contains(ny * S + nx))
                        continue;
                    float h = H[ny * S + nx];
                    if (h < bestH)
                    {
                        bestH = h;
                        bx = ox; by = oy;
                    }
                }
            }
            float curH = H[y * S + x];
            if (bestH >= curH - 0.0001f)
            {
                // 洼地/平地：向东南强制滑行（东/东南/南三邻取最低），维持大势；不设上限——必达图缘
                bx = 1; by = 1;
                float hE = H[y * S + x + 1];
                float hS = H[(y + 1) * S + x];
                float hSE = H[(y + 1) * S + x + 1];
                if (hE <= hS && hE <= hSE && !visited.Contains(y * S + x + 1)) { bx = 1; by = 0; }
                else if (hS <= hSE && !visited.Contains((y + 1) * S + x)) { bx = 0; by = 1; }
            }
            if (bx == 0 && by == 0)
            {
                // 前向三邻全堵（visited/河线围困）：强行向东南，必达图缘
                bx = 1; by = 1;
                if (visited.Contains(y * S + x + 1) || _riverCells.Contains(y * S + x + 1)) { bx = 0; by = 1; }
                else if (visited.Contains((y + 1) * S + x) || _riverCells.Contains((y + 1) * S + x)) { bx = 1; by = 0; }
            }
            x += bx; y += by;
        }
        return path;
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
