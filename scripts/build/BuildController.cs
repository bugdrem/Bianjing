using System.Collections.Generic;
using Godot;

namespace Bianjing;

public enum BuildMode
{
    None,
    Road,
    Bridge,
    Building,
    Zone,
    Tree,
    Demolish,
}

/// <summary>分区工具（批次七十一）：拖拽拉矩形（默认）、笔刷沿路径涂抹、油漆桶洪水填充；
/// 与分区操作（规划/删除）正交组合，由分区菜单按钮切换，暂未绑定快捷键。</summary>
public enum ZoneTool
{
    Brush,
    Rect,
    Bucket,
}

/// <summary>道路绘制工具（批次八十）：直线（默认）/贝塞尔曲线/手绘涂抹，主路辅路桥梁通用；
/// 直线与曲线为“按下定起点、拖动中预览、松开一次性落笔”，手绘为拖动实时涂抹。</summary>
public enum RoadTool
{
    Straight,
    Bezier,
    Freehand,
}

/// <summary>建造交互控制器：鼠标网格预览、放置/拖动铺路/拖框划坊/拆除。坊区划定（ZoneController 职责）合并于此。</summary>
public partial class BuildController : Node
{
    private static readonly Color ValidColor = new(0.3f, 1f, 0.3f, 0.4f);
    private static readonly Color InvalidColor = new(1f, 0.25f, 0.25f, 0.4f);
    private static readonly Color DemolishColor = new(1f, 0.4f, 0.1f, 0.45f);

    private readonly RtsCameraRig _rig;
    private readonly GridRenderer _renderer;

    public Hud Hud { get; set; }

    /// <summary>居民代理管理器（点选拾取 NPC 用）。</summary>
    public AgentManager Agents { get; set; }

    /// <summary>外来访客系统（点选拾取外城来人用）。</summary>
    public VisitorSystem Visitors { get; set; }

    /// <summary>相机云台（供点选面板定位镜头用）。</summary>
    public RtsCameraRig Rig => _rig;

    public BuildMode Mode { get; private set; } = BuildMode.None;

    private BuildingDef _def;
    private ZoneType _zone = ZoneType.Buildable; // 默认建筑区：进分区模式未选类型时不致误清规划
    public ZoneType Zone => _zone; // 分区类型（分区菜单按钮组初始态同步用）

    /// <summary>分区工具与操作（批次七十一）：油漆桶/笔刷/拖拽 × 规划/删除，按钮切换。</summary>
    public ZoneTool ZoneTool { get; private set; } = ZoneTool.Rect;
    public bool ZoneErase { get; private set; }
    private Vector2I? _lastBrushCell; // 笔刷上一盖戳格（沿线插值防跳格）
    private bool _zoneDirty; // 笔刷/拖框的分区变更累积标记：每帧至多广播一次重建
    private bool _dragging;
    private Vector2I _dragStart;
    private Vector2I _hover = new(-1, -1);
    private bool _hoverInMap;

    // 道路/桥方形画笔拖动：上一盖戳中心格（沿线插值防跳格）
    private Vector2I? _lastRoadCell;

    /// <summary>道路绘制工具（批次八十）：默认直线；主路/辅路/桥梁共用一套工具。</summary>
    public RoadTool RoadTool { get; private set; } = RoadTool.Straight;

    /// <summary>直线/曲线：按下时的起点格（拖动中预览、松开一次性落笔）。</summary>
    private Vector2I? _roadStart;

    // 直线/曲线路径预览：把落笔格序列画成一排贴地半透明方块（不实际铺路）
    private MeshInstance3D _pathPreview;
    private ImmediateMesh _pathMesh;

    private MeshInstance3D _preview;
    private StandardMaterial3D _previewMat;

    // 阶段 D：建筑放置预览用「真实宋代轮廓」装配（同源 BuildingModelFactory.MakePreview），
    // 取代单一方块，避免预览误导实际造型。
    private Node3D _previewAssembly;
    private MultiMeshInstance3D _paFound, _paBody, _paRoof, _paRoofEnd, _paEave, _paRidge, _paPillar, _paBanner, _paLantern;
    private StandardMaterial3D _previewVcMat, _previewBodyMat;

    public BuildController(RtsCameraRig rig, GridRenderer renderer)
    {
        _rig = rig;
        _renderer = renderer;
    }

    public override void _Ready()
    {
        _previewMat = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = ValidColor,
        };
        _preview = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            MaterialOverride = _previewMat,
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        // 批次八十七：直接挂自身（旧版 GetParent().CallDeferred 延迟到帧末挂父节点，
        // 父节点若在帧末前释放（返回标题重载场景）会 AddChild 到已释放节点；
        // BuildController 自身无变换，预览用局部坐标与挂父等价）
        AddChild(_preview);

        // 阶段 D：建筑预览装配（9 角色 MultiMesh，顶点色受光；房体半透），默认隐藏
        _previewVcMat = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _previewBodyMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        _previewAssembly = new Node3D { Visible = false };
        AddChild(_previewAssembly);
        MakePreviewMulti(ref _paFound, new BoxMesh { Size = Vector3.One, Material = _previewVcMat });
        MakePreviewMulti(ref _paBody, new BoxMesh { Size = Vector3.One, Material = _previewBodyMat });
        MakePreviewMulti(ref _paRoof, new PrismMesh { Size = Vector3.One, Material = _previewVcMat });
        MakePreviewMulti(ref _paRoofEnd, new PrismMesh { Size = Vector3.One, Material = _previewVcMat });
        MakePreviewMulti(ref _paEave, new BoxMesh { Size = Vector3.One, Material = _previewVcMat });
        MakePreviewMulti(ref _paRidge, new BoxMesh { Size = Vector3.One, Material = _previewVcMat });
        MakePreviewMulti(ref _paPillar, new CylinderMesh { TopRadius = 0.13f, BottomRadius = 0.13f, Height = 1f, Material = _previewVcMat });
        MakePreviewMulti(ref _paBanner, new BoxMesh { Size = Vector3.One, Material = _previewVcMat });
        MakePreviewMulti(ref _paLantern, new SphereMesh { Radius = 0.5f, Height = 1f, Material = _previewVcMat });

        // 路径预览（批次八十）：直线/曲线拖动中整条线半透明显示，材质与单格预览同款
        _pathMesh = new ImmediateMesh();
        _pathPreview = new MeshInstance3D
        {
            Mesh = _pathMesh,
            MaterialOverride = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = ValidColor,
            },
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_pathPreview);
    }

    // ---- 模式切换（由 BuildMenu 调用）----

    public void SetModeNone() => SwitchMode(BuildMode.None);

    /// <summary>当前道路铺设种类（由建造栏选择）。</summary>
    private RoadKind _roadKind = RoadKind.Side;

    public void SetRoadMode(RoadKind kind = RoadKind.Side)
    {
        _roadKind = kind;
        SwitchMode(BuildMode.Road);
    }

    public void SetBridgeMode() => SwitchMode(BuildMode.Bridge);

    public void SetBuildingMode(BuildingDef def)
    {
        _def = def;
        SwitchMode(BuildMode.Building);
    }

    public void SetZoneMode(ZoneType zone)
    {
        _zone = zone;
        SwitchMode(BuildMode.Zone);
    }

    /// <summary>切换分区工具（拖拽/笔刷/油漆桶）：未在分区模式则自动进入。</summary>
    public void SetZoneTool(ZoneTool tool)
    {
        ZoneTool = tool;
        if (Mode != BuildMode.Zone)
            SwitchMode(BuildMode.Zone);
    }

    /// <summary>切换道路绘制工具（直线/曲线/手绘）：未在铺路模式则自动进入（沿用当前道路类型）。</summary>
    public void SetRoadTool(RoadTool tool)
    {
        RoadTool = tool;
        if (Mode != BuildMode.Road && Mode != BuildMode.Bridge)
            SwitchMode(BuildMode.Road);
    }

    /// <summary>切换分区操作：规划（落当前类型）/ 删除（清点击处一切规划，与类型无关）。</summary>
    public void SetZoneErase(bool erase)
    {
        ZoneErase = erase;
        if (Mode != BuildMode.Zone)
            SwitchMode(BuildMode.Zone);
    }

    public void SetDemolishMode() => SwitchMode(BuildMode.Demolish);

    public void SetTreeMode() => SwitchMode(BuildMode.Tree);

    private void SwitchMode(BuildMode mode)
    {
        // 首建门槛（批次八十一）：王爷府未落成前锁定一切模式切换——开局选位中右键/选择/分区等
        // 均不可退出放置模式（菜单无王爷府入口，退出即死锁），只能落成王爷府；落成后恢复正常切换。
        // 放行当前正在放置王爷府本体；放置成功时 PrinceMansionBuilt 已为真，退出不受阻。
        if (!GameState.I.PrinceMansionBuilt
            && !(mode == BuildMode.Building && _def != null && _def.Id == PrinceMansionConfig.DefId))
        {
            Hud?.ShowCellInfo("请先点击地图落成王爷府——落成后解锁一切营造");
            return;
        }
        Mode = mode;
        _dragging = false;
        _lastRoadCell = null;
        _lastBrushCell = null;
        _roadStart = null; // 直线/曲线的待落笔起点作废
        if (_zoneDirty)
        {
            _zoneDirty = false;
            EventBus.RaiseZonesChanged(); // 笔刷残留变更即时落盘，防切模式后色块缺角
        }
        _renderer.SetGridVisible(mode != BuildMode.None);
        _renderer.SetZonesVisible(mode == BuildMode.Zone); // 批次七十：规划色块仅分区模式显示
        _preview.Visible = false;
        _pathPreview.Visible = false;
    }

    // ---- 输入 ----

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton mb)
            return;

        if (mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
                OnLeftPressed();
            else
                OnLeftReleased();
        }
        else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed && Mode != BuildMode.None)
        {
            SetModeNone();
        }
    }

    private void OnLeftPressed()
    {
        if (!_hoverInMap)
            return;

        // 点到游戏世界（非 UI，否则不会进 _UnhandledInput）：收起政策/财政/科技侧面板（公告栏常驻不收）
        Hud?.CloseSidePanels();

        // 开局首建门槛：未建成王爷府前锁定一切营造（放置王爷府本体除外）；「选择/查看」不受限
        var gsGate = GameState.I;
        if (!gsGate.PrinceMansionBuilt && Mode != BuildMode.None
            && !(Mode == BuildMode.Building && _def != null && _def.Id == PrinceMansionConfig.DefId))
        {
            Hud?.ShowCellInfo("请先建造王爷府");
            return;
        }

        switch (Mode)
        {
            case BuildMode.None:
                InspectAt(_hover);
                break;
            case BuildMode.Road:
                _dragging = true;
                _lastRoadCell = null;
                if (RoadTool == RoadTool.Freehand)
                    DragRoadTo(_hover, isBridge: false); // 手绘：按下即落第一戳
                else
                    _roadStart = _hover; // 直线/曲线：按下定起点，拖动中预览，松开一次性落笔
                break;
            case BuildMode.Bridge:
                _dragging = true;
                _lastRoadCell = null;
                if (RoadTool == RoadTool.Freehand)
                    DragRoadTo(_hover, isBridge: true);
                else
                    _roadStart = _hover;
                break;
            case BuildMode.Building:
                TryPlaceBuilding(BuildingOrigin());
                break;
            case BuildMode.Zone:
                // 批次七十一：按工具分派——油漆桶单击填充/清除（删除模式不查闭合），笔刷与拖拽进入拖动
                if (ZoneTool == ZoneTool.Bucket)
                {
                    if (ZoneErase)
                        FillZoneErase(_hover);
                    else
                        FillZone(_hover);
                    break;
                }
                _dragging = true;
                _dragStart = _hover;
                _lastBrushCell = null;
                if (ZoneTool == ZoneTool.Brush)
                    BrushZoneTo(_hover);
                break;
            case BuildMode.Tree:
                _dragging = true;
                GameState.I.PlaceTree(_hover);
                break;
            case BuildMode.Demolish:
                _dragging = true;
                GameState.I.DemolishAt(_hover);
                break;
        }
    }

    private void OnLeftReleased()
    {
        if (Mode == BuildMode.Zone)
        {
            if (_dragging && _hoverInMap && ZoneTool == ZoneTool.Rect)
                ApplyZoneRect(_dragStart, _hover);
            FlushZoneDirty();
        }
        else if ((Mode == BuildMode.Road || Mode == BuildMode.Bridge) && _dragging && _hoverInMap
            && RoadTool != RoadTool.Freehand && _roadStart.HasValue)
        {
            // 直线/曲线落笔：从起点到悬停格整条线一次性盖戳（手绘在拖动中已逐格盖戳）
            StampPathCells(CurrentPathCells(), Mode == BuildMode.Bridge);
        }
        _dragging = false;
        _lastRoadCell = null;
        _lastBrushCell = null;
        _roadStart = null;
        _pathPreview.Visible = false;
    }

    public override void _Process(double delta)
    {
        UpdateHoverCell();

        if (_dragging && _hoverInMap)
        {
            if (Mode == BuildMode.Road)
            {
                if (RoadTool == RoadTool.Freehand)
                    DragRoadTo(_hover, isBridge: false);
                else
                    UpdatePathPreview(isBridge: false); // 直线/曲线：拖动中只预览不落笔
            }
            else if (Mode == BuildMode.Bridge)
            {
                if (RoadTool == RoadTool.Freehand)
                    DragRoadTo(_hover, isBridge: true);
                else
                    UpdatePathPreview(isBridge: true);
            }
            else if (Mode == BuildMode.Tree)
                GameState.I.PlaceTree(_hover);
            else if (Mode == BuildMode.Demolish)
                GameState.I.DemolishAt(_hover);
            else if (Mode == BuildMode.Zone && ZoneTool == ZoneTool.Brush)
                BrushZoneTo(_hover); // 笔刷沿拖动轨迹涂抹
        }

        FlushZoneDirty(); // 笔刷拖动中每帧至多广播一次分区重建（防逐格刷爆）
        UpdatePreview();
    }

    /// <summary>悬停格：鼠标视线与地形高度场的首个交点所在格——沿视线半格步长下探，
    /// 比旧版 Y=0 平面求交准：高地/台地上预览框不再偏向远处（表现为方块挂在鼠标右上角）。</summary>
    private void UpdateHoverCell()
    {
        var vp = GetViewport();
        var mouse = vp.GetMousePosition();
        var cam = _rig.Cam;
        var from = cam.ProjectRayOrigin(mouse);
        var dir = cam.ProjectRayNormal(mouse);

        _hoverInMap = false;
        if (dir.Y >= -0.0001f)
            return; // 视线不朝下（贴地平视）：无落地点

        // 最高地形之上无可交，先快进到封顶面再逐步下探
        const float step = MapGrid.CellSize / 2f;
        var hf = GameState.I.Map.Height;
        float t = from.Y > TerrainConfig.MaxTerrainHeight ? (TerrainConfig.MaxTerrainHeight - from.Y) / dir.Y : 0f;
        for (int i = 0; i < 4096; i++, t += step)
        {
            var p = from + dir * t;
            if (p.Y < TerrainConfig.MinTerrainHeight - 0.5f)
                return; // 已穿透最深地形，视线落在图外
            var cell = MapGrid.WorldToCell(p);
            if (!MapGrid.InBounds(cell))
                continue;
            if (p.Y <= hf.SampleWorld(p.X, p.Z))
            {
                _hover = cell;
                _hoverInMap = true;
                return;
            }
        }
    }

    // ---- 放置操作 ----

    /// <summary>拖动铺设道路/桥：方形画笔（主路 4×4、辅路 2×2、桥 4×4）沿拖动轨迹逐米盖戳，
    /// 鼠标快速拖动时从上一中心格沿线插值不断档；每前进一米扣一次造价（重叠区不重复扣）。</summary>
    private void DragRoadTo(Vector2I c, bool isBridge)
    {
        var gs = GameState.I;
        if (_lastRoadCell == null)
        {
            LayStamp(gs, c, isBridge);
            _lastRoadCell = c;
            return;
        }

        var from = _lastRoadCell.Value;
        if (c == from)
            return;
        var d = c - from;
        int steps = Mathf.Max(Mathf.Abs(d.X), Mathf.Abs(d.Y));
        for (int i = 1; i <= steps; i++)
            LayStamp(gs, new Vector2I(from.X + d.X * i / steps, from.Y + d.Y * i / steps), isBridge);
        _lastRoadCell = c;
    }

    private void LayStamp(GameState gs, Vector2I center, bool isBridge)
    {
        if (isBridge)
            gs.PlaceBridgeStamp(center);
        else
            gs.PlaceRoadStamp(center, _roadKind);
    }

    // ---- 直线/曲线落笔与预览（批次八十）----

    /// <summary>当前工具从起点到悬停格的落笔格序列（直线 Bresenham / 贝塞尔采样）。</summary>
    private List<Vector2I> CurrentPathCells()
        => RoadTool == RoadTool.Straight
            ? StraightLineCells(_roadStart!.Value, _hover)
            : BezierCells(_roadStart!.Value, _hover);

    /// <summary>直线落笔：Bresenham 直线（含两端点），逐格盖戳。</summary>
    private static List<Vector2I> StraightLineCells(Vector2I a, Vector2I b)
    {
        var cells = new List<Vector2I>();
        int dx = Mathf.Abs(b.X - a.X), dy = Mathf.Abs(b.Y - a.Y);
        int sx = a.X < b.X ? 1 : -1, sy = a.Y < b.Y ? 1 : -1;
        int err = dx - dy;
        var c = a;
        while (true)
        {
            cells.Add(c);
            if (c == b)
                break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; c.X += sx; }
            if (e2 < dx) { err += dx; c.Y += sy; }
        }
        return cells;
    }

    /// <summary>贝塞尔曲线落笔：二次贝塞尔，控制点 = 起点终点连线中点沿拖动方向右法线偏移 30% 弧高
    /// （曲线始终弯向拖动方向右侧）；按每半格一个采样点，大弯处补 Bresenham 防跳格。</summary>
    private static List<Vector2I> BezierCells(Vector2I p0, Vector2I p3)
    {
        var cells = new List<Vector2I> { p0 };
        var v0 = new Vector2(p0.X, p0.Y);
        var v3 = new Vector2(p3.X, p3.Y);
        float len = (v3 - v0).Length();
        if (len < 0.5f)
            return cells;
        var d = (v3 - v0) / len;
        var ctrl = (v0 + v3) / 2f + new Vector2(-d.Y, d.X) * (len * 0.3f); // 右法线 × 30% 弧高
        int steps = Mathf.Max(2, (int)Mathf.Ceil(len * 2f));
        Vector2I? last = null;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float u = 1f - t;
            var p = u * u * v0 + 2f * u * t * ctrl + t * t * v3;
            var c = new Vector2I((int)Mathf.Round(p.X), (int)Mathf.Round(p.Y));
            if (c == last)
                continue;
            if (last.HasValue && Mathf.Max(Mathf.Abs(c.X - last.Value.X), Mathf.Abs(c.Y - last.Value.Y)) > 1)
                cells.AddRange(StraightLineCells(last.Value, c)); // 大弯处采样跨格：补线防断档
            cells.Add(c);
            last = c;
        }
        return cells;
    }

    /// <summary>沿线逐格盖戳（直线/曲线落笔共用；每格内部自己处理宽笔与桥/覆盖/计费）。</summary>
    private void StampPathCells(List<Vector2I> cells, bool isBridge)
    {
        foreach (var c in cells)
            LayStamp(GameState.I, c, isBridge);
    }

    /// <summary>拖动中更新路径预览（直线/曲线）：起点到悬停格整条线的半透明方块。
    /// 预览不校验落笔合法性（落笔时逐格自行跳过不可放格），常显绿色便于看清路径走向。</summary>
    private void UpdatePathPreview(bool isBridge)
    {
        if (_roadStart == null)
            return;
        ShowPathPreview(CurrentPathCells());
    }

    /// <summary>路径预览绘制：把格子序列画成一排贴地半透明方块（ImmediateMesh 单实例，每格一个 quad）。</summary>
    private void ShowPathPreview(List<Vector2I> cells)
    {
        const float cs = MapGrid.CellSize;
        _pathMesh.ClearSurfaces();
        _pathMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var c in cells)
        {
            var w = MapGrid.CellToWorld(c);
            float y = GameState.I.Map.GroundY(c) + 0.12f; // 逐格贴地，台地上不悬空
            var v0 = new Vector3(w.X - cs / 2f, y, w.Z - cs / 2f);
            var v1 = new Vector3(w.X + cs / 2f, y, w.Z - cs / 2f);
            var v2 = new Vector3(w.X + cs / 2f, y, w.Z + cs / 2f);
            var v3 = new Vector3(w.X - cs / 2f, y, w.Z + cs / 2f);
            _pathMesh.SurfaceAddVertex(v0);
            _pathMesh.SurfaceAddVertex(v1);
            _pathMesh.SurfaceAddVertex(v2);
            _pathMesh.SurfaceAddVertex(v0);
            _pathMesh.SurfaceAddVertex(v2);
            _pathMesh.SurfaceAddVertex(v3);
        }
        _pathMesh.SurfaceEnd();
        _pathPreview.Visible = true;
    }

    private void TryPlaceBuilding(Vector2I origin)
    {
        var gs = GameState.I;
        if (PlacementValidator.CanPlaceBuilding(gs, _def, origin))
        {
            gs.PlaceBuilding(_def, origin);
            // 王爷府首建（批次八十一）：一次性落成，放置成功即退出建造模式（预览收起，落成后解锁一切营造）
            if (_def.Id == PrinceMansionConfig.DefId)
                SetModeNone();
        }
    }

    private void ApplyZoneRect(Vector2I a, Vector2I b)
    {
        int x0 = Mathf.Min(a.X, b.X), x1 = Mathf.Max(a.X, b.X);
        int y0 = Mathf.Min(a.Y, b.Y), y1 = Mathf.Max(a.Y, b.Y);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                ApplyZoneStamp(new Vector2I(x, y)); // 逐格盖戳（规划/删除同路）
        FlushZoneDirty();
    }

    /// <summary>单格分区写操作（批次七十一）：规划=仅可规划空地落当前类型；删除=清除该格一切规划。
    /// 经 GameState.SetZone 统一写入口，分区索引同步维护；只置脏标记，由 FlushZoneDirty 统一广播。
    /// 返回是否实际变更（幂等同格返回 false）。</summary>
    private bool ApplyZoneStamp(Vector2I c)
    {
        var gs = GameState.I;
        if (ZoneErase)
        {
            if (gs.Map.CellAt(c).Zone == ZoneType.None)
                return false;
            gs.SetZone(c, ZoneType.None);
            _zoneDirty = true;
            return true;
        }
        if (!PlacementValidator.CanZone(gs, c) || gs.Map.CellAt(c).Zone == _zone)
            return false;
        gs.SetZone(c, _zone);
        _zoneDirty = true;
        return true;
    }

    /// <summary>笔刷涂抹（批次七十一）：沿拖动轨迹逐格盖戳，快速拖动时从上一格插值补格（同铺路）。</summary>
    private void BrushZoneTo(Vector2I c)
    {
        if (_lastBrushCell == null)
        {
            ApplyZoneStamp(c);
            _lastBrushCell = c;
            return;
        }
        var from = _lastBrushCell.Value;
        if (c == from)
            return;
        var d = c - from;
        int steps = Mathf.Max(Mathf.Abs(d.X), Mathf.Abs(d.Y));
        for (int i = 1; i <= steps; i++)
            ApplyZoneStamp(new Vector2I(from.X + d.X * i / steps, from.Y + d.Y * i / steps));
        _lastBrushCell = c;
    }

    /// <summary>分区变更统一广播（每帧至多一次）：笔刷拖动期间不逐格重建分区色块。</summary>
    private void FlushZoneDirty()
    {
        if (!_zoneDirty)
            return;
        _zoneDirty = false;
        EventBus.RaiseZonesChanged();
    }

    /// <summary>油漆桶填充分区（批次七十）：Shift+左键单击——以道路（主/辅/桥面，不含小路）与河流为界，
    /// 向四周扩散把整片封闭区域刷成当前分区类型；扩散出图（围合未封闭）则提示且不生效。
    /// 树/已有建筑不阻断填充（非围合线），但只有可规划空地才落区。</summary>
    private void FillZone(Vector2I start)
    {
        var gs = GameState.I;
        ref var sc = ref gs.Map.CellAt(start);
        if ((sc.HasRoad && sc.RoadKind != RoadKind.Lane) || sc.HasWater)
        {
            Hud?.ShowCellInfo("油漆桶需点击封闭区域内部（以主/辅路与河流为界）");
            return;
        }

        const int MaxFillCells = 400_000; // 单次填充上限：防全图开放区刷爆（正常封闭区域远小于此）
        var visited = new HashSet<Vector2I>();
        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);
        bool closed = true;
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (!visited.Add(c))
                continue;
            if (visited.Count > MaxFillCells)
            {
                closed = false; // 区域过大：按未封闭处理，免整图刷爆
                break;
            }
            if (!MapGrid.InBounds(c))
            {
                closed = false; // 扩散出图：围合边界不封闭
                break;
            }
            ref var cell = ref gs.Map.CellAt(c);
            // 边界墙：道路（不含小路）与河流——只作围合线，不扩散不填充
            if ((cell.HasRoad && cell.RoadKind != RoadKind.Lane) || cell.HasWater)
                continue;
            queue.Enqueue(c + new Vector2I(1, 0));
            queue.Enqueue(c + new Vector2I(-1, 0));
            queue.Enqueue(c + new Vector2I(0, 1));
            queue.Enqueue(c + new Vector2I(0, -1));
        }
        if (!closed)
        {
            // 未封闭：尚未落区即返回（修复前 BFS 边扩散边 ApplyZoneStamp，出图后已改部分未回滚，
            // 玩家看到"未生效"提示却已有部分分区被静默写入）
            Hud?.ShowCellInfo("油漆桶未生效：区域未封闭（道路/河流未围拢）");
            return;
        }
        // 封闭确认后再统一落区（visited 均在地图内；边界墙/不可规划格由 ApplyZoneStamp 自行跳过）
        int changed = 0;
        foreach (var c in visited)
            if (ApplyZoneStamp(c))
                changed++;
        if (changed > 0)
            FlushZoneDirty();
        else
            Hud?.ShowCellInfo("该封闭区域内没有可规划的空地");
    }

    /// <summary>油漆桶删除（批次七十一）：从点击处向四周扩散清除一切分区规划（与类型无关），
    /// 路/河照旧作扩散边界；不检查闭合——扩散出图即止、不提示不撤销（规划版出图会判未封闭）。</summary>
    private void FillZoneErase(Vector2I start)
    {
        var gs = GameState.I;
        const int MaxFillCells = 400_000; // 单次扩散上限：防全图刷爆
        var visited = new HashSet<Vector2I>();
        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);
        int changed = 0;
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (!visited.Add(c))
                continue;
            if (visited.Count > MaxFillCells)
                break;
            if (!MapGrid.InBounds(c))
                continue; // 出图即止：删除模式不做闭合检查
            ref var cell = ref gs.Map.CellAt(c);
            // 边界墙与规划版一致（主/辅路与河流）；树/建筑不阻断扩散
            if ((cell.HasRoad && cell.RoadKind != RoadKind.Lane) || cell.HasWater)
                continue;
            if (ApplyZoneStamp(c))
                changed++;
            queue.Enqueue(c + new Vector2I(1, 0));
            queue.Enqueue(c + new Vector2I(-1, 0));
            queue.Enqueue(c + new Vector2I(0, 1));
            queue.Enqueue(c + new Vector2I(0, -1));
        }
        if (changed > 0)
            FlushZoneDirty();
        else
            Hud?.ShowCellInfo("该区域没有分区规划");
    }

    // ---- 预览 ----

    private void UpdatePreview()
    {
        if (Mode == BuildMode.None || !_hoverInMap)
        {
            _preview.Visible = false;
            return;
        }

        var gs = GameState.I;
        const float cs = MapGrid.CellSize;
        float groundY = gs.Map.GroundY(_hover); // 预览框叠加悬停格地形海拔，免在台地上半埋
        _preview.Visible = true;
        _previewAssembly.Visible = false; // 默认隐藏，仅建筑预览显示轮廓装配

        switch (Mode)
        {
            case BuildMode.Road:
            {
                // 直线/曲线拖动中：整条路径由 _pathPreview 显示，不再重复画单格方块
                if (_dragging && RoadTool != RoadTool.Freehand)
                    break;
                // 方形画笔预览：w×w整块（宽 4 时偏移 -1..2，中心偏移半格）
                int w = GameState.RoadWidthOf(_roadKind);
                SetPreviewBox(StampCenter(w) + Vector3.Up * (groundY + 0.15f), StampSize(w, 0.3f),
                    PlacementValidator.CanPlaceRoad(gs, _hover, _roadKind) ? ValidColor : InvalidColor);
                break;
            }

            case BuildMode.Bridge:
                // 直线/曲线拖动中同理交给路径预览
                if (_dragging && RoadTool != RoadTool.Freehand)
                    break;
                SetPreviewBox(StampCenter(GameState.BridgeWidth) + Vector3.Up * 0.25f, StampSize(GameState.BridgeWidth, 0.5f),
                    PlacementValidator.CanPlaceBridge(gs, _hover) ? ValidColor : InvalidColor);
                break;

            case BuildMode.Building:
            {
                // 阶段 D：用真实宋代轮廓装配预览（同源 BuildingModelFactory.MakePreview），取代方块
                var originCell = BuildingOrigin();
                ShowBuildingPreviewDef(_def, originCell, groundY,
                    PlacementValidator.CanPlaceBuilding(gs, _def, originCell));
                break;
            }

            case BuildMode.Zone:
            {
                // 批次七十一：拖拽显示拖框，笔刷/油漆桶显示单格落点；删除模式用拆除色区分
                var color = ZoneErase ? DemolishColor : ValidColor;
                if (ZoneTool == ZoneTool.Rect)
                {
                    var a = _dragging ? _dragStart : _hover;
                    var wa = MapGrid.CellToWorld(a);
                    var wb = MapGrid.CellToWorld(_hover);
                    var center = (wa + wb) / 2f + Vector3.Up * (groundY + 0.1f);
                    var size = new Vector3(Mathf.Abs(wa.X - wb.X) + cs, 0.2f, Mathf.Abs(wa.Z - wb.Z) + cs);
                    SetPreviewBox(center, size, color);
                }
                else
                {
                    SetPreviewBox(MapGrid.CellToWorld(_hover) + Vector3.Up * (groundY + 0.1f),
                        new Vector3(cs, 0.2f, cs), color);
                }
                break;
            }

            case BuildMode.Tree:
            {
                ref var cell = ref gs.Map.CellAt(_hover);
                SetPreviewBox(MapGrid.CellToWorld(_hover) + Vector3.Up * (groundY + 1f), new Vector3(cs * 0.6f, 2f, cs * 0.6f),
                    cell.IsEmpty && !cell.HasTree ? ValidColor : InvalidColor);
                break;
            }

            case BuildMode.Demolish:
                SetPreviewBox(MapGrid.CellToWorld(_hover) + Vector3.Up * (groundY + 0.5f), new Vector3(cs, 1f, cs), DemolishColor);
                break;
        }
    }

    private void SetPreviewBox(Vector3 center, Vector3 size, Color color)
    {
        _preview.Position = center;
        _preview.Scale = size;
        _previewMat.AlbedoColor = color;
    }

    // ---- 阶段 D：建筑预览轮廓装配 ----

    private void MakePreviewMulti(ref MultiMeshInstance3D field, Mesh mesh)
    {
        field = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = mesh,
            },
        };
        _previewAssembly.AddChild(field);
    }

    private static void FillPreviewMulti(MultiMesh mm, List<Transform3D> xforms, List<Color> colors)
    {
        mm.InstanceCount = xforms.Count;
        for (int i = 0; i < xforms.Count; i++)
        {
            mm.SetInstanceTransform(i, xforms[i]);
            mm.SetInstanceColor(i, colors[i]);
        }
    }

    /// <summary>用真实宋代轮廓装配预览：同源 BuildingModelFactory.MakePreview 填 9 角色 MultiMesh，
    /// 装配挂到占地原点世界位置（MakePreview 返回局部坐标已含地面/层高）；另用薄方块作占地/合法性指示。</summary>
    private void ShowBuildingPreviewDef(BuildingDef def, Vector2I originCell, float groundY, bool valid)
    {
        _previewAssembly.Visible = true;
        _previewAssembly.Position = MapGrid.CellToWorld(originCell);

        var pv = BuildingModelFactory.MakePreview(def, groundY, 1);
        FillPreviewMulti(_paFound.Multimesh, pv.Found.X, pv.Found.C);
        FillPreviewMulti(_paBody.Multimesh, pv.Body.X, pv.Body.C);
        FillPreviewMulti(_paRoof.Multimesh, pv.Roof.X, pv.Roof.C);
        FillPreviewMulti(_paRoofEnd.Multimesh, pv.RoofEnd.X, pv.RoofEnd.C);
        FillPreviewMulti(_paEave.Multimesh, pv.Eave.X, pv.Eave.C);
        FillPreviewMulti(_paRidge.Multimesh, pv.Ridge.X, pv.Ridge.C);
        FillPreviewMulti(_paPillar.Multimesh, pv.Pillar.X, pv.Pillar.C);
        FillPreviewMulti(_paBanner.Multimesh, pv.Banner.X, pv.Banner.C);
        FillPreviewMulti(_paLantern.Multimesh, pv.Lantern.X, pv.Lantern.C);

        // 薄方块作占地/合法性指示（绿=可放 红=不可放），不抢轮廓
        const float cs = MapGrid.CellSize;
        var origin = MapGrid.CellToWorld(originCell);
        var center = origin + new Vector3((def.SizeX - 1) * cs / 2f, groundY + 0.05f, (def.SizeY - 1) * cs / 2f);
        SetPreviewBox(center, new Vector3(def.SizeX * cs, 0.1f, def.SizeY * cs), valid ? ValidColor : InvalidColor);
    }

    /// <summary>方形画笔预览中心：宽度偏移范围 -(w-1)/2..w/2 非对称，中心沿两轴各偏移半步。</summary>
    private Vector3 StampCenter(int w)
    {
        float offset = (-((w - 1) / 2) + w / 2) / 2f * MapGrid.CellSize;
        return MapGrid.CellToWorld(_hover) + new Vector3(offset, 0f, offset);
    }

    /// <summary>方形画笔预览尺寸：w×w 格。</summary>
    private static Vector3 StampSize(int w, float h)
    {
        const float cs = MapGrid.CellSize;
        return new Vector3(w * cs, h, w * cs);
    }

    /// <summary>当前建筑的放置原点（左上角格）：以悬停格为占地中心反推——
    /// 预览方块居中跟随鼠标（尤其 1×1 水井不再觉得偏），预览与落地同源不错位。</summary>
    private Vector2I BuildingOrigin()
        => _hover - new Vector2I((_def.SizeX - 1) / 2, (_def.SizeY - 1) / 2);

    // ---- 查看格子信息 ----

    /// <summary>无模式左键点选：优先拾取居民 → 野物 → 沿视线拾取建筑/树木/地面堆 → 退化为格子信息。</summary>
    private void InspectAt(Vector2I c)
    {
        var citizen = PickCitizen();
        if (citizen != null)
        {
            Hud?.ShowCitizen(citizen);
            return;
        }

        var animal = PickAnimal();
        if (animal != null)
        {
            Hud?.ShowAnimal(animal);
            return;
        }

        // 外城来人：优先级低于市民/野物、高于建筑（与 PickCitizen 同款屏幕投影就近法）
        var visitor = PickVisitor();
        if (visitor != null)
        {
            Hud?.ShowVisitor(visitor);
            return;
        }

        // 沿视线深度拾取：点中高大物件的「身体」也能选中（而非打到其身后地面）
        switch (PickWorldObject(out var groundCell))
        {
            case BuildingInstance b:
                Hud?.ShowBuilding(b);
                return;
            case PlantObj plant:
                Hud?.ShowTree(plant);
                return;
            case ItemPileObj pile:
                Hud?.ShowPile(pile);
                return;
        }

        Hud?.CloseInspect();
        ShowCellInfo(groundCell ?? c);
    }

    /// <summary>沿鼠标视线半格步长推进，按深度返回首个命中的世界物件：
    /// 建筑体（含屋顶余量）→ 树木（冠高内）→ 落地处的物资堆；无命中时 groundCell 为视线落地格
    /// （比 Y=0 平面求交更准，台地/缓丘上点选不再偏到身后格）。</summary>
    private object PickWorldObject(out Vector2I? groundCell)
    {
        groundCell = null;
        var cam = _rig.Cam;
        var mouse = GetViewport().GetMousePosition();
        var from = cam.ProjectRayOrigin(mouse);
        var dir = cam.ProjectRayNormal(mouse);
        if (dir.Y >= -0.0001f)
            return null; // 视线不朝下（贴地平视）：不做世界拾取

        var gs = GameState.I;
        const float step = MapGrid.CellSize / 2f; // 半格步长，不漏格不超距
        // 最高可拾面：地形最高海拔（随 TerrainConfig 联动）+ 峰上树冠/最高建筑余量；其上无可拾物件，直接快进
        const float maxObjTop = TerrainConfig.MaxTerrainHeight + 7.5f;
        float t = from.Y > maxObjTop ? (maxObjTop - from.Y) / dir.Y : 0f;

        for (int i = 0; i < 4096; i++, t += step)
        {
            var p = from + dir * t;
            if (p.Y < -3f)
                break; // 已穿透最深河床（约 -2.1m）以下，再无可拾
            var c = MapGrid.WorldToCell(p);
            if (!MapGrid.InBounds(c))
                continue;
            ref var cell = ref gs.Map.CellAt(c);
            float groundY = gs.Map.GroundY(c);

            // 建筑：视线点落在楼体+屋顶高度内即命中（点屋身/屋顶都算点中该栋）
            if (cell.BuildingId >= 0 && gs.Buildings.TryGetValue(cell.BuildingId, out var b))
            {
                float height = b.Def.Height * (1f + 0.35f * (b.Level - 1));
                float roof = Mathf.Clamp(height * 0.3f, 0.5f, 1.8f);
                if (p.Y <= groundY + height + roof)
                    return b;
            }

            // 树木：视线点在树高范围内即命中（树冠顶约 3.5m，取 4m 余量）
            if (cell.HasTree && p.Y <= groundY + 4f
                && gs.Plants.TryGetValue(GameState.CellIndex(c), out var plant))
                return plant;

            // 落到地表附近：命中地面物资堆，否则就此结束（交由格子信息展示）
            if (p.Y <= groundY + 0.9f)
            {
                groundCell = c;
                if (gs.Piles.TryGetValue(GameState.CellIndex(c), out var pile))
                    return pile;
                return null;
            }
        }
        return null;
    }

    /// <summary>把在场代理投影到屏幕，取鼠标 12px 内最近的一位（模型已缩小到 0.25，命中圈收紧：
    /// 只有光标几乎压在小人上才选中，否则落空交给建筑视线拾取，免点房子时误选周围的人）。</summary>
    private Citizen PickCitizen()
    {
        if (Agents == null)
            return null;

        var cam = _rig.Cam;
        var mouse = GetViewport().GetMousePosition();
        Citizen best = null;
        float bestDist = 12f;
        foreach (var agent in Agents.Agents)
        {
            // 瞄准缩放后的身躯中部（旧值 +1m 在小模型头顶老高处，投影偏离视觉位置致难点中）
            var world = agent.Position + Vector3.Up * (VillagerConfig.ModelScale * 1.1f);
            if (cam.IsPositionBehind(world))
                continue;
            float d = cam.UnprojectPosition(world).DistanceTo(mouse);
            if (d < bestDist)
            {
                // 点屋顶显示房屋信息：被建筑（楼体/屋顶）遮挡的居民不让位，视线拾取接管该点
                // （免点击穿透到屋内/屋后的人身上）
                if (RayBlockedByBuilding(world))
                    continue;
                bestDist = d;
                best = agent.C;
            }
        }
        return best;
    }

    /// <summary>外来访客拾取：与居民同款屏幕投影就近法（模型已缩小，命中圈收紧到 12px）；
    /// 被建筑遮挡的访客让位给视线拾取（免点房子误选屋后的人）。</summary>
    private ForeignVisitor PickVisitor()
    {
        if (Visitors == null)
            return null;

        var cam = _rig.Cam;
        var mouse = GetViewport().GetMousePosition();
        ForeignVisitor best = null;
        float bestDist = 12f;
        foreach (var v in Visitors.ActiveVisitors)
        {
            var world = v.Position + Vector3.Up * (VillagerConfig.ModelScale * 1.1f);
            if (cam.IsPositionBehind(world))
                continue;
            float d = cam.UnprojectPosition(world).DistanceTo(mouse);
            if (d < bestDist && !RayBlockedByBuilding(world))
            {
                bestDist = d;
                best = v;
            }
        }
        return best;
    }

    /// <summary>从相机到目标点的视线是否先被建筑（楼体/屋顶）遮挡：被遮挡时该点选应归建筑，
    /// 供 PickCitizen 跳过屋前/屋内/屋后不可见的居民（命中判定与 PickWorldObject 同款）。</summary>
    private bool RayBlockedByBuilding(Vector3 target)
    {
        var cam = _rig.Cam;
        var from = cam.ProjectRayOrigin(GetViewport().GetMousePosition());
        var dir = (target - from).Normalized();
        if (dir.Y >= -0.0001f)
            return false; // 视线不朝下（贴地平视）：无遮挡可言

        var gs = GameState.I;
        const float step = MapGrid.CellSize / 2f; // 与 PickWorldObject 同款半格步长
        float maxT = from.DistanceTo(target);
        for (float t = 0f; t <= maxT; t += step)
        {
            var p = from + dir * t;
            var c = MapGrid.WorldToCell(p);
            if (!MapGrid.InBounds(c))
                continue;
            ref var cell = ref gs.Map.CellAt(c);
            if (cell.BuildingId < 0 || !gs.Buildings.TryGetValue(cell.BuildingId, out var b))
                continue;
            float height = b.Def.Height * (1f + 0.35f * (b.Level - 1));
            float roof = Mathf.Clamp(height * 0.3f, 0.5f, 1.8f);
            if (p.Y <= gs.Map.GroundY(c) + height + roof)
                return true;
        }
        return false;
    }

    /// <summary>野物拾取：同居民的屏幕投影就近法，命中圈 14px（野物体小且带位置扰动，圈略宽于体型但不遮建筑）。</summary>
    private AnimalObj PickAnimal()
    {
        var gs = GameState.I;
        var cam = _rig.Cam;
        var mouse = GetViewport().GetMousePosition();
        AnimalObj best = null;
        float bestDist = 14f;
        foreach (var a in gs.Animals.Values)
        {
            var c = new Vector2I(a.X, a.Y);
            var world = MapGrid.CellToWorld(c) + Vector3.Up * (gs.Map.GroundY(c) + 0.35f);
            if (cam.IsPositionBehind(world))
                continue;
            float d = cam.UnprojectPosition(world).DistanceTo(mouse);
            if (d < bestDist)
            {
                bestDist = d;
                best = a;
            }
        }
        return best;
    }

    private void ShowCellInfo(Vector2I c)
    {
        var gs = GameState.I;
        ref var cell = ref gs.Map.CellAt(c);

        string what;
        if (cell.HasBridge)
            what = "桥梁";
        else if (cell.HasWater)
            what = "河流";
        else if (cell.HasRoad)
            what = "道路";
        else if (cell.BuildingId >= 0 && gs.Buildings.TryGetValue(cell.BuildingId, out var b))
            what = $"{b.Def.Name} {b.Level}级 完好{b.Condition:F0}%";
        else if (cell.HasTree)
            what = "树林";
        else if (cell.Zone != ZoneType.None)
            what = "可建设区（空地）";
        else
            what = "荒地";

        // 格上有地面物资堆：附带列出堆内货品明细
        string pileInfo = "";
        if (gs.Piles.TryGetValue(GameState.CellIndex(c), out var pile))
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var s in pile.Inv.Stacks)
                parts.Add($"{Goods.NameOf(s.GoodsId)} {s.Amount:F1}份");
            pileInfo = $"  【落地物资：{string.Join("、", parts)}】";
        }

        Hud?.ShowCellInfo($"({c.X},{c.Y})  {what}  吸引力 {cell.Desirability:F1}{pileInfo}");
    }
}
