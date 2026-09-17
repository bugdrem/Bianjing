using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 地表渲染协调器：地表内容拆成六个独立图层（地形/水面/道路/植被/建筑/叠加，见 layers/ 目录），
/// 本类不再自己建网格，只负责三件事——
/// ① 订阅 EventBus 把变更翻译成脏标记（全图/矩形/单格/树层/建筑/坊区/裙板各走各的粒度）；
/// ② _Process 按预算仲裁消费脏块（整块 12 块/帧 + 只刷树 32 块/帧，尖峰摊多帧防卡顿）；
/// ③ 持有图缘裙板（属卷轴装裱 RenderLayers.Scroll，不属于任何地表内容层）。
/// 分块几何由 ChunkGeometryBuilder 单趟遍历产出，再分发给各图层的 ApplyChunk——
/// 遍历只做一次，图层拆开不增加重建开销。
/// </summary>
public partial class GridRenderer : Node3D
{
    /// <summary>图缘裙板色：比卷轴纸面略深的纸色，地形断面读作「画的厚度」。</summary>
    private static readonly Color SkirtColor = new(0.74f, 0.68f, 0.54f);

    /// <summary>每帧最多重建的分块数：限制全图标脏时的单帧重建量，把尖峰摊到多帧防卡顿
    /// （12 块/帧 → 1024 图 256 块约 22 帧（~0.35s@60fps）铺完，无可见顿挫）。</summary>
    private const int MaxChunkRebuildsPerFrame = 12;

    /// <summary>每帧最多只刷树层的分块数：树层重建（纯 MultiMesh 填充）远轻于整块重建，限额可更宽。</summary>
    private const int MaxTreeRebuildsPerFrame = 32;

    // ---- 六个内容图层：各自持节点与材质，本类只调 Build/ApplyChunk/Rebuild ----
    private TerrainLayer _terrain;
    private WaterLayer _water;
    private RoadLayer _road;
    private VegetationLayer _vegetation;
    private BuildingLayer _building;
    private OverlayLayer _overlay;

    /// <summary>单块几何暂存：单趟遍历的产出容器，逐块复用（Clear 后重填），免逐块分配。</summary>
    private readonly ChunkGeometry _geo = new();

    /// <summary>分块脏标记（地形/水/路/树整块）与树层独立脏标记（月度生长只刷树）。</summary>
    private bool[] _chunkDirty;
    private bool[] _treesDirty;
    private int _chunksPerSide;

    private MeshInstance3D _skirt; // 图缘裙板：周长带状网格，从图缘顶点垂到卷轴画布面，遮住侧向镂空
    private bool _buildingsDirty = true;
    private bool _zonesDirty = true;
    private bool _skirtDirty = true;

    public override void _Ready()
    {
        // 六图层依内容顺序挂载：地形 → 水面 → 道路 → 植被 → 建筑 → 叠加
        // （叠加以半透明色块/网格线压在一切实体之上，后挂保证绘制顺序）
        _terrain = new TerrainLayer { Name = "TerrainLayer" };
        _water = new WaterLayer { Name = "WaterLayer" };
        _road = new RoadLayer { Name = "RoadLayer" };
        _vegetation = new VegetationLayer { Name = "VegetationLayer" };
        _building = new BuildingLayer { Name = "BuildingLayer" };
        _overlay = new OverlayLayer { Name = "OverlayLayer" };
        AddChild(_terrain);
        AddChild(_water);
        AddChild(_road);
        AddChild(_vegetation);
        AddChild(_building);
        AddChild(_overlay);
        _terrain.Build();
        _water.Build();
        _road.Build();
        _vegetation.Build();
        _building.Build();
        _overlay.Build();

        // 分块脏标记阵列（分块划分与图层共用同一套 ChunkCells，保证寻址一致）
        _chunksPerSide = MapLayer.ChunksPerSide;
        int n = _chunksPerSide * _chunksPerSide;
        _chunkDirty = new bool[n];
        _treesDirty = new bool[n];
        for (int i = 0; i < n; i++)
            _chunkDirty[i] = true; // 首帧全量铺开

        // 图缘裙板：双面受光（低角度内外侧都可能看到），随 MapChanged 重建。
        // 裙板是「画的厚度」，属卷轴装裱（地图外）→ 归 RenderLayers.Scroll 层，与地图内分层渲染
        _skirt = new MeshInstance3D
        {
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                // 批次九十二：裙板属卷轴装裱（地图外），材质 Unshaded 不受光照，颜色纯取顶点色
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
            Layers = RenderLayers.Scroll,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_skirt);

        EventBus.MapChanged += MarkAllDirty;
        EventBus.ZonesChanged += MarkZonesDirty;
        EventBus.CellChanged += OnCellChanged;
        EventBus.RectChanged += MarkRectDirty;           // 建筑落成/拆除/扩建：只重建覆盖分块
        EventBus.BuildingsChanged += MarkBuildingsDirty; // 升级/转业：只重建建筑层
        EventBus.TreesChanged += MarkTreesDirty;         // 月度生长：只刷各块树木 MultiMesh
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

    /// <summary>全图变更（读档/月度生长/建筑增减）：全部分块 + 建筑 + 坊区 + 裙板一起重建。</summary>
    private void MarkAllDirty()
    {
        for (int i = 0; i < _chunkDirty.Length; i++)
            _chunkDirty[i] = true;
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
        for (int i = 0; i < _treesDirty.Length; i++)
            _treesDirty[i] = true;
    }

    /// <summary>矩形区域变更（建筑落成/拆除/扩建的垫基整平）：只标脏矩形覆盖的分块
    /// （外扩 1 格：整平会动到与邻块共享的边界顶点），建筑/坊区层跟随刷新。</summary>
    private void MarkRectDirty(Vector2I origin, Vector2I size)
    {
        int cx0 = Mathf.Clamp((origin.X - 1) / MapLayer.ChunkCells, 0, _chunksPerSide - 1);
        int cy0 = Mathf.Clamp((origin.Y - 1) / MapLayer.ChunkCells, 0, _chunksPerSide - 1);
        int cx1 = Mathf.Clamp((origin.X + size.X) / MapLayer.ChunkCells, 0, _chunksPerSide - 1);
        int cy1 = Mathf.Clamp((origin.Y + size.Y) / MapLayer.ChunkCells, 0, _chunksPerSide - 1);
        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
                _chunkDirty[cy * _chunksPerSide + cx] = true;
        _buildingsDirty = true;
        _zonesDirty = true;
        if (origin.X <= 1 || origin.Y <= 1 || origin.X + size.X >= MapGrid.Size - 1 || origin.Y + size.Y >= MapGrid.Size - 1)
            _skirtDirty = true; // 图缘附近的垫基可能动到边缘顶点
    }

    /// <summary>单格变更（铺路/砍树/拆除/扩地/垫基）：重建所在分块；整平垫基会动到与邻块共享的边界顶点，
    /// 位于块缘的格连带标脏相邻块，避免地形接缝错位；坊区/建筑层跟随刷新。</summary>
    private void OnCellChanged(Vector2I c)
    {
        int cx = c.X / MapLayer.ChunkCells, cy = c.Y / MapLayer.ChunkCells;
        for (int ox = -1; ox <= 1; ox++)
        {
            for (int oy = -1; oy <= 1; oy++)
            {
                // 非块缘格不波及对应方向的邻块（边界顶点才与邻块共享）
                if (ox != 0 && (ox < 0 ? c.X % MapLayer.ChunkCells != 0 : c.X % MapLayer.ChunkCells != MapLayer.ChunkCells - 1))
                    continue;
                if (oy != 0 && (oy < 0 ? c.Y % MapLayer.ChunkCells != 0 : c.Y % MapLayer.ChunkCells != MapLayer.ChunkCells - 1))
                    continue;
                int mx = cx + ox, my = cy + oy;
                if (mx >= 0 && mx < _chunksPerSide && my >= 0 && my < _chunksPerSide)
                    _chunkDirty[my * _chunksPerSide + mx] = true;
            }
        }
        _zonesDirty = true;    // 坊区色块层便宜，跟随刷新
        _buildingsDirty = true; // 房体檐隙随临路变化，建筑数量级小整层重建不贵
        if (c.X <= 1 || c.Y <= 1 || c.X >= MapGrid.Size - 2 || c.Y >= MapGrid.Size - 2)
            _skirtDirty = true; // 图缘格变更（垂基整平可能动到边缘顶点）：裙板跟随重建
    }

    /// <summary>建造网格线显隐（仅建造模式打开）：委托叠加层。</summary>
    public void SetGridVisible(bool visible) => _overlay.SetGridVisible(visible);

    /// <summary>规划色块显隐（批次七十）：仅分区模式下显示，平时不画规划底图；委托叠加层。</summary>
    public void SetZonesVisible(bool visible) => _overlay.SetZonesVisible(visible);

    public override void _Process(double delta)
    {
        // 分块重建限额：全图变更（读档/月度生长/建筑升级转业）会把全部分块标脏，
        // 若同帧重建全部（1024 图 256 块×每块 4096 格≈百万格）会造成尖峰卡顿（尤其 4x 下建筑频变）；
        // 限每帧最多重建 MaxChunkRebuildsPerFrame 块，将尖峰摊到多帧（余脏块下帧续建）。
        // 预算仲裁集中在协调器而非各图层自跑——六个图层各持一份预算会把同帧重建量翻六倍。
        int budget = MaxChunkRebuildsPerFrame;
        for (int i = 0; i < _chunkDirty.Length && budget > 0; i++)
        {
            if (!_chunkDirty[i])
                continue;
            _chunkDirty[i] = false;
            _treesDirty[i] = false; // 整块重建已含树层
            RebuildChunk(i);
            budget--;
        }
        // 树层单独刷新（月度生长）：只填 MultiMesh 不重建网格，限额更宽
        int treeBudget = MaxTreeRebuildsPerFrame;
        for (int i = 0; i < _treesDirty.Length && treeBudget > 0; i++)
        {
            if (!_treesDirty[i] || _chunkDirty[i])
                continue; // 整块待重建的交给上方循环顺带刷树
            _treesDirty[i] = false;
            RebuildChunkTrees(i);
            treeBudget--;
        }
        if (_zonesDirty)
        {
            _zonesDirty = false;
            _overlay.RebuildZones();
        }
        if (_buildingsDirty)
        {
            _buildingsDirty = false;
            _building.Rebuild();
        }
        if (_skirtDirty)
        {
            _skirtDirty = false;
            RebuildSkirt();
        }
    }

    /// <summary>重建单个分块：单趟遍历块内格产出水面/道路/桥/树缓冲，分发给各图层；
    /// 地形网格由地形层独立重建（不依赖遍历产出）。</summary>
    private void RebuildChunk(int index)
    {
        var (x0, y0, x1, y1) = MapLayer.ChunkRect(index);
        _geo.Clear();
        ChunkGeometryBuilder.Build(GameState.I, _geo, x0, y0, x1, y1, _vegetation.TreeModelMeshes);
        _terrain.ApplyChunk(index);
        _water.ApplyChunk(index, _geo);
        _road.ApplyChunk(index, _geo);
        _vegetation.ApplyChunk(index, _geo);
    }

    /// <summary>只刷新单块的树木 MultiMesh（月度生长/散播）：不重建地形/水面/道路网格，
    /// 开销仅为遍历块内格 + MultiMesh 填充，远轻于整块重建。</summary>
    private void RebuildChunkTrees(int index)
    {
        var (x0, y0, x1, y1) = MapLayer.ChunkRect(index);
        _geo.ClearTrees();
        ChunkGeometryBuilder.BuildTrees(GameState.I, _geo, x0, y0, x1, y1, _vegetation.TreeModelMeshes);
        _vegetation.ApplyChunk(index, _geo);
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

        var buf = new MeshBuffers();
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
                int b0 = buf.Verts.Count;
                buf.Verts.Add(pa);
                buf.Verts.Add(pb);
                buf.Verts.Add(new Vector3(pa.X, bottom, pa.Z));
                buf.Verts.Add(new Vector3(pb.X, bottom, pb.Z));
                for (int k = 0; k < 4; k++)
                {
                    buf.Normals.Add(outward);
                    buf.Colors.Add(SkirtColor);
                }
                buf.Index.Add(b0); buf.Index.Add(b0 + 1); buf.Index.Add(b0 + 2);
                buf.Index.Add(b0 + 1); buf.Index.Add(b0 + 3); buf.Index.Add(b0 + 2);
            }
        }
        _skirt.Mesh = LayerKit.MeshFrom(buf);
    }
}
