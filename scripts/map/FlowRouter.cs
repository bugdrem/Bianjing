using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 流向路由场（批次六十九新增）：在给定高度场上一次遍历同时求出三件事——
/// ① <b>优先洪水填洼</b>（Priority-Flood，Barnes 变体）：从图缘最低格起，按「填充高度」升序
///    向外扩散，每格填充高 = max(本格地形高, 来路格填充高 + ε)。结果每个格都有一条严格下降的
///    出路（洼地被填平到溢出口高度），且<b>出队次序本身就是天然的拓扑序</b>（先出队者更下游）。
/// ② <b>D8 流向</b>：每格指向「使其出队的那一格」（即填充高最低的下游邻格）。
/// ③ <b>汇流累积</b>：按出队次序<b>逆序</b>（填充高降序 = 先上游后下游）把格数累加给下游，
///    一次线性扫描即得每格的上游汇水格数（Hack 定律里河宽的依据）。
/// 副产品 Depression = 填充高 - 地形高：大于 0 的连通块就是天然洼地（LakeGenerator 的天然湖盆）。
/// 纯数据、无 Godot 依赖，可在后台线程运行。
/// </summary>
public sealed class FlowField
{
    /// <summary>路由网格边长（格）。</summary>
    public int Size;

    /// <summary>一个路由格代表的世界米数（成品地形 = RouteDownsample 米，草图预览 = SketchScale 米）。</summary>
    public float CellMeters;

    /// <summary>降采样倍率：路由格 (rx,ry) 采样基础场索引 (rx*Downsample + Downsample/2, ...)。</summary>
    public int Downsample;

    /// <summary>路由分辨率下的地形高（行主序，Size²）。</summary>
    public float[] Base = Array.Empty<float>();

    /// <summary>填洼后的高（行主序）：每格都有到图缘的下降通路。</summary>
    public float[] Filled = Array.Empty<float>();

    /// <summary>填洼量 = Filled - Base（行主序）：天然洼地的深度，&gt;0 的连通块即湖盆候选。</summary>
    public float[] Depression = Array.Empty<float>();

    /// <summary>D8 下游格索引（行主序）；-1 = 图缘出口（水直接流出地图）。</summary>
    public int[] Dir = Array.Empty<int>();

    /// <summary>洪泛出队次序（长度 Size²，升序 Filled：下标越小越下游）。</summary>
    public int[] Order = Array.Empty<int>();

    /// <summary>汇流格数（行主序，含自身）：上游所有流入本格的路由格总数。</summary>
    public int[] Area = Array.Empty<int>();

    /// <summary>格数转世界平方米：汇流面积（m²）= Area × CellArea。</summary>
    public float CellArea => CellMeters * CellMeters;

    /// <summary>八邻偏移（行主序索引增量），顺序无关紧要但须与 Dir 语义一致。</summary>
    public const int NeighborCount = 8;

    private static readonly int[] Dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
    private static readonly int[] Dy = { 0, 1, 1, 1, 0, -1, -1, -1 };

    /// <summary>取路由格 (rx,ry) 的八邻索引（越界返回 -1）。供 RiverNetwork/LakeGenerator 遍历上游。</summary>
    public int NeighborIndex(int rx, int ry, int k)
    {
        int nx = rx + Dx[k], ny = ry + Dy[k];
        if (nx < 0 || ny < 0 || nx >= Size || ny >= Size)
            return -1;
        return ny * Size + nx;
    }

    /// <summary>路由格索引 → 路由格坐标 X。</summary>
    public int XOf(int idx) => idx % Size;

    /// <summary>路由格索引 → 路由格坐标 Y。</summary>
    public int YOf(int idx) => idx / Size;
}

/// <summary>
/// 流向路由求解器：把「高度场」变成「谁往哪流、汇了多少水、哪里有洼地」。
/// 两个入口：成品地形（HeightField，路由格 = 2m）与 128² 草图（预览示意用，路由格 = 8m）。
/// </summary>
public static class FlowRouter
{
    /// <summary>在成品地形高度场上求流向场（路由格 = RouteDownsample 米）。</summary>
    public static FlowField Build(HeightField hf) =>
        Build(hf.Raw, HeightField.VertsPerSide, MapGrid.CellSize, WaterConfig.RouteDownsample);

    /// <summary>在任意行主序高度场上求流向场。
    /// 路由格 (rx,ry) 采样基础场元素 (rx*downsample + downsample/2, ry*downsample + downsample/2)，
    /// 即取该块中心，避免系统性偏移；路由网格边长 = baseSize / downsample。</summary>
    /// <param name="baseH">基础高度场（行主序，边长 baseSize）。</param>
    /// <param name="baseSize">基础场边长（顶点场用 1025，值场用边长本身）。</param>
    /// <param name="baseCellMeters">基础场相邻元素的世界间距（米）。</param>
    /// <param name="downsample">降采样倍率（1 = 不降采样）。</param>
    public static FlowField Build(float[] baseH, int baseSize, float baseCellMeters, int downsample)
    {
        if (downsample < 1)
            downsample = 1;
        int size = baseSize / downsample;
        int n = size * size;
        int half = downsample / 2;

        var field = new FlowField
        {
            Size = size,
            CellMeters = baseCellMeters * downsample,
            Downsample = downsample,
            Base = new float[n],
            Filled = new float[n],
            Depression = new float[n],
            Dir = new int[n],
            Order = new int[n],
            Area = new int[n],
        };

        // ① 采样：路由格中心取值
        for (int ry = 0; ry < size; ry++)
        {
            for (int rx = 0; rx < size; rx++)
            {
                int sx = Math.Min(rx * downsample + half, baseSize - 1);
                int sy = Math.Min(ry * downsample + half, baseSize - 1);
                field.Base[ry * size + rx] = baseH[sy * baseSize + sx];
            }
        }

        // ② 优先洪水：图缘入队 → 按填充高升序出队 → 未定格被「填充高最低的已定邻格」收编
        var closed = new bool[n];
        var open = new PriorityQueue<int, float>(n / 8);
        int ord = 0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (x != 0 && y != 0 && x != size - 1 && y != size - 1)
                    continue;
                int i = y * size + x;
                closed[i] = true;
                field.Dir[i] = -1;      // 图缘格：水直接出图
                field.Filled[i] = field.Base[i];
                open.Enqueue(i, field.Base[i]);
            }
        }

        float eps = WaterConfig.FloodEpsilon;
        while (open.Count > 0)
        {
            int c = open.Dequeue();
            field.Order[ord++] = c;
            int cx = c % size, cy = c / size;
            float fc = field.Filled[c] + eps;

            for (int k = 0; k < FlowField.NeighborCount; k++)
            {
                int nx = cx + Dx[k], ny = cy + Dy[k];
                if (nx < 0 || ny < 0 || nx >= size || ny >= size)
                    continue;
                int ni = ny * size + nx;
                if (closed[ni])
                    continue;
                // 出队次序即填充高升序：第一个收编本格的邻居就是「填充高最低的下游邻格」
                closed[ni] = true;
                float b = field.Base[ni];
                float f = b > fc ? b : fc;
                field.Filled[ni] = f;
                field.Depression[ni] = f - b;
                field.Dir[ni] = c;
                open.Enqueue(ni, f);
            }
        }

        // ③ 汇流累积：逆出队序（填充高降序 = 先上游后下游），每格把自身累计量交给下游
        for (int i = 0; i < n; i++)
            field.Area[i] = 1;
        for (int k = n - 1; k >= 0; k--)
        {
            int c = field.Order[k];
            int t = field.Dir[c];
            if (t >= 0)
                field.Area[t] += field.Area[c];
        }

        return field;
    }

    private static readonly int[] Dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
    private static readonly int[] Dy = { 0, 1, 1, 1, 0, -1, -1, -1 };
}
