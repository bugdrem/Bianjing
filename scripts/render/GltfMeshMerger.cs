using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// glTF 模型 → 可批量实例化的 ArrayMesh（供 MultiMesh 使用），树木与动物共用。
///
/// 为什么必须重组：MultiMesh 一次只能承载一个 Mesh，而模型常是「身体+附件」多个子网格。
/// 本类把每个源表面按各自节点变换搬进同一个 ArrayMesh，且**每个源表面保留为独立 surface
/// 并挂自己的材质**——带贴图的保留 AlbedoTexture+UV；无贴图的把材质色（或模型顶点色）
/// 烘进顶点色，可整体去饱和调和。
///
/// 统一归一化到「底面 y=0、总高 1、水平居中」：实际尺寸/朝向由调用方在实例变换里决定，
/// 从而抹平不同来源模型的尺度差异。
///
/// 装载两条路径：已导入（有 .import）走 PackedScene 缓存；未导入用引擎自带 GltfDocument
/// 运行时解析（GLB 内嵌贴图也能读出），免于"必须先开一次编辑器"。
/// </summary>
public static class GltfMeshMerger
{
    /// <summary>装载模型实例；两条路径都失败返回 null，由调用方回退程序化造型。</summary>
    public static Node3D LoadInstance(string path)
    {
        var scene = BuildingAssetLoader.LoadScene(path); // 内部已按路径缓存（含失败）
        if (scene != null)
            return scene.Instantiate<Node3D>();

        // 未导入的 .glb：res:// 优先；导出的 pck 里没有该文件时，再退回本地绝对路径（编辑器内必有实体文件）
        foreach (var p in new[] { path, ProjectSettings.GlobalizePath(path) })
        {
            // 注意 Godot 4.7 的 C# 绑定把缩写改成 PascalCase：GltfDocument / GltfState（非 GLTFDocument）
            var doc = new GltfDocument();
            var state = new GltfState();
            // GenerateScene 的 C# 绑定返回 Node（glTF 根节点理论上也可能是非 3D 节点），转 Node3D 取网格
            if (doc.AppendFromFile(p, state) == Error.Ok)
                return doc.GenerateScene(state) as Node3D;
        }
        return null;
    }

    /// <summary>单个源表面的收集结果：变换后顶点数据 + 待挂材质。</summary>
    private sealed class SurfaceData
    {
        public readonly List<Vector3> Verts = new();
        public readonly List<Vector3> Norms = new();
        public List<Vector2> Uvs;   // 源表面带 UV 才分配（保留给贴图采样）
        public List<Color> Cols;    // 无贴图表面才烘焙顶点色（有贴图时颜色由贴图决定）
        public readonly List<int> Idx = new();
        public Material Mat;        // 该 surface 最终挂的材质
    }

    /// <summary>遍历 root 下所有 MeshInstance3D，逐表面搬进同一 ArrayMesh（各自保留材质），
    /// 并整体归一化；无可视网格返回 null。desaturate/brightness 作用于纯色路径的配色调和。</summary>
    public static ArrayMesh Merge(Node3D root, float desaturate, float brightness)
    {
        var surfaces = new List<SurfaceData>();

        void Visit(Node node)
        {
            if (node is MeshInstance3D mi && mi.Mesh != null)
            {
                var xf = RelativeTo(root, mi);
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                    CollectSurface(mi, s, xf, desaturate, brightness, surfaces);
            }
            foreach (var ch in node.GetChildren())
                Visit(ch);
        }
        Visit(root);

        if (surfaces.Count == 0)
            return null;

        // 全局包围盒：跨所有表面统一归一化，保持表面间相对位置/比例
        var box = new Aabb(surfaces[0].Verts[0], Vector3.Zero);
        foreach (var sf in surfaces)
            foreach (var v in sf.Verts)
                box = box.Expand(v);

        float scale = 1f / Mathf.Max(box.Size.Y, 1e-4f);
        var origin = new Vector3(
            box.Position.X + box.Size.X * 0.5f, // 水平居中：模型原点可能偏心
            box.Position.Y,                      // 底面落到 y=0
            box.Position.Z + box.Size.Z * 0.5f);

        var mesh = new ArrayMesh();
        foreach (var sf in surfaces)
        {
            if (sf.Idx.Count == 0 || sf.Verts.Count == 0)
                continue;

            // 等比缩放不改变法线方向，法线无需处理
            for (int i = 0; i < sf.Verts.Count; i++)
                sf.Verts[i] = (sf.Verts[i] - origin) * scale;

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = sf.Verts.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = sf.Norms.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = sf.Idx.ToArray();
            if (sf.Uvs != null)
                arrays[(int)Mesh.ArrayType.TexUV] = sf.Uvs.ToArray();
            if (sf.Cols != null)
                arrays[(int)Mesh.ArrayType.Color] = sf.Cols.ToArray();

            int si = mesh.GetSurfaceCount();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(si, sf.Mat);
        }
        return mesh.GetSurfaceCount() > 0 ? mesh : null;
    }

    /// <summary>收集一个源表面：顶点/法线按节点变换搬运；带贴图则克隆材质保留贴图，否则材质色烘顶点色。</summary>
    private static void CollectSurface(MeshInstance3D mi, int surface, Transform3D xf,
        float desaturate, float brightness, List<SurfaceData> outSurfaces)
    {
        var arr = mi.Mesh.SurfaceGetArrays(surface);
        if (arr == null || arr.Count == 0)
            return;
        var srcV = Attr<Vector3>(arr, Mesh.ArrayType.Vertex);
        if (srcV == null || srcV.Length == 0)
            return;

        var srcN = Attr<Vector3>(arr, Mesh.ArrayType.Normal);
        var srcUv = Attr<Vector2>(arr, Mesh.ArrayType.TexUV);
        var srcC = Attr<Color>(arr, Mesh.ArrayType.Color);
        var srcI = Attr<int>(arr, Mesh.ArrayType.Index);

        // 材质：优先节点级覆盖（作用于该 MeshInstance3D 全部表面），其次表面自带材质
        var mat = (mi.MaterialOverride ?? mi.Mesh.SurfaceGetMaterial(surface)) as StandardMaterial3D;
        bool hasTexture = mat?.AlbedoTexture != null;
        var matColor = mat?.AlbedoColor ?? Colors.White;

        // 顶点色一律烘焙（glTF 规范：最终底色 = 材质 baseColorFactor × 顶点色 COLOR_0）。
        // 注意不能"有顶点色就只用顶点色"——实测本作动物模型 COLOR_0 全为白 (1,1,1,1)、
        // 真实颜色全在材质里，只取顶点色会把动物烘成全白（表现为"没有色彩"）。
        var sf = new SurfaceData { Cols = new List<Color>() };
        if (hasTexture)
            sf.Uvs = new List<Vector2>(); // 有贴图才需要保留 UV（无贴图时丢弃，省显存）
        sf.Mat = hasTexture
            ? new StandardMaterial3D
            {
                // 带贴图：贴图 × 顶点色 × AlbedoColor，故顶点色只烘 COLOR_0，材质色留在 AlbedoColor
                AlbedoTexture = mat.AlbedoTexture,
                AlbedoColor = Mute(matColor, desaturate, brightness),
                Roughness = mat.Roughness,
                Metallic = mat.Metallic,
                VertexColorUseAsAlbedo = true,
            }
            : new StandardMaterial3D
            {
                // 无贴图：把"顶点色 × 材质色"烘进顶点色，AlbedoColor 留白以免二次相乘
                VertexColorUseAsAlbedo = true,
            };
        bool bakeMatColor = !hasTexture;

        var basis = xf.Basis;
        for (int i = 0; i < srcV.Length; i++)
        {
            sf.Verts.Add(xf * srcV[i]);
            // 法线只跟旋转走；等比缩放不改变方向，故无需逆转置
            sf.Norms.Add(srcN != null && i < srcN.Length ? (basis * srcN[i]).Normalized() : Vector3.Up);
            if (sf.Uvs != null)
                sf.Uvs.Add(srcUv != null && i < srcUv.Length ? srcUv[i] : Vector2.Zero);

            var vc = srcC != null && i < srcC.Length ? srcC[i] : Colors.White;
            var baked = bakeMatColor ? new Color(matColor.R * vc.R, matColor.G * vc.G, matColor.B * vc.B) : vc;
            sf.Cols.Add(Mute(baked, desaturate, brightness));
        }

        if (srcI != null && srcI.Length > 0)
        {
            for (int i = 0; i < srcI.Length; i++)
                sf.Idx.Add(srcI[i]); // 逐表面独立，无需跨表面偏移
        }
        else
        {
            for (int i = 0; i < srcV.Length; i++)
                sf.Idx.Add(i); // 无索引（非索引三角列表）时按序直连
        }

        outSurfaces.Add(sf);
    }

    /// <summary>配色调和：把模型的卡通高饱和色往灰度拉一档再压亮，使其贴近本作淡雅调。
    /// 只降饱和度不改变色相，故身体与附件的固有色关系得以保留。</summary>
    private static Color Mute(Color c, float desaturate, float brightness)
    {
        float lum = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B; // 亮度（Rec.709 权重）
        float r = Mathf.Lerp(c.R, lum, desaturate) * brightness;
        float g = Mathf.Lerp(c.G, lum, desaturate) * brightness;
        float b = Mathf.Lerp(c.B, lum, desaturate) * brightness;
        return new Color(r, g, b);
    }

    /// <summary>取表面某属性的强类型数组；该属性缺失时返回 null（调用方按需兜底）。</summary>
    private static T[] Attr<T>(Godot.Collections.Array arr, Mesh.ArrayType type)
    {
        var v = arr[(int)type];
        if (v.VariantType == Variant.Type.Nil)
            return null;
        if (typeof(T) == typeof(Vector3))
            return (T[])(object)v.AsVector3Array();
        if (typeof(T) == typeof(Vector2))
            return (T[])(object)v.AsVector2Array();
        if (typeof(T) == typeof(Color))
            return (T[])(object)v.AsColorArray();
        if (typeof(T) == typeof(int))
            return (T[])(object)v.AsInt32Array();
        return null;
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
}
