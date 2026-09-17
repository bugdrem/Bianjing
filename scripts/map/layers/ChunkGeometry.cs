using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 一组三角网格缓冲（顶点/法线/顶点色/UV/索引）：图层据此一次性建出 ArrayMesh。
/// 用可变缓冲而非每次 new List，是为了让分块重建复用同一批容器，免逐块分配的 GC 抖动。
/// </summary>
public sealed class MeshBuffers
{
    public readonly List<Vector3> Verts = new();
    public readonly List<Vector3> Normals = new();
    public readonly List<Color> Colors = new();
    public readonly List<Vector2> Uv = new();
    public readonly List<int> Index = new();

    /// <summary>本组是否带 UV：道路需世界坐标 UV 平铺砖纹；地形/水面/桥面无纹理。</summary>
    public readonly bool UseUv;

    public MeshBuffers(bool useUv = false) => UseUv = useUv;

    public void Clear()
    {
        Verts.Clear();
        Normals.Clear();
        Colors.Clear();
        Uv.Clear();
        Index.Clear();
    }
}

/// <summary>一组 MultiMesh 实例缓冲（逐实例变换 + 逐实例颜色）。</summary>
public sealed class InstanceBuffers
{
    public readonly List<Transform3D> Xforms = new();
    public readonly List<Color> Colors = new();

    public void Clear()
    {
        Xforms.Clear();
        Colors.Clear();
    }
}

/// <summary>
/// 单块 64×64 格**一趟**遍历的几何产出：水面 / 三类道路 / 桥面四组网格 + 树木若干组实例。
/// 由 ChunkGeometryBuilder 填充，各图层只取自己那份——遍历只做一次，
/// 免图层拆开后四个图层各扫一遍同一块格（重建开销翻四倍）。
/// </summary>
public sealed class ChunkGeometry
{
    public readonly MeshBuffers Water = new();
    public readonly MeshBuffers RoadMain = new(useUv: true);
    public readonly MeshBuffers RoadSide = new(useUv: true);
    public readonly MeshBuffers RoadLane = new(useUv: true);
    public readonly MeshBuffers Bridge = new();

    public readonly InstanceBuffers Trunks = new();
    public readonly InstanceBuffers ConeCrowns = new();
    public readonly InstanceBuffers BallCrowns = new();

    /// <summary>外部树木模型层：每种树一组（索引对应 TreeModelConfig 的树种）；未配置模型的树种缓冲恒为空。</summary>
    public readonly InstanceBuffers[] TreeModels;

    public ChunkGeometry()
    {
        TreeModels = new InstanceBuffers[TreeModelConfig.SpeciesCount];
        for (int s = 0; s < TreeModels.Length; s++)
            TreeModels[s] = new InstanceBuffers();
    }

    /// <summary>清空全部缓冲（整块重建前调用）。</summary>
    public void Clear()
    {
        Water.Clear();
        RoadMain.Clear();
        RoadSide.Clear();
        RoadLane.Clear();
        Bridge.Clear();
        ClearTrees();
    }

    /// <summary>只清树木缓冲（月度生长的轻量刷新路径：不重建水面/道路网格）。</summary>
    public void ClearTrees()
    {
        Trunks.Clear();
        ConeCrowns.Clear();
        BallCrowns.Clear();
        for (int s = 0; s < TreeModels.Length; s++)
            TreeModels[s].Clear();
    }
}
