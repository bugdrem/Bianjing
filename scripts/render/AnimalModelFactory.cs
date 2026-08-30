using Godot;

namespace Bianjing;

/// <summary>
/// 野外动物外部模型（.glb）→ 可批量实例化的 ArrayMesh（按物种缓存）。
/// 网格重组/装载/归一化在共用的 <see cref="GltfMeshMerger"/> 里，本类只负责
/// 物种配置读取与缓存语义（失败只尝试一次，之后由 AnimalRenderer 回退程序化方块猪）。
/// </summary>
public static class AnimalModelFactory
{
    private static readonly ArrayMesh[] Cache = new ArrayMesh[AnimalModelConfig.SpeciesCount];
    private static readonly bool[] Probed = new bool[AnimalModelConfig.SpeciesCount];

    /// <summary>取某物种的重组网格；加载失败返回 null（调用方回退原始体）。</summary>
    public static ArrayMesh MeshOf(int kind)
    {
        if (kind < 0 || kind >= AnimalModelConfig.SpeciesCount)
            return null;
        if (Probed[kind])
            return Cache[kind];
        Probed[kind] = true; // 只尝试一次：失败不再每帧重试

        string path = AnimalModelConfig.PathOf(kind);
        var inst = GltfMeshMerger.LoadInstance(path);
        if (inst == null)
        {
            GD.PushWarning($"动物模型装载失败：{path}（该物种回退原始体造型）");
            return null;
        }

        var mesh = GltfMeshMerger.Merge(inst, AnimalModelConfig.Desaturate, AnimalModelConfig.Brightness);
        inst.Free(); // 仅用于取网格，未入场景树，直接释放

        if (mesh == null)
        {
            GD.PushWarning($"动物模型无可重组网格：{path}（该物种回退原始体造型）");
            return null;
        }

        Cache[kind] = mesh;
        return mesh;
    }
}
