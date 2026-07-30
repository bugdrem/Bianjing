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

    public BuildMode Mode { get; private set; } = BuildMode.None;

    private BuildingDef _def;
    private ZoneType _zone;
    private bool _dragging;
    private Vector2I _dragStart;
    private Vector2I _hover = new(-1, -1);
    private bool _hoverInMap;

    // 道路/桥方形画笔拖动：上一盖戳中心格（沿线插值防跳格）
    private Vector2I? _lastRoadCell;

    private MeshInstance3D _preview;
    private StandardMaterial3D _previewMat;

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
        GetParent().CallDeferred(Node.MethodName.AddChild, _preview);
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

    public void SetDemolishMode() => SwitchMode(BuildMode.Demolish);

    public void SetTreeMode() => SwitchMode(BuildMode.Tree);

    private void SwitchMode(BuildMode mode)
    {
        Mode = mode;
        _dragging = false;
        _lastRoadCell = null;
        _renderer.SetGridVisible(mode != BuildMode.None);
        _preview.Visible = false;
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
                DragRoadTo(_hover, isBridge: false);
                break;
            case BuildMode.Bridge:
                _dragging = true;
                _lastRoadCell = null;
                DragRoadTo(_hover, isBridge: true);
                break;
            case BuildMode.Building:
                TryPlaceBuilding(_hover);
                break;
            case BuildMode.Zone:
                _dragging = true;
                _dragStart = _hover;
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
        if (Mode == BuildMode.Zone && _dragging && _hoverInMap)
            ApplyZoneRect(_dragStart, _hover);
        _dragging = false;
    }

    public override void _Process(double delta)
    {
        UpdateHoverCell();

        if (_dragging && _hoverInMap)
        {
            if (Mode == BuildMode.Road)
                DragRoadTo(_hover, isBridge: false);
            else if (Mode == BuildMode.Bridge)
                DragRoadTo(_hover, isBridge: true);
            else if (Mode == BuildMode.Tree)
                GameState.I.PlaceTree(_hover);
            else if (Mode == BuildMode.Demolish)
                GameState.I.DemolishAt(_hover);
        }

        UpdatePreview();
    }

    private void UpdateHoverCell()
    {
        var vp = GetViewport();
        var mouse = vp.GetMousePosition();
        var cam = _rig.Cam;
        var from = cam.ProjectRayOrigin(mouse);
        var dir = cam.ProjectRayNormal(mouse);

        _hoverInMap = false;
        if (Mathf.Abs(dir.Y) < 0.0001f)
            return;
        float t = -from.Y / dir.Y;
        if (t <= 0)
            return;

        var hit = from + dir * t;
        var cell = MapGrid.WorldToCell(hit);
        if (!MapGrid.InBounds(cell))
            return;
        _hover = cell;
        _hoverInMap = true;
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

    private void TryPlaceBuilding(Vector2I origin)
    {
        var gs = GameState.I;
        if (PlacementValidator.CanPlaceBuilding(gs, _def, origin))
            gs.PlaceBuilding(_def, origin);
    }

    private void ApplyZoneRect(Vector2I a, Vector2I b)
    {
        var gs = GameState.I;
        int x0 = Mathf.Min(a.X, b.X), x1 = Mathf.Max(a.X, b.X);
        int y0 = Mathf.Min(a.Y, b.Y), y1 = Mathf.Max(a.Y, b.Y);
        bool changed = false;
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                var c = new Vector2I(x, y);
                if (!PlacementValidator.CanZone(gs, c))
                    continue;
                gs.SetZone(c, _zone); // 经索引写入，坊区候选集同步维护
                changed = true;
            }
        }
        if (changed)
            EventBus.RaiseZonesChanged();
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

        switch (Mode)
        {
            case BuildMode.Road:
            {
                // 方形画笔预览：w×w整块（宽 4 时偏移 -1..2，中心偏移半格）
                int w = GameState.RoadWidthOf(_roadKind);
                SetPreviewBox(StampCenter(w) + Vector3.Up * (groundY + 0.15f), StampSize(w, 0.3f),
                    PlacementValidator.CanPlaceRoad(gs, _hover, _roadKind) ? ValidColor : InvalidColor);
                break;
            }

            case BuildMode.Bridge:
                SetPreviewBox(StampCenter(GameState.BridgeWidth) + Vector3.Up * 0.25f, StampSize(GameState.BridgeWidth, 0.5f),
                    PlacementValidator.CanPlaceBridge(gs, _hover) ? ValidColor : InvalidColor);
                break;

            case BuildMode.Building:
            {
                var origin = MapGrid.CellToWorld(_hover);
                var center = origin + new Vector3((_def.SizeX - 1) * cs / 2f, groundY + _def.Height / 2f, (_def.SizeY - 1) * cs / 2f);
                SetPreviewBox(center, new Vector3(_def.SizeX * cs, _def.Height, _def.SizeY * cs),
                    PlacementValidator.CanPlaceBuilding(gs, _def, _hover) ? ValidColor : InvalidColor);
                break;
            }

            case BuildMode.Zone:
            {
                var a = _dragging ? _dragStart : _hover;
                var wa = MapGrid.CellToWorld(a);
                var wb = MapGrid.CellToWorld(_hover);
                var center = (wa + wb) / 2f + Vector3.Up * (groundY + 0.1f);
                var size = new Vector3(Mathf.Abs(wa.X - wb.X) + cs, 0.2f, Mathf.Abs(wa.Z - wb.Z) + cs);
                SetPreviewBox(center, size, ValidColor);
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
        const float maxObjTop = 22f; // 石峰 15m + 峰上树 + 余量：此高度以上无可拾物件，直接快进
        float t = from.Y > maxObjTop ? (maxObjTop - from.Y) / dir.Y : 0f;

        for (int i = 0; i < 4096; i++, t += step)
        {
            var p = from + dir * t;
            if (p.Y < -1f)
                break; // 已穿透水面基准以下，再无可拾
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
                bestDist = d;
                best = agent.C;
            }
        }
        return best;
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
