using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 叠加层：坊区色块（建筑区浅蓝 / 耕种区浅黄绿）+ 建造网格线。
/// 两者都是「贴在地表之上的辅助显示」，与地形/建筑实体无关，平时默认隐藏，
/// 独立成层后显隐切换不再牵动任何实体层的重建。
/// </summary>
public partial class OverlayLayer : MapLayer
{
    /// <summary>建筑区色块：浅蓝底色（批次七十：原绿色改浅蓝，与耕种区浅黄绿区分；仅分区模式显示）。</summary>
    private static readonly Color BuildableZoneColor = new(0.45f, 0.68f, 0.95f, 0.35f);
    /// <summary>耕种区色块：浅黄绿底色（批次七十：修复耕种区不渲染，与建筑区浅蓝区分）。</summary>
    private static readonly Color FarmlandZoneColor = new(0.82f, 0.90f, 0.45f, 0.35f);

    private MultiMeshInstance3D _zones;
    private MeshInstance3D _gridLines;

    public override void Build()
    {
        // 坊区色块（半透明，无光照）
        var zoneMesh = new BoxMesh { Size = Vector3.One };
        zoneMesh.Material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _zones = LayerKit.MakeMulti(zoneMesh, useColors: true);
        _zones.Name = "Zones";
        _zones.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _zones.Visible = false; // 批次七十：规划色块默认隐藏，仅进入分区模式时显示
        AddChild(_zones);

        BuildGridLines();
    }

    /// <summary>重建坊区色块层：建筑区（浅蓝）与耕种区（浅黄绿）两个候选集分开上色，
    /// 只遍历增量索引（非全图扫描）；批次七十：补上耕种区渲染（旧版只画可建设区）。</summary>
    public void RebuildZones()
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
        foreach (var c in gs.FarmlandCells)
        {
            ref var cell = ref gs.Map.CellAt(c);
            if (cell.BuildingId >= 0)
                continue;
            var world = MapGrid.CellToWorld(c);
            float gy = gs.Map.GroundY(c);
            zoneXf.Add(new Transform3D(Basis.FromScale(new Vector3(cs * 0.96f, 0.08f, cs * 0.96f)), world + Vector3.Up * (gy + 0.05f)));
            zoneColor.Add(FarmlandZoneColor);
        }
        LayerKit.FillMultiMesh(_zones.Multimesh, zoneXf, zoneColor);
    }

    /// <summary>建造网格线显隐（仅建造模式打开）。</summary>
    public void SetGridVisible(bool visible) => _gridLines.Visible = visible;

    /// <summary>坊区规划色块显隐（批次七十）：仅分区模式下显示，平时不画规划底图。</summary>
    public void SetZonesVisible(bool visible) => _zones.Visible = visible;

    /// <summary>建造网格线：整幅一张 ImmediateMesh 线段网（默认隐藏，进入建造模式才显示）。</summary>
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

        _gridLines = new MeshInstance3D { Name = "GridLines", Mesh = im, Visible = false };
        AddChild(_gridLines);
    }
}
