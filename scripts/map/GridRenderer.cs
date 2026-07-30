using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 方块占位渲染层（分块增量重建，为大地图铺路）：
/// 地表（水/桥/道路）与树木按 64×64 格分块，每块独立 MultiMesh——铺路/砍树只重建所在块；
/// 建筑（半透明方块+边框+斜屋顶）与坊区色块数量有限，仍整层重建；
/// 全图事件（读档/月度生长）才全量重建。另含建造网格线。
/// </summary>
public partial class GridRenderer : Node3D
{
    private static readonly Color RoadColor = new(0.25f, 0.25f, 0.28f);
    private static readonly Color WaterColor = new(0.2f, 0.38f, 0.62f);
    private static readonly Color BridgeColor = new(0.55f, 0.42f, 0.26f);
    private static readonly Color TreeColor = new(0.2f, 0.45f, 0.2f);
    private static readonly Color FruitTreeColor = new(0.5f, 0.52f, 0.16f); // 果树：暖黄绿树冠，一眼可辨
    private static readonly Color EdgeColor = new(0.12f, 0.12f, 0.14f);
    private static readonly Color BuildableZoneColor = new(0.35f, 0.85f, 0.35f, 0.35f);

    // 地形土柱色：低处同草地基底色，高处渐变岩褐灰褐（随层数插值）
    private static readonly Color TerrainLowColor = new(0.45f, 0.5f, 0.32f); // 同 Main 地面草绿
    private static readonly Color TerrainHighColor = new(0.5f, 0.46f, 0.4f);  // 山顶岩褐

    /// <summary>门标记颜色：大门亮金（显眼），后门暗木色（低调）。</summary>
    private static readonly Color MainDoorColor = new(0.85f, 0.7f, 0.35f);
    private static readonly Color BackDoorColor = new(0.45f, 0.32f, 0.2f);

    /// <summary>建筑主体透明度（能看清屋内居民）。</summary>
    private const float BodyAlpha = 0.55f;

    /// <summary>分块边长（格）：128 图 2×2 块，1024 图 16×16 块，单块重建量恒定。</summary>
    private const int ChunkCells = 64;

    /// <summary>单个地表分块：地形方块 + 树木两套 MultiMesh。</summary>
    private class Chunk
    {
        public MultiMeshInstance3D Boxes;
        public MultiMeshInstance3D Trees;
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

    // 共享网格资源：所有分块复用同一份 Mesh，只是各自实例化
    private BoxMesh _boxMesh;
    private CylinderMesh _treeMesh;

    public override void _Ready()
    {
        _boxMesh = new BoxMesh { Size = Vector3.One };
        _boxMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };

        _treeMesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 1.1f, Height = 3f };
        _treeMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };

        // 地表/树木分块阵列
        _chunksPerSide = (MapGrid.Size + ChunkCells - 1) / ChunkCells;
        _chunks = new Chunk[_chunksPerSide * _chunksPerSide];
        for (int i = 0; i < _chunks.Length; i++)
        {
            var chunk = new Chunk
            {
                Boxes = MakeMulti(_boxMesh, useColors: true),
                Trees = MakeMulti(_treeMesh, useColors: true),
            };
            AddChild(chunk.Boxes);
            AddChild(chunk.Trees);
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

    /// <summary>单格变更（铺路/砍树/拆除/扩地）：只重建所在分块；格上坊区色可能被覆盖，坊区层一并刷新；
    /// 道路增减会改变住宅房体的临街贴边（檐隙），建筑层跟随重建。</summary>
    private void OnCellChanged(Vector2I c)
    {
        int cx = c.X / ChunkCells, cy = c.Y / ChunkCells;
        if (cx >= 0 && cx < _chunksPerSide && cy >= 0 && cy < _chunksPerSide)
            _chunks[cy * _chunksPerSide + cx].Dirty = true;
        _zonesDirty = true; // 坊区色块层便宜，跟随刷新
        _buildingsDirty = true; // 房体檐隙随临路变化，建筑数量级小整层重建不贵
    }

    public void SetGridVisible(bool visible) => _gridLines.Visible = visible;

    public override void _Process(double delta)
    {
        for (int i = 0; i < _chunks.Length; i++)
        {
            if (!_chunks[i].Dirty)
                continue;
            _chunks[i].Dirty = false;
            RebuildChunk(i);
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

    /// <summary>地形土柱色：层数越高越偏岩褐灰褐。</summary>
    private static Color TerrainColor(int height)
    {
        float t = Mathf.Clamp(height / (float)TerrainConfig.MaxMountainLayer, 0f, 1f);
        return TerrainLowColor.Lerp(TerrainHighColor, t);
    }

    /// <summary>重建单个分块：块内地形土柱 + 水/桥/路贴面 + 树木。</summary>
    private void RebuildChunk(int index)
    {
        var gs = GameState.I;
        int cx = index % _chunksPerSide, cy = index / _chunksPerSide;
        int x0 = cx * ChunkCells, y0 = cy * ChunkCells;
        int x1 = Mathf.Min(x0 + ChunkCells, MapGrid.Size);
        int y1 = Mathf.Min(y0 + ChunkCells, MapGrid.Size);

        var boxXf = new List<Transform3D>();
        var boxColor = new List<Color>();
        var treeXf = new List<Transform3D>();
        var treeColor = new List<Color>();

        const float cs = MapGrid.CellSize;
        for (int x = x0; x < x1; x++)
        {
            for (int y = y0; y < y1; y++)
            {
                ref var cell = ref gs.Map.CellAt(x, y);
                var world = MapGrid.CellToWorld(new Vector2I(x, y));
                float groundY = TerrainConfig.LayerToWorldY(cell.Height); // 本格地面海拔

                // 地形土柱：非水且高于基准的格填一根土柱，顶面到达 groundY（平地格靠整块底面兑底，不出实例）
                if (!cell.HasWater && cell.Height > 0)
                {
                    float pillarH = groundY + 0.5f; // 多埋 0.5m 避免与基底平面露缝
                    boxXf.Add(new Transform3D(Basis.FromScale(new Vector3(cs, pillarH, cs)), world + Vector3.Up * (groundY - pillarH / 2f)));
                    boxColor.Add(TerrainColor(cell.Height));
                }

                if (cell.HasBridge)
                {
                    // 桥面：高出水面的木板（桥跨水，水面一律在 0 基准）
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
                    // 三类道路按种类区分明度与厚度：主路最亮最厚，小路最暗最薄（贴在本格地面上）
                    float h;
                    Color rc;
                    switch (cell.RoadKind)
                    {
                        case RoadKind.Main:
                            h = 0.24f; rc = RoadColor.Lightened(0.25f); break;
                        case RoadKind.Lane:
                            h = 0.14f; rc = RoadColor.Darkened(0.2f); break;
                        default: // Side / 桥面(None)
                            h = 0.2f; rc = RoadColor; break;
                    }
                    boxXf.Add(new Transform3D(Basis.FromScale(new Vector3(cs, h, cs)), world + Vector3.Up * (groundY + h / 2f)));
                    boxColor.Add(rc);
                }

                // 树木：植物实体驱动，尺寸随生长进度放大（块内格→实体查询，重建量与块大小成正比）
                if (cell.HasTree && gs.Plants.TryGetValue(GameState.CellIndex(new Vector2I(x, y)), out var p))
                {
                    // 位置/大小用格坐标伪随机扰动，避免排队感；站在本格地面上
                    float jx = ((x * 73 + y * 31) % 7 - 3) * 0.15f;
                    float jz = ((x * 41 + y * 57) % 7 - 3) * 0.15f;
                    float s = (0.8f + ((x * 13 + y * 17) % 5) * 0.1f) * (0.35f + 0.65f * p.GrowthRatio);
                    treeXf.Add(new Transform3D(Basis.FromScale(new Vector3(s, s, s)), world + new Vector3(jx, groundY + 1.5f * s, jz)));
                    treeColor.Add(p.IsFruitTree ? FruitTreeColor : TreeColor);
                }
            }
        }

        var chunk = _chunks[index];
        FillMultiMesh(chunk.Boxes.Multimesh, boxXf, boxColor);

        chunk.Trees.Multimesh.InstanceCount = treeXf.Count;
        for (int i = 0; i < treeXf.Count; i++)
        {
            chunk.Trees.Multimesh.SetInstanceTransform(i, treeXf[i]);
            chunk.Trees.Multimesh.SetInstanceColor(i, treeColor[i]);
        }
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
            float gy = TerrainConfig.LayerToWorldY(cell.Height); // 贴本格地面
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

            // 斜屋顶：脊线沿长边，稍出檐（跟随房体尺寸与中心）
            float roofH = Mathf.Clamp(height * 0.3f, 0.5f, 1.8f);
            var roofBasis = w >= d
                ? Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f)) * Basis.FromScale(new Vector3(d * 1.06f, roofH, w * 1.06f))
                : Basis.FromScale(new Vector3(w * 1.06f, roofH, d * 1.06f));
            var roofCenter = new Vector3(center.X, groundY + height + roofH / 2f, center.Z);
            roofXf.Add(new Transform3D(roofBasis, roofCenter));
            roofColor.Add(color.Darkened(0.45f)); // 灰瓦感

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
