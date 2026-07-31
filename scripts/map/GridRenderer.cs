using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 地表渲染层（分块增量重建）：地形为顶点高度场三角网格——每块 65×65 顶点、每格两三角面，
/// 平滑法线受光，顶点色随海拔/坡度渐变；水面为统一水位的半透平面，河床透水可见；
/// 道路贴地（采四角顶点高，坡道上路面自然倾斜）；树木按块 MultiMesh。
/// 建筑（半透明方块+边框+斜屋顶）与坊区色块数量有限，仍整层重建；
/// 全图事件（读档/月度生长）才全量重建。另含建造网格线。
/// </summary>
public partial class GridRenderer : Node3D
{
    private static readonly Color RoadColor = new(0.56f, 0.53f, 0.46f); // 浅石板土路（主路提亮近白石街）
    private static readonly Color WaterColor = new(0.45f, 0.49f, 0.40f); // 灰绿浑水（参考宋画运河色，去蓝去艳）
    private static readonly Color BridgeColor = new(0.62f, 0.60f, 0.54f); // 石桥灰白（原木褐偏艳）
    private static readonly Color TreeColor = new(0.30f, 0.40f, 0.26f);   // 灰绿树冠（压饱和度）
    private static readonly Color FruitTreeColor = new(0.46f, 0.47f, 0.24f); // 果树：暖黄绿树冠，一眼可辨
    private static readonly Color TrunkColor = new(0.38f, 0.30f, 0.22f); // 树干木褐
    private static readonly Color EdgeColor = new(0.12f, 0.12f, 0.14f);
    private static readonly Color BuildableZoneColor = new(0.35f, 0.85f, 0.35f, 0.35f);

    // 地形顶点色：低处淡麦黄绿（参考宋画平原色），高处/陡坡渐变灰褐岩；水下河床泥沙色
    private static readonly Color TerrainLowColor = new(0.63f, 0.59f, 0.44f); // 同 Main 背景平面色
    private static readonly Color TerrainHighColor = new(0.51f, 0.47f, 0.41f); // 山顶/陡壁灰褐岩
    private static readonly Color BedColor = new(0.47f, 0.43f, 0.33f);         // 水下河床泥沙

    /// <summary>门标记颜色：大门亮金（显眼），后门暗木色（低调）。</summary>
    private static readonly Color MainDoorColor = new(0.85f, 0.7f, 0.35f);
    private static readonly Color BackDoorColor = new(0.45f, 0.32f, 0.2f);

    /// <summary>建筑主体透明度（能看清屋内居民）。</summary>
    private const float BodyAlpha = 0.55f;

    /// <summary>水面透明度：微浑而仍透见河床。</summary>
    private const float WaterAlpha = 0.85f;

    /// <summary>分块边长（格）：128 图 2×2 块，1024 图 16×16 块，单块重建量恒定。</summary>
    private const int ChunkCells = 64;

    /// <summary>每帧最多重建的分块数：限制全图标脏时的单帧重建量，把尖峰摊到多帧防卡顿
    /// （12 块/帧 → 1024 图 256 块约 22 帧（~0.35s@60fps）铺完，无可见顿挠）。</summary>
    private const int MaxChunkRebuildsPerFrame = 12;

    /// <summary>单个地表分块：地形三角网格 + 水面 + 贴地路（各一张 ArrayMesh）、桥面方块与树木 MultiMesh。</summary>
    private class Chunk
    {
        public MeshInstance3D Terrain;   // 地形三角网格（65×65 顶点，含河床）
        public MeshInstance3D Water;     // 水面：统一水位的半透平面（每水格一四边形）
        public MeshInstance3D Roads;     // 贴地道路：采四角顶点高的四边形，坡道上自然倾斜
        public MultiMeshInstance3D Bridges;    // 桥面：悬浮木板方块
        public MultiMeshInstance3D Trunks;     // 树干：圆柱
        public MultiMeshInstance3D ConeCrowns; // 圆锥树冠（针叶状）
        public MultiMeshInstance3D BallCrowns; // 椭球树冠（阔叶状，果树固定用此）
        public bool Dirty = true;
    }

    private int _chunksPerSide;
    private Chunk[] _chunks;

    private MultiMeshInstance3D _bldgBodies;
    private MultiMeshInstance3D _bldgRoofs;
    private MultiMeshInstance3D _bldgEdges;
    private MultiMeshInstance3D _doors;
    private MultiMeshInstance3D _zones;
    private MeshInstance3D _gridLines;

    private bool _buildingsDirty = true;
    private bool _zonesDirty = true;

    // 共享网格/材质资源：所有分块复用同一份，各自实例化
    private BoxMesh _boxMesh;
    private CylinderMesh _trunkMesh;
    private CylinderMesh _coneCrownMesh;
    private SphereMesh _ballCrownMesh;
    private StandardMaterial3D _terrainMat; // 地形：顶点色受光
    private StandardMaterial3D _waterMat;   // 水面：顶点色半透（透见河床）
    private StandardMaterial3D _roadMat;    // 贴地路：顶点色受光

    public override void _Ready()
    {
        _boxMesh = new BoxMesh { Size = Vector3.One };
        _boxMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };

        // 树木三件套：单位尺寸网格，实例变换里再按株缩放——
        // 树干圆柱（上细下粗）；树冠分圆锥（针叶）与椭球（阔叶）两形，逐株伪随机选型
        _trunkMesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.16f, Height = 1f };
        _trunkMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _coneCrownMesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 1.1f, Height = 3f };
        _coneCrownMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _ballCrownMesh = new SphereMesh { Radius = 0.5f, Height = 1f };
        _ballCrownMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };

        // 地形/水面/贴地路三套材质（顶点色）：水面半透可见河床，双面免低角度穿帮漏面
        _terrainMat = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _waterMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _roadMat = new StandardMaterial3D { VertexColorUseAsAlbedo = true };

        // 地表/树木分块阵列
        _chunksPerSide = (MapGrid.Size + ChunkCells - 1) / ChunkCells;
        _chunks = new Chunk[_chunksPerSide * _chunksPerSide];
        for (int i = 0; i < _chunks.Length; i++)
        {
            var chunk = new Chunk
            {
                Terrain = new MeshInstance3D { MaterialOverride = _terrainMat },
                Water = new MeshInstance3D { MaterialOverride = _waterMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off },
                Roads = new MeshInstance3D { MaterialOverride = _roadMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off },
                Bridges = MakeMulti(_boxMesh, useColors: true),
                Trunks = MakeMulti(_trunkMesh, useColors: true),
                ConeCrowns = MakeMulti(_coneCrownMesh, useColors: true),
                BallCrowns = MakeMulti(_ballCrownMesh, useColors: true),
            };
            AddChild(chunk.Terrain);
            AddChild(chunk.Water);
            AddChild(chunk.Roads);
            AddChild(chunk.Bridges);
            AddChild(chunk.Trunks);
            AddChild(chunk.ConeCrowns);
            AddChild(chunk.BallCrowns);
            _chunks[i] = chunk;
        }

        // 建筑主体：半透明方块，可透视屋内居民
        var bodyMesh = new BoxMesh { Size = Vector3.One };
        bodyMesh.Material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        _bldgBodies = MakeMulti(bodyMesh, useColors: true);
        _bldgBodies.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_bldgBodies);

        // 斜屋顶：不透明三棱柱，脊线沿建筑长边
        var roofMesh = new PrismMesh { Size = Vector3.One };
        roofMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _bldgRoofs = MakeMulti(roofMesh, useColors: true);
        AddChild(_bldgRoofs);

        // 建筑边框：单位立方体 12 条棱线，随主体同变换缩放
        _bldgEdges = MakeMulti(BuildUnitCubeEdges(), useColors: false);
        _bldgEdges.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_bldgEdges);

        // 建筑的门：小方块标记（大门大而亮金，后门小而暗木），朝向由门内外方向决定
        var doorMesh = new BoxMesh { Size = Vector3.One };
        doorMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _doors = MakeMulti(doorMesh, useColors: true);
        _doors.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_doors);

        // 坊区色块（半透明，无光照）
        var zoneMesh = new BoxMesh { Size = Vector3.One };
        zoneMesh.Material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _zones = MakeMulti(zoneMesh, useColors: true);
        _zones.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_zones);

        BuildGridLines();

        EventBus.MapChanged += MarkAllDirty;
        EventBus.ZonesChanged += MarkZonesDirty;
        EventBus.CellChanged += OnCellChanged;
    }

    public override void _ExitTree()
    {
        EventBus.MapChanged -= MarkAllDirty;
        EventBus.ZonesChanged -= MarkZonesDirty;
        EventBus.CellChanged -= OnCellChanged;
    }

    private static MultiMeshInstance3D MakeMulti(Mesh mesh, bool useColors) => new()
    {
        Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = useColors,
            Mesh = mesh,
        },
    };

    /// <summary>全图变更（读档/月度生长/建筑增减）：全部分块 + 建筑 + 坊区一起重建。</summary>
    private void MarkAllDirty()
    {
        foreach (var chunk in _chunks)
            chunk.Dirty = true;
        _buildingsDirty = true;
        _zonesDirty = true;
    }

    private void MarkZonesDirty() => _zonesDirty = true;

    /// <summary>单格变更（铺路/砍树/拆除/扩地/垫基）：重建所在分块；整平垫基会动到与邻块共享的边界顶点，
    /// 位于块缘的格连带标脏相邻块，避免地形接缝错位；坊区/建筑层跟随刷新。</summary>
    private void OnCellChanged(Vector2I c)
    {
        int cx = c.X / ChunkCells, cy = c.Y / ChunkCells;
        for (int ox = -1; ox <= 1; ox++)
        {
            for (int oy = -1; oy <= 1; oy++)
            {
                // 非块缘格不波及对应方向的邻块（边界顶点才与邻块共享）
                if (ox != 0 && (ox < 0 ? c.X % ChunkCells != 0 : c.X % ChunkCells != ChunkCells - 1))
                    continue;
                if (oy != 0 && (oy < 0 ? c.Y % ChunkCells != 0 : c.Y % ChunkCells != ChunkCells - 1))
                    continue;
                int mx = cx + ox, my = cy + oy;
                if (mx >= 0 && mx < _chunksPerSide && my >= 0 && my < _chunksPerSide)
                    _chunks[my * _chunksPerSide + mx].Dirty = true;
            }
        }
        _zonesDirty = true; // 坊区色块层便宜，跟随刷新
        _buildingsDirty = true; // 房体檐隙随临路变化，建筑数量级小整层重建不贵
    }

    public void SetGridVisible(bool visible) => _gridLines.Visible = visible;

    public override void _Process(double delta)
    {
        // 分块重建限额：全图变更（读档/月度生长/建筑升级转业）会把全部分块标脏，
        // 若同帧重建全部（1024 图 256 块×每块 4096 格≈百万格）会造成尖峰卡顿（尤其 4x 下建筑频变）；
        // 限每帧最多重建 MaxChunkRebuildsPerFrame 块，将尖峰摊到多帧（余脏块下帧续建）。
        int budget = MaxChunkRebuildsPerFrame;
        for (int i = 0; i < _chunks.Length && budget > 0; i++)
        {
            if (!_chunks[i].Dirty)
                continue;
            _chunks[i].Dirty = false;
            RebuildChunk(i);
            budget--;
        }
        if (_zonesDirty)
        {
            _zonesDirty = false;
            RebuildZones();
        }
        if (_buildingsDirty)
        {
            _buildingsDirty = false;
            RebuildBuildings();
        }
    }

    /// <summary>地形顶点色：水下→河床泥沙（越深越暗）；陆上取「海拔显岩」与「陡坡显岩」的较大者，
    /// 低平处草绿、高处/陡壁渐变岩褐。</summary>
    private static Color TerrainVertexColor(float h, Vector3 normal)
    {
        if (h < WaterConfig.WaterLevel)
            return BedColor.Darkened(Mathf.Clamp((WaterConfig.WaterLevel - h) * 0.25f, 0f, 0.35f));
        float byHeight = Mathf.Clamp(h / TerrainConfig.MaxTerrainHeight, 0f, 1f);
        float bySlope = Mathf.Clamp((1f - normal.Y) * 2.4f, 0f, 1f); // 30°坡时约 0.32，开始透岩色
        return TerrainLowColor.Lerp(TerrainHighColor, Mathf.Max(byHeight, bySlope));
    }

    /// <summary>高度场顶点法线：中央差分（水平间距 1m），供受光与坡度显岩。</summary>
    private static Vector3 VertexNormal(HeightField hf, int vx, int vy)
    {
        float dx = hf.VertexH(vx - 1, vy) - hf.VertexH(vx + 1, vy);
        float dz = hf.VertexH(vx, vy - 1) - hf.VertexH(vx, vy + 1);
        return new Vector3(dx, 2f * MapGrid.CellSize, dz).Normalized();
    }

    /// <summary>重建单个分块：地形三角网格（三点一面）+ 水面 + 贴地路 + 桥面 + 树木。</summary>
    private void RebuildChunk(int index)
    {
        var gs = GameState.I;
        var hf = gs.Map.Height;
        var chunk = _chunks[index];
        int cx = index % _chunksPerSide, cy = index / _chunksPerSide;
        int x0 = cx * ChunkCells, y0 = cy * ChunkCells;
        int x1 = Mathf.Min(x0 + ChunkCells, MapGrid.Size);
        int y1 = Mathf.Min(y0 + ChunkCells, MapGrid.Size);
        const float cs = MapGrid.CellSize;
        float half = MapGrid.Size * cs / 2f;

        // ---- 地形三角网格：块内 (格数+1)² 顶点、每格两三角；平滑法线受光，顶点色随海拔/坡度渐变 ----
        int nx = x1 - x0, nz = y1 - y0;
        int vw = nx + 1;
        var tVerts = new Vector3[vw * (nz + 1)];
        var tNormals = new Vector3[tVerts.Length];
        var tColors = new Color[tVerts.Length];
        for (int vy = 0; vy <= nz; vy++)
        {
            for (int vx = 0; vx <= nx; vx++)
            {
                int gvx = x0 + vx, gvy = y0 + vy;
                float h = hf.VertexH(gvx, gvy);
                var n = VertexNormal(hf, gvx, gvy);
                int vi = vy * vw + vx;
                tVerts[vi] = new Vector3(gvx * cs - half, h, gvy * cs - half);
                tNormals[vi] = n;
                tColors[vi] = TerrainVertexColor(h, n);
            }
        }
        var tIdx = new int[nx * nz * 6];
        int ii = 0;
        for (int gy = 0; gy < nz; gy++)
        {
            for (int gx = 0; gx < nx; gx++)
            {
                // Godot 以顺时针为正面（俯视）：每格两三角面拼成四边形
                int v00 = gy * vw + gx, v10 = v00 + 1, v01 = v00 + vw, v11 = v01 + 1;
                tIdx[ii++] = v00; tIdx[ii++] = v10; tIdx[ii++] = v01;
                tIdx[ii++] = v10; tIdx[ii++] = v11; tIdx[ii++] = v01;
            }
        }
        var tArrays = new Godot.Collections.Array();
        tArrays.Resize((int)Mesh.ArrayType.Max);
        tArrays[(int)Mesh.ArrayType.Vertex] = tVerts;
        tArrays[(int)Mesh.ArrayType.Normal] = tNormals;
        tArrays[(int)Mesh.ArrayType.Color] = tColors;
        tArrays[(int)Mesh.ArrayType.Index] = tIdx;
        var terrainMesh = new ArrayMesh();
        terrainMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, tArrays);
        chunk.Terrain.Mesh = terrainMesh;

        // ---- 水面/贴地路/桥面/树木：逐格收集 ----
        var waterV = new List<Vector3>(); var waterN = new List<Vector3>(); var waterC = new List<Color>(); var waterI = new List<int>();
        var roadV = new List<Vector3>(); var roadN = new List<Vector3>(); var roadC = new List<Color>(); var roadI = new List<int>();
        var bridgeXf = new List<Transform3D>();
        var bridgeColor = new List<Color>();
        var trunkXf = new List<Transform3D>();
        var trunkColor = new List<Color>();
        var coneXf = new List<Transform3D>();
        var coneColor = new List<Color>();
        var ballXf = new List<Transform3D>();
        var ballColor = new List<Color>();

        for (int x = x0; x < x1; x++)
        {
            for (int y = y0; y < y1; y++)
            {
                ref var cell = ref gs.Map.CellAt(x, y);
                var world = MapGrid.CellToWorld(new Vector2I(x, y));
                float groundY = gs.Map.GroundY(new Vector2I(x, y)); // 本格地面海拔（四角顶点均值）

                if (cell.HasBridge)
                {
                    // 桥面：悬浮在河面（-0.5）之上的木板，底 0.18、顶 0.34，与水面留明显空隙
                    bridgeXf.Add(new Transform3D(Basis.FromScale(new Vector3(cs, 0.16f, cs)), world + Vector3.Up * 0.26f));
                    bridgeColor.Add(BridgeColor);
                }
                else if (cell.HasWater)
                {
                    // 水面：统一水位的半透平面，河床（地形网格已下压）透水可见
                    var wc = new Color(WaterColor.R, WaterColor.G, WaterColor.B, WaterAlpha);
                    AddFlatQuad(waterV, waterN, waterC, waterI, x, y, WaterConfig.WaterLevelAt(new Vector2I(x, y)), wc);
                }
                else if (cell.HasRoad)
                {
                    // 三类道路按种类区分明度与抬升：主路最亮最高，小路最暗最薄；
                    // 四角采地形顶点高，坡道上路面自然倾斜贴地
                    float lift;
                    Color rc;
                    switch (cell.RoadKind)
                    {
                        case RoadKind.Main:
                            lift = 0.06f; rc = RoadColor.Lightened(0.25f); break;
                        case RoadKind.Lane:
                            lift = 0.04f; rc = RoadColor.Darkened(0.2f); break;
                        default: // Side
                            lift = 0.05f; rc = RoadColor; break;
                    }
                    AddDrapedQuad(hf, roadV, roadN, roadC, roadI, x, y, lift, rc);
                }
                else if (cell.BuildingId == 0 && hf.CellMinH(new Vector2I(x, y)) < WaterConfig.WaterLevel)
                {
                    // 贴岸陆格（共享顶点被河床下压到水位下）也补一片水面：
                    // 水线落在水面与地形斜面的交线上，沿岸连续平滑，消除逐格锯齿
                    var wc = new Color(WaterColor.R, WaterColor.G, WaterColor.B, WaterAlpha);
                    AddFlatQuad(waterV, waterN, waterC, waterI, x, y, WaterConfig.WaterLevelAt(new Vector2I(x, y)), wc);
                }

                // 树木：植物实体驱动，尺寸随生长进度放大（块内格→实体查询，重建量与块大小成正比）。
                // 造型：圆柱树干 + 树冠（逐株伪随机选圆锥/椭球；果树固定椭球阔叶状），位置/大小带扰动避免排队感
                if (cell.HasTree && gs.Plants.TryGetValue(GameState.CellIndex(new Vector2I(x, y)), out var p))
                {
                    float jx = ((x * 73 + y * 31) % 7 - 3) * 0.15f;
                    float jz = ((x * 41 + y * 57) % 7 - 3) * 0.15f;
                    float s = (0.8f + ((x * 13 + y * 17) % 5) * 0.1f) * (0.35f + 0.65f * p.GrowthRatio);
                    var root = world + new Vector3(jx, groundY, jz); // 树根落在本格地面

                    // 树干：高随株大小，颜色带微扰动（免成片同色塑料感）
                    float trunkH = 1.1f * s;
                    trunkXf.Add(new Transform3D(Basis.FromScale(new Vector3(s, trunkH, s)), root + Vector3.Up * (trunkH / 2f)));
                    trunkColor.Add(TrunkColor.Lightened(((x * 7 + y * 13) % 5) * 0.03f));

                    var crownCol = (p.IsFruitTree ? FruitTreeColor : TreeColor).Lightened(((x * 11 + y * 5) % 5) * 0.025f);
                    bool cone = !p.IsFruitTree && (x * 29 + y * 61) % 5 < 2; // 约两成针叶圆锥，果树恒为阔叶椭球
                    if (cone)
                    {
                        // 圆锥冠：坐在树干顶略下压（遮住接缝）
                        float crownH = 2.6f * s;
                        coneXf.Add(new Transform3D(Basis.FromScale(new Vector3(s * 0.9f, crownH / 3f, s * 0.9f)),
                            root + Vector3.Up * (trunkH - 0.25f * s + crownH / 2f)));
                        coneColor.Add(crownCol);
                    }
                    else
                    {
                        // 椭球冠：竖向略拉长，中心架在树干顶上方
                        var crownScale = new Vector3(1.8f * s, 2.3f * s, 1.8f * s);
                        ballXf.Add(new Transform3D(Basis.FromScale(crownScale),
                            root + Vector3.Up * (trunkH + crownScale.Y * 0.5f - 0.35f * s)));
                        ballColor.Add(crownCol);
                    }
                }
            }
        }

        chunk.Water.Mesh = MeshFrom(waterV, waterN, waterC, waterI);
        chunk.Roads.Mesh = MeshFrom(roadV, roadN, roadC, roadI);
        FillMultiMesh(chunk.Bridges.Multimesh, bridgeXf, bridgeColor);
        FillMultiMesh(chunk.Trunks.Multimesh, trunkXf, trunkColor);
        FillMultiMesh(chunk.ConeCrowns.Multimesh, coneXf, coneColor);
        FillMultiMesh(chunk.BallCrowns.Multimesh, ballXf, ballColor);
    }

    /// <summary>往网格数组追加一格水平四边形（水面用，法线朝上）。</summary>
    private static void AddFlatQuad(List<Vector3> v, List<Vector3> n, List<Color> c, List<int> idx,
        int x, int y, float lvl, Color col)
    {
        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        int b = v.Count;
        v.Add(new Vector3(x - half, lvl, y - half));
        v.Add(new Vector3(x + 1 - half, lvl, y - half));
        v.Add(new Vector3(x - half, lvl, y + 1 - half));
        v.Add(new Vector3(x + 1 - half, lvl, y + 1 - half));
        for (int i = 0; i < 4; i++)
        {
            n.Add(Vector3.Up);
            c.Add(col);
        }
        idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
        idx.Add(b + 1); idx.Add(b + 3); idx.Add(b + 2);
    }

    /// <summary>往网格数组追加一格贴地四边形（道路用）：四角采地形顶点高 + 抬升，坡道上自然倾斜。</summary>
    private static void AddDrapedQuad(HeightField hf, List<Vector3> v, List<Vector3> n, List<Color> c, List<int> idx,
        int x, int y, float lift, Color col)
    {
        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        int b = v.Count;
        v.Add(new Vector3(x - half, hf.VertexH(x, y) + lift, y - half));
        v.Add(new Vector3(x + 1 - half, hf.VertexH(x + 1, y) + lift, y - half));
        v.Add(new Vector3(x - half, hf.VertexH(x, y + 1) + lift, y + 1 - half));
        v.Add(new Vector3(x + 1 - half, hf.VertexH(x + 1, y + 1) + lift, y + 1 - half));
        n.Add(VertexNormal(hf, x, y));
        n.Add(VertexNormal(hf, x + 1, y));
        n.Add(VertexNormal(hf, x, y + 1));
        n.Add(VertexNormal(hf, x + 1, y + 1));
        for (int i = 0; i < 4; i++)
            c.Add(col);
        idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
        idx.Add(b + 1); idx.Add(b + 3); idx.Add(b + 2);
    }

    /// <summary>三角面数组 → ArrayMesh（空集返回 null，节点不挂网格）。</summary>
    private static ArrayMesh MeshFrom(List<Vector3> v, List<Vector3> n, List<Color> c, List<int> idx)
    {
        if (v.Count == 0)
            return null;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = n.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = c.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>重建坊区色块层：只遍历坊区候选集（增量索引），非全图扫描。</summary>
    private void RebuildZones()
    {
        var gs = GameState.I;
        var zoneXf = new List<Transform3D>();
        var zoneColor = new List<Color>();
        const float cs = MapGrid.CellSize;

        foreach (var c in gs.BuildableCells)
        {
            ref var cell = ref gs.Map.CellAt(c);
            if (cell.BuildingId >= 0)
                continue; // 已被建筑占用的坊区格不画色块
            var world = MapGrid.CellToWorld(c);
            float gy = gs.Map.GroundY(c); // 贴本格地面
            zoneXf.Add(new Transform3D(Basis.FromScale(new Vector3(cs * 0.96f, 0.08f, cs * 0.96f)), world + Vector3.Up * (gy + 0.05f)));
            zoneColor.Add(BuildableZoneColor);
        }
        FillMultiMesh(_zones.Multimesh, zoneXf, zoneColor);
    }

    /// <summary>重建建筑层（主体/屋顶/边框）：建筑数量级远小于格数，整层重建足够便宜。</summary>
    private void RebuildBuildings()
    {
        var gs = GameState.I;
        var bodyXf = new List<Transform3D>();
        var bodyColor = new List<Color>();
        var roofXf = new List<Transform3D>();
        var roofColor = new List<Color>();
        var doorXf = new List<Transform3D>();
        var doorColor = new List<Color>();
        const float cs = MapGrid.CellSize;

        foreach (var b in gs.Buildings.Values)
        {
            // 等级越高楼越高；年久失修则发暗
            float height = b.Def.Height * (1f + 0.35f * (b.Level - 1));

            // 房体范围：grown 与官营统一按占地 ~0.9 缩放整块绘制（房体=占地）；立在原点格地面上
            float w, d;
            var center = MapGrid.CellToWorld(b.Origin);
            float groundY = gs.Map.GroundY(b.Origin); // 地形高度基准（建筑要求平地，整块同高）
            w = b.FootX * cs * 0.9f;
            d = b.FootY * cs * 0.9f;
            center += new Vector3((b.FootX - 1) * cs / 2f, groundY + height / 2f, (b.FootY - 1) * cs / 2f);

            var color = b.Def.GodotColor;
            if (b.Condition < 50f)
                color = color.Darkened(0.35f * (1f - b.Condition / 50f));

            // 半透明主体 + 同变换边框
            var bodyTransform = new Transform3D(Basis.FromScale(new Vector3(w, height, d)), center);
            var bodyCol = color;
            bodyCol.A = BodyAlpha;
            bodyXf.Add(bodyTransform);
            bodyColor.Add(bodyCol);

            // 斜屋顶：脊线沿长边，稍出檐（跟随房体尺寸与中心）；农田等 NoRoof 地块只有地面不盖顶
            if (!b.Def.NoRoof)
            {
                float roofH = Mathf.Clamp(height * 0.3f, 0.5f, 1.8f);
                var roofBasis = w >= d
                    ? Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f)) * Basis.FromScale(new Vector3(d * 1.06f, roofH, w * 1.06f))
                    : Basis.FromScale(new Vector3(w * 1.06f, roofH, d * 1.06f));
                var roofCenter = new Vector3(center.X, groundY + height + roofH / 2f, center.Z);
                roofXf.Add(new Transform3D(roofBasis, roofCenter));
                roofColor.Add(color.Darkened(0.45f)); // 灰瓦感
            }

            // 门标记：沿占地边界贴墙放置，朝向由门内→门外方向决定（大门大而亮，后门小而暗）
            gs.EnsureDoors(b);
            if (b.Doors != null)
            {
                foreach (var door in b.Doors)
                {
                    var dir = new Vector2I(door.Outside.X - door.Inside.X, door.Outside.Y - door.Inside.Y);
                    var dirW = new Vector3(dir.X, 0f, dir.Y);
                    float doorH = door.IsMain ? 1.3f : 0.85f;
                    float wide = (door.IsMain ? 0.7f : 0.42f) * cs;
                    const float thick = 0.18f;
                    // 门面宽度沿墙面（垂直于 dir），厚度沿 dir
                    var scale = dir.X != 0 ? new Vector3(thick, doorH, wide) : new Vector3(wide, doorH, thick);
                    var pos = MapGrid.CellToWorld(door.Inside) + dirW * (cs * 0.5f) + Vector3.Up * (gs.Map.GroundY(door.Inside) + doorH / 2f);
                    doorXf.Add(new Transform3D(Basis.FromScale(scale), pos));
                    doorColor.Add(door.IsMain ? MainDoorColor : BackDoorColor);
                }
            }
        }

        FillMultiMesh(_bldgBodies.Multimesh, bodyXf, bodyColor);
        FillMultiMesh(_bldgRoofs.Multimesh, roofXf, roofColor);
        FillMultiMesh(_doors.Multimesh, doorXf, doorColor);

        // 边框与主体同变换（固定深色，无逐实例颜色）
        var mmEdges = _bldgEdges.Multimesh;
        mmEdges.InstanceCount = bodyXf.Count;
        for (int i = 0; i < bodyXf.Count; i++)
            mmEdges.SetInstanceTransform(i, bodyXf[i]);
    }

    private static void FillMultiMesh(MultiMesh mm, List<Transform3D> xforms, List<Color> colors)
    {
        mm.InstanceCount = xforms.Count;
        for (int i = 0; i < xforms.Count; i++)
        {
            mm.SetInstanceTransform(i, xforms[i]);
            mm.SetInstanceColor(i, colors[i]);
        }
    }

    /// <summary>单位立方体（边长 1，原点居中）的 12 条棱线，供建筑边框 MultiMesh 缩放复用。</summary>
    private static ArrayMesh BuildUnitCubeEdges()
    {
        const float h = 0.5f;
        var pts = new List<Vector3>();
        for (int s = -1; s <= 1; s += 2)
        {
            float y = h * s;
            pts.Add(new Vector3(-h, y, -h)); pts.Add(new Vector3(h, y, -h));
            pts.Add(new Vector3(h, y, -h)); pts.Add(new Vector3(h, y, h));
            pts.Add(new Vector3(h, y, h)); pts.Add(new Vector3(-h, y, h));
            pts.Add(new Vector3(-h, y, h)); pts.Add(new Vector3(-h, y, -h));
        }
        for (int i = 0; i < 4; i++)
        {
            float x = (i & 1) == 0 ? -h : h;
            float z = (i & 2) == 0 ? -h : h;
            pts.Add(new Vector3(x, -h, z)); pts.Add(new Vector3(x, h, z));
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pts.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = EdgeColor,
        });
        return mesh;
    }

    private void BuildGridLines()
    {
        var im = new ImmediateMesh();
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1f, 1f, 1f, 0.15f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };

        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        const float y = 0.06f;
        im.SurfaceBegin(Mesh.PrimitiveType.Lines, mat);
        for (int i = 0; i <= MapGrid.Size; i++)
        {
            float p = i * MapGrid.CellSize - half;
            im.SurfaceAddVertex(new Vector3(p, y, -half));
            im.SurfaceAddVertex(new Vector3(p, y, half));
            im.SurfaceAddVertex(new Vector3(-half, y, p));
            im.SurfaceAddVertex(new Vector3(half, y, p));
        }
        im.SurfaceEnd();

        _gridLines = new MeshInstance3D { Mesh = im, Visible = false };
        AddChild(_gridLines);
    }
}
