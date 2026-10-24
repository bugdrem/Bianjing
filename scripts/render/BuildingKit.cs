using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 建筑模块化零件套件：把 <see cref="BuildingModelConfig"/> 里配置的 .glb 零件网格
/// 加载/合并/缓存成可批渲的 ArrayMesh（按槽位各一份）。
///
/// **与旧「整栋资产」方案的分工**：
///   · 旧 <see cref="BuildingAssetLoader"/> 仍是「整栋一个 PackedScene」的实例化路径，
///     供王府/宫殿这类需要整体造型的地标建筑使用（BuildingDef.ModelPath）；
///   · 本类服务「按零件替换」路径——一座建筑的几何变换由
///     <see cref="BuildingModelFactory"/> 算出（13 类角色的 Transform3D + Color 列表），
///     本类只把其中某些角色换成 GLB 网格，用同一个 MultiMesh 批渲。
///
/// **为什么这样切**：几何数学（占地换算、路向感知、院落规则、等级缩放）是 531 行验证过的代码，
/// 不该为了接资产而重写；而「用什么网格渲染这个角色」本来就是可换的。于是接资产的风险从
/// 「重写造型」降到「填一个路径」，且可逐槽位灰度上线。
///
/// 装载/合并/归一化复用 <see cref="GltfMeshMerger"/>（与树木/动物同一条管线），
/// 失败静默返回 null，调用方回退该槽位的原始体——**部分资产缺失不影响其它槽位**。
/// </summary>
public static class BuildingKit
{
    private static readonly ArrayMesh[] Cache = new ArrayMesh[BuildingModelConfig.SlotCount];
    private static readonly bool[] Probed = new bool[BuildingModelConfig.SlotCount];

    /// <summary>取某槽位的零件网格；未配置或加载失败返回 null（该槽位走原始体）。
    /// 探测只做一次：失败后不再每帧重试（同 TreeModelFactory 的缓存语义）。</summary>
    public static ArrayMesh MeshOf(BuildingModelConfig.Slot slot)
    {
        int i = (int)slot;
        if (i < 0 || i >= BuildingModelConfig.SlotCount)
            return null;
        if (Probed[i])
            return Cache[i];
        Probed[i] = true;

        string path = BuildingModelConfig.PathOf(slot);
        if (string.IsNullOrEmpty(path))
            return null; // 未配置：静默走原始体

        var inst = GltfMeshMerger.LoadInstance(path);
        if (inst == null)
        {
            GD.PushWarning($"建筑零件装载失败：{path}（槽位 {slot} 回退原始体）");
            return null;
        }

        var mesh = GltfMeshMerger.Merge(inst,
            BuildingModelConfig.DesaturateOf(slot),
            BuildingModelConfig.BrightnessOf(slot));
        inst.Free(); // 仅用于取网格，未入场景树，直接释放

        if (mesh == null)
        {
            GD.PushWarning($"建筑零件无可重组网格：{path}（槽位 {slot} 回退原始体）");
            return null;
        }

        Cache[i] = mesh;
        GD.Print($"建筑零件已接入：{slot} ← {path}");
        return mesh;
    }

    /// <summary>全部槽位的网格表（未配置/失败者为 null）；供图层建层用。</summary>
    public static ArrayMesh[] AllMeshes()
    {
        var all = new ArrayMesh[BuildingModelConfig.SlotCount];
        for (int i = 0; i < BuildingModelConfig.SlotCount; i++)
            all[i] = MeshOf((BuildingModelConfig.Slot)i);
        return all;
    }

    /// <summary>清空缓存（改配置后调用，便于开发期热替换零件模型）。</summary>
    public static void Invalidate()
    {
        for (int i = 0; i < BuildingModelConfig.SlotCount; i++)
        {
            Cache[i] = null;
            Probed[i] = false;
        }
    }

    /// <summary>槽位 → 该槽位在 <see cref="BuildingModelFactory.BuildRoleLists"/> 里的角色索引映射表。
    /// BuildingLayer 据此把同一批变换分流到「GLB 零件层」或「原始体层」。</summary>
    public static BuildingModelConfig.Slot SlotOfRole(int roleIndex) => roleIndex switch
    {
        0 => BuildingModelConfig.Slot.Foundation,
        1 => BuildingModelConfig.Slot.Step,
        2 => BuildingModelConfig.Slot.Body,
        3 => BuildingModelConfig.Slot.Roof,
        4 => BuildingModelConfig.Slot.RoofEnd,
        5 => BuildingModelConfig.Slot.Eave,
        6 => BuildingModelConfig.Slot.Ridge,
        7 => BuildingModelConfig.Slot.Pillar,
        8 => BuildingModelConfig.Slot.Banner,
        9 => BuildingModelConfig.Slot.Lantern,
        10 => BuildingModelConfig.Slot.Window,
        11 => BuildingModelConfig.Slot.Wall,
        _ => BuildingModelConfig.Slot.Body,
    };
}
