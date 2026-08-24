using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 建筑外部 3D 资产（.glb / .gltf）加载与缓存：PackedScene 按路径缓存，按建筑实例实例化后
/// 自动缩放贴合占地与层高并落地。阶段 C 中 GridRenderer 对 HasModel 的建筑走此路径，否则回退到
/// BuildingModelFactory 的原始体宋代造型。加载失败或资源缺失时返回 null，由调用方降级到原始体。
///
/// 用法：在 BuildingDef 的 ModelPath 填 res:// 相对路径（如 "res://assets/buildings/shop.glb"），
/// 资产由 3D 生成/美术导入；留空则继续用代码原始体造型。
/// </summary>
public static class BuildingAssetLoader
{
    private static readonly Dictionary<string, PackedScene> Cache = new();

    /// <summary>取（并缓存）某路径的 PackedScene；路径为空或加载失败返回 null（调用方应回退原始体）。</summary>
    public static PackedScene LoadScene(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (Cache.TryGetValue(path, out var cached))
            return cached;
        var scene = ResourceLoader.Load<PackedScene>(path);
        Cache[path] = scene; // 可能为 null（缺失/未导入/损坏），缓存避免每帧重试用
        return scene;
    }

    /// <summary>把资产实例缩放贴合目标占地(w×d)与层高(height)，落于地面中心(posX/Z, baseY)。</summary>
    public static void FitAndPlace(Node3D instance, float w, float d, float height, float baseY,
        float posX, float posZ)
    {
        instance.Position = Vector3.Zero;
        instance.Rotation = Vector3.Zero;
        instance.Scale = Vector3.One;

        var box = GetMeshAabb(instance); // 局部包围盒（含子节点，相对 instance 自身）
        if (box.Size.LengthSquared() < 1e-6f)
            return; // 量不到包围盒（无可视网格）→ 不缩放，保持原样摆位

        float sx = w / Mathf.Max(box.Size.X, 1e-3f);
        float sy = height / Mathf.Max(box.Size.Y, 1e-3f);
        float sz = d / Mathf.Max(box.Size.Z, 1e-3f);
        // 等比贴合，取最小轴避免穿出占地/过高；占地近方形时三轴相近
        float s = Mathf.Min(sx, Mathf.Min(sy, sz));
        instance.Scale = Vector3.One * s;

        // 落地：把包围盒底面（可能高于原点）对齐 baseY
        float minY = box.Position.Y * s;
        instance.Position = new Vector3(posX, baseY - minY, posZ);
    }

    /// <summary>沿父链把 vi 的局部空间变换到 root 的局部空间（不依赖全局变换是否刷新）。</summary>
    private static Transform3D RelativeTo(Node root, Node vi)
    {
        var t = Transform3D.Identity;
        var cur = vi;
        while (cur != null && cur != root)
        {
            if (cur is Node3D n3)
                t = n3.Transform * t;
            cur = cur.GetParent();
        }
        return t;
    }

    /// <summary>聚合 instance 下所有 VisualInstance3D 的局部包围盒，换算到 instance 自身的局部空间。</summary>
    private static Aabb GetMeshAabb(Node3D root)
    {
        var box = new Aabb();
        bool any = false;

        void Visit(Node node)
        {
            if (node is VisualInstance3D vi)
            {
                var a = vi.GetAabb(); // vi 局部包围盒
                var rel = RelativeTo(root, vi); // vi 局部 → root 局部
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
}
