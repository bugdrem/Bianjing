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

## 地图生成与渲染架构（批次九十三起，权威描述在 specs/DESIGN.md）
- 水系 = 汇流求解制：`FlowRouter`（Priority-Flood 填洼 + D8 + 汇流累积，路由格 2m）→ `RiverNetwork`（河宽∝√面积、Chaikin、蛇曲、**河口外推 56m 出图**）→ `LakeGenerator`（洼地塘 + 三档选址湖 3~6 座）→ `RiverGenerator.BuildWaterSystem`（水位拓扑单趟取 max 回灌、河床按离岸距离 1.0~3.5m）。改水系参数去 `WaterConfig`，别恢复旧「草图引导线」方案。
- 山体：2 座 88~105m 主峰（谐波锥 + 侵蚀后 `ReapplyPrimaryPeaks` 补削顶），MaxTerrainHeight=110 / MinTerrainHeight=−5；小山按 `PeakInfluence` 远离峰群选址（不是象限判据）。
- 渲染：`GridRenderer` 只是协调器（脏标 + 预算仲裁 12+32 + 裙板），内容在 `scripts/map/layers/` 六图层（Terrain/Water/Road/Vegetation/Building/Overlay，基类 MapLayer）；分块几何走 `ChunkGeometryBuilder` 单趟遍历产出 `ChunkGeometry` 缓冲再分发 `ApplyChunk`。**图层不订阅 EventBus、不写 _Process**（预算会翻六倍）。新增地表内容层时：缓冲加进 ChunkGeometry、产出加进 Builder、分发加进 GridRenderer.RebuildChunk。

## 经济循环（实现版摘要，权威文档 = specs/ECONOMY.md）
- 钱在「外部注入 → 官库 → 村民家庭 → 铺面/工坊 → 回流官库」闭环内转；货在「自然 → 原料 → 中间品 → 成品 → 消费」单向流。
- **三条守恒律**（历批次反复修的坑）：① 官库该付的钱一律「先发放、按实扣款」(`GameState.PayBuildWages`，无人领则留官库)；② 家庭该交的钱一律实扣公产、不足少收 (`TakeFromFamily`/`TakeLandTax`)；③ **价只有一个出口**——只取 `Goods.PriceOf`(基价) / `Goods.RetailPrice`(基价×1.5×库存倍率，家庭零买) / `Goods.BuyerPrice`(基价×库存倍率，买方为铺面/工坊)，任何调用点不得自己乘倍率（各写各的正是「同货不同价」的成因）。
- **库存联动定价（批次九十四已接线）**：`Goods.FactorOf` = `StockPriceFactor(整仓占用率 Inv.Total/Inv.Capacity)`；档位 ≥95% ×0.7 / ≥80% ×0.9 / ≤20% ×1.1。容量≤0 的非贸易建筑取平价（否则 0% 占用被误判缺货加价）。此前 `StockPriceFactor` 全项目零调用、售价恒定。
- **商税两种征法**：买方为居民 → 买方家庭按成交额另付；**买方为建筑** → `GameState.PayFromBuildingTaxed` 代扣（恒等式「买方出资 = 卖方所得 + 官库税收」，买方付不足按比例缩放、卖方不倒贴）。朝廷采购与外贸进出口不征。
- 工资分制：`official`/`field` 发固定月俸（旬记 `WagesOwed`、月结发）；`grown` 店坊不发固定工钱（靠售货分账 `PayToBuilding`）**且自负进料货款——名义雇工、实质≈合伙人**；`court` 衙门俸禄由朝廷凭空出（不占官库）。
- 仍未做：分品类库存定价（现用整仓占用率，满仓废料会连带折价卖铁器）；外贸按基价平价（不受城内库存倍率影响）；农田开垦 0 成本；朝廷采购无配额。
- 旧 `.qoder/specs/req.md` 需求稿已废（「交易链单向不可跨级」「商铺加工」「黄金货币」等均不再成立）。

## 物品时效系统（批次九十五起，权威文档 = specs/TIMELINESS.md）
- 双时间轴：**软轴 Fresh**（0~100，三阶段 全效果≥60 / 渐损 15~60 / 已失效<15，失效后"可作他用"）+ **硬轴 Lifespan**（归零即消亡或降级为 `Goods.Scrap`）。两轴**独立计时**。
- `TimedState` **只存三个客观量**：`Fresh` / `UsedLifespan` / `RenewCount`。`Span` 与衰减速率**一律现算**（`TimedRules.Resolve`）——环境会实时改阈值，存派生量会失真、且季节切换要全城改写。剩余天数必须 `max(0, Span - Used)` 夹紧。
- **效果打折 = 消耗时有效量折算**：`有效量 = 取用量 × EffectOf(Fresh)`。一处改动同时覆盖粮食与柴薪（湿柴要烧双份）。折算系数 <0.02 的批次不参与正常消耗（防除零放大取用量）。
- **环境修正走可注册接口**（`ITimedModifier` + `TimedRegistry`，乘算累积，基准 = 露天+春季）。新增地窖/熏房/冰鉴只需一个类 + 一行注册，衰减器不动。
- **合并键不含环境，且用"已耗寿限"而非"剩余寿限"**：`SameBand` = 鲜度档(10点) + 已耗寿限档(30日) + 翻新次数。理由——剩余是派生量（随搬家变，当键会拆散同批货）；衰减速率只取决于当前储存点与季节，故"露天坏得快"会**自然沉淀到 `Fresh` 数值**里自动落档分开，无需环境标签、也无需季节变化时重新分批。
- **翻新 = 带代价的恢复**（用户举例定的）：恢复鲜度但 `寿限 ×0.65^n`、`衰减 ×1.8^n` 复合叠加，递减收益自然涌现，**不需要额外规则禁止无限回锅**。
- **搬运必须保状态**（`TakeBatch`→`StoreBatch`），否则"露天搬进仓库"这条核心玩法会在半路丢失损耗。生产端（收获/加工/开局赠送）走全新状态无需改。
- 坑：**`GoodsStack.Fresh` 的字段初始化器 `= 100f` 不可省**（旧档缺字段时保留 100，默认 0 会让全部库存瞬间"已失效"）；**`const bool` 配置开关会被编译器折叠**致关闭分支报 CS0162，须用 `static readonly`。
- ⚠️ **道路状态必须走稀疏字典**：`Cell` 是 struct 且活在 100 万格 dense 数组（`MapGrid`）里，**不能加字段**。
- 时间尺度：**1 游戏日 = 20 秒现实**（1 旬=60 秒=3 日、1 月=3 分钟、1 年=36 分钟）。调时效数值必先按此换算，否则玩家来不及反应。
- **房屋/道路复用同一套机制**（批次②）：房屋软轴=既有 `Condition`（仍由 MaintenanceSystem 驱动，它有修缮匠与住户集资两条回滚通路），硬轴=新增 `UsedLifespan`（按 Category 给寿限）；**修缮即翻新**——累计修复量每满 100% 记一次大修 → `寿限 ×0.65^n`（**必须用累计量**：逐次计数会把日常养护顶满上限）。道路状态走 `GameState.RoadStates` 稀疏字典，**建档/清除挂 `RegisterRoadCell`/`UnregisterRoadCell`** 上所以铺/拆路自动覆盖；路况乘移速（下限 0.55）、寿限耗尽 `DegradeRoad` 降级（**刻意不拆路**——会破坏玩家路网与临路判定）；路面颜色按路况朝土褐插值做可见反馈，且**只在跨阶段门槛时广播 `CellChanged`**（逐日全标脏 = 每天重建整张地图）。
- 存档：`GoodsStack`/`BuildingInstance`/`MapSave` 的新字段**刻意不升 `FormatVersion`**（都有安全默认值，旧档读入即"万物如新"）。⚠️ 读档时**道路状态必须在 `RegisterRoadCell` 之后写回**，否则被"新铺=全新"覆盖。
- **植物/动物/人接入**（批次③）：植物软轴=既有 `Hp`（"砍伤+闲置自愈"本就与统一机制同形，不重写），另给 `VigorPercent` 归一化接统一阶段；硬轴=新增 `UsedLifespan`（到寿枯死倒木）。动物软轴=新增 `Fresh` 体况（冬季 −1.8/日、其余 +0.42/日，年净 ≈ −4.9 使"一生从肥壮到羸弱"恰好吻合 15 年寿限）；硬轴=年龄老死，与既有随机减员并存。人软轴=既有 `Citizen.Health`（**此前恒 100、纯死字段**，`LifeConfig` 注释自陈"待健康系统接入"），现由断炊/受冻损耗、温饱回血；效果落到 `LaborFactor` 并接进田间收获与工坊加工——两处由"数人头"改为"算劳动当量"。**给生物加时效务必设效果下限**（动物体况归零 = 绝育 → 种群自灭）。
- 坑：**`MathF` 没有 `Clamp` 重载**（用 `Math.Clamp`）；`AnimalObj.Fresh` 的字段初始化器 `= 100f` 不可省（同 `GoodsStack.Fresh`，否则旧档动物瞬间全"极瘦弱"）。
- **恢复动作（翻新）**（批次④）：动作表 `TimelinessConfig.RenewSpecs`（回锅/重腌/澄清/烘干/晾晒/复晒），执行入口 `sim/timeliness/RenewAction.cs`。恢复量是**"拉回到固定值"而非"+N 点"**——后者可被无脑反复刷，前者第二次用时鲜度本就没掉多少、等于白费一次寿限折扣，递减收益自动生效。⚠️ **执行后必须重新归档**（摘出旧批 → 改状态 → `StoreForceBatch` 重入）：档位由鲜度+已耗寿限+翻新次数共同决定，**直接改字段会让它赖在错误的档里**。玩家入口挂面板物品行的 `[url=renew:货品]` BBCode 链接（复用既有 `MetaClicked`，不动面板结构）；NPC 侧 `TimelinessSystem.AutoRenew` 替民居打理，触发线"鲜度<40 且翻新<2 次"（不加闸 NPC 会把食物翻到上限、寿限归零）。「失效后可作他用」= `Salvage` 按 0.3 降级为 `Goods.Scrap`（燃料类可烧）。
- 道路养护：修缮匠修完建筑后余量转去补路况最差的一格（与"修最破的一座"同策略，均摊等于都修不好）。
- 坑：`HousingCapacity` 在 **BuildingInstance** 上而非 `Def` 上（两类同在 `BuildingDef.cs`）。
- **进度**：① 内核+货品 ✅、② 房屋+道路 ✅、③ 植物/动物/人 ✅、④ 恢复动作与操作入口 ✅（四批完成）。**未做**（见 TIMELINESS.md §8.1）：配方状态条件、加工输入按鲜度折算、售价按鲜度折算。

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
