using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 一条河道：折线 + 逐点河宽 + 逐点地形高。坐标用<b>连续格坐标</b>
/// （格 (x,y) 的中心为 (x+0.5, y+0.5)，1 单位 = 1 米；成品地图上取值 0~MapGrid.Size，
/// 河口外推点可略微越界）。水面尚未刻入网格——由 RiverGenerator 消费。
/// </summary>
public sealed class RiverPath
{
    /// <summary>折线顶点（连续格坐标）。</summary>
    public List<Vector2> Points = new();

    /// <summary>逐点河宽（米），与 Points 一一对应。</summary>
    public List<float> Widths = new();

    /// <summary>逐点地形高（米，刻水前采样）：水位求解的输入。</summary>
    public List<float> Terrain = new();

    /// <summary>源头汇流格数（路由格）：越大越是干流（RiverNetwork 按此降序排列）。</summary>
    public int DrainCells;

    /// <summary>是否流出地图（是则中心线已外推出图、末段按喇叭口展宽）。</summary>
    public bool ExitsMap;
}

/// <summary>
/// 河网提取与塑形（批次六十九新增）：把 FlowRouter 的汇流场变成可直接刻水的河道折线。
/// ① <b>找源头</b>：汇流面积达标、且上游再无达标格的格即河源（树状水系的根）；
/// ② <b>顺流向走线</b>：按 D8 从源头走到图缘或撞上已有河道（汇流终止），得到天然树状河网；
/// ③ <b>剪短枝</b>：短于 ChannelMinLengthCells 的支流丢弃；
/// ④ <b>河宽</b>：Hack 定律 宽度 ∝ √汇流面积，滑动平均平滑；
/// ⑤ <b>塑形</b>：Chaikin 圆角消 D8 阶梯 → 正弦蛇曲（摆幅 ∝ 河宽）+ 垂向最低点吸附回谷底 → 再圆角；
/// ⑥ <b>河口</b>：中心线外推出图 MouthExtendMeters 且末段展宽，治「河流出图收成一个小点」。
/// 纯几何/数据运算，可在后台线程运行。
/// </summary>
public static class RiverNetwork
{
    /// <summary>提河网。</summary>
    /// <param name="flow">FlowRouter 产出的流向场。</param>
    /// <param name="sampleH">连续格坐标 → 地形高的采样器（成品地图用 HeightField.SampleCell，草图预览用草图插值）。</param>
    /// <param name="rng">随机源（蛇曲相位）。</param>
    public static List<RiverPath> Build(FlowField flow, Func<float, float, float> sampleH, Random rng)
    {
        var result = new List<RiverPath>();
        var sources = FindSources(flow);
        if (sources.Count == 0)
            return result;

        int n = flow.Size * flow.Size;
        var used = new bool[n];

        foreach (int src in sources)
        {
            if (used[src])
                continue; // 已被更上游的大河收编

            // 顺流向走线：撞上已有河道即汇流终止，走到图缘即出图
            var cells = new List<int>();
            int cur = src;
            bool exits = false;
            while (cur >= 0 && !used[cur])
            {
                cells.Add(cur);
                int t = flow.Dir[cur];
                if (t < 0)
                {
                    exits = true;
                    break;
                }
                cur = t;
            }
            if (cells.Count < WaterConfig.ChannelMinLengthCells)
                continue; // 短枝：丢弃且不占格（留给后续河源）

            foreach (int c in cells)
                used[c] = true;

            var path = Shape(cells, flow, sampleH, rng, exits);
            path.DrainCells = flow.Area[src];
            path.ExitsMap = exits;
            result.Add(path);
        }

        result.Sort((a, b) => b.DrainCells.CompareTo(a.DrainCells));
        return result;
    }

    // ---- ① 河源 ----

    /// <summary>河源 = 汇流面积达标、且八邻中无任何「流入本格且同样达标」的格（即上游还是坡面漫流）。
    /// 按汇流面积降序取前 ChannelMaxCount 个；若阈值过高一个都找不到，逐级折半放宽。</summary>
    private static List<int> FindSources(FlowField flow)
    {
        int threshold = WaterConfig.ChannelMinDrainCells;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var found = new List<int>();
            int n = flow.Size * flow.Size;
            for (int i = 0; i < n; i++)
            {
                if (flow.Area[i] < threshold)
                    continue;
                if (HasUpstreamChannel(flow, i, threshold))
                    continue;
                found.Add(i);
            }
            if (found.Count > 0)
            {
                found.Sort((a, b) => flow.Area[b].CompareTo(flow.Area[a]));
                int take = Math.Min(found.Count, WaterConfig.ChannelMaxCount);
                return found.GetRange(0, take);
            }
            threshold = Math.Max(2, threshold / 2);
        }
        return new List<int>();
    }

    /// <summary>本格是否有达标的直接上游（八邻中流向本格者）。</summary>
    private static bool HasUpstreamChannel(FlowField flow, int idx, int threshold)
    {
        int x = flow.XOf(idx), y = flow.YOf(idx);
        for (int k = 0; k < FlowField.NeighborCount; k++)
        {
            int ni = flow.NeighborIndex(x, y, k);
            if (ni < 0)
                continue;
            if (flow.Dir[ni] == idx && flow.Area[ni] >= threshold)
                return true;
        }
        return false;
    }

    // ---- ④~⑥ 塑形 ----

    /// <summary>路由格序列 → 塑形后的河道折线（宽度/蛇曲/圆角/河口）。</summary>
    private static RiverPath Shape(List<int> cells, FlowField flow,
        Func<float, float, float> sampleH, Random rng, bool exits)
    {
        var pts = new List<Vector2>(cells.Count);
        var widths = new List<float>(cells.Count);
        float ds = flow.Downsample;

        for (int i = 0; i < cells.Count; i++)
        {
            pts.Add(new Vector2((flow.XOf(cells[i]) + 0.5f) * ds, (flow.YOf(cells[i]) + 0.5f) * ds));
            float areaM2 = flow.Area[cells[i]] * flow.CellArea;
            float w = WaterConfig.WidthCoef * Mathf.Sqrt(areaM2);
            widths.Add(Mathf.Clamp(w, WaterConfig.WidthMin, WaterConfig.WidthMax));
        }

        SmoothValues(widths, WaterConfig.WidthSmoothWindow);

        // 正弦蛇曲 + 垂向最低点吸附（先摆后圆角，免得圆角把摆动抹平）
        Chaikin(pts, widths, 1);
        Meander(pts, widths, sampleH, rng);
        Chaikin(pts, widths, Math.Max(1, WaterConfig.ChaikinIterations - 1));

        if (exits)
            ExtendMouth(pts, widths);

        var path = new RiverPath { Points = pts, Widths = widths, ExitsMap = exits };
        for (int i = 0; i < pts.Count; i++)
            path.Terrain.Add(sampleH(pts[i].X, pts[i].Y));
        return path;
    }

    // ---- 平滑与圆角 ----

    /// <summary>一维滑动平均（边缘缩窗）：抹掉单点面积跳变造成的宽窄突变。</summary>
    private static void SmoothValues(List<float> v, int window)
    {
        if (v.Count < 3 || window < 3)
            return;
        int hw = window / 2;
        var src = v.ToArray();
        for (int i = 0; i < v.Count; i++)
        {
            int a = Math.Max(0, i - hw), b = Math.Min(v.Count - 1, i + hw);
            float sum = 0;
            for (int j = a; j <= b; j++)
                sum += src[j];
            v[i] = sum / (b - a + 1);
        }
    }

    /// <summary>Chaikin 圆角：每段取 1/4、3/4 两个内分点替代原端点，阶梯折线 → 圆滑曲线；
    /// 宽度同步线性内插（与几何同一套权重，宽窄过渡不被打乱）。</summary>
    private static void Chaikin(List<Vector2> pts, List<float> widths, int iterations)
    {
        for (int it = 0; it < iterations; it++)
        {
            int n = pts.Count;
            if (n < 3)
                return;
            var np = new List<Vector2>(n * 2);
            var nw = new List<float>(n * 2);
            np.Add(pts[0]);
            nw.Add(widths[0]);
            for (int i = 0; i < n - 1; i++)
            {
                np.Add(pts[i] * 0.75f + pts[i + 1] * 0.25f);
                nw.Add(widths[i] * 0.75f + widths[i + 1] * 0.25f);
                np.Add(pts[i] * 0.25f + pts[i + 1] * 0.75f);
                nw.Add(widths[i] * 0.25f + widths[i + 1] * 0.75f);
            }
            np.Add(pts[n - 1]);
            nw.Add(widths[n - 1]);
            pts.Clear(); pts.AddRange(np);
            widths.Clear(); widths.AddRange(nw);
        }
    }

    // ---- ⑤ 蛇曲 ----

    /// <summary>蛇曲：沿弧长做正弦横向摆动（摆幅 = 河宽 × MeanderAmpFactor，宽河摆得开、窄溪几乎笔直），
    /// 再按 MeanderSnap 比例混进「垂向最低点」偏移——纯正弦会爬上谷壁，吸附项把河线按回谷底。</summary>
    private static void Meander(List<Vector2> pts, List<float> widths,
        Func<float, float, float> sampleH, Random rng)
    {
        int n = pts.Count;
        if (n < 3)
            return;

        var arc = new float[n];
        for (int i = 1; i < n; i++)
            arc[i] = arc[i - 1] + pts[i].DistanceTo(pts[i - 1]);

        double phase = rng.NextDouble() * Math.PI * 2;
        double wave = WaterConfig.MeanderWaveMeters;
        float snap = WaterConfig.MeanderSnap;

        for (int i = 1; i < n - 1; i++)
        {
            var tangent = (pts[i + 1] - pts[i - 1]);
            if (tangent.LengthSquared() < 1e-6f)
                continue;
            tangent = tangent.Normalized();
            var perp = new Vector2(-tangent.Y, tangent.X);

            float amp = widths[i] * WaterConfig.MeanderAmpFactor;
            if (amp < 0.5f)
                continue;

            float sine = amp * (float)Math.Sin(2 * Math.PI * arc[i] / wave + phase);
            float low = snap > 0f ? LowestOffset(pts[i], perp, amp, sampleH) : 0f;
            pts[i] += perp * (sine * (1f - snap) + low * snap);
        }
    }

    /// <summary>在垂线上 ±amp 范围内找地形最低的偏移（三点平均滤细节噪声）：-amp ~ +amp。</summary>
    private static float LowestOffset(Vector2 p, Vector2 perp, float amp,
        Func<float, float, float> sampleH)
    {
        int steps = Mathf.Clamp(Mathf.CeilToInt(amp), 2, 24);
        float bestOff = 0f, bestH = float.MaxValue;
        for (int k = -steps; k <= steps; k++)
        {
            float off = amp * k / steps;
            float h = 0f;
            for (int t = -1; t <= 1; t++)
            {
                float d = off + t * 1.5f;
                h += sampleH(p.X + perp.X * d, p.Y + perp.Y * d);
            }
            h /= 3f;
            if (h < bestH)
            {
                bestH = h;
                bestOff = off;
            }
        }
        return bestOff;
    }

    // ---- ⑥ 河口 ----

    /// <summary>河口外推与展宽：中心线沿末段方向伸到图外 MouthExtendMeters，
    /// 并自末点回溯 MouthTaperMeters 把河宽渐增到 MouthWidthFactor 倍（喇叭口）。
    /// 外推是「河流出图仍是宽大水面」的关键——刻盘中心在图外，图缘处被满宽圆盘覆盖。</summary>
    private static void ExtendMouth(List<Vector2> pts, List<float> widths)
    {
        int n = pts.Count;
        if (n < 2)
            return;

        var dir = (pts[n - 1] - pts[n - 2]);
        if (dir.LengthSquared() < 1e-6f)
            return;
        dir = dir.Normalized();

        float ext = WaterConfig.MouthExtendMeters;
        for (int s = 1; s <= 3; s++)
        {
            pts.Add(pts[n - 1] + dir * (ext * s / 3f));
            widths.Add(widths[n - 1]);
        }

        // 弧长 → 末段展宽
        int m = pts.Count;
        var arc = new float[m];
        for (int i = 1; i < m; i++)
            arc[i] = arc[i - 1] + pts[i].DistanceTo(pts[i - 1]);
        float total = arc[m - 1];
        float cap = WaterConfig.WidthMax * WaterConfig.MouthWidthFactor;
        for (int i = 0; i < m; i++)
        {
            float d = total - arc[i];
            float t = 1f - Mathf.Clamp(d / WaterConfig.MouthTaperMeters, 0f, 1f);
            widths[i] = Mathf.Min(cap, widths[i] * Mathf.Lerp(1f, WaterConfig.MouthWidthFactor, t));
        }
    }
}
