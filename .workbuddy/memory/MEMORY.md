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

## Godot 4 常见坑（项目内验证过）
- **`RichTextLabel` 默认正文字色走主题 `default_color`，不是 `font_color`**——覆盖 `font_color` 等于无效，BBCode 显式 `[color=…]` 正常、纯文本仍是 Godot 默认白字。`UiTheme.SetColor("default_color", "RichTextLabel", Ink)` 或节点 `AddThemeColorOverride("default_color", Ink)` 才是正确的强制墨字写法。
- **`ItemList` 主题键**：`font_color` / `font_hover_color` / `font_selected_color` 三色各自独立；样式盒除 `panel` / `selected` / `selected_focus` 外还有 `hovered` / `hovered_selected` / `hovered_selected_focus`。漏设 `font_hover_color` 时 Godot 默认 hover 文字偏白，叠浅米/玻璃面板几乎不可读；常配 `hovered` 样式盒统一为「浅青底墨字」风格。
- **`Sprite3D` 没贴图 → 零尺寸四边形不渲染**——若改用自定义 unshaded 着色器现场算圆盘（太阳/月亮），仍需给个 `Texture`（任意大小，shader 忽略像素）。
- **CanvasLayer 内 `SCREEN_TEXTURE` 只采本层**——真·毛玻璃必须把 `BackBufferCopy(COPY_MODE_VIEWPORT)` 作为 CanvasLayer 的第一个子节点。
- **`DirectionalLight3D` 无 `ShadowColor`、Godot 4 着色器无 `Transparency`（用 `blend_mix` / `blend_add`）、`Image.Create` 废弃（用 `CreateEmpty`）**。
- **着色器 `uniform vec4` 不接受 `Color`**——只认 `Vector4`，否则写入被静默丢弃 → 黑/默认色。用 `vec3 tint + float alpha` 双 uniform 更稳。
- **地平雾不要按拉距升满**——`FogDensity` 恒定低值（如 0.00025）凭指数衰减只让极远缘软化；"拉远起烟"会让俯瞰满雾、城市朦胧。

## Godot 4.7 C# 着色器坑（自定义 ShaderMaterial 必读）
- **`SetShaderParameter` 的 vec4 uniform 不接受 `Color`**（只认 `Vector4`），传 `Color` 会被静默丢弃、uniform 停在默认 (0,0,0,0)。
  - 症状：依赖该 uniform 的精灵渲染成**黑盘**（尤其 `render_mode unshaded` 又不写 `blend_mix` 时 → 不透明黑）。
  - 规避：颜色/透明度拆成 `uniform vec3 tint` + `uniform float alpha` 两个 uniform，分别传 `Color` / `float`（与月亮同机制，已验证可用）。
- 自定义 `ShaderMaterial` **不含任何雾处理** → 天体（太阳/月亮）用它可天然豁免距离雾，避免地平雾糊掉太阳。
- `Sprite3D` 无 `Texture` 时四边形尺寸为 0（不可见）：自定义着色器仍需挂一张贴图撑出非 0 四边形（着色器可忽略其像素，仅用 UV 算形状）。
