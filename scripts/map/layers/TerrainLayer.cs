using Godot;

namespace Bianjing;

/// <summary>
/// 地形层：每分块一张 65×65 顶点的三角网格——平滑法线受光，顶点色随海拔/坡度渐变。
/// 拆出来后地形与水面/道路/植被/建筑互不牵连：改地形着色不会碰动其它层的重建路径。
/// </summary>
public partial class TerrainLayer : MapLayer
{
    /// <summary>平原混色高度上限（米）：0~此高度内暗橄榄→橄榄→亮橄榄渐变，其后再渐透岩色。</summary>
    private const float PlainMixMaxMeters = 12f;

    /// <summary>岩色渐透跨度（米）：12m 起渐透、52m 全岩（坡度显岩另按 bySlope 取大）。
    /// 与 TerrainConfig.MaxTerrainHeight 解耦——主峰抬到 110m 后仍保持这条山体着色带不变。</summary>
    private const float RockRampMeters = 40f;

    /// <summary>雪线（米）：极高主峰（88~105m）顶部由此渐白，与常规山体（≤62m）拉开层次；
    /// 与 GameMenu 高度色阶 64m 以上渐白呼应，小地图与实景观感一致。</summary>
    private const float SnowLineMeters = 68f;

    /// <summary>雪线渐白跨度（米）：68m 起始、93m 以上近全白。</summary>
    private const float SnowRampMeters = 25f;

    // ---- 平原橄榄色系（批次九十二调整，降饱和：向灰阶靠拢，明度层次保留）----
    // 参考显示色：常规光照下预期观感（#66793e 橄榄绿·高处 / #6d6d26 橄榄色·中 / #535f3e 暗橄榄绿·低洼）
    private static readonly Color PlainDarkDisplay = new(0.324f, 0.371f, 0.242f);
    private static readonly Color PlainMidDisplay = new(0.427f, 0.427f, 0.151f);
    private static readonly Color PlainLightDisplay = new(0.400f, 0.475f, 0.243f);
    /// <summary>雪面参考显示色：近白微冷，主峰戴上雪冠。</summary>
    private static readonly Color SnowDisplay = new(0.93f, 0.94f, 0.92f);

    // 拟合顶点色：反向跑完整渲染管线（sRGB→线性→光照增益→Filmic→Adjustment→sRGB）使显示 ≈ 参考色。
    // 渲染管线是非线性的（线性空间光照 + Filmic 暗部大幅提亮 + 降饱和），解析预补偿不准（曾两版偏黄），
    // 改静态初始化时逐通道二分拟合；光照参数变更时 SimulatePipeline 内增益公式同步 WorldConfig。
    private static readonly Color PlainDarkVertex = FitVertex(PlainDarkDisplay);
    private static readonly Color PlainMidVertex = FitVertex(PlainMidDisplay);
    private static readonly Color PlainLightVertex = FitVertex(PlainLightDisplay);
    private static readonly Color SnowVertex = FitVertex(SnowDisplay);

    /// <summary>山顶/陡壁灰褐岩。</summary>
    private static readonly Color TerrainHighColor = new(0.51f, 0.47f, 0.41f);
    /// <summary>水下河床泥沙。</summary>
    private static readonly Color BedColor = new(0.47f, 0.43f, 0.33f);

    private StandardMaterial3D _mat;
    private MeshInstance3D[] _meshes;

    public override void Build()
    {
        _mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _meshes = new MeshInstance3D[ChunksPerSide * ChunksPerSide];
        for (int i = 0; i < _meshes.Length; i++)
        {
            _meshes[i] = new MeshInstance3D { Name = $"Terrain{i}", MaterialOverride = _mat };
            AddChild(_meshes[i]);
        }
    }

    /// <summary>重建单块地形三角网格：块内 (格数+1)² 顶点、每格两三角面。
    /// 顶点色按「水下河床 / 平原橄榄混色 / 岩褐 / 雪白」四段取，法线中央差分使山体受光连续。</summary>
    public void ApplyChunk(int index)
    {
        var gs = GameState.I;
        var hf = gs.Map.Height;
        var (x0, y0, x1, y1) = ChunkRect(index);
        const float cs = MapGrid.CellSize;
        float half = MapGrid.Size * cs / 2f;

        int nx = x1 - x0, nz = y1 - y0;
        int vw = nx + 1;
        var verts = new Vector3[vw * (nz + 1)];
        var normals = new Vector3[verts.Length];
        var colors = new Color[verts.Length];
        for (int vy = 0; vy <= nz; vy++)
        {
            for (int vx = 0; vx <= nx; vx++)
            {
                int gvx = x0 + vx, gvy = y0 + vy;
                float h = hf.VertexH(gvx, gvy);
                var n = LayerKit.VertexNormal(hf, gvx, gvy);
                int vi = vy * vw + vx;
                verts[vi] = new Vector3(gvx * cs - half, h, gvy * cs - half);
                normals[vi] = n;
                colors[vi] = VertexColor(h, n, LocalWaterAtVertex(gs, gvx, gvy), gvx, gvy);
            }
        }
        var idx = new int[nx * nz * 6];
        int ii = 0;
        for (int gy = 0; gy < nz; gy++)
        {
            for (int gx = 0; gx < nx; gx++)
            {
                // Godot 以顺时针为正面（俯视）：每格两三角面拼成四边形
                int v00 = gy * vw + gx, v10 = v00 + 1, v01 = v00 + vw, v11 = v01 + 1;
                idx[ii++] = v00; idx[ii++] = v10; idx[ii++] = v01;
                idx[ii++] = v10; idx[ii++] = v11; idx[ii++] = v01;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _meshes[index].Mesh = mesh;
    }

    /// <summary>地形顶点色：水下（低于本地水面 localWater）→河床泥沙（越深越暗）；
    /// 陆上平原区按海拔三色橄榄混色（批次九十二：参考 #6b8e23/#808000/#556b2f 经 FitVertex 反推
    /// 顶点色，常规光照下显示 ≈ 色值直出；顶点伪随机抖动出草地斑驳），
    /// 高处/陡壁再渐透岩褐，雪线（68m）以上最后渐透雪白。
    /// localWater 取自共享该顶点的水格最高水位（无水邻格为 -∞，不走河床色）。</summary>
    private static Color VertexColor(float h, Vector3 normal, float localWater, int gx, int gy)
    {
        if (h < localWater)
            return BedColor.Darkened(Mathf.Clamp((localWater - h) * 0.25f, 0f, 0.35f));
        float bySlope = Mathf.Clamp((1f - normal.Y) * 2.4f, 0f, 1f); // 30°坡时约 0.32，开始透岩色

        // 平原混色：0~12m 内暗橄榄→橄榄→亮橄榄（拟合顶点色，显示 ≈ 参考色），叠加稳定伪随机抖动（±6%）显斑驳
        float plainK = Mathf.Clamp(h / PlainMixMaxMeters, 0f, 1f);
        plainK = Mathf.Clamp(plainK + (VertexJitter(gx, gy) - 0.5f) * 0.12f, 0f, 1f);
        Color plain = plainK < 0.5f
            ? PlainDarkVertex.Lerp(PlainMidVertex, plainK * 2f)
            : PlainMidVertex.Lerp(PlainLightVertex, (plainK - 0.5f) * 2f);

        // 显岩：陡坡优先，其次高海拔（12m 起渐透，52m 全岩）
        float rock = Mathf.Max(bySlope, Mathf.Clamp((h - PlainMixMaxMeters) / RockRampMeters, 0f, 1f));
        var col = plain.Lerp(TerrainHighColor, rock);
        // 雪冠：极高主峰顶部渐白（雪线以下完全不受影响）
        return col.Lerp(SnowVertex, Mathf.Clamp((h - SnowLineMeters) / SnowRampMeters, 0f, 1f));
    }

    /// <summary>顶点伪随机（0~1）：固定坐标的稳定整数 hash，供草地斑驳混色抖动（同顶点永不闪变）。</summary>
    private static float VertexJitter(int x, int y)
    {
        uint n = (uint)(x * 374761393 + y * 668265263);
        n = (n ^ (n >> 13)) * 1274126177;
        return ((n ^ (n >> 16)) & 0xFFFFFF) / (float)0x1000000;
    }

    /// <summary>顶点处的局部水面高：共享该顶点的 ≤4 个水格中的最高水位（无水邻格返回负无穷）：
    /// 水位改逐格变化后，河床着色不能再用全图统一水位判淹没。</summary>
    private static float LocalWaterAtVertex(GameState gs, int vx, int vy)
    {
        float level = float.MinValue;
        for (int ox = -1; ox <= 0; ox++)
            for (int oy = -1; oy <= 0; oy++)
            {
                var c = new Vector2I(vx + ox, vy + oy);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.HasWater && cell.WaterH > level)
                    level = cell.WaterH;
            }
        return level;
    }

    // ---- 渲染管线模拟（供 FitVertex 反推顶点色）----
    // 增益：暖阳（1.0/0.96/0.88 ×DaySunEnergy）+ Sky 暖环境（0.78/0.75/0.67 ×DayAmbientEnergy）逐通道累加；
    // 环境色以地平线暖灰近似（天空半球含顶部蓝，实际略偏蓝，残余偏差可由显示色常量微调吸收）
    private static float RGain => WorldConfig.DaySunEnergy + WorldConfig.DayAmbientEnergy * 0.78f;
    private static float GGain => WorldConfig.DaySunEnergy * 0.96f + WorldConfig.DayAmbientEnergy * 0.75f;
    private static float BGain => WorldConfig.DaySunEnergy * 0.88f + WorldConfig.DayAmbientEnergy * 0.67f;

    /// <summary>sRGB → 线性（顶点色按 sRGB 输入，材质转线性空间做光照）。</summary>
    private static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>线性 → sRGB（tonemap 后输出到显示器的还原）。</summary>
    private static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;

    /// <summary>Filmic 色调映射（Godot tonemap_filmic，Hejl-Burgess-Dawson 曲线：暗部大幅提亮，
    /// 是顶点色直出与最终显示差异的主要来源之一）。</summary>
    private static float Filmic(float x)
    {
        x = Mathf.Max(0f, x - 0.004f);
        return (x * (6.2f * x + 0.5f)) / (x * (6.2f * x + 1.7f) + 0.06f);
    }

    /// <summary>完整管线模拟：sRGB 顶点色 → 线性 → 光照增益 → Filmic → Adjustment（降饱和 0.85 ×亮度 0.97）→ sRGB 显示。
    /// 逐通道单调递增，供二分反推。Adjustment 参数同步 Main.SetupEnvironment。</summary>
    private static Color SimulatePipeline(Color v)
    {
        float r = Filmic(SrgbToLinear(v.R) * RGain);
        float g = Filmic(SrgbToLinear(v.G) * GGain);
        float b = Filmic(SrgbToLinear(v.B) * BGain);
        float gray = (r + g + b) / 3f;
        r = (gray + (r - gray) * 0.85f) * 0.97f;
        g = (gray + (g - gray) * 0.85f) * 0.97f;
        b = (gray + (b - gray) * 0.85f) * 0.97f;
        return new Color(LinearToSrgb(Mathf.Clamp(r, 0f, 1f)),
            LinearToSrgb(Mathf.Clamp(g, 0f, 1f)), LinearToSrgb(Mathf.Clamp(b, 0f, 1f)));
    }

    /// <summary>反推顶点色：逐通道二分（24 次迭代，收敛精度 2⁻²⁴），使 SimulatePipeline(顶点色) ≈ 目标显示色。</summary>
    private static Color FitVertex(Color target)
    {
        var lo = new Color(0f, 0f, 0f);
        var hi = new Color(1f, 1f, 1f);
        for (int it = 0; it < 24; it++)
        {
            var mid = (lo + hi) / 2f;
            var sim = SimulatePipeline(mid);
            if (sim.R < target.R) lo.R = mid.R; else hi.R = mid.R;
            if (sim.G < target.G) lo.G = mid.G; else hi.G = mid.G;
            if (sim.B < target.B) lo.B = mid.B; else hi.B = mid.B;
        }
        return (lo + hi) / 2f;
    }
}
