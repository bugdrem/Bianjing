using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 新地图水系生成（树状水系，带水流方向；仅新图运行一次，地形之后——水面/流向/河床随存档保存）：
/// ① 主干河——一条自西源蜿蜒东流入海口的完整干流，越往下游越宽，中心线点列供支流取源，水流指向河口；
/// ② 支流树——从干流递归分叉出支流与小溪（二叉树式），逐级变细变短，水流指向汇入的母河（下游）；
/// ③ 湖泊——多正弦谐波扭曲的大湖（湖缘呈不规则湾汊），可含湖中岛；坐落河上者天然带入水口/出水口，
///    离河独立的小湖另凿一条出水渠连向最近水体；
/// ④ 河床下压——按离岸距离把水格顶点压到水面以下（边缘浅、中心深），
///    岸形由地势自然涌现：平原岸缓入水成浅滩，山体被河切穿处成峡谷陡岸。参数集中在 WaterConfig。
/// </summary>
public static class RiverGenerator
{
    public static void Carve(MapGrid map, Random rng)
    {
        // 1) 主干：贯穿全图，记录中心线点列（含每点流向切线）供支流取源
        var spine = CarveMainRiver(map, rng);

        // 2) 支流树：从干流中段随机取源，递归分叉；水流指向汇入的母河
        int branches = WaterConfig.PrimaryBranchMin + rng.Next(WaterConfig.PrimaryBranchMax - WaterConfig.PrimaryBranchMin + 1);
        int lo = spine.Count / 8, hi = spine.Count * 7 / 8; // 避开河口/源头两端
        for (int i = 0; i < branches; i++)
        {
            var src = spine[lo + rng.Next(Math.Max(1, hi - lo))];
            // 初始方向大致垂直主河走向（随机一侧），略带随机偏转
            double angle = (rng.Next(2) == 0 ? -Math.PI / 2 : Math.PI / 2) + (rng.NextDouble() - 0.5) * 0.6;
            int length = WaterConfig.PrimaryLenMin + rng.Next(WaterConfig.PrimaryLenMax - WaterConfig.PrimaryLenMin + 1);
            GrowTributary(map, rng, src.X, src.Y, angle, WaterConfig.PrimaryWidth, length, WaterConfig.TributaryDepth);
        }

        // 3) 大湖：坐落在河网点上，天然与河相连（入水口=上游、出水口=下游），含湖中岛
        int riverLakes = WaterConfig.RiverLakeMin + rng.Next(WaterConfig.RiverLakeMax - WaterConfig.RiverLakeMin + 1);
        for (int i = 0; i < riverLakes; i++)
        {
            var center = spine[lo + rng.Next(Math.Max(1, hi - lo))];
            int radius = WaterConfig.BigLakeRadiusMin + rng.Next(WaterConfig.BigLakeRadiusMax - WaterConfig.BigLakeRadiusMin + 1);
            CarveLake(map, rng, center, radius, islands: true);
        }

        // 4) 独立小湖：远离河的水塘，另凿一条出水渠连向最近水体
        int soloLakes = WaterConfig.SoloLakeMin + rng.Next(WaterConfig.SoloLakeMax - WaterConfig.SoloLakeMin + 1);
        for (int i = 0; i < soloLakes; i++)
        {
            int radius = WaterConfig.SoloLakeRadiusMin + rng.Next(WaterConfig.SoloLakeRadiusMax - WaterConfig.SoloLakeRadiusMin + 1);
            var center = new Vector2I(
                radius + 8 + rng.Next(MapGrid.Size - 2 * (radius + 8)),
                radius + 8 + rng.Next(MapGrid.Size - 2 * (radius + 8)));
            CarveLake(map, rng, center, radius, islands: rng.NextDouble() < 0.4);
            CarveOutlet(map, rng, center, radius);
        }

        // 5) 河床下压：水体拓扑定型后，按离岸距离把所有水格顶点压到水面之下（岸形自然涌现）
        CarveBed(map);
    }

    /// <summary>河床下压（顶点高度场）：多源 BFS 算每个水格的离岸距离，
    /// 深度按距离从 BedDepthEdge 插值到 BedDepthCenter（BedFalloffDist 处满深），
    /// 把水格四角顶点压到「水面 - 深度」（只降不升）。
    /// 岸缘共享顶点被拉到浅滩深度：平原岸缓入水；若河道切穿山体，高岸顶点落差巨大，自然呈陡岸/峡谷。</summary>
    private static void CarveBed(MapGrid map)
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

    /// <summary>主干河：中心线沿 x 轴推进，纵向由低频正弦 + 随机漫步蜿蜒；河宽自源头(西)向河口(东)线性变宽。
    /// 逐列刻一条纵向水带并记录中心点（流向≈推进切线，指向河口）。返回中心线点列。</summary>
    private static List<Vector2I> CarveMainRiver(MapGrid map, Random rng)
    {
        var spine = new List<Vector2I>();
        int baseY = MapGrid.Size / 4 + rng.Next(MapGrid.Size / 2);
        double phase = rng.NextDouble() * Math.PI * 2;
        double drift = 0;                 // 低频随机漫步（叠加在正弦上，避免过于规则）
        int prevYc = baseY;

        for (int x = 0; x < MapGrid.Size; x++)
        {
            double t = (double)x / MapGrid.Size;                       // 0(源头) → 1(河口)
            drift += (rng.NextDouble() - 0.5) * 1.4;
            drift = Math.Clamp(drift, -WaterConfig.MainMeanderAmp, WaterConfig.MainMeanderAmp);
            double sine = Math.Sin(x * 2 * Math.PI / WaterConfig.MainMeanderWave + phase) * WaterConfig.MainMeanderAmp * 0.6;
            int yc = (int)Math.Clamp(baseY + sine + drift, 20, MapGrid.Size - 21);
            int width = (int)Mathf.Lerp(WaterConfig.MainWidthMin, WaterConfig.MainWidthMax, (float)t);

            byte flow = EncodeFlow(1, Math.Sign(yc - prevYc)); // 东向为主，带纵向分量
            int half = width / 2;
            for (int dy = -half; dy <= width - half; dy++)
            {
                var c = new Vector2I(x, yc + dy);
                if (MapGrid.InBounds(c))
                    SetWater(map, c, flow);
            }
            spine.Add(new Vector2I(x, yc));
            prevYc = yc;
        }
        return spine;
    }

    /// <summary>支流（递归）：从 (px,py) 沿 angle 逐米推进、蜿蜒，沿途刻圆盘；水流指向来路（=汇入母河的下游方向）。
    /// 出图或行进一段后撞上其它水体（汇流）即止；depth>0 时沿途分叉出更细更短的子支（小溪），构成二叉树式水系。</summary>
    private static void GrowTributary(MapGrid map, Random rng, double px, double py, double angle, float width, int length, int depth)
    {
        // 预排子支的出发步数（均匀撒在中后段，避免刚离母河就分叉）
        var childSteps = new List<int>();
        if (depth > 0)
        {
            int children = WaterConfig.ChildBranchMin + rng.Next(WaterConfig.ChildBranchMax - WaterConfig.ChildBranchMin + 1);
            for (int i = 0; i < children; i++)
                childSteps.Add(length / 3 + rng.Next(Math.Max(1, length * 2 / 3)));
        }

        for (int step = 0; step < length; step++)
        {
            double dx = Math.Cos(angle), dy = Math.Sin(angle);
            px += dx;
            py += dy;
            var c = new Vector2I((int)Math.Round(px), (int)Math.Round(py));
            if (!MapGrid.InBounds(c))
                return; // 出图即止

            // 汇流检测：探测点须在自身圆盘前缘之外，前 32 米刚离母河不算撞水
            var probe = new Vector2I(
                (int)(px + dx * (width / 2f + 1.5)),
                (int)(py + dy * (width / 2f + 1.5)));
            if (step > 32 && MapGrid.InBounds(probe) && map.CellAt(probe).HasWater)
                return;

            // 流向指向来路（下游=朝母河），即推进方向取反
            byte flow = EncodeFlow(-Math.Sign(dx == 0 ? 0 : dx), -Math.Sign(dy == 0 ? 0 : dy));
            CarveDisk(map, c, width / 2f, flow);
            angle += (rng.NextDouble() - 0.5) * WaterConfig.TributaryWaver;

            // 到点分叉：子支更细更短、递归层数-1，向左右任一侧偏转 BranchAngle
            if (depth > 0 && childSteps.Contains(step))
            {
                double side = rng.Next(2) == 0 ? -1 : 1;
                double childAngle = angle + side * WaterConfig.BranchAngle + (rng.NextDouble() - 0.5) * 0.3;
                GrowTributary(map, rng, px, py, childAngle,
                    width * WaterConfig.ChildWidthFactor,
                    (int)(length * WaterConfig.ChildLenFactor), depth - 1);
            }
        }
    }

    /// <summary>湖泊：以多正弦谐波调制半径 r(θ)=base×(1+波动) 生成不规则湾汊湖面（静水，流向 0）；
    /// islands=true 时按概率在湖心附近扣出 1-2 座湖中岛（保留陆地不淹，四周环水）。</summary>
    private static void CarveLake(MapGrid map, Random rng, Vector2I center, int baseR, bool islands)
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

    /// <summary>独立小湖出水渠：从湖边向外找最近的既有水体（≤OutletMaxDist），凿一条窄渠连过去；
    /// 找不到则不连（保留孤湖）。渠水流向从湖指向外部水体。</summary>
    private static void CarveOutlet(MapGrid map, Random rng, Vector2I lakeCenter, int radius)
    {
        // 环形逐距扫描找最近水体（跳过本湖自身：距湖心 > radius+2 才算外部）
        for (int dist = radius + 3; dist <= radius + WaterConfig.OutletMaxDist; dist += 2)
        {
            for (int a = 0; a < 48; a++)
            {
                double ang = a * Math.PI * 2 / 48;
                var target = lakeCenter + new Vector2I((int)(Math.Cos(ang) * dist), (int)(Math.Sin(ang) * dist));
                if (!MapGrid.InBounds(target) || !map.CellAt(target).HasWater)
                    continue;
                // 沿湖心→目标方向凿渠
                var start = new Vector2((float)(lakeCenter.X + Math.Cos(ang) * radius), (float)(lakeCenter.Y + Math.Sin(ang) * radius));
                var end = new Vector2(target.X, target.Y);
                double steps = start.DistanceTo(end);
                byte flow = EncodeFlow(Math.Sign(target.X - lakeCenter.X), Math.Sign(target.Y - lakeCenter.Y));
                for (double s = 0; s <= steps; s += 1)
                {
                    var p = start.Lerp(end, (float)(s / Math.Max(1, steps)));
                    CarveDisk(map, new Vector2I((int)Math.Round(p.X), (int)Math.Round(p.Y)), WaterConfig.OutletChannelWidth / 2f, flow);
                }
                return; // 一条出水渠足矣
            }
        }
    }

    /// <summary>以 center 为圆心刻一片水面圆盘（半径 radius 米，流向 flow），越界格跳过。</summary>
    private static void CarveDisk(MapGrid map, Vector2I center, float radius, byte flow)
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
    private static void ClearDisk(MapGrid map, Vector2I center, float radius)
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
    private static void SetWater(MapGrid map, Vector2I c, byte flow)
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
