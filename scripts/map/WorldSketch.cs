using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 128² 内存草图（世界生成第一步，仅存在于生成期）：先在低分辨率上定宏观大势，
/// 再由 WorldGenerator 上采样映射到 1025² 顶点高度场。步骤：
/// ① 趋势场——西北高东南低的对角线性梯度 + 低幅 fBm 平原缓起伏；
/// ② 主峰——西北山区带内撒 1~2 座极高主峰（角向谐波破圆锥对称，成山汇），鹤立鸡群；
/// ③ 普通峰点——西北半包围带撒随机高度峰点，高斯锥取高叠加；
/// ④ 山脊——近邻峰对之间连线抬脊（鞍部下凹 + 沿脊起伏），群山连绵不成孤包；
/// ⑤ 低矮丘陵——按「远离既有峰群」选点成簇撒布（东北/西南/中部/东南都落），带两道坡度守卫；
/// ⑥ 草图级水力侵蚀收尾；
/// ⑦ 示意河网——在草图上跑一遍轻量 FlowRouter+RiverNetwork，仅供新游戏预览画蓝线。
/// 批次六十九：草图不再规划水系实体（河湖改在成品地形上按真实汇流求解，见 RiverGenerator），
/// 草图只负责山形；示意河网只画预览、不参与地形，且放在最后执行（不消耗影响地形的 rng）。
/// 坐标单位=草图格（1 格 = SketchScale 米），高度单位=米。
/// </summary>
public class WorldSketch
{
    private const int S = TerrainConfig.SketchSize;
    private const float Scale = TerrainConfig.SketchScale; // 草图格 → 米

    /// <summary>草图高度（米，行主序 y*S+x）。</summary>
    public float[] H;

    /// <summary>峰点（草图坐标 + 峰高 + 高斯锥半径草图格数）：山脊连接与丘陵避让共用。</summary>
    public List<(Vector2 pos, float h, float r)> Peaks = new();

    /// <summary>主峰（草图坐标 + 目标峰高 + 半径米）：WorldGenerator 侵蚀后据此二次隆升补回削顶。</summary>
    public List<(Vector2 pos, float h, float rMeters)> PrimaryPeaks = new();

    /// <summary>示意河网（草图格点列）：仅供新游戏预览画蓝线；与成品水系同源算法，但分辨率仅 8m/格。</summary>
    public List<List<Vector2I>> PreviewRivers = new();

    /// <summary>构建草图：按 ①→⑦ 顺序执行（纯内存数据，可在后台线程运行）。</summary>
    public static WorldSketch Build(Random rng)
    {
        var sk = new WorldSketch { H = new float[S * S] };
        sk.LayTrendAndPlain(rng);
        sk.ScatterPrimaryPeaks(rng);
        sk.ScatterPeaks(rng);
        sk.LinkRidges(rng);
        sk.ScatterLowHills(rng);
        // 草图级侵蚀：小规模水滴冲刷宏观形态（笔刷半径 1，分辨率低无需摊开）
        HydraulicEroder.Erode(sk.H, S, TerrainConfig.ErodeDropletsSketch, 1, rng);
        sk.TracePreviewRivers(rng); // ⑦ 示意河网：放在最后，不消耗影响地形的 rng
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

    // ---- ② 主峰（一两张王牌，突破普通峰高上限）----

    /// <summary>在山区带内撒主峰：拒绝采样（带内、避中心圆、主峰间留 PrimaryPeakMinSeparation），
    /// 峰高按 PrimaryPeakHeight 抽取并<b>超填</b> PrimaryPeakOverbuild——侵蚀会削顶，
    /// 由 WorldGenerator 在侵蚀后按 PrimaryPeaks 二次隆升补回。
    /// 形体用「角向谐波高斯锥」（RaiseLobedCone）：半径随方位角做 3 阶 + 2 阶谐波调制，
    /// 长出山脊凸出与沟谷凹入，不再是俯视图里一眼假的完美圆锥。</summary>
    private void ScatterPrimaryPeaks(Random rng)
    {
        float bandCells = TerrainConfig.MountainBandDepth / Scale;
        float exclCells = TerrainConfig.CenterExclusionRadius / Scale;
        float sepCells = TerrainConfig.PrimaryPeakMinSeparation / Scale;
        var center = new Vector2(S / 2f, S / 2f);

        for (int i = 0; i < TerrainConfig.PrimaryPeakCount; i++)
        {
            float rMeters = Mathf.Lerp(TerrainConfig.PrimaryPeakRadiusMin, TerrainConfig.PrimaryPeakRadiusMax,
                (float)rng.NextDouble());
            float rCells = rMeters / Scale;
            float margin = rCells * TerrainConfig.PeakEdgeMarginFactor;

            Vector2 pos = default;
            bool ok = false;
            for (int tries = 0; tries < 160 && !ok; tries++)
            {
                pos = new Vector2(
                    margin + (float)rng.NextDouble() * (S - 2 * margin),
                    margin + (float)rng.NextDouble() * (S - 2 * margin));
                ok = Math.Min(pos.X, pos.Y) < bandCells
                    && pos.DistanceTo(center) > exclCells
                    && FarFromPrimaryPeaks(pos, sepCells);
            }
            if (!ok)
                continue; // 采不中即放弃（数量随缘，不硬凑）

            float peakH = Mathf.Lerp(TerrainConfig.PrimaryPeakHeightMin,
                    TerrainConfig.PrimaryPeakHeightMax, (float)rng.NextDouble())
                + TerrainConfig.PrimaryPeakOverbuild;

            Peaks.Add((pos, peakH, rCells)); // 入峰群：参与连脊，主峰与群山相连成山汇
            PrimaryPeaks.Add((pos, peakH - TerrainConfig.PrimaryPeakOverbuild, rMeters));
            double phase3 = rng.NextDouble() * Math.PI * 2;
            double phase2 = rng.NextDouble() * Math.PI * 2;
            RaiseLobedCone(pos, peakH, rCells, phase3, phase2);
        }
    }

    private bool FarFromPrimaryPeaks(Vector2 pos, float sepCells)
    {
        foreach (var (p, _, _) in PrimaryPeaks)
        {
            if (p.DistanceTo(pos) < sepCells)
                return false;
        }
        return true;
    }

    /// <summary>角向谐波高斯锥：有效半径按方位角做 3 阶 + 2 阶谐波调制（幅度 PrimaryPeakLobeAmp），
    /// 衰减 exp(-3(d/lobeR)²)——山体沿山脊伸得更远、在沟谷方向收得更早，
    /// 俯视轮廓不再是圆；取高不叠加。</summary>
    private void RaiseLobedCone(Vector2 pos, float peakH, float rCells, double phase3, double phase2)
    {
        float lobe = TerrainConfig.PrimaryPeakLobeAmp;
        int r = Mathf.CeilToInt(rCells * (1f + lobe));
        for (int oy = -r; oy <= r; oy++)
        {
            for (int ox = -r; ox <= r; ox++)
            {
                int px = (int)pos.X + ox, py = (int)pos.Y + oy;
                if (px < 0 || py < 0 || px >= S || py >= S)
                    continue;
                float dist = new Vector2(ox, oy).Length();
                double theta = Math.Atan2(oy, ox);
                float lobeR = rCells * (1f + lobe * (0.6f * (float)Math.Sin(3 * theta + phase3)
                                                   + 0.4f * (float)Math.Sin(2 * theta + phase2)));
                if (dist > lobeR)
                    continue;
                float d = dist / lobeR;
                float hh = peakH * MathF.Exp(-3f * d * d);
                if (H[py * S + px] < hh)
                    H[py * S + px] = hh;
            }
        }
    }

    // ---- ③ 普通峰点（西北半包围带，离图缘留边）----

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
            Peaks.Add((pos, peakH, rCells));
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
        foreach (var (p, _, _) in Peaks)
            best = Math.Min(best, p.DistanceTo(pos));
        return best;
    }

    /// <summary>某点受既有峰群的影响强度（0~1）：取所有峰高斯包络的最大值。
    /// 丘陵选点据此避让——「远离峰群」比旧判据「东南象限」更贴近真实地貌，
    /// 于是东北/西南/中部只要有空档都能落小山。</summary>
    private float PeakInfluence(Vector2 pos)
    {
        float best = 0f;
        foreach (var (p, _, r) in Peaks)
        {
            float d = p.DistanceTo(pos) / Math.Max(0.001f, r);
            if (d > 1f)
                continue;
            float v = MathF.Exp(-3f * d * d);
            if (v > best)
                best = v;
        }
        return best;
    }

    // ---- ④ 山脊连接 ----

    /// <summary>近邻峰对之间连脊：脊高两端峰高插值、中段鞍部下凹、沿脊正弦起伏；
    /// 余弦横截面取高叠加——峰点由脊串联成连绵山脉，不再是孤立土包。</summary>
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
    private void RaiseRidge((Vector2 pos, float h, float r) a, (Vector2 pos, float h, float r) b, Random rng)
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

    // ---- ⑤ 低矮丘陵（全图点缀：东北/西南/中部/东南皆可，成簇出现）----

    /// <summary>成簇撒低矮丘陵：先选组心（避开既有峰群 PeakInfluence、避中心圆、离图缘留边），
    /// 再在组内散布 2~4 座小山——天然丘陵多半成群，均匀撒点反而不像。
    /// 每座山落点前过两道守卫：① 现状坡角 ≤ LowHillMaxSiteSlopeDeg（不在陡坡上堆锥）；
    /// ② 高宽比 ≤ LowHillMaxAspect（矮胖不尖刺，不超安息角）。</summary>
    private void ScatterLowHills(Random rng)
    {
        int want = TerrainConfig.LowHillCountMin
            + rng.Next(TerrainConfig.LowHillCountMax - TerrainConfig.LowHillCountMin + 1);
        int clusters = TerrainConfig.LowHillClusterMin
            + rng.Next(TerrainConfig.LowHillClusterMax - TerrainConfig.LowHillClusterMin + 1);
        float exclCells = TerrainConfig.CenterExclusionRadius / Scale;
        float spreadCells = TerrainConfig.LowHillClusterSpread / Scale;
        var center = new Vector2(S / 2f, S / 2f);
        int placed = 0;

        for (int c = 0; c < clusters && placed < want; c++)
        {
            // 组心：受峰群影响弱、避中心圆、离图缘留边
            Vector2 hub = default;
            bool ok = false;
            for (int tries = 0; tries < 80 && !ok; tries++)
            {
                hub = new Vector2(4 + (float)rng.NextDouble() * (S - 8), 4 + (float)rng.NextDouble() * (S - 8));
                ok = PeakInfluence(hub) < TerrainConfig.LowHillMaxPeakInfluence
                    && hub.DistanceTo(center) > exclCells;
            }
            if (!ok)
                continue;

            int groupSize = Math.Min(want - placed, 2 + rng.Next(3)); // 每组 2~4 座
            for (int i = 0; i < groupSize; i++)
            {
                Vector2 pos = hub;
                if (i > 0)
                {
                    double a = rng.NextDouble() * Math.PI * 2;
                    float d = (float)rng.NextDouble() * spreadCells;
                    pos = hub + new Vector2(d * (float)Math.Cos(a), d * (float)Math.Sin(a));
                }
                if (pos.X < 0 || pos.Y < 0 || pos.X >= S || pos.Y >= S)
                    continue;

                float rMeters = Mathf.Lerp(TerrainConfig.LowHillRadiusMin, TerrainConfig.LowHillRadiusMax,
                    (float)rng.NextDouble());
                float rCells = rMeters / Scale;
                if (SiteSlopeDeg(pos) > TerrainConfig.LowHillMaxSiteSlopeDeg)
                    continue; // 现状已是陡坡：不堆锥（免形成超安息角的孤立尖刺）

                float hillH = Mathf.Lerp(TerrainConfig.LowHillHeightMin, TerrainConfig.LowHillHeightMax,
                    (float)rng.NextDouble());
                hillH = Mathf.Min(hillH, rMeters * TerrainConfig.LowHillMaxAspect); // 高宽比守卫
                RaiseGaussianCone(pos, hillH, rCells);
                placed++;
            }
        }
    }

    /// <summary>落点现状坡角（度）：取四邻高差最大值按 SketchScale 米换算。</summary>
    private float SiteSlopeDeg(Vector2 pos)
    {
        int x = Math.Clamp((int)pos.X, 1, S - 2), y = Math.Clamp((int)pos.Y, 1, S - 2);
        float h0 = H[y * S + x];
        float drop = Mathf.Max(
            Mathf.Max(Mathf.Abs(H[y * S + x + 1] - h0), Mathf.Abs(H[y * S + x - 1] - h0)),
            Mathf.Max(Mathf.Abs(H[(y + 1) * S + x] - h0), Mathf.Abs(H[(y - 1) * S + x] - h0)));
        return Mathf.RadToDeg(Mathf.Atan(drop / Scale));
    }

    // ---- ⑦ 示意河网（仅供预览画线）----

    /// <summary>在草图上跑一遍轻量 FlowRouter + RiverNetwork（128²，8m/格），得到示意河网。
    /// 成品水系在 512² 成品地形上另算（见 WorldGenerator），此处只是宏观骨架示意，
    /// 但同源算法保证「大河大致在这些位置」。放在 Build 最后执行，不消耗影响地形的 rng。</summary>
    private void TracePreviewRivers(Random rng)
    {
        var flow = FlowRouter.Build(H, S, Scale, 1);
        var rivers = RiverNetwork.Build(flow, SampleH, rng);
        foreach (var river in rivers)
        {
            var pts = new List<Vector2I>();
            int lastX = -1, lastY = -1;
            foreach (var p in river.Points)
            {
                // RiverNetwork 以「基础场格」为单位输出，草图基础场即 128² 网格，可直接取整
                int px = (int)p.X, py = (int)p.Y;
                if (px < 0 || py < 0 || px >= S || py >= S)
                    continue;
                if (px == lastX && py == lastY)
                    continue; // 低分辨率下折线点常落在同一格：去重
                lastX = px; lastY = py;
                pts.Add(new Vector2I(px, py));
            }
            if (pts.Count > 1)
                PreviewRivers.Add(pts);
        }
    }

    /// <summary>草图高度双线性采样（连续格坐标，格心为 x+0.5）：RiverNetwork 蛇曲吸附用。</summary>
    private float SampleH(float cx, float cy)
    {
        float fx = cx - 0.5f, fy = cy - 0.5f;
        int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
        float tx = fx - ix, ty = fy - iy;
        int x0 = Math.Clamp(ix, 0, S - 1), x1 = Math.Clamp(ix + 1, 0, S - 1);
        int y0 = Math.Clamp(iy, 0, S - 1), y1 = Math.Clamp(iy + 1, 0, S - 1);
        float a = H[y0 * S + x0], b = H[y0 * S + x1];
        float c = H[y1 * S + x0], d = H[y1 * S + x1];
        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
    }
}
