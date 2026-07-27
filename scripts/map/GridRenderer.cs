using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>方块占位渲染层：道路/建筑用 MultiMesh 实例化方块，树木用锥体，坊区用半透明色块，另含建造网格线。</summary>
public partial class GridRenderer : Node3D
{
    private static readonly Color RoadColor = new(0.25f, 0.25f, 0.28f);
    private static readonly Color WaterColor = new(0.2f, 0.38f, 0.62f);
    private static readonly Color BridgeColor = new(0.55f, 0.42f, 0.26f);
    private static readonly Color TreeColor = new(0.2f, 0.45f, 0.2f);
    private static readonly Color ResidentialZoneColor = new(0.35f, 0.85f, 0.35f, 0.35f);
    private static readonly Color MarketZoneColor = new(0.35f, 0.55f, 0.95f, 0.35f);
    private static readonly Color WorkshopZoneColor = new(0.9f, 0.7f, 0.25f, 0.35f);

    private MultiMeshInstance3D _boxes;
    private MultiMeshInstance3D _trees;
    private MultiMeshInstance3D _zones;
    private MeshInstance3D _gridLines;
    private bool _dirty = true;

    public override void _Ready()
    {
        var opaqueMesh = new BoxMesh { Size = Vector3.One };
        opaqueMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _boxes = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = opaqueMesh,
            },
        };
        AddChild(_boxes);

        // 树木：绿色锥体占位
        var treeMesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 1.1f, Height = 3f };
        treeMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _trees = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = treeMesh,
            },
        };
        AddChild(_trees);

        var zoneMesh = new BoxMesh { Size = Vector3.One };
        zoneMesh.Material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _zones = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = zoneMesh,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_zones);

        BuildGridLines();

        EventBus.MapChanged += MarkDirty;
        EventBus.ZonesChanged += MarkDirty;
    }

    public override void _ExitTree()
    {
        EventBus.MapChanged -= MarkDirty;
        EventBus.ZonesChanged -= MarkDirty;
    }

    private void MarkDirty() => _dirty = true;

    public void SetGridVisible(bool visible) => _gridLines.Visible = visible;

    public override void _Process(double delta)
    {
        if (!_dirty)
            return;
        _dirty = false;
        Rebuild();
    }

    private void Rebuild()
    {
        var gs = GameState.I;
        var boxXf = new List<Transform3D>();
        var boxColor = new List<Color>();
        var treeXf = new List<Transform3D>();
        var zoneXf = new List<Transform3D>();
        var zoneColor = new List<Color>();

        const float cs = MapGrid.CellSize;
        for (int x = 0; x < MapGrid.Size; x++)
        {
            for (int y = 0; y < MapGrid.Size; y++)
            {
                ref var cell = ref gs.Map.CellAt(x, y);
                var world = MapGrid.CellToWorld(new Vector2I(x, y));
                if (cell.HasBridge)
                {
                    // 桥面：高出水面的木板
                    boxXf.Add(new Transform3D(Basis.FromScale(new Vector3(cs, 0.35f, cs)), world + Vector3.Up * 0.25f));
                    boxColor.Add(BridgeColor);
                }
                else if (cell.HasWater)
                {
                    boxXf.Add(new Transform3D(Basis.FromScale(new Vector3(cs, 0.1f, cs)), world + Vector3.Up * 0.03f));
                    boxColor.Add(WaterColor);
                }
                else if (cell.HasRoad)
                {
                    boxXf.Add(new Transform3D(Basis.FromScale(new Vector3(cs, 0.2f, cs)), world + Vector3.Up * 0.1f));
                    boxColor.Add(RoadColor);
                }
                else if (cell.Zone != ZoneType.None && cell.BuildingId < 0)
                {
                    zoneXf.Add(new Transform3D(Basis.FromScale(new Vector3(cs * 0.96f, 0.08f, cs * 0.96f)), world + Vector3.Up * 0.05f));
                    zoneColor.Add(cell.Zone switch
                    {
                        ZoneType.Residential => ResidentialZoneColor,
                        ZoneType.Market => MarketZoneColor,
                        _ => WorkshopZoneColor,
                    });
                }
            }
        }

        // 树木：植物实体驱动，尺寸随生长进度放大
        foreach (var p in gs.Plants.Values)
        {
            int x = p.X, y = p.Y;
            var world = MapGrid.CellToWorld(new Vector2I(x, y));
            // 位置/大小用格坐标伪随机扰动，避免排队感
            float jx = ((x * 73 + y * 31) % 7 - 3) * 0.15f;
            float jz = ((x * 41 + y * 57) % 7 - 3) * 0.15f;
            float s = (0.8f + ((x * 13 + y * 17) % 5) * 0.1f) * (0.35f + 0.65f * p.GrowthRatio);
            treeXf.Add(new Transform3D(Basis.FromScale(new Vector3(s, s, s)), world + new Vector3(jx, 1.5f * s, jz)));
        }

        foreach (var b in gs.Buildings.Values)
        {
            var origin = MapGrid.CellToWorld(b.Origin);
            // 等级越高楼越高；年久失修则发暗
            float height = b.Def.Height * (1f + 0.35f * (b.Level - 1));
            var center = origin + new Vector3((b.Def.SizeX - 1) * cs / 2f, height / 2f, (b.Def.SizeY - 1) * cs / 2f);
            var scale = new Vector3(b.Def.SizeX * cs * 0.92f, height, b.Def.SizeY * cs * 0.92f);
            var color = b.Def.GodotColor;
            if (b.Condition < 50f)
                color = color.Darkened(0.35f * (1f - b.Condition / 50f));
            boxXf.Add(new Transform3D(Basis.FromScale(scale), center));
            boxColor.Add(color);
        }

        FillMultiMesh(_boxes.Multimesh, boxXf, boxColor);
        FillMultiMesh(_zones.Multimesh, zoneXf, zoneColor);

        _trees.Multimesh.InstanceCount = treeXf.Count;
        for (int i = 0; i < treeXf.Count; i++)
        {
            _trees.Multimesh.SetInstanceTransform(i, treeXf[i]);
            _trees.Multimesh.SetInstanceColor(i, TreeColor);
        }
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
