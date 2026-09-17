using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 水系落地（批次六十九重写）：把 RiverNetwork 的河道折线与 LakeGenerator 的湖群写进地图网格，
/// 再统一水位、下压河床。四步：
/// ① <b>刻河</b>：沿折线按「与河宽成比例」的步距盖圆盘，写入 HasWater/WaterH/FlowDir；
///    水面 = 平滑后的沿程地形 - RiverSurfaceDrop，取运行最小保证不倒流；重叠处取较高者（上游优先）。
/// ② <b>落湖</b>：湖格整体覆盖（同湖统一水位、FlowDir=0 静水），湖面是权威值。
/// ③ <b>统一水位</b>：在 FlowRouter 的洪泛拓扑序上单趟扫描（先下游后上游），
///    令「水位沿流向单调不增」——支流汇入处自动回灌抬平（backwater），不再出现水位倒挂；
///    湖面冻结不参与抬升（出水口呈跌水，符合真实湖口形态）。
/// ④ <b>河床下压</b>（唯一的地形修改）：按连通水体分别求多源 BFS 离岸距离，
///    深度按水体自身的最大离岸距离缩放——窄河仍是浅槽，大湖自然成深盆。
/// 全程除 ④ 外只读地势，保证地形生成算法的纯粹性。
/// </summary>
public static class RiverGenerator
{
    /// <summary>水系总入口：刻河 → 落湖 → 统一水位 → 下压河床。</summary>
    public static void BuildWaterSystem(MapGrid map, FlowField flow,
        List<RiverPath> rivers, List<LakeShape> lakes)
    {
        foreach (var river in rivers)
            CarveRiver(map, river);
        StampLakes(map, lakes);
        SolveLevels(map, flow);
        CarveBed(map);
    }

    // ---- ① 刻河 ----

    /// <summary>沿折线刻一条河：先由沿程地形求水位（滑动平均 → 运行最小 → 让出岸顶 → 下限 0），
    /// 再按弧长步进盖圆盘（步距 ∝ 河宽：窄溪密、宽河疏），末点补一刀保证河口满宽。</summary>
    private static void CarveRiver(MapGrid map, RiverPath path)
    {
        int n = path.Points.Count;
        if (n < 2)
            return;

        var levels = ComputeLevels(path.Terrain);

        var arc = new float[n];
        for (int i = 1; i < n; i++)
            arc[i] = arc[i - 1] + path.Points[i].DistanceTo(path.Points[i - 1]);
        float total = arc[n - 1];
        if (total < 1f)
            return;

        int seg = 0;
        for (float s = 0f; s <= total;)
        {
            while (seg < n - 2 && arc[seg + 1] < s)
                seg++;
            float span = arc[seg + 1] - arc[seg];
            float t = span > 1e-4f ? (s - arc[seg]) / span : 0f;
            var p = path.Points[seg].Lerp(path.Points[seg + 1], t);
            float w = Mathf.Lerp(path.Widths[seg], path.Widths[seg + 1], t);
            float lv = Mathf.Lerp(levels[seg], levels[seg + 1], t);
            var dir = path.Points[seg + 1] - path.Points[seg];
            CarveDisk(map, p, w * 0.5f, EncodeFlow(dir.X, dir.Y), lv);
            s += Mathf.Max(0.6f, w * 0.3f);
        }

        // 末点补刻：河口在图外时最后一段步进可能刚好跨过图缘，补一刀保证图缘处满宽
        var tail = path.Points[n - 1] - path.Points[n - 2];
        CarveDisk(map, path.Points[n - 1], path.Widths[n - 1] * 0.5f,
            EncodeFlow(tail.X, tail.Y), levels[n - 1]);
    }

    /// <summary>沿程水位：折线上的地形高 → 滑动平均（滤逐米噪声）→ 运行最小（水不倒流上坡）
    /// → 让出岸顶 RiverSurfaceDrop（岸坡露出水面而非与水齐平）→ 下限 MinWaterLevel。</summary>
    private static float[] ComputeLevels(List<float> terrain)
    {
        int n = terrain.Count;
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

        var levels = new float[n];
        float run = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            run = Math.Min(run, smooth[i]);
            levels[i] = Mathf.Max(WaterConfig.MinWaterLevel, run - WaterConfig.RiverSurfaceDrop);
        }
        return levels;
    }

    /// <summary>以 p（连续格坐标）为圆心盖一片水面圆盘（半径 radius 米、流向 flow、水位 level）：
    /// 已是水的格只抬高不压低（上游/湖面优先，互不削弱）；新格全量赋值。</summary>
    private static void CarveDisk(MapGrid map, Vector2 p, float radius, byte flow, float level)
    {
        int r = Mathf.CeilToInt(radius);
        int cx = Mathf.FloorToInt(p.X), cy = Mathf.FloorToInt(p.Y);
        float rr = radius * radius;
        for (int oy = -r; oy <= r; oy++)
        {
            for (int ox = -r; ox <= r; ox++)
            {
                // 按格中心到圆心的距离判定（格 (x,y) 的中心 = x+0.5）
                float dx = cx + ox + 0.5f - p.X, dy = cy + oy + 0.5f - p.Y;
                if (dx * dx + dy * dy > rr)
                    continue;
                var c = new Vector2I(cx + ox, cy + oy);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref map.CellAt(c);
                if (cell.HasWater)
                {
                    if (level > cell.WaterH)
                        cell.WaterH = level;
                    continue; // 保留先到者的流向（上游河段/湖面不被后来者改写）
                }
                cell.HasWater = true;
                cell.WaterH = level;
                cell.FlowDir = flow;
            }
        }
    }

    // ---- ② 落湖 ----

    /// <summary>湖格整体覆盖写入：同湖统一水位、FlowDir=0（静水）。
    /// 湖面是权威值——覆盖河格，让穿湖的河段与湖面齐平。</summary>
    private static void StampLakes(MapGrid map, List<LakeShape> lakes)
    {
        foreach (var lake in lakes)
        {
            foreach (var c in lake.Cells)
            {
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref map.CellAt(c);
                cell.HasWater = true;
                cell.WaterH = lake.Level;
                cell.FlowDir = 0; // 湖为静水
            }
        }
    }

    // ---- ③ 统一水位 ----

    /// <summary>沿 FlowRouter 的洪泛拓扑序单趟扫描，令水位沿流向单调不增：
    /// 出队序即填充高升序（先下游后上游），扫到某格时其下游格的水位已是终值，
    /// 直接取 max 即可——支流汇入干流处自动回灌抬平。
    /// 湖面（FlowDir=0）冻结不抬：保住湖自身水位，出水口呈跌水。
    /// 分辨率：路由格（成品 2m）——河湖混杂在同一路由格时以湖面为准。</summary>
    private static void SolveLevels(MapGrid map, FlowField flow)
    {
        int ds = Math.Max(1, flow.Downsample);
        int rs = flow.Size;
        int rn = rs * rs;

        var riverLevel = new float[rn];
        var hasRiver = new bool[rn];
        var lakeLevel = new float[rn];
        var isLake = new bool[rn];

        // 1m 格 → 路由格聚合：湖格与河格分开累计，同格取最高
        for (int y = 0; y < MapGrid.Size; y++)
        {
            for (int x = 0; x < MapGrid.Size; x++)
            {
                ref var cell = ref map.CellAt(x, y);
                if (!cell.HasWater)
                    continue;
                int ri = (y / ds) * rs + (x / ds);
                if (cell.FlowDir == 0)
                {
                    if (!isLake[ri] || cell.WaterH > lakeLevel[ri])
                        lakeLevel[ri] = cell.WaterH;
                    isLake[ri] = true;
                }
                else if (!isLake[ri])
                {
                    if (!hasRiver[ri] || cell.WaterH > riverLevel[ri])
                        riverLevel[ri] = cell.WaterH;
                    hasRiver[ri] = true;
                }
            }
        }

        var level = new float[rn];
        var hasWater = new bool[rn];
        for (int i = 0; i < rn; i++)
        {
            if (isLake[i])
            {
                level[i] = lakeLevel[i];
                hasWater[i] = true;
            }
            else if (hasRiver[i])
            {
                level[i] = riverLevel[i];
                hasWater[i] = true;
            }
        }

        // 拓扑单趟：下游 → 上游（Order 即填充高升序）
        for (int k = 0; k < rn; k++)
        {
            int c = flow.Order[k];
            if (!hasWater[c] || isLake[c])
                continue;
            int t = flow.Dir[c];
            if (t < 0 || !hasWater[t])
                continue;
            if (level[c] < level[t])
                level[c] = level[t]; // 回灌抬平（水位沿流向单调不增）
        }

        // 写回 1m 格
        for (int y = 0; y < MapGrid.Size; y++)
        {
            for (int x = 0; x < MapGrid.Size; x++)
            {
                ref var cell = ref map.CellAt(x, y);
                if (!cell.HasWater)
                    continue;
                int ri = (y / ds) * rs + (x / ds);
                if (hasWater[ri])
                    cell.WaterH = level[ri];
            }
        }
    }

    // ---- ④ 河床下压（唯一的地形修改）----

    /// <summary>按连通水体分别下压：多源 BFS 求每个水格的离岸距离，
    /// 深度按<b>本水体</b>的最大离岸距离缩放（窄河 → BedDepthCenterMin 浅槽，大湖 → BedDepthCenterMax 深盆），
    /// 由岸缘的 BedDepthEdge 浅滩插值到中心满深，只降不升。
    /// 岸缘共享顶点被拉到浅滩深度：平原岸缓入水；山区河谷两壁自然成峡谷陡岸。</summary>
    private static void CarveBed(MapGrid map)
    {
        int size = MapGrid.Size;
        int n = size * size;
        var comp = new int[n];
        var dist = new int[n];
        var queue = new int[n];
        var members = new List<int>();
        int compId = 0;

        for (int seed = 0; seed < n; seed++)
        {
            if (!map.CellAt(seed % size, seed / size).HasWater || comp[seed] != 0)
                continue;

            // 1) 连通分量（4-连通）：一个水体一套深度参数
            compId++;
            members.Clear();
            int qh = 0, qt = 0;
            comp[seed] = compId;
            queue[qt++] = seed;
            while (qh < qt)
            {
                int idx = queue[qh++];
                members.Add(idx);
                int cx = idx % size, cy = idx / size;
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int ny = cy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= size || ny >= size)
                        continue;
                    int ni = ny * size + nx;
                    if (comp[ni] != 0 || !map.CellAt(nx, ny).HasWater)
                        continue;
                    comp[ni] = compId;
                    queue[qt++] = ni;
                }
            }

            // 2) 多源 BFS：贴岸水格（四邻含陆地/图缘）为源，向水体内部扩散
            qh = 0; qt = 0;
            int maxDist = 1;
            foreach (int idx in members)
            {
                int cx = idx % size, cy = idx / size;
                bool shore = false;
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int ny = cy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= size || ny >= size || !map.CellAt(nx, ny).HasWater)
                    {
                        shore = true;
                        break;
                    }
                }
                if (!shore)
                    continue;
                dist[idx] = 1;
                queue[qt++] = idx;
            }
            while (qh < qt)
            {
                int idx = queue[qh++];
                int cx = idx % size, cy = idx / size;
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int ny = cy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= size || ny >= size)
                        continue;
                    int ni = ny * size + nx;
                    if (comp[ni] != compId || dist[ni] != 0)
                        continue;
                    dist[ni] = dist[idx] + 1;
                    if (dist[ni] > maxDist)
                        maxDist = dist[ni];
                    queue[qt++] = ni;
                }
            }

            // 3) 深度按水体胖瘦缩放：最大离岸距离越大，中心越深、过渡带越宽
            float t = Mathf.Clamp((maxDist - WaterConfig.BedDepthShallowDist)
                / (WaterConfig.BedDepthDeepDist - WaterConfig.BedDepthShallowDist), 0f, 1f);
            float centerDepth = Mathf.Lerp(WaterConfig.BedDepthCenterMin, WaterConfig.BedDepthCenterMax, t);
            float falloff = Mathf.Clamp(maxDist * WaterConfig.BedFalloffRatio,
                WaterConfig.BedFalloffMin, WaterConfig.BedFalloffMax);
            float floorH = TerrainConfig.MinTerrainHeight + 0.05f;

            var hf = map.Height;
            foreach (int idx in members)
            {
                int cx = idx % size, cy = idx / size;
                float d = dist[idx];
                if (d <= 0)
                    d = maxDist; // 未达格兜底：按满深处理
                float k2 = Mathf.Min(1f, (d - 1) / falloff);
                float target = Mathf.Max(
                    map.CellAt(cx, cy).WaterH - Mathf.Lerp(WaterConfig.BedDepthEdge, centerDepth, k2),
                    floorH);
                for (int vx = cx; vx <= cx + 1; vx++)
                    for (int vy = cy; vy <= cy + 1; vy++)
                        if (hf.VertexH(vx, vy) > target)
                            hf.SetVertex(vx, vy, target);
            }
        }
    }

    /// <summary>把方向分量 (sx,sy) 量化为八方向编码：0=静水，1=东,2=东南,3=南,4=西南,5=西,6=西北,7=北,8=东北。</summary>
    public static byte EncodeFlow(float sx, float sy)
    {
        int x = Math.Sign(sx), y = Math.Sign(sy);
        return (x, y) switch
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
