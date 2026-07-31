using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 128² 内存草图（世界生成第一步，仅存在于生成期）：先在低分辨率上定宏观大势，
/// 再由 WorldGenerator 上采样映射到 1025² 顶点高度场。步骤：
/// ① 趋势场——西北高东南低的对角线性梯度 + 低幅 fBm 平原缓起伏；
/// ② 峰点——西北半包围带（贴西/北缘、避开中心圆）撒随机高度峰点，高斯锥取高叠加；
/// ③ 谷线河流——峰间鞍部取源，沿最陡下降走线（撞既有河即汇流），逐条压出 V 形河谷；
/// ④ 湖泊——干流途中随机凹陷成湖盘；
/// ⑤ 山脊——未被河湖拦截的近邻峰对之间连线抬脊（鞍部下凹 + 沿脊起伏），群山连绵不成孤包；
/// ⑥ 草图级水力侵蚀收尾。
/// 坐标单位=草图格（1 格 = SketchScale 米），高度单位=米。
/// </summary>
public class WorldSketch
{
    private const int S = TerrainConfig.SketchSize;
    private const float Scale = TerrainConfig.SketchScale; // 草图格 → 米

    /// <summary>草图高度（米，行主序 y*S+x）。</summary>
    public float[] H;

    /// <summary>河流点列（草图坐标，[0]=干流）：WorldGenerator 放大后落地成水格。</summary>
    public List<List<Vector2>> Rivers = new();

    /// <summary>湖泊（草图坐标圆心 + 半径草图格）：落地时用 CarveLake 生成谐波湖缘。</summary>
    public List<(Vector2 pos, float radius)> Lakes = new();

    // 生成期内部状态：全部水域采样点（点 + 拦截半宽，草图格），供撞河检测与脊线拦截
    private readonly List<(Vector2 p, float halfWidth)> _waterPts = new();
    private List<(Vector2 pos, float h)> _peaks = new();

    /// <summary>构建草图：按 ①→⑥ 顺序执行（纯内存数据，可在后台线程运行）。</summary>
    public static WorldSketch Build(Random rng)
    {
        var sk = new WorldSketch { H = new float[S * S] };
        sk.LayTrendAndPlain(rng);
        sk.ScatterPeaks(rng);
        sk.TraceRivers(rng);
        sk.PlaceLakes(rng);
        sk.LinkRidges(rng);
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

    // ---- ② 峰点撒布（西北半包围带）----

    /// <summary>在山区带内撒峰点（拒绝采样：贴西/北缘、避中心圆、峰间留距），高斯锥取高叠加。</summary>
    private void ScatterPeaks(Random rng)
    {
        int count = TerrainConfig.PeakCountMin + rng.Next(TerrainConfig.PeakCountMax - TerrainConfig.PeakCountMin + 1);
        float bandCells = TerrainConfig.MountainBandDepth / Scale;
        float exclCells = TerrainConfig.CenterExclusionRadius / Scale;
        var center = new Vector2(S / 2f, S / 2f);

        for (int i = 0; i < count; i++)
        {
            Vector2 pos = default;
            bool ok = false;
            for (int tries = 0; tries < 60 && !ok; tries++)
            {
                pos = new Vector2(3 + (float)rng.NextDouble() * (S - 6), 3 + (float)rng.NextDouble() * (S - 6));
                // 半包围带：到西缘或北缘的较小距离在带内；且避开地图中心圆
                ok = Math.Min(pos.X, pos.Y) < bandCells
                    && pos.DistanceTo(center) > exclCells
                    && NearestPeakDist(pos) > 6f; // 峰间至少 6 草图格（48m），防扎堆成一坨
            }
            if (!ok)
                continue; // 采不中即放弃该峰（数量随缘，不硬凑）

            float peakH = Mathf.Lerp(TerrainConfig.PeakHeightMin, TerrainConfig.PeakHeightMax, (float)rng.NextDouble());
            float rCells = Mathf.Lerp(TerrainConfig.PeakRadiusMin, TerrainConfig.PeakRadiusMax, (float)rng.NextDouble()) / Scale;
            _peaks.Add((pos, peakH));

            // 高斯锥取高叠加：exp(-3(d/r)²)，r 处衰减到 ~5%，峰脚自然融入平原
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
    }

    private float NearestPeakDist(Vector2 pos)
    {
        float best = float.MaxValue;
        foreach (var (p, _) in _peaks)
            best = Math.Min(best, p.DistanceTo(pos));
        return best;
    }

    // ---- ③ 谷线河流（最陡下降走线 + V 形压谷）----

    /// <summary>河源取峰间鞍部（近邻峰对中点），按源点海拔降序取若干条：
    /// 首条走到底成干流，后续撞既有河即汇流（树状水系天成）；每条走完立即压谷，
    /// 后走的支线会被已成型的河谷吸引，汇流更自然。</summary>
    private void TraceRivers(Random rng)
    {
        // 候选源：每峰与最近邻峰的中点（去重靠彼此距离），按该点当前海拔降序
        var sources = new List<Vector2>();
        for (int i = 0; i < _peaks.Count; i++)
        {
            var (pi, _) = _peaks[i];
            float bestD = float.MaxValue;
            Vector2 mid = default;
            for (int j = 0; j < _peaks.Count; j++)
            {
                if (j == i) continue;
                float d = _peaks[j].pos.DistanceTo(pi);
                if (d < bestD)
                {
                    bestD = d;
                    mid = (pi + _peaks[j].pos) / 2f;
                }
            }
            if (bestD < float.MaxValue && sources.TrueForAll(s => s.DistanceTo(mid) > 5f))
                sources.Add(mid);
        }
        sources.Sort((a, b) => HAt(b).CompareTo(HAt(a))); // 海拔高者先走（成干流）

        int riverCount = Math.Min(sources.Count,
            TerrainConfig.RiverSourceMin + rng.Next(TerrainConfig.RiverSourceMax - TerrainConfig.RiverSourceMin + 1));
        for (int i = 0; i < riverCount; i++)
        {
            var pts = TraceOne(sources[i], isMain: i == 0);
            if (pts.Count < 8)
                continue; // 走线过短（源点即贴水/贴缘）：弃之
            Rivers.Add(pts);
            CarveValley(pts, isMain: Rivers.Count == 1);
        }
    }

    private float HAt(Vector2 p) => H[Math.Clamp((int)p.Y, 0, S - 1) * S + Math.Clamp((int)p.X, 0, S - 1)];

    /// <summary>单条走线：8 邻取最低下行；洼地/平地则强制向东南滑行（保证大势东南流）；
    /// 撞上既有水域（离任一水点小于其拦截半宽）即汇流终止；出图缘终止。</summary>
    private List<Vector2> TraceOne(Vector2 source, bool isMain)
    {
        var pts = new List<Vector2>();
        var visited = new HashSet<int>();
        int x = (int)source.X, y = (int)source.Y;

        for (int step = 0; step < S * 4; step++)
        {
            if (x < 1 || y < 1 || x >= S - 1 || y >= S - 1)
            {
                // 出图缘：补记当前点 + 沿最后步进方向延伸一个图外点，
                // 保证 ×8 放大落地后河面刻满至真实图缘（越界格由落地层 InBounds 跳过）
                var cur = new Vector2(x, y);
                var dir = pts.Count > 0 ? cur - pts[^1] : new Vector2(1, 1);
                if (dir.LengthSquared() < 0.5f)
                    dir = new Vector2(1, 1);
                pts.Add(cur);
                pts.Add(cur + dir.Normalized() * 4f);
                break;
            }
            pts.Add(new Vector2(x, y));
            visited.Add(y * S + x);

            // 汇流检测（离源 8 步后才检，防在源头附近自撞）
            if (step > 8 && !isMain && HitsWater(new Vector2(x, y), out var join))
            {
                pts.Add(join);
                break;
            }

            // 8 邻中选未走过的最低格
            int bx = 0, by = 0;
            float bestH = float.MaxValue;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oy == 0) continue;
                    int nx = x + ox, ny = y + oy;
                    if (nx < 0 || ny < 0 || nx >= S || ny >= S || visited.Contains(ny * S + nx))
                        continue;
                    if (H[ny * S + nx] < bestH)
                    {
                        bestH = H[ny * S + nx];
                        bx = ox; by = oy;
                    }
                }
            }
            if (bestH >= H[y * S + x] - 0.0001f)
            {
                // 洼地/平地：强制向东南滑行（两个东南向邻格取较低者），维持西北→东南大势
                bx = 1; by = 1;
                if (!visited.Contains(y * S + x + 1) && H[y * S + x + 1] < H[(y + 1) * S + x]) { bx = 1; by = 0; }
                else if (!visited.Contains((y + 1) * S + x)) { bx = 0; by = 1; }
            }
            if (bx == 0 && by == 0)
                break; // 无处可走
            x += bx; y += by;
        }
        return pts;
    }

    /// <summary>点是否撞上既有水域（河点列/湖）：距任一水点小于其拦截半宽×1.2。</summary>
    private bool HitsWater(Vector2 p, out Vector2 hit)
    {
        foreach (var (wp, hw) in _waterPts)
        {
            if (p.DistanceTo(wp) <= hw * 1.2f + 1f)
            {
                hit = wp;
                return true;
            }
        }
        hit = default;
        return false;
    }

    /// <summary>沿河点列压 V 形谷：中心压到谷底目标高（沿程下降），半宽内平滑接回原地形——
    /// 山区段两侧仍高耸（峡谷），出山后与平原自然衔接；河道不悬山腰。同时登记水域拦截点。</summary>
    private void CarveValley(List<Vector2> pts, bool isMain)
    {
        float widthScale = isMain ? 1f : 0.6f; // 支线谷更窄
        for (int i = 0; i < pts.Count; i++)
        {
            float t = i / (float)Math.Max(1, pts.Count - 1);
            float floor = Mathf.Lerp(TerrainConfig.ValleyFloorSourceH, TerrainConfig.ValleyFloorMouthH, t);
            float hw = Mathf.Lerp(TerrainConfig.ValleyHalfWidthSource, TerrainConfig.ValleyHalfWidthMouth, t)
                / Scale * widthScale;
            _waterPts.Add((pts[i], hw * 0.35f)); // 拦截半宽取谷心区（脊线/汇流判定用）

            int r = Mathf.CeilToInt(hw);
            int cx = (int)pts[i].X, cy = (int)pts[i].Y;
            for (int oy = -r; oy <= r; oy++)
            {
                for (int ox = -r; ox <= r; ox++)
                {
                    int px = cx + ox, py = cy + oy;
                    if (px < 0 || py < 0 || px >= S || py >= S)
                        continue;
                    float d = new Vector2(ox, oy).Length() / hw;
                    if (d > 1f)
                        continue;
                    float k = d * d * (3f - 2f * d); // smoothstep：谷心平、谷壁缓接原地形
                    float target = floor * (1 - k) + H[py * S + px] * k;
                    if (H[py * S + px] > target)
                        H[py * S + px] = target;
                }
            }
        }
    }

    // ---- ④ 湖泊（干流途中凹陷）----

    /// <summary>沿干流中段随机取 1~2 处成湖：草图上压出湖盆凹陷并登记拦截区，
    /// 真实湖缘（谐波湾汊/湖中岛）由落地阶段 CarveLake 生成。</summary>
    private void PlaceLakes(Random rng)
    {
        if (Rivers.Count == 0)
            return;
        var main = Rivers[0];
        int lakes = WaterConfig.RiverLakeMin + rng.Next(WaterConfig.RiverLakeMax - WaterConfig.RiverLakeMin + 1);
        for (int i = 0; i < lakes && main.Count > 20; i++)
        {
            var pos = main[main.Count / 4 + rng.Next(main.Count / 2)]; // 中段取点
            float rCells = (WaterConfig.BigLakeRadiusMin
                + rng.Next(WaterConfig.BigLakeRadiusMax - WaterConfig.BigLakeRadiusMin + 1)) / Scale;
            Lakes.Add((pos, rCells));
            _waterPts.Add((pos, rCells)); // 整湖列为拦截区（脊线不得穿湖）

            // 湖盆凹陷：圆内压向 0 之下少许，缘部平滑接回
            int r = Mathf.CeilToInt(rCells * 1.2f);
            for (int oy = -r; oy <= r; oy++)
            {
                for (int ox = -r; ox <= r; ox++)
                {
                    int px = (int)pos.X + ox, py = (int)pos.Y + oy;
                    if (px < 0 || py < 0 || px >= S || py >= S)
                        continue;
                    float d = new Vector2(ox, oy).Length() / (rCells * 1.2f);
                    if (d > 1f)
                        continue;
                    float k = d * d * (3f - 2f * d);
                    float target = -0.2f * (1 - k) + H[py * S + px] * k;
                    if (H[py * S + px] > target)
                        H[py * S + px] = target;
                }
            }
        }
    }

    // ---- ⑤ 山脊连接 ----

    /// <summary>近邻峰对之间连脊（连线被河/湖拦截则弃）：脊高两端峰高插值、中段鞍部下凹、
    /// 沿脊正弦起伏；余弦横截面取高叠加——峰点由脊串联成连绵山脉，不再是孤立土包。</summary>
    private void LinkRidges(Random rng)
    {
        var linked = new HashSet<(int, int)>();
        for (int i = 0; i < _peaks.Count; i++)
        {
            // 距离升序取最近的 RidgeNeighborLinks 个峰
            var order = new List<int>();
            for (int j = 0; j < _peaks.Count; j++)
                if (j != i) order.Add(j);
            int self = i;
            order.Sort((a, b) => _peaks[a].pos.DistanceTo(_peaks[self].pos)
                .CompareTo(_peaks[b].pos.DistanceTo(_peaks[self].pos)));

            for (int k = 0; k < Math.Min(TerrainConfig.RidgeNeighborLinks, order.Count); k++)
            {
                int j = order[k];
                var key = (Math.Min(i, j), Math.Max(i, j));
                if (linked.Contains(key))
                    continue;
                linked.Add(key);
                if (!RidgeBlocked(_peaks[i].pos, _peaks[j].pos))
                    RaiseRidge(_peaks[i], _peaks[j], rng);
            }
        }
    }

    /// <summary>峰对连线是否被水域拦截：沿线每 1 格采样，任一点落入水域拦截半宽即拦。</summary>
    private bool RidgeBlocked(Vector2 a, Vector2 b)
    {
        int steps = Mathf.CeilToInt(a.DistanceTo(b));
        for (int s = 0; s <= steps; s++)
        {
            var p = a.Lerp(b, s / (float)Math.Max(1, steps));
            foreach (var (wp, hw) in _waterPts)
                if (p.DistanceTo(wp) <= hw + 0.5f)
                    return true;
        }
        return false;
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
}
