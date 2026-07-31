using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 地表渲染层（分块增量重建）：地形为顶点高度场三角网格——每块 65×65 顶点、每格两三角面，
/// 平滑法线受光，顶点色随海拔/坡度渐变；水面同样走顶点插值（邻水格 WaterH 均值）的三角网格，
/// 坡河上水面连续倾斜不再逐格阶梯，河床透水可见；道路贴地（采四角顶点高，坡道上路面自然倾斜）；
/// 树木按块 MultiMesh（另设独立脏标：月度生长只刷树层不重建地形）；图缘裙板遮住地形侧向镂空。
/// 建筑（地基+半透明方块+边框+斜屋顶）与坊区色块数量有限，仍整层重建；
/// 全图事件（读档）才全量重建，建筑落成/拆除只重建占地矩形覆盖的分块。另含建造网格线。
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
    private static readonly Color TerrainLowColor = new(0.63f, 0.59f, 0.44f); // 同 Main 卷轴画面基色
    private static readonly Color TerrainHighColor = new(0.51f, 0.47f, 0.41f); // 山顶/陡壁灰褐岩
    private static readonly Color BedColor = new(0.47f, 0.43f, 0.33f);         // 水下河床泥沙

    /// <summary>建筑地基色：夯土石基灰褐，斜坡上露出的基座侧面读作台基。</summary>
    private static readonly Color FoundationColor = new(0.52f, 0.48f, 0.42f);

    /// <summary>图缘裙板色：比卷轴纸面略深的纸色，地形断面读作「画的厚度」。</summary>
    private static readonly Color SkirtColor = new(0.74f, 0.68f, 0.54f);

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

    /// <summary>每帧最多只刷树层的分块数：树层重建（纯 MultiMesh 填充）远轻于整块重建，限额可更宽。</summary>
    private const int MaxTreeRebuildsPerFrame = 32;

    /// <summary>单个地表分块：地形三角网格 + 水面 + 贴地路（各一张 ArrayMesh）、桥面方块与树木 MultiMesh。</summary>
    private class Chunk
    {
        public MeshInstance3D Terrain;   // 地形三角网格（65×65 顶点，含河床）
        public MeshInstance3D Water;     // 水面：顶点插值水位的半透三角网格（同地形模式）
        public MeshInstance3D Roads;     // 贴地道路：采四角顶点高的四边形，坡道上自然倾斜
        public MeshInstance3D Bridge;    // 桥面：三角化桥面（同道路顶面模式），两端引桥降到岸路高
        public MultiMeshInstance3D Trunks;     // 树干：圆柱
        public MultiMeshInstance3D ConeCrowns; // 圆锥树冠（针叶状）
        public MultiMeshInstance3D BallCrowns; // 椭球树冠（阔叶状，果树固定用此）
        public bool Dirty = true;
        public bool TreesDirty;          // 树层单独脏（月度生长）：只刷树木 MultiMesh 不重建地形
    }

    private int _chunksPerSide;
    private Chunk[] _chunks;

    private MultiMeshInstance3D _bldgFounds; // 地基：房体下不透明基座，斜坡上遮悬空
    private MultiMeshInstance3D _bldgBodies;
    private MultiMeshInstance3D _bldgRoofs;
    private MultiMeshInstance3D _bldgEdges;
    private MultiMeshInstance3D _doors;
    private MultiMeshInstance3D _zones;
    private MeshInstance3D _gridLines;
    private MeshInstance3D _skirt; // 图缘裙板：周长带状网格，从图缘顶点垂到卷轴画布面，遮住侧向镂空

    private bool _buildingsDirty = true;
    private bool _zonesDirty = true;
    private bool _skirtDirty = true;

    // 共享网格/材质资源：所有分块复用同一份，各自实例化
    private CylinderMesh _trunkMesh;
    private CylinderMesh _coneCrownMesh;
    private SphereMesh _ballCrownMesh;
    private StandardMaterial3D _terrainMat; // 地形：顶点色受光
    private StandardMaterial3D _waterMat;   // 水面：顶点色半透（透见河床）
    private StandardMaterial3D _roadMat;    // 贴地路：顶点色受光
    private StandardMaterial3D _bridgeMat;  // 桥面：顶点色受光，双面（低角见桥底）

    public override void _Ready()
    {
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
        _roadMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // 双面：地基立面低角内外侧都可见
        };
        _bridgeMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // 双面：低角看桥底不漏面
        };

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
                Bridge = new MeshInstance3D { MaterialOverride = _bridgeMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off },
                Trunks = MakeMulti(_trunkMesh, useColors: true),
                ConeCrowns = MakeMulti(_coneCrownMesh, useColors: true),
                BallCrowns = MakeMulti(_ballCrownMesh, useColors: true),
            };
            AddChild(chunk.Terrain);
            AddChild(chunk.Water);
            AddChild(chunk.Roads);
            AddChild(chunk.Bridge);
            AddChild(chunk.Trunks);
            AddChild(chunk.ConeCrowns);
            AddChild(chunk.BallCrowns);
            _chunks[i] = chunk;
        }

        // 建筑地基：不透明基座，从房体底面向下延伸 FoundationDepth，斜坡上建造时遮住悬空底部
        var foundMesh = new BoxMesh { Size = Vector3.One };
        foundMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _bldgFounds = MakeMulti(foundMesh, useColors: true);
        AddChild(_bldgFounds);

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

        // 图缘裙板：双面受光（低角度内外侧都可能看到），随 MapChanged 重建
        _skirt = new MeshInstance3D
        {
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_skirt);

        EventBus.MapChanged += MarkAllDirty;
        EventBus.ZonesChanged += MarkZonesDirty;
        EventBus.CellChanged += OnCellChanged;
        EventBus.RectChanged += MarkRectDirty;       // 建筑落成/拆除/扩建：只重建覆盖分块
        EventBus.BuildingsChanged += MarkBuildingsDirty; // 升级/转业：只重建建筑层
        EventBus.TreesChanged += MarkTreesDirty;     // 月度生长：只刷各块树木 MultiMesh
    }

    public override void _ExitTree()
    {
        EventBus.MapChanged -= MarkAllDirty;
        EventBus.ZonesChanged -= MarkZonesDirty;
        EventBus.CellChanged -= OnCellChanged;
        EventBus.RectChanged -= MarkRectDirty;
        EventBus.BuildingsChanged -= MarkBuildingsDirty;
        EventBus.TreesChanged -= MarkTreesDirty;
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

    /// <summary>全图变更（读档/月度生长/建筑增减）：全部分块 + 建筑 + 坊区 + 裙板一起重建。</summary>
    private void MarkAllDirty()
    {
        foreach (var chunk in _chunks)
            chunk.Dirty = true;
        _buildingsDirty = true;
        _zonesDirty = true;
        _skirtDirty = true;
    }

    private void MarkZonesDirty() => _zonesDirty = true;

    private void MarkBuildingsDirty() => _buildingsDirty = true;

    /// <summary>仅树木变化（月度生长/散播）：全部分块只标树层脏，不重建地形/水面/道路网格——
    /// 旧版这里走全图 MapChanged，4x 下每月一次百万格网格重建是间歇卡顿主源之一。</summary>
    private void MarkTreesDirty()
    {
        foreach (var chunk in _chunks)
            chunk.TreesDirty = true;
    }

    /// <summary>矩形区域变更（建筑落成/拆除/扩建的垫基整平）：只标脏矩形覆盖的分块
    /// （外扩 1 格：整平会动到与邻块共享的边界顶点），建筑/坊区层跟随刷新。</summary>
    private void MarkRectDirty(Vector2I origin, Vector2I size)
    {
        int cx0 = Mathf.Clamp((origin.X - 1) / ChunkCells, 0, _chunksPerSide - 1);
        int cy0 = Mathf.Clamp((origin.Y - 1) / ChunkCells, 0, _chunksPerSide - 1);
        int cx1 = Mathf.Clamp((origin.X + size.X) / ChunkCells, 0, _chunksPerSide - 1);
        int cy1 = Mathf.Clamp((origin.Y + size.Y) / ChunkCells, 0, _chunksPerSide - 1);
        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
                _chunks[cy * _chunksPerSide + cx].Dirty = true;
        _buildingsDirty = true;
        _zonesDirty = true;
        if (origin.X <= 1 || origin.Y <= 1 || origin.X + size.X >= MapGrid.Size - 1 || origin.Y + size.Y >= MapGrid.Size - 1)
            _skirtDirty = true; // 图缘附近的垫基可能动到边缘顶点
    }

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
        if (c.X <= 1 || c.Y <= 1 || c.X >= MapGrid.Size - 2 || c.Y >= MapGrid.Size - 2)
            _skirtDirty = true; // 图缘格变更（垂基整平可能动到边缘顶点）：裙板跟随重建
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
            _chunks[i].TreesDirty = false; // 整块重建已含树层
            RebuildChunk(i);
            budget--;
        }
        // 树层单独刷新（月度生长）：只填 MultiMesh 不重建网格，限额更宽
        int treeBudget = MaxTreeRebuildsPerFrame;
        for (int i = 0; i < _chunks.Length && treeBudget > 0; i++)
        {
            if (!_chunks[i].TreesDirty || _chunks[i].Dirty)
                continue; // 整块待重建的交给上方循环顺带刷树
            _chunks[i].TreesDirty = false;
            RebuildChunkTrees(i);
            treeBudget--;
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
        if (_skirtDirty)
        {
            _skirtDirty = false;
            RebuildSkirt();
        }
    }

    /// <summary>地形顶点色：水下（低于本地水面 localWater）→河床泥沙（越深越暗）；
    /// 陆上取「海拔显岩」与「陡坡显岩」的较大者，低平处草绿、高处/陡壁渐变岩褐。
    /// localWater 取自共享该顶点的水格最高水位（无水邻格为 -∞，不走河床色）。</summary>
    private static Color TerrainVertexColor(float h, Vector3 normal, float localWater)
    {
        if (h < localWater)
            return BedColor.Darkened(Mathf.Clamp((localWater - h) * 0.25f, 0f, 0.35f));
        float byHeight = Mathf.Clamp(h / TerrainConfig.MaxTerrainHeight, 0f, 1f);
        float bySlope = Mathf.Clamp((1f - normal.Y) * 2.4f, 0f, 1f); // 30°坡时约 0.32，开始透岩色
        return TerrainLowColor.Lerp(TerrainHighColor, Mathf.Max(byHeight, bySlope));
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
                tColors[vi] = TerrainVertexColor(h, n, LocalWaterAtVertex(gs, gvx, gvy));
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
        var bridgeV = new List<Vector3>(); var bridgeN = new List<Vector3>(); var bridgeC = new List<Color>(); var bridgeI = new List<int>();
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
                float groundY = gs.Map.GroundY(new Vector2I(x, y)); // 本格地面海拔（四角顶点均值）

                if (cell.HasBridge)
                {
                    // 桥格也是水面格：先铺桥下水面，再铺桥体板（否则桥下露底无水）
                    var bwc = new Color(WaterColor.R, WaterColor.G, WaterColor.B, WaterAlpha);
                    AddWaterQuad(gs, waterV, waterN, waterC, waterI, x, y, cell.WaterH, bwc);
                    // 桥面：实体桥体板（顶面 + 体厚），顶面四角取 DeckVertexTop；与引桥/道路同属桥面网格
                    AddDeckBox(gs, hf, bridgeV, bridgeN, bridgeC, bridgeI, x, y, BridgeColor);
                }
                else if (cell.HasWater)
                {
                    // 水面：四角顶点取邻水格 WaterH 均值（同地形顶点模式），坡河上连续倾斜不逐格阶梯
                    var wc = new Color(WaterColor.R, WaterColor.G, WaterColor.B, WaterAlpha);
                    AddWaterQuad(gs, waterV, waterN, waterC, waterI, x, y, cell.WaterH, wc);
                }
                else if (cell.HasRoad)
                {
                    if (NearBridge(gs, x, y))
                    {
                        // 引桥：桥旁陆地路格——同桥面实体板渲染，按离桥距从桥面高渐降到岸路高，
                        // 与桥、与普通道路两头无缝相接
                        AddDeckBox(gs, hf, bridgeV, bridgeN, bridgeC, bridgeI, x, y, BridgeColor);
                    }
                    else
                    {
                        // 三类道路按种类区分明度（主路最亮、小路最暗），抬升统一取 RoadSurfaceLift；
                        // 四角采地形顶点高，坡道上路面自然倾斜贴地；外边缘垂一圈地基立面
                        Color rc = cell.RoadKind switch
                        {
                            RoadKind.Main => RoadColor.Lightened(0.25f),
                            RoadKind.Lane => RoadColor.Darkened(0.2f),
                            _ => RoadColor, // Side
                        };
                        AddDrapedQuad(hf, roadV, roadN, roadC, roadI, x, y, WorldConfig.RoadSurfaceLift, rc);
                        AddRoadFoundation(gs, hf, roadV, roadN, roadC, roadI, x, y, rc.Darkened(0.35f));
                    }
                }
                else if (cell.BuildingId < 0)
                {
                    // 贴岸陆格（共享顶点被河床下压到邻格水位之下）也补一片水面：
                    // 水线落在水面与地形斜面的交线上，沿岸连续平滑，消除逐格锯齿；
                    // 邻格水位取四邻水格最高者（旧版此处误写 BuildingId == 0，一并改正为无建筑 < 0）
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
                    {
                        var wc = new Color(WaterColor.R, WaterColor.G, WaterColor.B, WaterAlpha);
                        AddWaterQuad(gs, waterV, waterN, waterC, waterI, x, y, shoreLevel, wc);
                    }
                }

                // 树木：植物实体驱动，逐株造型抽到 CollectTree（树层单独刷新时复用）
                if (cell.HasTree)
                    CollectTree(gs, x, y, groundY, trunkXf, trunkColor, coneXf, coneColor, ballXf, ballColor);
            }
        }

        chunk.Water.Mesh = MeshFrom(waterV, waterN, waterC, waterI);
        chunk.Roads.Mesh = MeshFrom(roadV, roadN, roadC, roadI);
        chunk.Bridge.Mesh = MeshFrom(bridgeV, bridgeN, bridgeC, bridgeI);
        FillMultiMesh(chunk.Trunks.Multimesh, trunkXf, trunkColor);
        FillMultiMesh(chunk.ConeCrowns.Multimesh, coneXf, coneColor);
        FillMultiMesh(chunk.BallCrowns.Multimesh, ballXf, ballColor);
    }

    /// <summary>只刷新单块的树木三层 MultiMesh（月度生长/散播）：不重建地形/水面/道路网格，
    /// 开销仅为遍历块内格 + MultiMesh 填充，远轻于整块重建。</summary>
    private void RebuildChunkTrees(int index)
    {
        var gs = GameState.I;
        var chunk = _chunks[index];
        int cx = index % _chunksPerSide, cy = index / _chunksPerSide;
        int x0 = cx * ChunkCells, y0 = cy * ChunkCells;
        int x1 = Mathf.Min(x0 + ChunkCells, MapGrid.Size);
        int y1 = Mathf.Min(y0 + ChunkCells, MapGrid.Size);

        var trunkXf = new List<Transform3D>();
        var trunkColor = new List<Color>();
        var coneXf = new List<Transform3D>();
        var coneColor = new List<Color>();
        var ballXf = new List<Transform3D>();
        var ballColor = new List<Color>();

        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
                if (gs.Map.CellAt(x, y).HasTree)
                    CollectTree(gs, x, y, gs.Map.GroundY(new Vector2I(x, y)),
                        trunkXf, trunkColor, coneXf, coneColor, ballXf, ballColor);

        FillMultiMesh(chunk.Trunks.Multimesh, trunkXf, trunkColor);
        FillMultiMesh(chunk.ConeCrowns.Multimesh, coneXf, coneColor);
        FillMultiMesh(chunk.BallCrowns.Multimesh, ballXf, ballColor);
    }

    /// <summary>收集一株树的渲染实例：尺寸随生长进度放大，圆柱树干 + 树冠
    /// （逐株伪随机选圆锥/椭球；果树固定椭球阔叶状），位置/大小带扰动避免排队感。</summary>
    private static void CollectTree(GameState gs, int x, int y, float groundY,
        List<Transform3D> trunkXf, List<Color> trunkColor,
        List<Transform3D> coneXf, List<Color> coneColor,
        List<Transform3D> ballXf, List<Color> ballColor)
    {
        if (!gs.Plants.TryGetValue(GameState.CellIndex(new Vector2I(x, y)), out var p))
            return;
        float jx = ((x * 73 + y * 31) % 7 - 3) * 0.15f;
        float jz = ((x * 41 + y * 57) % 7 - 3) * 0.15f;
        float s = (0.8f + ((x * 13 + y * 17) % 5) * 0.1f) * (0.35f + 0.65f * p.GrowthRatio);
        var root = MapGrid.CellToWorld(new Vector2I(x, y)) + new Vector3(jx, groundY, jz); // 树根落在本格地面

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

    /// <summary>往网格数组追加一格水面四边形：四角取顶点插值水位（WaterVertexH），
    /// 同地形三顶点模式——坡河上水面随水位连续倾斜，不再逐格阶梯错层；
    /// 四边向外扩 WaterEdgeOverlap 嵌入邻格，水平面从高岸下方穿过被岸地遮住，消隙除锯齿。</summary>
    private static void AddWaterQuad(GameState gs, List<Vector3> v, List<Vector3> n, List<Color> c, List<int> idx,
        int x, int y, float fallback, Color col)
    {
        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        float m = WaterConfig.WaterEdgeOverlap;
        float x0 = x - half - m, x1 = x + 1 - half + m;
        float y0 = y - half - m, y1 = y + 1 - half + m;
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

    /// <summary>桥旁陆地路格是否属于引桥过渡带（委托给 MapGrid.NearBridge，渲染与站面同源）。</summary>
    private static bool NearBridge(GameState gs, int x, int y) => gs.Map.NearBridge(x, y);

    /// <summary>桥面/引桥顶面某顶点海拔（委托给 MapGrid.DeckVertexTop，渲染与村民站面共用同一顶点高）。</summary>
    private static float DeckVertexTop(GameState gs, HeightField hf, int vx, int vy) => gs.Map.DeckVertexTop(vx, vy);

    /// <summary>往桥面网格追加一格实体桥体板：顶面四角取 DeckVertexTop（同道路顶面三点模式），
    /// 从顶面向下拉 BridgeBodyThickness 作底面与四侧壁——桥为实体板而非平面；
    /// 桥格坐桥面高平抬于水面之上，边缘引桥格自然渐降接岸路。</summary>
    private static void AddDeckBox(GameState gs, HeightField hf, List<Vector3> v, List<Vector3> n, List<Color> c, List<int> idx,
        int x, int y, Color col)
    {
        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        float thick = WorldConfig.BridgeBodyThickness;
        float x0 = x - half, x1 = x + 1 - half, z0 = y - half, z1 = y + 1 - half;
        float t00 = DeckVertexTop(gs, hf, x, y);
        float t10 = DeckVertexTop(gs, hf, x + 1, y);
        float t01 = DeckVertexTop(gs, hf, x, y + 1);
        float t11 = DeckVertexTop(gs, hf, x + 1, y + 1);

        // 顶面（法线朝上）
        AddQuad(v, n, c, idx,
            new Vector3(x0, t00, z0), new Vector3(x1, t10, z0),
            new Vector3(x0, t01, z1), new Vector3(x1, t11, z1), Vector3.Up, col);
        // 底面（下移体厚，法线朝下，绕序反转）
        AddQuad(v, n, c, idx,
            new Vector3(x1, t10 - thick, z0), new Vector3(x0, t00 - thick, z0),
            new Vector3(x1, t11 - thick, z1), new Vector3(x0, t01 - thick, z1), Vector3.Down, col);
        // 四侧壁（双面材质，绕序不拘）：南边 z0 / 北边 z1 / 西边 x0 / 东边 x1
        Color side = col.Darkened(0.15f);
        AddQuad(v, n, c, idx,
            new Vector3(x0, t00, z0), new Vector3(x1, t10, z0),
            new Vector3(x0, t00 - thick, z0), new Vector3(x1, t10 - thick, z0), new Vector3(0, 0, -1), side);
        AddQuad(v, n, c, idx,
            new Vector3(x1, t11, z1), new Vector3(x0, t01, z1),
            new Vector3(x1, t11 - thick, z1), new Vector3(x0, t01 - thick, z1), new Vector3(0, 0, 1), side);
        AddQuad(v, n, c, idx,
            new Vector3(x0, t01, z1), new Vector3(x0, t00, z0),
            new Vector3(x0, t01 - thick, z1), new Vector3(x0, t00 - thick, z0), new Vector3(-1, 0, 0), side);
        AddQuad(v, n, c, idx,
            new Vector3(x1, t10, z0), new Vector3(x1, t11, z1),
            new Vector3(x1, t10 - thick, z0), new Vector3(x1, t11 - thick, z1), new Vector3(1, 0, 0), side);
    }

    /// <summary>以四顶点（a=左上 b=右上 c=左下 d=右下）拼一四边形（两三角），统一法线/色。</summary>
    private static void AddQuad(List<Vector3> v, List<Vector3> nl, List<Color> cl, List<int> idx,
        Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Vector3 nrm, Color col)
    {
        int bi = v.Count;
        v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
        for (int i = 0; i < 4; i++) { nl.Add(nrm); cl.Add(col); }
        idx.Add(bi); idx.Add(bi + 1); idx.Add(bi + 2);
        idx.Add(bi + 1); idx.Add(bi + 3); idx.Add(bi + 2);
    }

    /// <summary>道路地基立面：本路格四边中邻格非路且非桥的边，垂一面从路面顶下到 −RoadFoundationDepth，
    /// 路面读作坐在高台基上（内部路-路边隐藏，只在路网轮廓垂基）。</summary>
    private static void AddRoadFoundation(GameState gs, HeightField hf, List<Vector3> v, List<Vector3> n, List<Color> c, List<int> idx,
        int x, int y, Color col)
    {
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
            AddQuad(v, n, c, idx, new Vector3(x0, h00, z0), new Vector3(x1, h10, z0),
                new Vector3(x0, h00 - depth, z0), new Vector3(x1, h10 - depth, z0), new Vector3(0, 0, -1), col);
        if (!RoadLike(x, y + 1))
            AddQuad(v, n, c, idx, new Vector3(x1, h11, z1), new Vector3(x0, h01, z1),
                new Vector3(x1, h11 - depth, z1), new Vector3(x0, h01 - depth, z1), new Vector3(0, 0, 1), col);
        if (!RoadLike(x - 1, y))
            AddQuad(v, n, c, idx, new Vector3(x0, h01, z1), new Vector3(x0, h00, z0),
                new Vector3(x0, h01 - depth, z1), new Vector3(x0, h00 - depth, z0), new Vector3(-1, 0, 0), col);
        if (!RoadLike(x + 1, y))
            AddQuad(v, n, c, idx, new Vector3(x1, h10, z0), new Vector3(x1, h11, z1),
                new Vector3(x1, h10 - depth, z0), new Vector3(x1, h11 - depth, z1), new Vector3(1, 0, 0), col);
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

    /// <summary>重建建筑层（地基/主体/屋顶/边框/门）：建筑数量级远小于格数，整层重建足够便宜。</summary>
    private void RebuildBuildings()
    {
        var gs = GameState.I;
        var foundXf = new List<Transform3D>();
        var foundColor = new List<Color>();
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

            // 房体范围：grown 与官营统一按占地 ~0.9 缩放整块绘制（房体=占地）；
            // 底面 = 垫基台面 + BuildingBaseLift（整体抬起免与地表穿插）
            float w, d;
            var center = MapGrid.CellToWorld(b.Origin);
            float groundY = gs.Map.GroundY(b.Origin); // 地形高度基准（建筑要求平地，整块同高）
            float baseY = groundY + WorldConfig.BuildingBaseLift; // 房体底面海拔
            w = b.FootX * cs * 0.9f;
            d = b.FootY * cs * 0.9f;
            center += new Vector3((b.FootX - 1) * cs / 2f, baseY + height / 2f, (b.FootY - 1) * cs / 2f);

            var color = b.Def.GodotColor;
            if (b.Condition < 50f)
                color = color.Darkened(0.35f * (1f - b.Condition / 50f));

            // 地基：不透明基座从房体底面向下延伸 FoundationDepth，斜坡上建造时遮住悬空的底部
            foundXf.Add(new Transform3D(
                Basis.FromScale(new Vector3(w, WorldConfig.FoundationDepth, d)),
                new Vector3(center.X, baseY - WorldConfig.FoundationDepth / 2f, center.Z)));
            foundColor.Add(FoundationColor);

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
                var roofCenter = new Vector3(center.X, baseY + height + roofH / 2f, center.Z);
                roofXf.Add(new Transform3D(roofBasis, roofCenter));
                roofColor.Add(color.Darkened(0.45f)); // 灰瓦感
            }

            // 门标记：沿占地边界贴墙放置，朝向由门内→门外方向决定；
            // 门高比成年村民（约 0.46m，见 VillagerConfig.ModelScale）略高，
            // 前后门同高，靠颜色（亮金/暗木）与宽度（宽/窄）区分
            gs.EnsureDoors(b);
            if (b.Doors != null)
            {
                foreach (var door in b.Doors)
                {
                    var dir = new Vector2I(door.Outside.X - door.Inside.X, door.Outside.Y - door.Inside.Y);
                    var dirW = new Vector3(dir.X, 0f, dir.Y);
                    const float doorH = 0.55f;
                    float wide = (door.IsMain ? 0.5f : 0.28f) * cs;
                    const float thick = 0.12f;
                    // 门面宽度沿墙面（垂直于 dir），厚度沿 dir；门底与房体底面同高
                    var scale = dir.X != 0 ? new Vector3(thick, doorH, wide) : new Vector3(wide, doorH, thick);
                    var pos = MapGrid.CellToWorld(door.Inside) + dirW * (cs * 0.5f)
                        + Vector3.Up * (gs.Map.GroundY(door.Inside) + WorldConfig.BuildingBaseLift + doorH / 2f);
                    // 大门居中：沿墙面方向对齐到占地几何中心（偶数宽建筑旧版会因卡格偏向一侧），
                    // 后门保持偏侧位（错落感）
                    if (door.IsMain)
                    {
                        if (dir.X != 0)
                            pos.Z = center.Z; // 东西墙：沿南北居中
                        else
                            pos.X = center.X; // 南北墙：沿东西居中
                    }
                    doorXf.Add(new Transform3D(Basis.FromScale(scale), pos));
                    doorColor.Add(door.IsMain ? MainDoorColor : BackDoorColor);
                }
            }
        }

        FillMultiMesh(_bldgFounds.Multimesh, foundXf, foundColor);
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

    /// <summary>重建图缘裙板：沿四条图缘逐顶点拉一圈竖直带状网格——上沿贴图缘地形顶点、
    /// 下沿垂到卷轴画布面（MinTerrainHeight 之下），遮住地形与画布间的侧向镂空；
    /// 4×1024 个四边形，重建开销可忽略（仅读档/新局/图缘垂基时触发）。</summary>
    private void RebuildSkirt()
    {
        var hf = GameState.I.Map.Height;
        const float cs = MapGrid.CellSize;
        float half = MapGrid.Size * cs / 2f;
        float bottom = TerrainConfig.MinTerrainHeight - 0.2f; // 与卷轴画布面齐平（Main 同值）
        int n = MapGrid.Size;

        var v = new List<Vector3>(4 * n * 4);
        var norm = new List<Vector3>(4 * n * 4);
        var col = new List<Color>(4 * n * 4);
        var idx = new List<int>(4 * n * 6);

        // 四条边：北（vy=0）/南（vy=n）/西（vx=0）/东（vx=n），法线朝外
        for (int side = 0; side < 4; side++)
        {
            var outward = side switch
            {
                0 => new Vector3(0, 0, -1),
                1 => new Vector3(0, 0, 1),
                2 => new Vector3(-1, 0, 0),
                _ => new Vector3(1, 0, 0),
            };
            for (int i = 0; i < n; i++)
            {
                // 本段两端顶点的图缘坐标（顶点索引）
                (int ax, int ay, int bx, int by) = side switch
                {
                    0 => (i, 0, i + 1, 0),
                    1 => (i, n, i + 1, n),
                    2 => (0, i, 0, i + 1),
                    _ => (n, i, n, i + 1),
                };
                float ha = hf.VertexH(ax, ay), hb = hf.VertexH(bx, by);
                var pa = new Vector3(ax * cs - half, ha, ay * cs - half);
                var pb = new Vector3(bx * cs - half, hb, by * cs - half);
                int b0 = v.Count;
                v.Add(pa);
                v.Add(pb);
                v.Add(new Vector3(pa.X, bottom, pa.Z));
                v.Add(new Vector3(pb.X, bottom, pb.Z));
                for (int k = 0; k < 4; k++)
                {
                    norm.Add(outward);
                    col.Add(SkirtColor);
                }
                idx.Add(b0); idx.Add(b0 + 1); idx.Add(b0 + 2);
                idx.Add(b0 + 1); idx.Add(b0 + 3); idx.Add(b0 + 2);
            }
        }
        _skirt.Mesh = MeshFrom(v, norm, col, idx);
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
