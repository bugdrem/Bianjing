using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>野外动物渲染：逐只实例化外部 .glb（Quaternius Farm Animal Pack，带骨架与动画），
/// 移动时播 Walk、静止时播 Idle，朝向跟随前进方向。
///
/// 为什么不用 MultiMesh：这批模型是**骨骼绑定**的（含 JOINTS_0/WEIGHTS_0 与 Idle/Walk/Run 等动画），
/// MultiMesh 只能静态批量实例化、无法驱动骨架，会把动画整个丢弃（表现为"动物不会动"）。
///
/// 为什么用包装节点：glTF 场景根节点自身常带单位换算缩放/偏移；若直接改写它的 Transform 会
/// 破坏模型自身定位（表现为"动物飞天"）。故每只动物套一个受控的空 Node3D，模型实例作为子节点
/// 保留自身变换，缩放/落地/朝向/平移都作用在包装节点上，包围盒也以包装节点为基准测量。
///
/// 视野分级（LOD）：相机视锥内且距离够近 → 每帧更新 + 播动画；否则按 FarUpdateSeconds 间隔
/// 只更新后台位置（动物仍在走动、数据照常推进），并暂停动画以省 CPU。
/// 某物种模型装载失败时回退到内置程序化方块猪（静止，但保证动物永远可见）。</summary>
public partial class AnimalRenderer : Node3D
{
    /// <summary>猪主体色（粉褐）与鼻/耳/蹄的深色（仅模型装载失败时的兜底造型用）。</summary>
    private static readonly Color PigColor = new(0.78f, 0.58f, 0.56f);
    private static readonly Color PigAccent = new(0.62f, 0.42f, 0.42f);

    /// <summary>兜底方块猪的本体高度（模型单位）：归一化缩放按"高 1"折算，兜底网格需按此换算。</summary>
    private const float PigFallbackHeight = 0.92f;

    /// <summary>插值速度（每秒趋近目标的比例系数）。</summary>
    private const float LerpSpeed = 2.5f;

    /// <summary>判定"在移动"的位移阈值（米）：小于此值视为静止，播 Idle。</summary>
    private const float MoveEpsilon = 0.03f;

    /// <summary>物种共享信息（缩放/中心偏移/动画名），探测一次后缓存。</summary>
    private sealed class KindInfo
    {
        public bool Usable;          // 外部模型可用（不可用则回退方块猪）
        public float Scale = 1f;     // 模型高 → 目标站立高度的缩放
        public Vector3 Center;       // 模型局部（含 glTF 根自身变换）：底面 y + XZ 中心
        public string Idle, Walk;    // 模型自带动画名（按关键字匹配）
    }

    /// <summary>单只动物的可视化实例。</summary>
    private sealed class Visual
    {
        public Node3D Pivot;         // 受控包装节点：缩放/落地/朝向/平移都写在这里
        public AnimationPlayer Player;
        public string Idle, Walk;
        public float Scale;
        public Vector3 Center;
        public float Yaw;            // 当前朝向（静止时沿用，避免停下瞬间转向抖动）
        public bool Moving;          // 当前是否在播 Walk
        public bool Animating;       // 当前是否处于动画档（防止每帧重复 Play）
        public bool Placed;          // 是否已设置过一次变换（新个体需立即落位）
    }

    private readonly KindInfo[] _kinds = new KindInfo[AnimalModelConfig.SpeciesCount];
    private readonly Dictionary<int, Visual> _visuals = new();
    private readonly Dictionary<int, Vector3> _smoothPos = new();
    private ArrayMesh _pigMesh;
    private double _farAccum;        // 非动画档的节流累加器（秒）

    public override void _Ready()
    {
        _pigMesh = BuildPigMesh();
        EventBus.GameLoaded += OnGameLoaded;
    }

    public override void _ExitTree()
    {
        EventBus.GameLoaded -= OnGameLoaded;
    }

    private void OnGameLoaded()
    {
        // 读档/新开局：平滑位置与实例全部作废，按新数据重建
        _smoothPos.Clear();
        foreach (var v in _visuals.Values)
            v.Pivot.QueueFree();
        _visuals.Clear();
    }

    public override void _Process(double delta)
    {
        var gs = GameState.I;
        var animals = gs.Animals;

        // 清理已消失个体的实例与平滑位置
        if (_visuals.Count > 0)
        {
            var gone = new List<int>();
            foreach (int id in _visuals.Keys)
                if (!animals.ContainsKey(id))
                    gone.Add(id);
            foreach (int id in gone)
            {
                _visuals[id].Pivot.QueueFree();
                _visuals.Remove(id);
                _smoothPos.Remove(id);
            }
        }

        if (animals.Count == 0)
            return;

        // 视野分级：本帧是否轮到"非动画档"的后台更新
        _farAccum += delta;
        bool farTick = _farAccum >= AnimalModelConfig.FarUpdateSeconds;
        if (farTick)
            _farAccum = 0f;

        var cam = GetViewport()?.GetCamera3D();
        float t = Mathf.Min(1f, (float)delta * LerpSpeed);

        foreach (var a in animals.Values)
        {
            int kind = a.Kind >= 0 && a.Kind < AnimalModelConfig.SpeciesCount ? a.Kind : 0;

            // 目标位置按 Id 伪随机扰动，避免整齐划一；叠加地形海拔，四蹄落在本格地面上
            float jx = (a.Id * 37 % 11 - 5) * 0.12f;
            float jz = (a.Id * 53 % 11 - 5) * 0.12f;
            var ac = new Vector2I(a.X, a.Y);
            var target = MapGrid.CellToWorld(ac) + new Vector3(jx, gs.Map.GroundY(ac) + 0.02f, jz);

            // 在动画档内才逐帧插值；否则沿用上次平滑位置（数据层仍在推进，靠近时自然同步）
            var pos = _smoothPos.TryGetValue(a.Id, out var cur) ? cur.Lerp(target, t) : target;
            _smoothPos[a.Id] = pos;

            // 分级判定：视锥内 + 距离够近 → 动画档
            bool inView = cam != null && cam.IsPositionInFrustum(pos);
            bool near = cam != null && pos.DistanceTo(cam.GlobalPosition) <= AnimalModelConfig.AnimateMaxDistance;
            bool animate = inView && near;

            if (!animate)
            {
                // 远景/画外：按间隔更新一次后台位置即可（动物照样在走，只是不逐帧刷新、不播动画）
                if (farTick)
                {
                    var v = GetOrCreate(a, kind);
                    if (v == null)
                        continue;
                    ApplyTransform(v, pos, false);
                    if (v.Animating)
                        StopAnim(v);
                }
                continue;
            }

            var vis = GetOrCreate(a, kind);
            if (vis == null)
                continue;

            // 移动中：朝向跟随前进方向；静止：沿用上次朝向
            bool moving = (target - pos).Length() > MoveEpsilon;
            if (moving)
                vis.Yaw = Mathf.Atan2(target.X - pos.X, target.Z - pos.Z);

            ApplyTransform(vis, pos, moving);
            UpdateAnim(vis, moving);
        }
    }

    /// <summary>取（或首次创建）某只动物的可视化实例。</summary>
    private Visual GetOrCreate(AnimalObj a, int kind)
    {
        if (_visuals.TryGetValue(a.Id, out var v))
            return v;
        v = CreateVisual(a, kind);
        if (v != null)
            _visuals[a.Id] = v;
        return v;
    }

    /// <summary>写入变换：直接设 pivot 的 Position/Rotation/Scale。
    /// 模型基点已在 CreateVisual 中平移到 pivot 原点（inst.Position = -info.Center），
    /// 故这里给 pivot 设 pos 即把基点放到 pos，绕 Y 转向即绕基点转，缩放即以基点为锚。
    /// 避免了手算 Transform3D 复合链与 glTF 根自身缩放之间的微妙偏差。</summary>
    private static void ApplyTransform(Visual v, Vector3 pos, bool moving)
    {
        v.Placed = true;
        v.Pivot.Position = pos;
        v.Pivot.Rotation = new Vector3(0, v.Yaw, 0);
        v.Pivot.Scale = Vector3.One * v.Scale;
        if (moving != v.Moving)
            v.Moving = moving; // 朝向/动画在 UpdateAnim 里处理，此处仅同步状态
    }

    /// <summary>按移动状态切换动画（仅状态变化时 Play，避免每帧重启动画）。</summary>
    private static void UpdateAnim(Visual v, bool moving)
    {
        if (v.Player == null)
            return;
        string want = moving ? (v.Walk ?? v.Idle) : v.Idle;
        if (string.IsNullOrEmpty(want))
            return;

        if (!v.Animating)
        {
            v.Animating = true;
            v.Player.Play(want);
        }
        else if (moving != v.Moving)
        {
            v.Player.Play(want);
        }
        v.Moving = moving;
    }

    /// <summary>退出动画档：暂停动画播放（省 CPU），可视节点随视锥自动剔除。</summary>
    private static void StopAnim(Visual v)
    {
        v.Animating = false;
        v.Player?.Pause();
    }

    /// <summary>为一只动物创建可视化实例（包装节点 + 模型实例；模型不可用则回退方块猪）。</summary>
    private Visual CreateVisual(AnimalObj a, int kind)
    {
        var info = KindInfoOf(kind);
        float size = 0.9f + (a.Id * 13 % 5) * 0.05f; // 0.9~1.1 个体体型差异

        var pivot = new Node3D(); // 受控包装节点：不覆盖模型根节点自身的变换
        AnimationPlayer ap = null;

        if (info.Usable)
        {
            var inst = GltfMeshMerger.LoadInstance(AnimalModelConfig.PathOf(kind));
            if (inst != null)
            {
                pivot.AddChild(inst); // 模型保留 glTF 根自身变换（单位换算/偏移）
                // 把模型基点（底面 + XZ 中心）平移到 pivot 原点：之后给 pivot 设 Position/Rotation/Scale
                // 就能直接把基点定位到 pos，并绕基点旋转/以基点为锚缩放——避免手算 Transform3D 复合链
                // 在 glTF 根带缩放时可能产生的微妙偏移（表现为"飞天"）
                inst.Position = -info.Center;
                ap = FindDescendant<AnimationPlayer>(pivot);
            }
            else
            {
                pivot.AddChild(new MeshInstance3D { Mesh = _pigMesh }); // 装载失败兜底
            }
        }
        else
        {
            pivot.AddChild(new MeshInstance3D { Mesh = _pigMesh });
        }
        AddChild(pivot);

        var v = new Visual
        {
            Pivot = pivot,
            Player = ap,
            Idle = info.Idle,
            Walk = info.Walk,
            Scale = info.Scale * size,
            Center = info.Center,
            Yaw = a.Id * 2.399f % Mathf.Tau,
            Moving = false,
            Animating = false,
            Placed = false,
        };

        // 起始相位按 Id 错开，免得所有动物动作完全同步
        if (ap != null && !string.IsNullOrEmpty(info.Idle))
        {
            ap.Play(info.Idle);
            ap.Advance((a.Id % 37) * 0.1f);
            v.Animating = true;
        }
        return v;
    }

    /// <summary>物种共享信息：探测一次（量 AABB + 取动画名）后缓存。
    /// 测量以包装节点为基准，故包含 glTF 根节点自身的缩放/偏移。</summary>
    private KindInfo KindInfoOf(int kind)
    {
        var cached = _kinds[kind];
        if (cached != null)
            return cached;

        var info = new KindInfo();
        var probe = GltfMeshMerger.LoadInstance(AnimalModelConfig.PathOf(kind));
        if (probe != null)
        {
            var pivot = new Node3D();
            pivot.AddChild(probe);               // 与运行时结构一致：包装节点 → 模型根
            var box = SceneAabb(pivot);          // 以 pivot 为基准，含 glTF 根自身变换
            info.Usable = box.Size.Y > 1e-4f;
            info.Scale = AnimalModelConfig.HeightOf(kind) / Mathf.Max(box.Size.Y, 1e-4f);
            info.Center = new Vector3(
                box.Position.X + box.Size.X * 0.5f, // XZ 居中：旋转轴心落在模型中心
                box.Position.Y,                      // 底面：落地用
                box.Position.Z + box.Size.Z * 0.5f);

            var ap = FindDescendant<AnimationPlayer>(pivot);
            if (ap != null)
            {
                info.Idle = PickAnim(ap, "idle");
                info.Walk = PickAnim(ap, "walk") ?? PickAnim(ap, "run");
            }
            pivot.Free(); // 连带释放探测用的模型实例
        }
        else
        {
            // 无模型：兜底方块猪（本体 ≈PigFallbackHeight 高，底面与 XZ 中心均在原点）
            info.Usable = false;
            info.Scale = AnimalModelConfig.HeightOf(kind) / PigFallbackHeight;
            info.Center = Vector3.Zero;
        }

        _kinds[kind] = info;
        return info;
    }

    /// <summary>在动画列表中按关键字取第一个匹配的动画名（模型命名为 "Armature|Idle" 之类，故用包含匹配）。</summary>
    private static string PickAnim(AnimationPlayer ap, string key)
    {
        foreach (var name in ap.GetAnimationList())
            if (name.ToLowerInvariant().Contains(key))
                return name;
        return null;
    }

    /// <summary>广度优先查找第一个指定类型的后代节点。</summary>
    private static T FindDescendant<T>(Node root) where T : Node
    {
        var queue = new Queue<Node>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n is T hit)
                return hit;
            foreach (var ch in n.GetChildren())
                queue.Enqueue(ch);
        }
        return null;
    }

    /// <summary>聚合子树内所有 MeshInstance3D 的包围盒（按节点变换换算到 root 局部空间）。
    /// 关键：**蒙皮网格的几何由骨架骨骼驱动，而非网格节点自身的变换**——故有骨架时必须以骨架为
    /// 基准累乘变换。本包模型存在骨架/网格节点缩放不一致的情况（Sheep 骨架 100 / 网格 65.46，
    /// Pug 骨架 39.55 / 网格 100），若按网格节点量会算错底点与高度，表现为动物漂浮或尺寸异常。
    /// 骨骼模型取绑定姿态，与静止时外形一致，用于缩放与落地足够。</summary>
    private static Aabb SceneAabb(Node3D root)
    {
        var box = new Aabb();
        bool any = false;

        void Visit(Node node)
        {
            if (node is MeshInstance3D mi && mi.Mesh != null)
            {
                // 有骨架 → 以骨架为基准；否则用网格节点自身
                Node basis = mi;
                if (!mi.Skeleton.IsEmpty)
                {
                    var skel = mi.GetNodeOrNull<Skeleton3D>(mi.Skeleton);
                    if (skel != null)
                        basis = skel;
                }

                var a = mi.Mesh.GetAabb();
                var rel = RelativeTo(root, basis);
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) != 0 ? a.Position.X + a.Size.X : a.Position.X,
                        (i & 2) != 0 ? a.Position.Y + a.Size.Y : a.Position.Y,
                        (i & 4) != 0 ? a.Position.Z + a.Size.Z : a.Position.Z);
                    var wc = rel * corner;
                    if (!any)
                    {
                        box = new Aabb(wc, Vector3.Zero);
                        any = true;
                    }
                    else
                    {
                        box = box.Expand(wc);
                    }
                }
            }
            foreach (var ch in node.GetChildren())
                Visit(ch);
        }
        Visit(root);
        return box;
    }

    /// <summary>沿父链把 node 的局部空间变换到 root 的局部空间（不依赖全局变换是否已刷新）。</summary>
    private static Transform3D RelativeTo(Node root, Node node)
    {
        var t = Transform3D.Identity;
        var cur = node;
        while (cur != null && cur != root)
        {
            if (cur is Node3D n3)
                t = n3.Transform * t;
            cur = cur.GetParent();
        }
        return t;
    }

    /// <summary>拼一头低多边方块猪：主体表面（身/头/腿/尾）+ 深色表面（拱嘴/耳/蹄）。
    /// 局部坐标 y=0 为地面（四腿底贴地），+Z 为猪头朝向，XZ 居中。仅作外部模型不可用时的兜底造型。</summary>
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
            (Vector3.Down, new[] { new Vector3(-h.X, -h.Y, h.Z), new Vector3(-h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, h.Z), new Vector3(h.X, -h.Y, h.Z) }),
            (Vector3.Back, new[] { new Vector3(h.X, -h.Y, h.Z), new Vector3(h.X, h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z) }),
            (Vector3.Forward, new[] { new Vector3(-h.X, -h.Y, -h.Z), new Vector3(-h.X, h.Y, -h.Z), new Vector3(h.X, -h.Y, -h.Z), new Vector3(-h.X, -h.Y, -h.Z) }),
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
