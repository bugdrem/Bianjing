using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 水系生成（批次五十起）：在侵蚀完成的「成品地形」上循坡走线——只读地势、不改地势
/// （唯一例外：河床下压，把水格顶点压到本格水面之下），保证地形生成算法的纯粹性。
/// ① 河源取峰间鞍部（沿草图峰点放大到世界坐标），沿最陡下降走线，撞既有水体即汇流（树状水系）；
/// ② 逐格水位 Cell.WaterH：沿程取「平滑地形的运行最小值」、下限 0（以 0 为最低点）——
///    水面随地势逐级下降形成流向观感，河岸高差由地形自然涌现（平原浅滩、山区峡谷）；
/// ③ 湖泊坐落干流低平处：谐波湾汊圈内「地形低于湖面」的格才着水，高地自然留成湖中岛/岬角；
/// ④ 支流汇入处水位回灌抬平（backwater），免支口低于干流水面的倒挂。
/// </summary>
public static class RiverGenerator
{
    /// <summary>水系总入口：走线 → 刻水/赋水位/流向 → 干流点湖 → 河床下压。peaks 为世界坐标峰点。</summary>
    public static void BuildWaterSystem(MapGrid map, List<(Vector2 pos, float h)> peaks, Random rng)
    {
        var sources = PickSources(map, peaks, rng);
        for (int i = 0; i < sources.Count; i++)
        {
            var path = TracePath(map, sources[i]);
            if (path.Count < 40)
                continue; // 走线过短（源点即贴水/贴缘）：弃之
            var levels = ComputeLevels(map, path);
            CarveRiver(map, path, levels, isMain: i == 0);
            if (i == 0)
                PlaceLakes(map, path, levels, rng); // 湖只挂在干流上
        }
        CarveBed(map);
    }

    // ---- ① 河源与走线 ----

    /// <summary>河源候选：每峰与最近邻峰的中点（鞍部），按该点地形海拔降序，取 RiverCount 条。</summary>
    private static List<Vector2I> PickSources(MapGrid map, List<(Vector2 pos, float h)> peaks, Random rng)
    {
        var candidates = new List<Vector2>();
        for (int i = 0; i < peaks.Count; i++)
        {
            float bestD = float.MaxValue;
            Vector2 mid = default;
            for (int j = 0; j < peaks.Count; j++)
            {
                if (j == i) continue;
                float d = peaks[j].pos.DistanceTo(peaks[i].pos);
                if (d < bestD)
                {
                    bestD = d;
                    mid = (peaks[i].pos + peaks[j].pos) / 2f;
                }
            }
            if (bestD < float.MaxValue && candidates.TrueForAll(s => s.DistanceTo(mid) > 40f))
                candidates.Add(mid);
        }
        candidates.Sort((a, b) =>
            map.Height.SampleWorld(b.X - MapGrid.Size / 2f, b.Y - MapGrid.Size / 2f)
            .CompareTo(map.Height.SampleWorld(a.X - MapGrid.Size / 2f, a.Y - MapGrid.Size / 2f))); // 海拔高者先走（成干流）

        int count = Math.Min(candidates.Count,
            WaterConfig.RiverCountMin + rng.Next(WaterConfig.RiverCountMax - WaterConfig.RiverCountMin + 1));
        var sources = new List<Vector2I>();
        for (int i = 0; i < count; i++)
            sources.Add(new Vector2I(
                Math.Clamp((int)candidates[i].X, 1, MapGrid.Size - 2),
                Math.Clamp((int)candidates[i].Y, 1, MapGrid.Size - 2)));
        return sources;
    }

    /// <summary>单条走线（1024² 格中心高上循坡）：8 邻取未访问的最低格；洼地/平地向东南强制滑行
    /// （维持西北→东南大势，连续强制超限即断）；撞既有水体即汇流终止；出图缘终止。</summary>
    private static List<Vector2I> TracePath(MapGrid map, Vector2I source)
    {
        var path = new List<Vector2I>();
        var visited = new HashSet<int>();
        int x = source.X, y = source.Y;
        int forcedRun = 0;

        for (int step = 0; step < MapGrid.Size * 4; step++)
        {
            if (x < 1 || y < 1 || x >= MapGrid.Size - 1 || y >= MapGrid.Size - 1)
            {
                path.Add(new Vector2I(x, y));
                break; // 出图缘：河口
            }
            var cur = new Vector2I(x, y);
            path.Add(cur);
            visited.Add(y * MapGrid.Size + x);

            // 汇流检测（离源 32 步后才检，防在源头附近自撞）
            if (step > 32 && map.CellAt(cur).HasWater)
                break;

            // 8 邻中选未走过的最低格
            int bx = 0, by = 0;
            float bestH = float.MaxValue;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oy == 0) continue;
                    int nx = x + ox, ny = y + oy;
                    if (nx < 0 || ny < 0 || nx >= MapGrid.Size || ny >= MapGrid.Size
                        || visited.Contains(ny * MapGrid.Size + nx))
                        continue;
                    float h = map.Height.CellCenterH(new Vector2I(nx, ny));
                    if (h < bestH)
                    {
                        bestH = h;
                        bx = ox; by = oy;
                    }
                }
            }
            float curH = map.Height.CellCenterH(cur);
            if (bestH >= curH - 0.0001f)
            {
                // 洼地/平地：向东南强制滑行（东/东南/南三邻取最低），维持大势；连续强制过久即弃
                if (++forcedRun > WaterConfig.MaxForcedSteps)
                    break;
                bx = 1; by = 1;
                float hE = map.Height.CellCenterH(new Vector2I(x + 1, y));
                float hS = map.Height.CellCenterH(new Vector2I(x, y + 1));
                float hSE = map.Height.CellCenterH(new Vector2I(x + 1, y + 1));
                if (hE <= hS && hE <= hSE && !visited.Contains(y * MapGrid.Size + x + 1)) { bx = 1; by = 0; }
                else if (hS <= hSE && !visited.Contains((y + 1) * MapGrid.Size + x)) { bx = 0; by = 1; }
            }
            else
            {
                forcedRun = 0;
            }
            if (bx == 0 && by == 0)
                break; // 无处可走
            x += bx; y += by;
        }
        return path;
    }

    // ---- ② 沿程水位 ----

    /// <summary>沿程水位：走线上的格中心高 → 滑动平均（滤逐米噪声）→ 运行最小值（水不倒流上坡）
    /// → clamp 下限 MinWaterLevel（以 0 为最低点）。地势台阶保留成急流/跌水观感。</summary>
    private static float[] ComputeLevels(MapGrid map, List<Vector2I> path)
    {
        int n = path.Count;
        var terrain = new float[n];
        for (int i = 0; i < n; i++)
            terrain[i] = map.Height.CellCenterH(path[i]);

        // 滑动平均（窗口 LevelSmoothWindow，边缘缩窗）
        var smooth = new float[n];
        int hw = WaterConfig.LevelSmoothWindow / 2;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - hw), b = Math.Min(n - 1, i + hw);
            float sum = 0;
            for (int j = a; j <= b; j++)
                sum += terrain[j];
            smooth[i] = sum / (b - a + 1);
        }

        // 运行最小 + 下限 0
        var levels = new float[n];
        float run = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            run = Math.Min(run, smooth[i]);
            levels[i] = Math.Max(WaterConfig.MinWaterLevel, run);
        }
        return levels;
    }

    // ---- ③ 刻水（河/湖）----

    /// <summary>沿走线逐点刻圆盘：宽度沿程渐宽（干流封顶 RiverWidthMouth、支线 BranchWidthMouth），
    /// 流向取走线切向八方向；支流汇入时把尾段水位回灌抬平到干流水面（backwater）。</summary>
    private static void CarveRiver(MapGrid map, List<Vector2I> path, float[] levels, bool isMain)
    {
        int n = path.Count;
        float mouthW = isMain ? WaterConfig.RiverWidthMouth : WaterConfig.BranchWidthMouth;

        // 汇流回灌：终点若落在既有水体上且其水面高于本线尾段，把尾段抬平到汇入点水面（免倒挂）
        var last = path[n - 1];
        if (MapGrid.InBounds(last) && map.CellAt(last).HasWater)
        {
            float mouthLevel = map.CellAt(last).WaterH;
            for (int i = n - 1; i >= 0 && levels[i] < mouthLevel; i--)
                levels[i] = mouthLevel;
        }

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Math.Max(1, n - 1);
            float width = Mathf.Lerp(WaterConfig.RiverWidthSource, mouthW, t);
            var dir = i + 1 < n ? path[i + 1] - path[i] : path[i] - path[Math.Max(0, i - 1)];
            byte flow = EncodeFlow(dir.X, dir.Y);
            CarveDisk(map, path[i], width / 2f, flow, levels[i]);
        }
    }

    /// <summary>干流低平处点湖（1~2 座）：湖面 = 该点河水位；谐波湾汊圈内「地形低于湖面」的格才着水
    /// （静水、流向清零），高于湖面的高地自然留成湖中岛/岬角——不再强制抠岛。</summary>
    private static void PlaceLakes(MapGrid map, List<Vector2I> path, float[] levels, Random rng)
    {
        int lakes = WaterConfig.RiverLakeMin + rng.Next(WaterConfig.RiverLakeMax - WaterConfig.RiverLakeMin + 1);
        int n = path.Count;
        for (int i = 0; i < lakes; i++)
        {
            // 中后段取低平点（水位低于 LakeMaxSiteLevel 才成湖，山区不点湖）
            int idx = n / 3 + rng.Next(Math.Max(1, n / 2));
            if (idx >= n || levels[idx] > WaterConfig.LakeMaxSiteLevel)
                continue;
            var center = path[idx];
            float level = levels[idx];
            int baseR = WaterConfig.BigLakeRadiusMin + rng.Next(WaterConfig.BigLakeRadiusMax - WaterConfig.BigLakeRadiusMin + 1);

            // 三组随机相位谐波，叠出扭曲湖缘
            double p1 = rng.NextDouble() * Math.PI * 2, p2 = rng.NextDouble() * Math.PI * 2, p3 = rng.NextDouble() * Math.PI * 2;
            double w = WaterConfig.LakeEdgeWaviness;
            int rMax = (int)(baseR * (1 + w)) + 2;

            for (int ox = -rMax; ox <= rMax; ox++)
            {
                for (int oy = -rMax; oy <= rMax; oy++)
                {
                    var c = center + new Vector2I(ox, oy);
                    if (!MapGrid.InBounds(c))
                        continue;
                    double dist = Math.Sqrt(ox * ox + oy * oy);
                    double theta = Math.Atan2(oy, ox);
                    double edge = baseR * (1 + w * (0.5 * Math.Sin(3 * theta + p1) + 0.3 * Math.Sin(5 * theta + p2) + 0.2 * Math.Sin(7 * theta + p3)));
                    if (dist > edge)
                        continue;
                    // 地形高出湖面超并入容差的格留作湖中岛/岬角；容差内并入湖盆（后续河床下压削到水下）
                    if (map.Height.CellCenterH(c) >= level + WaterConfig.LakeFloodTolerance && !map.CellAt(c).HasWater)
                        continue;
                    ref var cell = ref map.CellAt(c);
                    cell.HasWater = true;
                    cell.WaterH = level;
                    cell.FlowDir = 0; // 湖为静水
                }
            }
        }
    }

    /// <summary>以 center 为圆心刻一片水面圆盘（半径 radius 米，流向 flow，水位 level）：
    /// 已是水的格保留原水位/流向（上游先刻，汇流处以先到者为准），新格全量赋值。</summary>
    private static void CarveDisk(MapGrid map, Vector2I center, float radius, byte flow, float level)
    {
        int r = Mathf.CeilToInt(radius);
        for (int ox = -r; ox <= r; ox++)
            for (int oy = -r; oy <= r; oy++)
            {
                if (ox * ox + oy * oy > radius * radius)
                    continue;
                var c = center + new Vector2I(ox, oy);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref map.CellAt(c);
                if (cell.HasWater)
                    continue; // 先到者为准（同河重叠盖戳水位连续，异河汇流不互改）
                cell.HasWater = true;
                cell.WaterH = level;
                cell.FlowDir = flow;
            }
    }

    // ---- ④ 河床下压（唯一的地形修改）----

    /// <summary>河床下压（顶点高度场）：多源 BFS 算每个水格的离岸距离，
    /// 深度按距离从 BedDepthEdge 插值到 BedDepthCenter（BedFalloffDist 处满深），
    /// 把水格四角顶点压到「本格水面 - 深度」（只降不升）。
    /// 岸缘共享顶点被拉到浅滩深度：平原岸缓入水；山区河谷两壁自然成峡谷陡岸。</summary>
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

        // 2) 逐水格下压四角顶点：目标高度 = 本格水面 - 深度（离岸越远越深，只降不升）
        for (int y = 0; y < MapGrid.Size; y++)
        {
            for (int x = 0; x < MapGrid.Size; x++)
            {
                ref var cell = ref map.CellAt(x, y);
                if (!cell.HasWater)
                    continue;
                int d = dist[y * MapGrid.Size + x];
                if (d <= 0)
                    d = WaterConfig.BedFalloffDist; // 孤立未达格兜底：按满深处理
                float t = Mathf.Min(1f, (d - 1) / (float)WaterConfig.BedFalloffDist);
                float target = cell.WaterH - Mathf.Lerp(WaterConfig.BedDepthEdge, WaterConfig.BedDepthCenter, t);
                for (int vx = x; vx <= x + 1; vx++)
                    for (int vy = y; vy <= y + 1; vy++)
                        if (hf.VertexH(vx, vy) > target)
                            hf.SetVertex(vx, vy, target);
            }
        }
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
