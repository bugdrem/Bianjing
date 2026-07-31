using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 水体落地工具集（批次四十九起）：河湖拓扑由 WorldSketch 草图谷线决定，
/// 本类只负责把拓扑"落地"为真实地图数据——刻水面格/湖盘（含湖中岛）、赋流向、
/// 以及按离岸距离把河床顶点压到水面之下（岸形由地势自然涌现：
/// 平原岸缓入水成浅滩，山区河谷两侧高耸成峡谷）。供 WorldGenerator 调用。
/// </summary>
public static class RiverGenerator
{
    /// <summary>河床下压（顶点高度场）：多源 BFS 算每个水格的离岸距离，
    /// 深度按距离从 BedDepthEdge 插值到 BedDepthCenter（BedFalloffDist 处满深），
    /// 把水格四角顶点压到「水面 - 深度」（只降不升）。
    /// 岸缘共享顶点被拉到浅滩深度：平原岸缓入水；山区河谷两壁自然成峡谷陡岸。</summary>
    public static void CarveBed(MapGrid map)
    {
        var hf = map.Height;
        // 1) 多源 BFS：从贴岸水格（四邻含陆地/图缘）向水体内部扩散，得每个水格离岸距离（贴岸=1）
        var dist = new int[MapGrid.Size * MapGrid.Size];
        var queue = new Queue<int>();
        Span<Vector2I> dirs = stackalloc Vector2I[] { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        for (int y = 0; y < MapGrid.Size; y++)
        {
            for (int x = 0; x < MapGrid.Size; x++)
            {
                if (!map.CellAt(x, y).HasWater)
                    continue;
                bool shore = false;
                foreach (var d in dirs)
                {
                    var n = new Vector2I(x + d.X, y + d.Y);
                    if (!MapGrid.InBounds(n) || !map.CellAt(n).HasWater)
                    {
                        shore = true;
                        break;
                    }
                }
                if (shore)
                {
                    dist[y * MapGrid.Size + x] = 1;
                    queue.Enqueue(y * MapGrid.Size + x);
                }
            }
        }
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int cx = idx % MapGrid.Size, cy = idx / MapGrid.Size;
            for (int i = 0; i < 4; i++)
            {
                var n = new Vector2I(cx + (i == 0 ? 1 : i == 1 ? -1 : 0), cy + (i == 2 ? 1 : i == 3 ? -1 : 0));
                if (!MapGrid.InBounds(n) || !map.CellAt(n).HasWater)
                    continue;
                int ni = n.Y * MapGrid.Size + n.X;
                if (dist[ni] != 0)
                    continue;
                dist[ni] = dist[idx] + 1;
                queue.Enqueue(ni);
            }
        }

        // 2) 逐水格下压四角顶点：目标高度 = 水面 - 深度（离岸越远越深，只降不升）
        for (int y = 0; y < MapGrid.Size; y++)
        {
            for (int x = 0; x < MapGrid.Size; x++)
            {
                if (!map.CellAt(x, y).HasWater)
                    continue;
                int d = dist[y * MapGrid.Size + x];
                if (d <= 0)
                    d = WaterConfig.BedFalloffDist; // 孤立未达格兜底：按满深处理
                float t = Mathf.Min(1f, (d - 1) / (float)WaterConfig.BedFalloffDist);
                float target = WaterConfig.WaterLevelAt(new Vector2I(x, y))
                    - Mathf.Lerp(WaterConfig.BedDepthEdge, WaterConfig.BedDepthCenter, t);
                for (int vx = x; vx <= x + 1; vx++)
                    for (int vy = y; vy <= y + 1; vy++)
                        if (hf.VertexH(vx, vy) > target)
                            hf.SetVertex(vx, vy, target);
            }
        }
    }

    /// <summary>湖泊落地：以多正弦谐波调制半径 r(θ)=base×(1+波动) 生成不规则湾汊湖面（静水，流向 0）；
    /// islands=true 时按概率在湖心附近扣出 1-2 座湖中岛（保留陆地不淹，四周环水）。</summary>
    public static void CarveLake(MapGrid map, Random rng, Vector2I center, int baseR, bool islands)
    {
        // 三组随机相位/频率的谐波，叠出扭曲湖缘
        double p1 = rng.NextDouble() * Math.PI * 2, p2 = rng.NextDouble() * Math.PI * 2, p3 = rng.NextDouble() * Math.PI * 2;
        double w = WaterConfig.LakeEdgeWaviness;
        int rMax = (int)(baseR * (1 + w)) + 2;

        for (int ox = -rMax; ox <= rMax; ox++)
        {
            for (int oy = -rMax; oy <= rMax; oy++)
            {
                double dist = Math.Sqrt(ox * ox + oy * oy);
                if (dist < 0.5)
                {
                    var cc = center + new Vector2I(ox, oy);
                    if (MapGrid.InBounds(cc)) SetWater(map, cc, 0);
                    continue;
                }
                double theta = Math.Atan2(oy, ox);
                double edge = baseR * (1 + w * (0.5 * Math.Sin(3 * theta + p1) + 0.3 * Math.Sin(5 * theta + p2) + 0.2 * Math.Sin(7 * theta + p3)));
                if (dist > edge)
                    continue;
                var c = center + new Vector2I(ox, oy);
                if (MapGrid.InBounds(c))
                    SetWater(map, c, 0);
            }
        }

        if (!islands || rng.NextDouble() > WaterConfig.IslandChance)
            return;
        int isleCount = 1 + rng.Next(WaterConfig.IslandMaxPerLake);
        for (int i = 0; i < isleCount; i++)
        {
            float ir = baseR * Mathf.Lerp(WaterConfig.IslandRadiusMin, WaterConfig.IslandRadiusMax, (float)rng.NextDouble());
            // 岛心落在湖内圈（≤base×0.4），并留出足够环水边距，避免岛啃到湖岸
            double ang = rng.NextDouble() * Math.PI * 2;
            double off = rng.NextDouble() * baseR * 0.4;
            var isleCenter = center + new Vector2I((int)(Math.Cos(ang) * off), (int)(Math.Sin(ang) * off));
            ClearDisk(map, isleCenter, ir);
        }
    }

    /// <summary>以 center 为圆心刻一片水面圆盘（半径 radius 米，流向 flow），越界格跳过。</summary>
    public static void CarveDisk(MapGrid map, Vector2I center, float radius, byte flow)
    {
        int r = Mathf.CeilToInt(radius);
        for (int ox = -r; ox <= r; ox++)
            for (int oy = -r; oy <= r; oy++)
            {
                if (ox * ox + oy * oy > radius * radius)
                    continue;
                var c = center + new Vector2I(ox, oy);
                if (MapGrid.InBounds(c))
                    SetWater(map, c, flow);
            }
    }

    /// <summary>以 center 为圆心退去一片水面（湖中岛）：把圆盘内格 HasWater 复位为陆地。</summary>
    public static void ClearDisk(MapGrid map, Vector2I center, float radius)
    {
        int r = Mathf.CeilToInt(radius);
        for (int ox = -r; ox <= r; ox++)
            for (int oy = -r; oy <= r; oy++)
            {
                if (ox * ox + oy * oy > radius * radius)
                    continue;
                var c = center + new Vector2I(ox, oy);
                if (MapGrid.InBounds(c))
                {
                    ref var cell = ref map.CellAt(c);
                    cell.HasWater = false;
                    cell.FlowDir = 0;
                }
            }
    }

    /// <summary>写入水面格并赋流向：flow>0 才覆盖流向（湖泊静水 flow=0 不改动既有河流向）。</summary>
    public static void SetWater(MapGrid map, Vector2I c, byte flow)
    {
        ref var cell = ref map.CellAt(c);
        cell.HasWater = true;
        if (flow != 0)
            cell.FlowDir = flow;
    }

    /// <summary>把方向分量 (sx,sy)∈{-1,0,1} 量化为八方向编码：0=静水，1=东,2=东南,3=南,4=西南,5=西,6=西北,7=北,8=东北。</summary>
    public static byte EncodeFlow(int sx, int sy)
    {
        sx = Math.Sign(sx);
        sy = Math.Sign(sy);
        return (sx, sy) switch
        {
            (1, 0) => 1,
            (1, 1) => 2,
            (0, 1) => 3,
            (-1, 1) => 4,
            (-1, 0) => 5,
            (-1, -1) => 6,
            (0, -1) => 7,
            (1, -1) => 8,
            _ => 0,
        };
    }
}
