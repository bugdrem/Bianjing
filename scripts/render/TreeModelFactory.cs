using Godot;

namespace Bianjing;

/// <summary>
/// 树木外部模型（.glb / .gltf）→ 可批量实例化的 ArrayMesh（按树种缓存）。
/// 网格重组/装载/归一化的具体工作在共用的 <see cref="GltfMeshMerger"/> 里，本类只负责
/// 树种配置读取与缓存语义（失败只尝试一次，之后回退到代码原始体造型，由 GridRenderer 处理）。
/// </summary>
public static class TreeModelFactory
{
    private static readonly ArrayMesh[] Cache = new ArrayMesh[TreeModelConfig.SpeciesCount];
    private static readonly bool[] Probed = new bool[TreeModelConfig.SpeciesCount];

    /// <summary>取某树种的重组网格；未配置或加载失败返回 null（调用方回退原始体）。</summary>
    public static ArrayMesh MeshOf(int species)
    {
        if (species < 0 || species >= TreeModelConfig.SpeciesCount)
            return null;
        if (Probed[species])
            return Cache[species];
        Probed[species] = true; // 只尝试一次：失败不再每帧重试（同 BuildingAssetLoader 的缓存语义）

        string path = TreeModelConfig.PathOf(species);
        if (string.IsNullOrEmpty(path))
            return null; // 未配置：静默走原始体造型

        var inst = GltfMeshMerger.LoadInstance(path);
        if (inst == null)
        {
            GD.PushWarning($"树木模型装载失败：{path}（该树种回退原始体造型）");
            return null;
        }

        var mesh = GltfMeshMerger.Merge(inst, TreeModelConfig.Desaturate, TreeModelConfig.Brightness);
        inst.Free(); // 仅用于取网格，未入场景树，直接释放

        if (mesh == null)
        {
            GD.PushWarning($"树木模型无可重组网格：{path}（该树种回退原始体造型）");
            return null;
        }

        Cache[species] = mesh;
        return mesh;
    }
}
