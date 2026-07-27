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

    public BuildMode Mode { get; private set; } = BuildMode.None;

    private BuildingDef _def;
    private ZoneType _zone;
    private bool _dragging;
    private Vector2I _dragStart;
    private Vector2I _hover = new(-1, -1);
    private bool _hoverInMap;

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

    public void SetRoadMode() => SwitchMode(BuildMode.Road);

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

        switch (Mode)
        {
            case BuildMode.None:
                ShowCellInfo(_hover);
                break;
            case BuildMode.Road:
                _dragging = true;
                TryPlaceRoad(_hover);
                break;
            case BuildMode.Bridge:
                _dragging = true;
                TryPlaceBridge(_hover);
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
                TryPlaceRoad(_hover);
            else if (Mode == BuildMode.Bridge)
                TryPlaceBridge(_hover);
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

    private static void TryPlaceRoad(Vector2I c)
    {
        var gs = GameState.I;
        if (PlacementValidator.CanPlaceRoad(gs, c))
            gs.PlaceRoad(c);
    }

    private static void TryPlaceBridge(Vector2I c)
    {
        var gs = GameState.I;
        if (PlacementValidator.CanPlaceBridge(gs, c))
            gs.PlaceBridge(c);
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
                gs.Map.CellAt(c).Zone = _zone;
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
        _preview.Visible = true;

        switch (Mode)
        {
            case BuildMode.Road:
                SetPreviewBox(MapGrid.CellToWorld(_hover) + Vector3.Up * 0.15f, new Vector3(cs, 0.3f, cs),
                    PlacementValidator.CanPlaceRoad(gs, _hover) ? ValidColor : InvalidColor);
                break;

            case BuildMode.Bridge:
                SetPreviewBox(MapGrid.CellToWorld(_hover) + Vector3.Up * 0.25f, new Vector3(cs, 0.5f, cs),
                    PlacementValidator.CanPlaceBridge(gs, _hover) ? ValidColor : InvalidColor);
                break;

            case BuildMode.Building:
            {
                var origin = MapGrid.CellToWorld(_hover);
                var center = origin + new Vector3((_def.SizeX - 1) * cs / 2f, _def.Height / 2f, (_def.SizeY - 1) * cs / 2f);
                SetPreviewBox(center, new Vector3(_def.SizeX * cs, _def.Height, _def.SizeY * cs),
                    PlacementValidator.CanPlaceBuilding(gs, _def, _hover) ? ValidColor : InvalidColor);
                break;
            }

            case BuildMode.Zone:
            {
                var a = _dragging ? _dragStart : _hover;
                var wa = MapGrid.CellToWorld(a);
                var wb = MapGrid.CellToWorld(_hover);
                var center = (wa + wb) / 2f + Vector3.Up * 0.1f;
                var size = new Vector3(Mathf.Abs(wa.X - wb.X) + cs, 0.2f, Mathf.Abs(wa.Z - wb.Z) + cs);
                SetPreviewBox(center, size, ValidColor);
                break;
            }

            case BuildMode.Tree:
            {
                ref var cell = ref gs.Map.CellAt(_hover);
                SetPreviewBox(MapGrid.CellToWorld(_hover) + Vector3.Up * 1f, new Vector3(cs * 0.6f, 2f, cs * 0.6f),
                    cell.IsEmpty && !cell.HasTree ? ValidColor : InvalidColor);
                break;
            }

            case BuildMode.Demolish:
                SetPreviewBox(MapGrid.CellToWorld(_hover) + Vector3.Up * 0.5f, new Vector3(cs, 1f, cs), DemolishColor);
                break;
        }
    }

    private void SetPreviewBox(Vector3 center, Vector3 size, Color color)
    {
        _preview.Position = center;
        _preview.Scale = size;
        _previewMat.AlbedoColor = color;
    }

    // ---- 查看格子信息 ----

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
            what = cell.Zone switch
            {
                ZoneType.Residential => "住宅坊（空地）",
                ZoneType.Market => "市坊（空地）",
                _ => "工坊区（空地）",
            };
        else
            what = "荒地";

        Hud?.ShowCellInfo($"({c.X},{c.Y})  {what}  吸引力 {cell.Desirability:F1}");
    }
}
