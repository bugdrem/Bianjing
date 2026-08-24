# 汴京盛卷 (Bianjing) 项目记忆

## 开发阶段约定（用户明确要求，务必遵守）
- 当前为游戏早期开发阶段，**无需考虑存档兼容性**。
- 后续会**大幅重构调整**，不要为兼容旧存档/旧结构而束手束脚。
- 新功能可直接采用代码原始体造型、骨骼化等方案，不必追求资产管线完备。

## 技术栈
- Godot 4.7.1（Forward+，net8.0 / C# Mono），dotnet 10 SDK 构建。
- 关键坑：`dotnet build` 偶发卡死/退出码 1 → 加 `-p:UseSharedCompilation=false` 绕过（疑似共享编译服务器挂死）。

## Godot 4.7 C# 骨骼系统（重要，反直觉）
- **没有 `Bone3D` 类**。骨以索引管理：`Skeleton3D.AddBone(name)` / `SetBoneParent` / `SetBoneRest(idx,T)` / `SetBonePose(idx,T)` / `FindBone`。
- 网格挂到骨用 `BoneAttachment3D`（属性 `BoneName`），其变换跟随骨（含 pose）。
- 动画走 `SetBonePose`（骨架自有通道）：组合为 rest*pose，pose 取纯旋转时绕 rest 原点旋转（正好作支点）。**不要**直接改 Bone 节点旋转（无 Bone 节点可改）。
- 村民/NPC 已用此方案：Skeleton3D + 5 根骨（root→spine→{head,armL,armR}），代码驱动 4 段动作（idle/walk/carry/working），无 AnimationPlayer。

## 建筑造型
- `BuildingModelFactory`：纯代码宋代轮廓造型（地基/半透房体/三棱柱坡顶/檐口/屋脊/立柱/招幌/灯笼），按 Category/占地/等级拆角色，供 GridRenderer 多 MultiMesh 与 BuildController 预览同源复用。
- 阶段 C 资产管线：`BuildingDef.ModelPath` + `BuildingAssetLoader`（glb 加载缓存 + 自动贴合占地层高），GridRenderer 对 HasModel 建筑走此路径，缺失则回退原始体。
