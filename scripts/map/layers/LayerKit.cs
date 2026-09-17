using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 图层共用工具集：高度场采样与网格/MultiMesh 小工具。
/// 抽出来是因为它们不只属于某一个图层——地形顶点法线既供地形受光，也供贴地路面取坡。
/// </summary>
public static class LayerKit
{
    /// <summary>高度场顶点法线：中央差分（水平间距 1m），供受光与坡度显岩。</summary>
    public static Vector3 VertexNormal(HeightField hf, int vx, int vy)
    {
        float dx = hf.VertexH(vx - 1, vy) - hf.VertexH(vx + 1, vy);
        float dz = hf.VertexH(vx, vy - 1) - hf.VertexH(vx, vy + 1);
        return new Vector3(dx, 2f * MapGrid.CellSize, dz).Normalized();
    }

    /// <summary>网格缓冲 → ArrayMesh（空集返回 null，节点不挂网格）。
    /// 是否写 UV 由缓冲自身的 UseUv 决定（道路带砖纹世界坐标 UV；地形/水面/桥面无纹理）。</summary>
    public static ArrayMesh MeshFrom(MeshBuffers m)
    {
        if (m == null || m.Verts.Count == 0)
            return null;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = m.Verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = m.Normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = m.Colors.ToArray();
        if (m.UseUv)
            arrays[(int)Mesh.ArrayType.TexUV] = m.Uv.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = m.Index.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>直接以两个平表刷 MultiMesh（建筑各角色由 BuildingModelFactory 直接产出平表时用）。</summary>
    public static void FillMultiMesh(MultiMesh mm, List<Transform3D> xforms, List<Color> colors)
    {
        mm.InstanceCount = xforms.Count;
        for (int i = 0; i < xforms.Count; i++)
        {
            mm.SetInstanceTransform(i, xforms[i]);
            mm.SetInstanceColor(i, colors[i]);
        }
    }

    /// <summary>把实例缓冲刷进 MultiMesh：先定容再逐实例写变换与颜色。
    /// 注意 MultiMesh 一次只能承载一个 Mesh，故每种造型自占一层。</summary>
    public static void FillMultiMesh(MultiMesh mm, InstanceBuffers b)
    {
        mm.InstanceCount = b.Xforms.Count;
        for (int i = 0; i < b.Xforms.Count; i++)
        {
            mm.SetInstanceTransform(i, b.Xforms[i]);
            mm.SetInstanceColor(i, b.Colors[i]);
        }
    }

    /// <summary>建一个带 MultiMesh 的实例节点：共享 mesh，逐实例变换 + 可选逐实例颜色。</summary>
    public static MultiMeshInstance3D MakeMulti(Mesh mesh, bool useColors) => new()
    {
        Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = useColors,
            Mesh = mesh,
        },
    };
}
