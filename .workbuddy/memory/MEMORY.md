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
- **CanvasLayer 内 `SCREEN_TEXTURE` 只采本层**——真·毛玻璃必须把 `BackBufferCopy(COPY_MODE_VIEWPORT)` 作为 CanvasLayer 的第一个子节点；**且必须显式设 `Rect` 覆盖整个 viewport（默认仅 256×256）**——`SCREEN_UV` 大部分位置对应不到 backbuffer，shader 采样返回无效色，毛玻璃退化为单色带。同步订阅 `Window.SizeChanged` 跟新分辨率/全屏切换。
- **`DirectionalLight3D` 无 `ShadowColor`、Godot 4 着色器无 `Transparency`（用 `blend_mix` / `blend_add`）、`Image.Create` 废弃（用 `CreateEmpty`）**。
- **着色器 `uniform vec4` 不接受 `Color`**——只认 `Vector4`，否则写入被静默丢弃 → 黑/默认色。用 `vec3 tint + float alpha` 双 uniform 更稳。
- **地平雾不要按拉距升满**——`FogDensity` 恒定低值（如 0.00025）凭指数衰减只让极远缘软化；"拉远起烟"会让俯瞰满雾、城市朦胧。

## Godot 4.7 C# 着色器坑（自定义 ShaderMaterial 必读）
- **`SetShaderParameter` 的 vec4 uniform 不接受 `Color`**（只认 `Vector4`），传 `Color` 会被静默丢弃、uniform 停在默认 (0,0,0,0)。
  - 症状：依赖该 uniform 的精灵渲染成**黑盘**（尤其 `render_mode unshaded` 又不写 `blend_mix` 时 → 不透明黑）。
  - 规避：颜色/透明度拆成 `uniform vec3 tint` + `uniform float alpha` 两个 uniform，分别传 `Color` / `float`（与月亮同机制，已验证可用）。
- 自定义 `ShaderMaterial` **不含任何雾处理** → 天体（太阳/月亮）用它可天然豁免距离雾，避免地平雾糊掉太阳。
- `Sprite3D` 无 `Texture` 时四边形尺寸为 0（不可见）：自定义着色器仍需挂一张贴图撑出非 0 四边形（着色器可忽略其像素，仅用 UV 算形状）。

## 外部 3D 模型接入（树木/动物/资产管线）
- **共用管线 `GltfMeshMerger`**（scripts/render/）：装载（PackedScene 缓存 → GltfDocument 运行时解析）+
  重组（每个源表面保留为独立 surface 挂自己材质；带贴图保留 AlbedoTexture+UV，无贴图烘顶点色+可去饱和）+
  归一化（底面 y=0 / 高 1 / 水平居中）。TreeModelFactory（树）与 AnimalModelFactory（动物）都是它的薄层，
  各自的 configs（TreeModelConfig / AnimalModelConfig）管路径/高度/去饱和，路径留空即回退程序化造型。
- **`MultiMesh` 一次只能承载一个 Mesh**——树木这类"树干+树冠多子网格"模型必须先合并成单一
  `ArrayMesh` 才能批量实例化。
- **`ArrayMesh` 没有 `Material` 属性**（那是 `PrimitiveMesh` 的），材质用 `SurfaceSetMaterial(0, mat)`。
  顶点色需材质 `VertexColorUseAsAlbedo = true`；`MultiMesh` 的逐实例颜色是**乘算**，适合做微亮扰动而非改色相。
- 改写色相/降饱和要在**烘焙顶点色阶段**做（只降饱和不改色相，保住树冠/树干固有色关系）；用实例色
  通道乘算无法同时把树冠拉向目标色又不把树干推歪。
- 树种配置集中在 `configs/TreeModelConfig.cs`（路径/高度/去饱和），路径留空即回退程序化原始体，可逐树种灰度替换。
- **glTF 底色 = 材质 baseColorFactor × 顶点色 `COLOR_0`，必须相乘**（不能"有顶点色就只用顶点色"）。
  实测本作动物模型 `COLOR_0` 全为白 (1,1,1,1)、真实颜色全在材质里，只取顶点色会烘成全白（"没色彩"）。
  有贴图时：贴图 × 顶点色 × AlbedoColor；无贴图时：把"顶点色 × 材质色"烘进顶点色、AlbedoColor 留白防二次相乘。
- **骨骼绑定模型不能走 MultiMesh**：含 `JOINTS_0`/`WEIGHTS_0`/`skins`/`animations` 的模型（如 Quaternius
  动物，自带 Idle/Walk/Run），MultiMesh 只能静态批渲、无法驱动骨架 → 动画全丢（表现为"不会动"）。
  判据：glTF 里是否有 JOINTS_0 或 animations。此类模型须改为**逐实例节点 + AnimationPlayer**
  （本项目见 `AnimalRenderer`）。静态模型（树/建筑）才用 MultiMesh 批渲。
- **量蒙皮模型包围盒要以"骨架"为基准，不是网格节点**：蒙皮几何由骨骼驱动；当 glTF 里
  骨架缩放 ≠ 网格节点缩放时（本包 Sheep 100/65.46、Pug 39.55/100），按网格节点算会把底点与
  高度算错 → 模型漂浮/尺寸异常，且只有缩放不一致的那几个模型出错（最难排查的一类）。
  写法：`!mi.Skeleton.IsEmpty` → `GetNodeOrNull<Skeleton3D>(mi.Skeleton)`，用它累乘变换。
- 资产：Kenney Nature Kit（CC0 1.0，可商用无需署名），树模型在 `assets/trees/`；首次用 Godot 编辑器打开项目
  才自动生成 `.import`，此前加载会返回 null 并优雅回退。
- 接入前务必先查清资产是**顶点色还是贴图**：`TreeModelFactory` 已支持两者——带贴图表面保留
  `AlbedoTexture`+UV 并克隆材质（每源表面一个独立 surface）；无贴图表面才把材质色烘进顶点色。
  注意去饱和 `Mute` 只作用于纯色路径，带贴图模型不改色（要调需另加 albedo_color 色调）。
- **别改写 glTF 根节点自身的 Transform**：glTF 场景根节点常带单位换算缩放/偏移，直接覆盖会破坏
  模型定位（表现为"模型飞天/尺度异常"）。正确做法：套一个受控 `Node3D` 包装节点，模型作子节点保留
  自身变换，缩放/落地/朝向/平移作用在包装节点上，包围盒也以包装节点为基准测量。
- **放资产 ≠ 能用**：`res://` 下的 .glb 必须先被 Godot 编辑器导入生成 `.import`，
  `ResourceLoader.Load<PackedScene>` 才拿得到东西；否则返回 null（静默回退，容易误判成"代码没生效"）。
  判断方法：看资源目录有没有同名 `.import` 文件。
- **免导入的运行时方案**：引擎自带 `GltfDocument` 可直接从文件解析 .glb，无需 `.import`
  ——适合开发期"丢进资产就生效"。但**导出时未导入的 .glb 不会进 pck**，正式导出前仍应开一次编辑器导入。
- Godot 4.7 C# 绑定的命名坑：glTF 相关类缩写改成了 PascalCase —— **`GltfDocument` / `GltfState`**
  （不是 `GLTFDocument`，也不在 `Godot.GLTF` 命名空间）；`GenerateScene()` 返回 `Node`，需 `as Node3D`。

## 水面渲染（规则纹路 / 透明度）
- **`WaterConfig.WaterEdgeOverlap = 0.7f` 不能四向无差别外扩**：1m 格子会被扩成 2.4m，相邻水面
  互相重叠约 1.4m → 半透明(alpha 0.85)逐层叠加，浮现 **1m 周期的规则网格**（大湖面明显、窄河道
  因太窄看不出来），且叠加后实际 ≈99% 不透明，"透见河床"的设计效果失效。
  正确做法（`AddWaterQuad`）：只朝**邻格非水**的方向外扩（保留"水平面钻到岸下、消除水陆交界锯齿"的
  原意图），水-水相邻方向共享边不重叠。桥格桥下仍有水面，`IsWaterCell` 需把 `HasBridge` 也算作水面。
- 排查"规则纹路"的通用思路：先排除 `_gridLines`（主视图默认隐藏，仅建造模式由 BuildController 打开），
  再查是否有**逐格几何互相重叠 + 半透明叠加**。
- 雾（`WorldEnvironment.FogEnabled` + `FogAerialPerspective`）会掩盖远景这类纹路：
  **去掉雾之后才暴露的渲染瑕疵，不代表是新引入的回归**。当前雾已按用户要求关闭（保留配置字段可复原）。
