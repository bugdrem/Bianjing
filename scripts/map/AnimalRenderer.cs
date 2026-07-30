using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>动物占位渲染：低多边「方块猪」MultiMesh（参考猪的体态——胖圆身躯 + 短四腿 + 拱嘴 + 小耳 + 卷尾），
/// 与村民/建筑的方块占位美术统一。合成为单个双表面网格（主体粉褐 + 猪鼻/耳/蹄的深色），MultiMesh 逐只实例化；
/// 数据层格位变化时不瞬移，而是记录每只动物的平滑位置逐帧插值走过去；增减个体时重建实例数。</summary>
public partial class AnimalRenderer : Node3D
{
    /// <summary>猪主体色（粉褐）与鼻/耳/蹄的深色。</summary>
    private static readonly Color PigColor = new(0.78f, 0.58f, 0.56f);
    private static readonly Color PigAccent = new(0.62f, 0.42f, 0.42f);

    /// <summary>插值速度（每秒趋近目标的比例系数）。</summary>
    private const float LerpSpeed = 2.5f;

    private MultiMeshInstance3D _mm;
    private bool _dirty = true;

    /// <summary>动物 Id → 当前平滑世界坐标（个体消失后清理）。</summary>
    private readonly Dictionary<int, Vector3> _smoothPos = new();

    public override void _Ready()
    {
        _mm = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = false, // 颜色烘进网格两表面材质，全体同色不逐实例上色
                Mesh = BuildPigMesh(),
            },
        };
        AddChild(_mm);

        EventBus.WildlifeChanged += MarkDirty;
        EventBus.GameLoaded += OnGameLoaded;
    }

    public override void _ExitTree()
    {
        EventBus.WildlifeChanged -= MarkDirty;
        EventBus.GameLoaded -= OnGameLoaded;
    }

    private void MarkDirty() => _dirty = true;

    private void OnGameLoaded()
    {
        // 读档/新开局：平滑位置作废，直接落到新格位
        _smoothPos.Clear();
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        var gs = GameState.I;
        var animals = gs.Animals;
        var mm = _mm.Multimesh;

        if (_dirty)
        {
            _dirty = false;
            mm.InstanceCount = animals.Count;

            // 清理已消失个体的平滑位置
            if (_smoothPos.Count > animals.Count)
            {
                var gone = new List<int>();
                foreach (int id in _smoothPos.Keys)
                    if (!animals.ContainsKey(id))
                        gone.Add(id);
                foreach (int id in gone)
                    _smoothPos.Remove(id);
            }
        }

        if (mm.InstanceCount == 0)
            return;

        float t = Mathf.Min(1f, (float)delta * LerpSpeed);
        int i = 0;
        foreach (var a in animals.Values)
        {
            // 目标位置按 Id 伪随机扰动，避免整齐划一；叠加地形海拔，四腿落在本格地面上
            float jx = (a.Id * 37 % 11 - 5) * 0.12f;
            float jz = (a.Id * 53 % 11 - 5) * 0.12f;
            var ac = new Vector2I(a.X, a.Y);
            var target = MapGrid.CellToWorld(ac) + new Vector3(jx, gs.Map.GroundY(ac) + 0.02f, jz);

            // 新个体直接落位，老个体缓步挪向新格
            var pos = _smoothPos.TryGetValue(a.Id, out var cur) ? cur.Lerp(target, t) : target;
            _smoothPos[a.Id] = pos;

            // 朝向按 Id 稳定散布；体型按 Id 微差异（参考村民体积：村民 ≈ 0.25×1.7m≈0.43m，
            // 方块猪本体高 ≈ 0.95m，故整体再乘 ~0.5 降到与村民相当的体量）
            float yaw = a.Id * 2.399f % Mathf.Tau;
            float scl = 0.48f + (a.Id * 13 % 5) * 0.03f; // 0.48~0.60：猪高 ≈ 0.46~0.57m
            var basis = Basis.FromEuler(new Vector3(0f, yaw, 0f)).Scaled(Vector3.One * scl);
            mm.SetInstanceTransform(i, new Transform3D(basis, pos));
            i++;
        }
    }

    /// <summary>拼一头低多边方块猪：主体表面（身/头/腿/尾）+ 深色表面（拱嘴/耳/蹄）。
    /// 局部坐标 y=0 为地面（四腿底贴地），+Z 为猪头朝向。</summary>
    private static ArrayMesh BuildPigMesh()
    {
        var mainV = new List<Vector3>();
        var mainN = new List<Vector3>();
        var mainI = new List<int>();
        var accV = new List<Vector3>();
        var accN = new List<Vector3>();
        var accI = new List<int>();

        // ---- 主体（粉褐）----
        AddBox(mainV, mainN, mainI, new Vector3(0f, 0.62f, 0f), new Vector3(0.72f, 0.6f, 1.15f)); // 胖圆身躯
        AddBox(mainV, mainN, mainI, new Vector3(0f, 0.55f, 0.72f), new Vector3(0.5f, 0.48f, 0.45f)); // 头
        AddBox(mainV, mainN, mainI, new Vector3(0f, 0.78f, -0.62f), new Vector3(0.08f, 0.08f, 0.18f)); // 卷尾（短桩示意）
        // 四条短腿（前后各一对）
        foreach (var (lx, lz) in new[] { (0.24f, 0.42f), (-0.24f, 0.42f), (0.24f, -0.42f), (-0.24f, -0.42f) })
            AddBox(mainV, mainN, mainI, new Vector3(lx, 0.19f, lz), new Vector3(0.17f, 0.34f, 0.19f));

        // ---- 深色附件（拱嘴/耳/蹄）----
        AddBox(accV, accN, accI, new Vector3(0f, 0.48f, 0.98f), new Vector3(0.26f, 0.22f, 0.16f)); // 拱嘴（前伸猪鼻）
        AddBox(accV, accN, accI, new Vector3(0.16f, 0.84f, 0.66f), new Vector3(0.14f, 0.16f, 0.06f)); // 左耳
        AddBox(accV, accN, accI, new Vector3(-0.16f, 0.84f, 0.66f), new Vector3(0.14f, 0.16f, 0.06f)); // 右耳
        foreach (var (lx, lz) in new[] { (0.24f, 0.42f), (-0.24f, 0.42f), (0.24f, -0.42f), (-0.24f, -0.42f) })
            AddBox(accV, accN, accI, new Vector3(lx, 0.04f, lz), new Vector3(0.19f, 0.09f, 0.21f)); // 蹄

        var mesh = new ArrayMesh();
        AddSurface(mesh, mainV, mainN, mainI, PigColor);
        AddSurface(mesh, accV, accN, accI, PigAccent);
        return mesh;
    }

    /// <summary>把一组顶点/法线/索引作为一个表面并入网格，配不透明烘色材质（双面渲染免绕背剔除）。</summary>
    private static void AddSurface(ArrayMesh mesh, List<Vector3> verts, List<Vector3> normals, List<int> indices, Color color)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, new StandardMaterial3D
        {
            AlbedoColor = color,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // 手搓盒面不苛求绕序，双面渲染稳妥
        });
    }

    /// <summary>向缓冲追加一个轴对齐立方体（6 面各 4 顶点、法线朝外），供拼装方块猪各部件。</summary>
    private static void AddBox(List<Vector3> v, List<Vector3> n, List<int> idx, Vector3 c, Vector3 size)
    {
        var h = size * 0.5f;
        // 六面：法线 + 面上四角（相对中心）
        (Vector3 Normal, Vector3[] Corners)[] faces =
        {
            (Vector3.Right, new[] { new Vector3(h.X, -h.Y, -h.Z), new Vector3(h.X, h.Y, -h.Z), new Vector3(h.X, h.Y, h.Z), new Vector3(h.X, -h.Y, h.Z) }),
            (Vector3.Left, new[] { new Vector3(-h.X, -h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z), new Vector3(-h.X, h.Y, -h.Z), new Vector3(-h.X, -h.Y, -h.Z) }),
            (Vector3.Up, new[] { new Vector3(-h.X, h.Y, -h.Z), new Vector3(-h.X, h.Y, h.Z), new Vector3(h.X, h.Y, h.Z), new Vector3(h.X, h.Y, -h.Z) }),
            (Vector3.Down, new[] { new Vector3(-h.X, -h.Y, h.Z), new Vector3(-h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, h.Z) }),
            (Vector3.Back, new[] { new Vector3(h.X, -h.Y, h.Z), new Vector3(h.X, h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z), new Vector3(-h.X, -h.Y, h.Z) }),
            (Vector3.Forward, new[] { new Vector3(-h.X, -h.Y, -h.Z), new Vector3(-h.X, h.Y, -h.Z), new Vector3(h.X, h.Y, -h.Z), new Vector3(h.X, -h.Y, -h.Z) }),
        };

        foreach (var (normal, corners) in faces)
        {
            int b = v.Count;
            foreach (var corner in corners)
            {
                v.Add(c + corner);
                n.Add(normal);
            }
            // 两三角：0-1-2、0-2-3
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
            idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
        }
    }
}
