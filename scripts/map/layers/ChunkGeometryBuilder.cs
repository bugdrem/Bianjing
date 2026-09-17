using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 分块几何构建器：对单块 64×64 格做**一趟**遍历，同时产出水面 / 三类道路 / 桥面 / 树木全部缓冲，
/// 再由 WaterLayer、RoadLayer、VegetationLayer 各取所需。
/// 原先这些收集逻辑与地形网格生成挤在同一个 RebuildChunk 里；图层拆开后若让四个图层各自扫一遍块，
/// 遍历量翻四倍——单趟产出 + 分发正是拆分仍能保住性能的关键。
/// 本类只产出数据，不持有任何节点：图层归属与材质由各图层自管。
/// </summary>
public static class ChunkGeometryBuilder
{
    /// <summary>灰绿浑水（参考宋画运河色，去蓝去艳）；顶点色 alpha 另带 WaterAlpha。</summary>
    private static readonly Color WaterColor = new(0.45f, 0.49f, 0.40f);
    /// <summary>石桥灰白（原木褐偏艳）。</summary>
    private static readonly Color BridgeColor = new(0.62f, 0.60f, 0.54f);
    /// <summary>灰绿树冠（压饱和度）。</summary>
    private static readonly Color TreeColor = new(0.30f, 0.40f, 0.26f);
    /// <summary>果树：暖黄绿树冠，一眼可辨。</summary>
    private static readonly Color FruitTreeColor = new(0.46f, 0.47f, 0.24f);
    /// <summary>树干木褐。</summary>
    private static readonly Color TrunkColor = new(0.38f, 0.30f, 0.22f);
    /// <summary>路面顶面顶点色：中性近白，砖色/明暗全由砖纹贴图承载（贴图×顶点色）。</summary>
    private static readonly Color RoadTopColor = new(0.97f, 0.96f, 0.93f);

    /// <summary>失修路面的目标色（批次九十五）：路况越差，路面越朝这个土褐色靠。
    /// 作为道路老化的<b>可见反馈</b>——移速变慢玩家未必察觉，颜色变暗发土一眼就能看出该养护哪段。</summary>
    private static readonly Color RoadWornColor = new(0.52f, 0.46f, 0.38f);
    /// <summary>路缘地基立面顶点色：暗灰，乘砖纹后读作台基侧壁。</summary>
    private static readonly Color RoadSideColor = new(0.30f, 0.29f, 0.27f);

    /// <summary>水面透明度：微浑而仍透见河床（alpha 写进顶点色，材质走 Alpha 混色）。</summary>
    private const float WaterAlpha = 0.85f;

    /// <summary>单趟遍历块内所有格：按桥/水/路/岸/树分派收集，产出填进 geo。</summary>
    public static void Build(GameState gs, ChunkGeometry geo, int x0, int y0, int x1, int y1,
        ArrayMesh[] treeModelMeshes)
    {
        var hf = gs.Map.Height;
        for (int x = x0; x < x1; x++)
        {
            for (int y = y0; y < y1; y++)
            {
                ref var cell = ref gs.Map.CellAt(x, y);
                float groundY = gs.Map.GroundY(new Vector2I(x, y)); // 本格地面海拔（四角顶点均值）

                if (cell.HasBridge)
                {
                    // 桥格也是水面格：先铺桥下水面，再铺桥体板（否则桥下露底无水）
                    AddWaterQuad(gs, geo.Water, x, y, cell.WaterH, WaterAlphaColor());
                    // 桥面：实体桥体板（顶面 + 体厚），顶面四角取 DeckVertexTop；与引桥/道路同属桥面网格
                    AddDeckBox(gs, hf, geo.Bridge, x, y, BridgeColor);
                }
                else if (cell.HasWater)
                {
                    // 水面：四角顶点取邻水格 WaterH 均值（同地形顶点模式），坡河上连续倾斜不逐格阶梯
                    AddWaterQuad(gs, geo.Water, x, y, cell.WaterH, WaterAlphaColor());
                }
                else if (cell.HasRoad)
                {
                    if (gs.Map.NearBridge(x, y))
                    {
                        // 引桥：桥旁陆地路格——同桥面实体板渲染，按离桥距从桥面高渐降到岸路高，
                        // 与桥、与普通道路两头无缝相接
                        AddDeckBox(gs, hf, geo.Bridge, x, y, BridgeColor);
                    }
                    else
                    {
                        // 三类道路按种类分进不同砖纹网格（主路白石 / 辅路青砖 / 小路深青砖），
                        // 抬升统一取 RoadSurfaceLift；四角采地形顶点高，坡道上路面自然倾斜贴地；
                        // 外边缘垂一圈地基立面（暗顶点色乘砖纹）
                        var road = cell.RoadKind switch
                        {
                            RoadKind.Main => geo.RoadMain,
                            RoadKind.Lane => geo.RoadLane,
                            _ => geo.RoadSide,
                        };
                        // 批次九十五：路面颜色随路况变暗发土（道路时效软轴的可见反馈）
                        float roadQ = gs.RoadQualityFactor(new Vector2I(x, y));
                        AddDrapedQuad(hf, road, x, y, WorldConfig.RoadSurfaceLift,
                            RoadTopColor.Lerp(RoadWornColor, 1f - roadQ));
                        AddRoadFoundation(gs, hf, road, x, y, RoadSideColor);
                    }
                }
                else if (cell.BuildingId < 0)
                {
                    // 贴岸陆格（共享顶点被河床下压到邻格水位之下）也补一片水面：
                    // 水线落在水面与地形斜面的交线上，沿岸连续平滑，消除逐格锯齿；
                    // 邻格水位取四邻水格最高者
                    float shoreLevel = float.MinValue;
                    for (int i4 = 0; i4 < 4; i4++)
                    {
                        var nc = new Vector2I(x + (i4 == 0 ? 1 : i4 == 1 ? -1 : 0), y + (i4 == 2 ? 1 : i4 == 3 ? -1 : 0));
                        if (!MapGrid.InBounds(nc))
                            continue;
                        ref var ncell = ref gs.Map.CellAt(nc);
                        if (ncell.HasWater && ncell.WaterH > shoreLevel)
                            shoreLevel = ncell.WaterH;
                    }
                    if (shoreLevel > float.MinValue && hf.CellMinH(new Vector2I(x, y)) < shoreLevel)
                        AddWaterQuad(gs, geo.Water, x, y, shoreLevel, WaterAlphaColor());
                }

                // 树木：植物实体驱动，逐株造型抽到 CollectTree（树层单独刷新时复用）
                if (cell.HasTree)
                    CollectTree(gs, x, y, groundY, geo, treeModelMeshes);
            }
        }
    }

    /// <summary>只收集块内树木（月度生长/散播的轻量路径）：不碰水面/道路缓冲。</summary>
    public static void BuildTrees(GameState gs, ChunkGeometry geo, int x0, int y0, int x1, int y1,
        ArrayMesh[] treeModelMeshes)
    {
        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
                if (gs.Map.CellAt(x, y).HasTree)
                    CollectTree(gs, x, y, gs.Map.GroundY(new Vector2I(x, y)), geo, treeModelMeshes);
    }

    /// <summary>水面半透顶点色（灰绿浑水 + WaterAlpha）。</summary>
    private static Color WaterAlphaColor() => new(WaterColor.R, WaterColor.G, WaterColor.B, WaterAlpha);

    /// <summary>
    /// 往水面缓冲追加一格四边形：四角取顶点插值水位（WaterVertexH），
    /// 同地形三顶点模式——坡河上水面随水位连续倾斜，不再逐格阶梯错层。
    /// 外扩规则：**只在邻格非水（陆地/越界）的方向**向外扩 WaterEdgeOverlap，
    /// 让水平面钻到高岸下方、消除水陆交界的锯齿与空隙（原设计意图，保留）；
    /// 而**水与水相邻的方向不再外扩**——否则相邻水面互相重叠约 1.4m，半透明逐层叠加会
    /// (1) 让水面浮现 1m 周期的规则网格（平坦大湖面尤其刺眼，窄河道因太窄看不出来），
    /// (2) 叠加后实际不透明度 ≈99%，“透见河床”的效果形同失效。
    /// </summary>
    private static void AddWaterQuad(GameState gs, MeshBuffers buf, int x, int y, float fallback, Color col)
    {
        var v = buf.Verts; var n = buf.Normals; var c = buf.Colors; var idx = buf.Index;
        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        float m = WaterConfig.WaterEdgeOverlap;
        // 四个方向分别判断：邻格是水面 → 不外扩（共享边，严丝合缝）；否则外扩嵌入岸地
        float x0 = x - half - (IsWaterCell(gs, x - 1, y) ? 0f : m);
        float x1 = x + 1 - half + (IsWaterCell(gs, x + 1, y) ? 0f : m);
        float y0 = y - half - (IsWaterCell(gs, x, y - 1) ? 0f : m);
        float y1 = y + 1 - half + (IsWaterCell(gs, x, y + 1) ? 0f : m);
        int b = v.Count;
        v.Add(new Vector3(x0, WaterVertexH(gs, x, y, fallback), y0));
        v.Add(new Vector3(x1, WaterVertexH(gs, x + 1, y, fallback), y0));
        v.Add(new Vector3(x0, WaterVertexH(gs, x, y + 1, fallback), y1));
        v.Add(new Vector3(x1, WaterVertexH(gs, x + 1, y + 1, fallback), y1));
        for (int i = 0; i < 4; i++)
        {
            n.Add(Vector3.Up); // 水面坡度极缓，法线统一朝上足够（半透材质受光差异不可辨）
            c.Add(col);
        }
        idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
        idx.Add(b + 1); idx.Add(b + 3); idx.Add(b + 2);
    }

    /// <summary>该格是否为水面（含桥下水面）：决定水面四边形朝该方向是否外扩。
    /// 桥格虽另铺桥体板，其下仍有水面，故一并视作水面以保证水面连续。</summary>
    private static bool IsWaterCell(GameState gs, int x, int y)
    {
        if (!MapGrid.InBounds(new Vector2I(x, y)))
            return false;
        ref var cell = ref gs.Map.CellAt(x, y);
        return cell.HasWater || cell.HasBridge;
    }

    /// <summary>某顶点的水面高：共享该顶点的 ≤4 个水格 WaterH 均值（同地形顶点共享模式），
    /// 相邻水格间水面在共享顶点处同高→坡河上水面连续倾斜；无邻水格（岸补水内侧角）退回 fallback。</summary>
    private static float WaterVertexH(GameState gs, int vx, int vy, float fallback)
    {
        float sum = 0f;
        int n = 0;
        for (int ox = -1; ox <= 0; ox++)
            for (int oy = -1; oy <= 0; oy++)
            {
                var c = new Vector2I(vx + ox, vy + oy);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.HasWater)
                {
                    sum += cell.WaterH;
                    n++;
                }
            }
        return n > 0 ? sum / n : fallback;
    }

    /// <summary>往网格缓冲追加一格贴地四边形（道路用）：四角采地形顶点高 + 抬升，坡道上自然倾斜；
    /// UV 采世界坐标（米），材质 Uv1Scale 按砖周期缩放——全图连续平铺，分块边界纹理不错位。</summary>
    private static void AddDrapedQuad(HeightField hf, MeshBuffers buf, int x, int y, float lift, Color col)
    {
        var v = buf.Verts; var n = buf.Normals; var c = buf.Colors; var uv = buf.Uv; var idx = buf.Index;
        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        int b = v.Count;
        v.Add(new Vector3(x - half, hf.VertexH(x, y) + lift, y - half));
        v.Add(new Vector3(x + 1 - half, hf.VertexH(x + 1, y) + lift, y - half));
        v.Add(new Vector3(x - half, hf.VertexH(x, y + 1) + lift, y + 1 - half));
        v.Add(new Vector3(x + 1 - half, hf.VertexH(x + 1, y + 1) + lift, y + 1 - half));
        n.Add(LayerKit.VertexNormal(hf, x, y));
        n.Add(LayerKit.VertexNormal(hf, x + 1, y));
        n.Add(LayerKit.VertexNormal(hf, x, y + 1));
        n.Add(LayerKit.VertexNormal(hf, x + 1, y + 1));
        for (int i = 0; i < 4; i++)
            c.Add(col);
        uv.Add(new Vector2(x - half, y - half));
        uv.Add(new Vector2(x + 1 - half, y - half));
        uv.Add(new Vector2(x - half, y + 1 - half));
        uv.Add(new Vector2(x + 1 - half, y + 1 - half));
        idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
        idx.Add(b + 1); idx.Add(b + 3); idx.Add(b + 2);
    }

    /// <summary>往桥面缓冲追加一格实体桥体板：顶面四角取 MapGrid.DeckVertexTop（渲染与村民站面同源），
    /// 从顶面向下拉 BridgeBodyThickness 作底面与四侧壁——桥为实体板而非平面；
    /// 桥格坐桥面高平抬于水面之上，边缘引桥格自然渐降接岸路。</summary>
    private static void AddDeckBox(GameState gs, HeightField hf, MeshBuffers buf, int x, int y, Color col)
    {
        var v = buf.Verts; var n = buf.Normals; var c = buf.Colors; var idx = buf.Index;
        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        float thick = WorldConfig.BridgeBodyThickness;
        float x0 = x - half, x1 = x + 1 - half, z0 = y - half, z1 = y + 1 - half;
        float t00 = gs.Map.DeckVertexTop(x, y);
        float t10 = gs.Map.DeckVertexTop(x + 1, y);
        float t01 = gs.Map.DeckVertexTop(x, y + 1);
        float t11 = gs.Map.DeckVertexTop(x + 1, y + 1);

        // 顶面（法线朝上）
        AddQuad(buf,
            new Vector3(x0, t00, z0), new Vector3(x1, t10, z0),
            new Vector3(x0, t01, z1), new Vector3(x1, t11, z1), Vector3.Up, col);
        // 底面（下移体厚，法线朝下，绕序反转）
        AddQuad(buf,
            new Vector3(x1, t10 - thick, z0), new Vector3(x0, t00 - thick, z0),
            new Vector3(x1, t11 - thick, z1), new Vector3(x0, t01 - thick, z1), Vector3.Down, col);
        // 四侧壁（双面材质，绕序不拘）：南边 z0 / 北边 z1 / 西边 x0 / 东边 x1
        Color side = col.Darkened(0.15f);
        AddQuad(buf,
            new Vector3(x0, t00, z0), new Vector3(x1, t10, z0),
            new Vector3(x0, t00 - thick, z0), new Vector3(x1, t10 - thick, z0), new Vector3(0, 0, -1), side);
        AddQuad(buf,
            new Vector3(x1, t11, z1), new Vector3(x0, t01, z1),
            new Vector3(x1, t11 - thick, z1), new Vector3(x0, t01 - thick, z1), new Vector3(0, 0, 1), side);
        AddQuad(buf,
            new Vector3(x0, t01, z1), new Vector3(x0, t00, z0),
            new Vector3(x0, t01 - thick, z1), new Vector3(x0, t00 - thick, z0), new Vector3(-1, 0, 0), side);
        AddQuad(buf,
            new Vector3(x1, t10, z0), new Vector3(x1, t11, z1),
            new Vector3(x1, t10 - thick, z0), new Vector3(x1, t11 - thick, z1), new Vector3(1, 0, 0), side);
    }

    /// <summary>以四顶点（a=左上 b=右上 c=左下 d=右下）拼一四边形（两三角），统一法线/色；
    /// uvA~uvD 仅在缓冲带 UV 时写入（道路地基立面采世界坐标，砖缝竖线如砌墙侧壁）。</summary>
    private static void AddQuad(MeshBuffers buf, Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Vector3 nrm, Color col,
        Vector2 uvA = default, Vector2 uvB = default, Vector2 uvC = default, Vector2 uvD = default)
    {
        var v = buf.Verts; var nl = buf.Normals; var cl = buf.Colors; var uv = buf.Uv; var idx = buf.Index;
        int bi = v.Count;
        v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
        for (int i = 0; i < 4; i++) { nl.Add(nrm); cl.Add(col); }
        if (buf.UseUv) { uv.Add(uvA); uv.Add(uvB); uv.Add(uvC); uv.Add(uvD); }
        idx.Add(bi); idx.Add(bi + 1); idx.Add(bi + 2);
        idx.Add(bi + 1); idx.Add(bi + 3); idx.Add(bi + 2);
    }

    /// <summary>道路地基立面：本路格四边中邻格非路且非桥的边，垂一面从路面顶下到 −RoadFoundationDepth，
    /// 路面读作坐在高台基上（内部路-路边隐藏，只在路网轮廓垂基）；UV 采世界坐标，侧壁砖缝竖线如砌墙。</summary>
    private static void AddRoadFoundation(GameState gs, HeightField hf, MeshBuffers buf, int x, int y, Color col)
    {
        var v = buf.Verts; var n = buf.Normals; var c = buf.Colors; var uv = buf.Uv; var idx = buf.Index;
        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        float lift = WorldConfig.RoadSurfaceLift;
        float depth = WorldConfig.RoadFoundationDepth;
        float x0 = x - half, x1 = x + 1 - half, z0 = y - half, z1 = y + 1 - half;
        // 四角路面高（=AddDrapedQuad 的顶点高，保证与路面边对齐）
        float h00 = hf.VertexH(x, y) + lift, h10 = hf.VertexH(x + 1, y) + lift;
        float h01 = hf.VertexH(x, y + 1) + lift, h11 = hf.VertexH(x + 1, y + 1) + lift;

        // 邻格是否也是“路面连续体”（路或桥）：是则内部边不垂基
        bool RoadLike(int nx, int ny)
        {
            var nc = new Vector2I(nx, ny);
            if (!MapGrid.InBounds(nc)) return false;
            ref var ncell = ref gs.Map.CellAt(nc);
            return ncell.HasRoad || ncell.HasBridge;
        }
        // 南边 z0（y-1）/ 北边 z1（y+1）/ 西边 x0（x-1）/ 东边 x1（x+1）
        if (!RoadLike(x, y - 1))
            AddQuad(buf, new Vector3(x0, h00, z0), new Vector3(x1, h10, z0),
                new Vector3(x0, h00 - depth, z0), new Vector3(x1, h10 - depth, z0), new Vector3(0, 0, -1), col,
                new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x0, z0), new Vector2(x1, z0));
        if (!RoadLike(x, y + 1))
            AddQuad(buf, new Vector3(x1, h11, z1), new Vector3(x0, h01, z1),
                new Vector3(x1, h11 - depth, z1), new Vector3(x0, h01 - depth, z1), new Vector3(0, 0, 1), col,
                new Vector2(x1, z1), new Vector2(x0, z1), new Vector2(x1, z1), new Vector2(x0, z1));
        if (!RoadLike(x - 1, y))
            AddQuad(buf, new Vector3(x0, h01, z1), new Vector3(x0, h00, z0),
                new Vector3(x0, h01 - depth, z1), new Vector3(x0, h00 - depth, z0), new Vector3(-1, 0, 0), col,
                new Vector2(x0, z1), new Vector2(x0, z0), new Vector2(x0, z1), new Vector2(x0, z0));
        if (!RoadLike(x + 1, y))
            AddQuad(buf, new Vector3(x1, h10, z0), new Vector3(x1, h11, z1),
                new Vector3(x1, h10 - depth, z0), new Vector3(x1, h11 - depth, z1), new Vector3(1, 0, 0), col,
                new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x1, z0), new Vector2(x1, z1));
    }

    /// <summary>收集一株树的渲染实例：尺寸随生长进度放大，圆柱树干 + 树冠
    /// （逐株伪随机选圆锥/椭球；果树固定椭球阔叶状），位置/大小带扰动避免排队感。</summary>
    private static void CollectTree(GameState gs, int x, int y, float groundY, ChunkGeometry geo,
        ArrayMesh[] modelMeshes)
    {
        if (!gs.Plants.TryGetValue(GameState.CellIndex(new Vector2I(x, y)), out var p))
            return;
        float jx = ((x * 73 + y * 31) % 7 - 3) * 0.15f;
        float jz = ((x * 41 + y * 57) % 7 - 3) * 0.15f;
        float s = (0.8f + ((x * 13 + y * 17) % 5) * 0.1f) * (0.35f + 0.65f * p.GrowthRatio);
        var root = MapGrid.CellToWorld(new Vector2I(x, y)) + new Vector3(jx, groundY, jz); // 树根落在本格地面

        // 树种：果树固定果树种，其余按坐标伪随机取约两成针叶（沿用原始体时代的选型规则）
        int species = p.IsFruitTree ? TreeModelConfig.Fruit
            : ((x * 29 + y * 61) % 5 < 2 ? TreeModelConfig.Conifer : TreeModelConfig.Broadleaf);

        // 外部模型：整株一次成型（树干+树冠已由 TreeModelFactory 合并为单一网格，
        // 并归一化到「底面 y=0、高 1、水平居中」），故实例变换只需等比缩放到目标树高，
        // 再随机绕 Y 转向打破成片重复感。
        if (modelMeshes != null && species >= 0 && species < modelMeshes.Length && modelMeshes[species] != null)
        {
            float h = TreeModelConfig.HeightOf(species) * s;
            float rotY = ((x * 17 + y * 23) % 360) * (Mathf.Pi / 180f);
            var basis = new Basis(Vector3.Up, rotY).Scaled(Vector3.One * h);
            // 顶点色已带树本色，实例色只做微亮扰动（乘算：>1 提亮 / <1 压暗）
            float tint = 0.92f + ((x * 7 + y * 11) % 5) * 0.04f;
            geo.TreeModels[species].Xforms.Add(new Transform3D(basis, root));
            geo.TreeModels[species].Colors.Add(new Color(tint, tint, tint));
            return;
        }

        // 树干：高随株大小，颜色带微扰动（免成片同色塑料感）
        float trunkH = 1.1f * s;
        geo.Trunks.Xforms.Add(new Transform3D(Basis.FromScale(new Vector3(s, trunkH, s)), root + Vector3.Up * (trunkH / 2f)));
        geo.Trunks.Colors.Add(TrunkColor.Lightened(((x * 7 + y * 13) % 5) * 0.03f));

        var crownCol = (p.IsFruitTree ? FruitTreeColor : TreeColor).Lightened(((x * 11 + y * 5) % 5) * 0.025f);
        bool cone = !p.IsFruitTree && (x * 29 + y * 61) % 5 < 2; // 约两成针叶圆锥，果树恒为阔叶椭球
        if (cone)
        {
            // 圆锥冠：坐在树干顶略下压（遮住接缝）
            float crownH = 2.6f * s;
            geo.ConeCrowns.Xforms.Add(new Transform3D(Basis.FromScale(new Vector3(s * 0.9f, crownH / 3f, s * 0.9f)),
                root + Vector3.Up * (trunkH - 0.25f * s + crownH / 2f)));
            geo.ConeCrowns.Colors.Add(crownCol);
        }
        else
        {
            // 椭球冠：竖向略拉长，中心架在树干顶上方
            var crownScale = new Vector3(1.8f * s, 2.3f * s, 1.8f * s);
            geo.BallCrowns.Xforms.Add(new Transform3D(Basis.FromScale(crownScale),
                root + Vector3.Up * (trunkH + crownScale.Y * 0.5f - 0.35f * s)));
            geo.BallCrowns.Colors.Add(crownCol);
        }
    }
}
