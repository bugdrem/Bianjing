using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 建筑层：与地形彻底分家——建筑按「角色」分十三个 MultiMesh 批渲
/// （地基/主体/屋顶/端坡/檐口/屋脊/立柱/招幌/灯笼/门/台基/门窗/院墙），
/// 另加一个 glb 资产挂载点（阶段 C：有 ModelPath 的建筑整栋走资产，缺失则回退原始体）。
/// 建筑数量级远小于格数，整层重建足够便宜，不按分块切。
/// </summary>
public partial class BuildingLayer : MapLayer
{
    private MultiMeshInstance3D _founds;   // 地基：房体下不透明基座
    private MultiMeshInstance3D _bodies;   // 主体：半透 Box，可透视屋内
    private MultiMeshInstance3D _roofs;    // 主坡屋顶：三棱柱
    private MultiMeshInstance3D _roofEnds; // 庑殿端坡：垂直三棱柱（威仪建筑）
    private MultiMeshInstance3D _eaves;    // 檐口：薄 Box 环
    private MultiMeshInstance3D _ridges;   // 屋脊：细 Box
    private MultiMeshInstance3D _pillars;  // 立柱：圆柱
    private MultiMeshInstance3D _banners;  // 招幌：薄竖 Box（商铺）
    private MultiMeshInstance3D _lanterns; // 灯笼：小球
    private MultiMeshInstance3D _doors;    // 门：小方块（大门金/后门暗木）
    private MultiMeshInstance3D _steps;    // 台基石阶：房体下方基座
    private MultiMeshInstance3D _windows;  // 门窗：大门亮金 + 窗半透灰玻
    private MultiMeshInstance3D _walls;    // 院墙：民居大院夯土围墙

    /// <summary>外部 glb 资产建筑挂载点（与原始体建筑层并列；内部按建筑 Id 缓存实例）。</summary>
    private Node3D _assetRoot;
    private readonly Dictionary<int, Node3D> _assetInstances = new();

    public override void Build()
    {
        // 顶点色受光材质共用一份；主体与门窗额外半透（见下）
        var vcMat = new StandardMaterial3D { VertexColorUseAsAlbedo = true };

        _founds = Add(Box(vcMat), "BldgFounds");

        // 主体：半透明方块，可透视屋内居民。AlphaHash + alpha-to-coverage 自动深度修正，
        // 免 Alpha 排序穿模；alpha=1 的威严建筑（王府/官署/宫殿）按 opaque 走，无视觉副作用。
        var bodyMesh = new BoxMesh { Size = Vector3.One };
        bodyMesh.Material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaHash,
            AlphaHashScale = 1.0f,
        };
        _bodies = Add(bodyMesh, "BldgBodies", castShadow: false);

        _roofs = Add(new PrismMesh { Size = Vector3.One, Material = vcMat }, "BldgRoofs");
        _roofEnds = Add(new PrismMesh { Size = Vector3.One, Material = vcMat }, "BldgRoofEnds");
        _eaves = Add(Box(vcMat), "BldgEaves");
        _ridges = Add(Box(vcMat), "BldgRidges");
        _pillars = Add(new CylinderMesh { TopRadius = 0.13f, BottomRadius = 0.13f, Height = 1f, Material = vcMat }, "BldgPillars");
        _banners = Add(Box(vcMat), "BldgBanners");
        _lanterns = Add(new SphereMesh { Radius = 0.5f, Height = 1f, Material = vcMat }, "BldgLanterns");
        _doors = Add(Box(vcMat), "BldgDoors", castShadow: false);
        _steps = Add(Box(vcMat), "BldgSteps");

        // 门窗：窗走 AlphaHash，与主体同处理免穿模
        var winMesh = new BoxMesh { Size = Vector3.One };
        winMesh.Material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaHash,
            AlphaHashScale = 1.0f,
        };
        _windows = Add(winMesh, "BldgWindows", castShadow: false);
        _walls = Add(Box(vcMat), "BldgWalls");

        _assetRoot = new Node3D { Name = "AssetBuildings" };
        AddChild(_assetRoot);
    }

    private static BoxMesh Box(Material mat) => new() { Size = Vector3.One, Material = mat };

    /// <summary>建一层 MultiMesh 实例节点（共享 mesh，逐实例变换 + 颜色）。</summary>
    private MultiMeshInstance3D Add(Mesh mesh, string name, bool castShadow = true)
    {
        var mm = LayerKit.MakeMulti(mesh, useColors: true);
        mm.Name = name;
        if (!castShadow)
            mm.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(mm);
        return mm;
    }

    /// <summary>整层重建（地基/主体/屋顶/边框/门）：建筑数量级远小于格数，整层重建足够便宜。</summary>
    public void Rebuild()
    {
        var gs = GameState.I;
        var ctx = new BuildingModelFactory.BuildRoleLists();
        var doorXf = new List<Transform3D>();
        var doorColor = new List<Color>();
        const float cs = MapGrid.CellSize;

        var modelIds = new HashSet<int>();

        foreach (var b in gs.Buildings.Values)
        {
            // 阶段 C：有 ModelPath 的建筑优先走 glb 资产路径；资源不可用则降级回原始体造型
            if (b.Def.HasModel)
            {
                var scene = BuildingAssetLoader.LoadScene(b.Def.ModelPath);
                if (scene != null)
                {
                    EnsureAssetInstance(b, scene, gs, cs, modelIds);
                    continue;
                }
            }

            // 各角色（地基/房体/屋顶/端坡/檐口/屋脊/立柱/招幌/灯笼）由工厂产出变换+颜色
            BuildingModelFactory.AppendAssembly(gs, b, ctx);

            // 门标记（沿占地边界贴墙，大门金/后门暗木；需 gs.EnsureDoors）
            gs.EnsureDoors(b);
            if (b.Doors == null)
                continue;
            var ccenter = MapGrid.CellToWorld(b.Origin)
                + new Vector3((b.FootX - 1) * cs / 2f, 0f, (b.FootY - 1) * cs / 2f);
            foreach (var door in b.Doors)
            {
                var dir = new Vector2I(door.Outside.X - door.Inside.X, door.Outside.Y - door.Inside.Y);
                var dirW = new Vector3(dir.X, 0f, dir.Y);
                const float doorH = 0.55f;
                float wide = (door.IsMain ? 0.5f : 0.28f) * cs;
                const float thick = 0.12f;
                var scale = dir.X != 0 ? new Vector3(thick, doorH, wide) : new Vector3(wide, doorH, thick);
                var pos = MapGrid.CellToWorld(door.Inside) + dirW * (cs * 0.5f)
                    + Vector3.Up * (gs.Map.GroundY(door.Inside) + WorldConfig.BuildingBaseLift + doorH / 2f);
                // 大门沿墙面居中到占地几何中心（后门保持偏侧错落）
                if (door.IsMain)
                {
                    if (dir.X != 0) pos.Z = ccenter.Z;
                    else pos.X = ccenter.X;
                }
                doorXf.Add(new Transform3D(Basis.FromScale(scale), pos));
                doorColor.Add(door.IsMain ? BuildingModelFactory.MainDoorColor : BuildingModelFactory.BackDoorColor);
            }
        }

        LayerKit.FillMultiMesh(_founds.Multimesh, ctx.Found.X, ctx.Found.C);
        LayerKit.FillMultiMesh(_bodies.Multimesh, ctx.Body.X, ctx.Body.C);
        LayerKit.FillMultiMesh(_roofs.Multimesh, ctx.Roof.X, ctx.Roof.C);
        LayerKit.FillMultiMesh(_roofEnds.Multimesh, ctx.RoofEnd.X, ctx.RoofEnd.C);
        LayerKit.FillMultiMesh(_eaves.Multimesh, ctx.Eave.X, ctx.Eave.C);
        LayerKit.FillMultiMesh(_ridges.Multimesh, ctx.Ridge.X, ctx.Ridge.C);
        LayerKit.FillMultiMesh(_pillars.Multimesh, ctx.Pillar.X, ctx.Pillar.C);
        LayerKit.FillMultiMesh(_banners.Multimesh, ctx.Banner.X, ctx.Banner.C);
        LayerKit.FillMultiMesh(_lanterns.Multimesh, ctx.Lantern.X, ctx.Lantern.C);
        LayerKit.FillMultiMesh(_steps.Multimesh, ctx.Step.X, ctx.Step.C);
        LayerKit.FillMultiMesh(_windows.Multimesh, ctx.Window.X, ctx.Window.C);
        LayerKit.FillMultiMesh(_walls.Multimesh, ctx.Wall.X, ctx.Wall.C);
        LayerKit.FillMultiMesh(_doors.Multimesh, doorXf, doorColor);

        // 释放已拆除或不再走资产路径的实例
        CleanupStaleAssetInstances(modelIds);
    }

    /// <summary>阶段 C：为走资产路径的建筑实例化/复用 glb 节点，贴合占地与层高；记录其 Id 供清理。</summary>
    private void EnsureAssetInstance(BuildingInstance b, PackedScene scene, GameState gs, float cs, HashSet<int> modelIds)
    {
        modelIds.Add(b.Id);
        if (!_assetInstances.TryGetValue(b.Id, out var inst) || inst == null || inst.GetParent() != _assetRoot)
        {
            inst = scene.Instantiate<Node3D>();
            _assetRoot.AddChild(inst);
            _assetInstances[b.Id] = inst;
        }
        float groundY = gs.Map.GroundY(b.Origin);
        float baseY = groundY + WorldConfig.BuildingBaseLift;
        float w = b.FootX * cs * 0.9f;
        float d = b.FootY * cs * 0.9f;
        float height = b.Def.Height * (1f + 0.35f * (b.Level - 1));
        var center = MapGrid.CellToWorld(b.Origin)
            + new Vector3((b.FootX - 1) * cs / 2f, 0f, (b.FootY - 1) * cs / 2f);
        BuildingAssetLoader.FitAndPlace(inst, w, d, height, baseY, center.X, center.Z);
    }

    /// <summary>释放已拆除或不再走资产路径（如资产缺失转回原始体）的建筑实例。</summary>
    private void CleanupStaleAssetInstances(HashSet<int> modelIds)
    {
        var stale = new List<int>();
        foreach (var kv in _assetInstances)
            if (!modelIds.Contains(kv.Key))
                stale.Add(kv.Key);
        foreach (var id in stale)
        {
            _assetInstances[id]?.QueueFree();
            _assetInstances.Remove(id);
        }
    }
}
