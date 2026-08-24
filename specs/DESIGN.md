# 汴京盛卷（Bianjing: The Grand Scroll）业务与技术设计文档

> 本文档由代码全量走查（scripts/ 全部 C# 源码、data/*.json、configs/、CHANGELOG.md）综合生成，
> 定位为「面向开发者的业务 + 技术总览」，与 `CHANGELOG.md`（逐批次迭代记录）、`CODEMAP.md`（业务↔代码速查）、`ECONOMY.md`（经济专项）、`GAME_DESIGN.md`（玩法框架原案）、`RULES.md`（开发规范）互补。
>
> **当前状态（截至批次九十一，2026-08）：** 已实现宋风建筑原语造型（`BuildingModelFactory`）、**骨骼村民**（Godot 4.7 索引式 `Skeleton3D` + `BoneAttachment3D`，`CitizenAnim` 四套代码驱动动画）、**glb 建筑资产管线**（`BuildingAssetLoader` + `BuildingDef.ModelPath`，加载失败回落原语）、建造预览与实际剪影同步（`BuildController` 复用 `BuildingModelFactory.MakePreview`）。货币内部单位已统一为**铜钱「文」**（1 两 = 1000 文、1 万两 = 10000 两 = 10,000,000 文，见 `CurrencyConfig`；**黄金单位已废除**）；税制为**三税种模型**（土地/商/人口）；里程碑 **8 级**；存档 `FormatVersion = 25`；建筑目录 **23 项**；货品 **23 种**。
>
> **结算口径：** 实际为**「每旬 / 每月」**（一游戏旬 ≈ 1 现实分钟，每月 3 旬，批次九十一定稿）；原日频概率已整体 ×7/3 折算为旬频。文中涉及频率处按此口径理解。
>
> **⚠️ 开发约束（用户明确，务必遵守）：** 当前为**早期开发版本**，**功能实现或重构无需考虑旧版本兼容**——枚举新值尾部追加、存档 `FormatVersion` 不符直接拒读即可，不要为兼容旧档做额外工作。

---

## 1. 项目概览

**汴京盛卷** 是一款以北宋都城汴京为背景的城市模拟经营游戏。玩家以「王爷」身份开府建城，
规划道路桥梁、放置官府/公共设施、划定坊区，由自主行动的居民个体自发建房、婚育、就业、生产、消费，
在真实人口生命周期与经济链条驱动下，将一处村落逐级发展为京城。视觉上，整座城市坐落在一幅可展开的宋式横卷（卷轴画）上。

### 1.1 技术栈与平台

| 项 | 内容 |
|---|---|
| 引擎 | Godot 4.7（.NET / Mono 版），`Godot_v4.7.1-stable_mono_win64` |
| 语言 | C# 12 / .NET 8，全部逻辑用 C#，**不使用 GDScript** |
| 第三方依赖 | 仅 LightningDB（LMDB 的 .NET 封装，用于存档）；不引入其它 NuGet 包 |
| 目标平台 | PC（Windows / Linux），暂不考虑移动端 |
| 场景 | 单一主场景 `scenes/Main.tscn`，挂 `scripts/Main.cs` 为根 `Node3D` |

### 1.2 开发约束（`.qoder/rules/Rules.md`）

- 全英文开发（目录/文件/标识符），中文仅作注释；注释率不低于 30%。
- **所有常量与公式集中存放于 `scripts/configs/`**，业务系统只引用不硬编码。
- 早期开发版本，不考虑历史兼容性（存档除外，枚举新值只能尾部追加）。
- 需求模糊 / 遗留逻辑不确定 / 需新增依赖 / 跨模块影响不明 / 业务与代码冲突时，立即停止并提问，禁止猜测。
- 新功能与现有结构不匹配时寻求重构而非打补丁；每次调整都在 `specs/CHANGELOG.md` 记录。

---

## 2. 总体架构

### 2.1 分层模型

项目采用「纯数据模型 + 无状态系统 + 表现层节点 + 事件总线」的分层结构：

```
┌───────────────────────────────────────────────────────────────┐
│  表现层 / 交互层（Godot Node）                                   │
│  GridRenderer  AnimalRenderer  PileRenderer  BuildingStockRenderer│
│  AgentManager/CitizenAgent  RtsCameraRig  BuildController  UI/*   │
└───────────────▲───────────────────────────────┬─────────────────┘
				│ 订阅 EventBus 事件重建/刷新     │ 读写
┌───────────────┴───────────────────────────────▼─────────────────┐
│  模拟系统层（每日/每月 Tick，无长期状态，操作 GameState）          │
│  Lifecycle Job Tax Economy Maintenance Goods Crafting            │
│  PlantGrowth Wildlife ZoneGrowth Desirability Milestone Tech     │
└───────────────────────────────▲─────────────────────────────────┘
								 │ 读写唯一真源
┌────────────────────────────────┴────────────────────────────────┐
│  数据模型层（可 JSON 序列化的纯数据 / 唯一运行时真源）             │
│  GameState（单例） MapGrid+HeightField+Cell  Citizen Family      │
│  BuildingInstance PlantObj AnimalObj ItemPileObj Ledger TaxPolicy │
└───────────────────────────────────────────────────────────────┘
			  ▲ 只读参数                       ▲ 静态定义
		configs/*（常量与公式）        data/buildings.json（数据驱动 + mod）
```

**核心设计原则：**

- **唯一真源**：一局游戏的全部运行时状态集中在 `GameState.I`；任何几何/派生值（桥面高、门位、户主、入住人数）都从数据即时推导或懒算缓存，不做多份副本。
- **数据即状态**：核心数据类（`Citizen`/`Family`/`BuildingInstance`/`Cell` 等）为不含 Godot 类型的纯数据，可直接序列化入存档。
- **系统无状态**：模拟系统类基本不持有长期状态（少数如 `DesirabilitySystem` 的吸引力场为性能缓存），每次 Tick 读写 `GameState`。
- **事件解耦**：数据变更通过 `EventBus` 广播，表现层订阅后局部刷新，模拟层与渲染层零直接耦合。
- **数据驱动 + mod**：建筑走 `buildings.json`，税种/科技/里程碑走注册表，玩家可放 `mods/<名>/buildings.json` 覆盖或追加。

### 2.2 目录结构与职责

| 目录 | 职责 |
|---|---|
| `scripts/core/` | 骨架：`GameState`（状态与地图修改入口）、`EventBus`、`GameClock`、`GamePaths`、`GameSettings`、`Ledger`、`NewsItem`、`Milestones` |
| `scripts/configs/` | 全部常量与公式（WorldConfig/TerrainConfig/WaterConfig/TimeConfig/EconomyConfig/PopulationConfig/LifeConfig/GrowthConfig/MovementConfig/VillagerConfig/CameraConfig/PlantConfig/WildlifeConfig/PrinceMansionConfig） |
| `scripts/map/` | 网格与地形渲染：`MapGrid`、`HeightField`、`Cell`、`GridRenderer`、`WorldGenerator`、`WorldSketch`、`HydraulicEroder`、`RiverGenerator`、`RoadNetwork`、`TreeGenerator`、`ValueNoise`、`AnimalRenderer`、`PileRenderer`、`BuildingStockRenderer`、`GoodsColors` |
| `scripts/render/` | 宋风美术与卷轴：`BuildingModelFactory`（原语造型 + `MakePreview`）、`ScrollBackdrop`、`RenderLayers` |
| `scripts/build/` | 建造：`BuildingDef`+`BuildingInstance`+`Door`、`BuildController`、`PlacementValidator`、`BuildingAssetLoader`（glb 管线） |
| `scripts/citizens/` | 人口模型与系统：`Citizen`、`Family`、`LifecycleSystem`、`JobSystem`、`NameGenerator` |
| `scripts/agents/` | 居民 3D 表现：`AgentManager`、`CitizenAgent`（骨骼村民）、`CitizenAnim`（四套代码驱动姿态） |
| `scripts/sim/` | 经济与自然：`Goods`、`GoodsSystem`、`CraftingSystem`、`EconomySystem`、`MaintenanceSystem`、`DesirabilitySystem`、`PlantGrowthSystem`、`WildlifeSystem`、`Inventory`、`RecipeDef`、`DemandLedger`、`FarmlandSystem`、`Obj` 相关实体 |
| `scripts/zone/` | `ZoneGrowthSystem`（坊区自发生长） |
| `scripts/policy/` | `TaxPolicy`（税档数据）、`TaxSystem`（月度征税） |
| `scripts/tech/` | `TechDef`、`TechSystem`（科技） |
| `scripts/save/` | `SaveData`、`SaveService`（LMDB 异步原子存档，依赖 LightningDB 0.22.0） |
| `scripts/camera/` | `RtsCameraRig`（RTS 轨道相机） |
| `scripts/ui/` | 全部界面：`Hud`、`TopBar`、`BuildMenu`、`InspectPanel`、`FinancePanel`、`PolicyPanel`、`NewsPanel`、`TechPanel`、`GameMenu`、`LoadingScreen` |
| `scripts/objects/` | `Obj`（世界实体基类，含 `PlantObj`/`AnimalObj`/`ItemPileObj`） |
| `data/` | `buildings.json`（建筑静态定义）、`techs.json`（科技定义） |
| `specs/` | 设计文档与变更日志 |

### 2.3 全局事件总线 EventBus

`EventBus`（静态类）定义所有跨模块事件；数据层调用 `Raise*` 广播，表现层订阅刷新。重开一局时 `Reset()` 清空全部订阅防重复。

| 事件 | 载荷 | 语义 / 主要消费者 |
|---|---|---|
| `MapChanged` | — | 全图变化（新局/读档）；渲染器全量重建 |
| `CellChanged` | `Vector2I` | 单格地表变化（铺路/砍树/拆除）；只重建所在分块 |
| `RectChanged` | `origin,size` | 矩形区域变化（建筑落成/拆除/扩建垫基）；只重建覆盖分块（取代旧版全图重建，是 4x 卡顿优化核心） |
| `BuildingsChanged` | — | 仅建筑外观变化（升级楼高/转业换色）；只重建建筑层 |
| `TreesChanged` | — | 仅树木变化（月度生长/散播）；只刷树木 MultiMesh |
| `ZonesChanged` | — | 坊区着色变化 |
| `StatsChanged` | — | 金钱/人口等统计变化；HUD 刷新 |
| `CitizenAdded/Removed` | `Citizen` | 居民增减；`AgentManager` 增删代理 |
| `CitizenSelected` | `int`（-1=取消） | 点选居民；目标路线绘制 |
| `WildlifeChanged` | — | 动物增减/移动；`AnimalRenderer` 刷新 |
| `MilestoneReached` | `int` | 城市晋级；菜单解锁、HUD 弹报 |
| `TechUnlocked` | `string` | 科技研成；HUD 弹报 |
| `NewsPosted` | — | 新公告入栏；公告栏刷新 |
| `BuildingPlaced` | `BuildingInstance` | 实时放置钩子（王爷府开基）；读档重建**不触发** |
| `GameLoaded` | — | 读档完成 |

---

## 3. 运行生命周期

### 3.1 启动与世界生成

`Main._Ready()`：
1. `EventBus.Reset()` → `GameSettings.Load()/Apply()`（读设置并应用窗口/画质）。
2. `GameState.I = new GameState(BuildingDef.LoadAll())`——加载建筑定义（含 mod 合并）。
3. 挂 `LoadingScreen`（标题「初入汴京 · 正在生成世界」），调用 `WorldGenerator.GenerateAsync()` 在**后台线程**生成世界。
   - 此时渲染节点尚未创建，生成只操作纯数据（Map/Plants/Animals），线程安全；加载画面主线程轮询进度。
4. 生成完成回调 `FinishSetup()`（主线程）装配全部节点与系统。

### 3.2 系统装配（FinishSetup）

按序创建并挂载：
1. 环境 `SetupEnvironment()`：暖阳 `DirectionalLight3D`、素雅 Filmic 天空/环境光、卷轴背景 `BuildScrollBackdrop()`。
2. 渲染器：`GridRenderer`、`AnimalRenderer`、`PileRenderer`、`BuildingStockRenderer`。
3. `RtsCameraRig` 相机。
4. `GameClock`，订阅 `DayPassed += OnDayPassed`、`MonthPassed += OnMonthPassed`。
5. 全部模拟系统（见下）实例化。
6. `BuildController`（接相机与渲染器）、`AgentManager`（接时钟）。
7. `Hud`、最后加入 `GameMenu`（自行暂停全树展示主菜单）。
8. 订阅 `BuildingPlaced += OnBuildingPlaced`（王爷府开基钩子）。

### 3.3 时间体系（GameClock + TimeConfig）

- 日历：每天 **24 小时**（= 12 时辰，每时辰 2h）、每月 **12 天**、每年 **12 月**。
- 流速：`Speed ∈ {0（暂停）, 0.5, 1, 2, 4}`（float，支持 0.5x 慢放）。`_Process` 按 `delta×Speed` 累加，每满 `SecondsPerGameHour`（≈0.833 秒/游戏时，1x 下约 7200 秒/年）推进一小时。
- 键位（`_Input` 优先于 UI）：空格暂停/恢复、`1/2/3` = 1x/2x/4x；文本框聚焦时放行。
- 事件：跨日触发 `DayPassed`，跨月额外触发 `MonthPassed`。`AbsoluteDay` 供轮休等周期作息使用；`Shichen` 返回时辰名。

### 3.4 每日 / 每月结算顺序（Tick 编排，Main）

**每日 `OnDayPassed`（顺序敏感）：**
```
同步 CurYear/CurMonth
→ Desirability.EnsureUpdated  (先刷宜居度供选址)
→ ZoneGrowth.TickDay          (居民选址自建/升级/转业/扩建)
→ Lifecycle.TickDay           (迁入/婚配/生育/交友/迁出)
→ Job.TickDay                 (求职/家计/退休)
→ Tax.TickDay
→ Economy.TickDay             (官粮日耗)
→ Maintenance.TickDay         (老化/修缮)
→ Goods.TickDay               (家庭消耗/市集备货需求)
→ Crafting.TickDay            (工坊/商铺加工成品)
→ Plant.TickDay               (挂果/落果)
→ Wildlife.TickDay            (动物游走)
→ Milestone.TickDay           (人口达标晋级)
→ Tech.TickDay                (被动/主动科技推进)
```
**每月 `OnMonthPassed`：** `Lifecycle.TickMonth`（老化/生死）→ `Tax.TickMonth`（征税）→ `Goods.TickMonth`（农田收获散落）→ `Plant.TickMonth`（树木月度生长）→ `Wildlife.TickMonth`（繁育）→ `Ledger.Rotate()`（账本本月转上月）。

> 金钱与货品**不走时钟**：由居民动作完成时即时结算（交易、采集、搬运等），时钟只驱动周期性事务。

### 3.5 自动保存

`Main._Process` 在未暂停时累计真实时间，达 `GameSettings.AutoSaveMinutes×60` 秒触发 `SaveService.SaveAsync(AutoSlot)`：主线程快照+序列化，后台线程写盘免卡帧，完成回调经 `CallDeferred` 回主线程再碰 HUD。

---

## 4. 世界与地形

### 4.1 网格模型

- `MapGrid.Size = 1024`，`CellSize = 1f` → 世界为 1024m × 1024m。
- 坐标系：世界原点居中，`half = Size×CellSize/2 = 512`。`CellToWorld`/`WorldToCell` 以 `half` 偏移换算；格 `(x,y)` 占世界 `[x-half, x+1-half] × [y-half, y+1-half]`。
- `CellIndex(c) = c.Y*Size + c.X`（植物/物资堆字典键）。

### 4.2 Cell 结构体字段

每格 `Cell`（值类型，`Map.CellAt` 返回 `ref`）：

| 字段 | 含义 |
|---|---|
| `HasWater` | 是否河/湖水面格 |
| `WaterH` | 逐格水位海拔（河床下压模型：河心低、谐波湖缘） |
| `HasRoad` / `RoadKind` | 是否有路 / 道路种类（`Main`/`Side`/`Lane`/`None`）。桥面为 `HasRoad=true` 且 `RoadKind=None` |
| `HasBridge` | 是否桥面格（架在水面格之上） |
| `HasTree` | 是否有树（对应 `Plants` 中一株） |
| `BuildingId` | 占用建筑实例 Id（<0 为空） |
| `Zone` | 坊区类型（`None`/`Buildable`/…，经 `GameState.SetZone` 统一写入以维护 `BuildableCells` 索引） |
| `IsEmpty` | 派生：无路无建筑等的空地 |

### 4.3 高度场 HeightField

顶点级 float 高度场（灰度地图模型），`VertsPerSide = Size+1 = 1025`，一维数组 `_h[vy*VertsPerSide+vx]`。关键方法：

- `VertexH(vx,vy)`（越界钳边）/ `SetVertex`（世界生成/垫基/塑形唯一写入口）。
- `SampleWorld(wx,wz)`：双线性插值四邻顶点——村民/物件贴地用，坡面平滑。
- `CellCenterH`（四角均值，= `GroundY` 的 Y 基准）/ `CellMinH`/`CellMaxH`/`FootprintAvgH`。
- `CellSlopeDeg`：格内最陡坡角（邻边按 1m、对角按 √2m 换算）。
- `FlattenRect`：占地整平垫基（放置建筑时压平成台面）。

### 4.4 地形生成管线（TerrainConfig）

「先宏观后细节」的生成模型（批次四十九起）：
1. **草图规划**（`WorldSketch`，128² 内存小图，映射比 8m/格）：西北高东南低对角趋势（`TrendHeight`）+ 平原低频 fBm；峰点撒在西北半包围带（`PeakCount 10~14`，`PeakHeight 30~62m`，高斯锥），避中心圆（`CenterExclusionRadius 280m`）与图缘；山脊连接最近邻峰（余弦横截面 + 鞍部下凹）；中部/东南零星低矮独立山。
2. **上采样**映射到 1025² 顶点。
3. **水力侵蚀**（`HydraulicEroder`，droplet 水滴模型）：全图级 25 万滴、草图级 6 千滴，携沙/沉积/蒸发模型冲出自然冲沟；随后**热侵蚀**（塌方松弛，安息坡角 ≈33°）磨平山脚毛刺。
4. **fBm 细节**叠加（坡度削减系数少叠陡坡噪声，专治山脚毛刺）。
5. 统一 clamp 到 `[MinTerrainHeight -3m, MaxTerrainHeight 64m]`。

**通行/坡度规则**：相邻格高差 ≤`MaxStepHeight 0.5m` 或坡角 ≤`MaxWalkSlopeDeg 30°` 才可通行/铺路（`Traversable`）；陡坡天然挡人。整平垫基要求占地高差 ≤`MaxBuildFlattenDiff 1m`。采集豁免：海拔 >`ForageMaxHeight 4.5m` 的树/野物视为高山景观，不派人采集。

### 4.5 水系（RiverGenerator + WaterConfig）

水系在**侵蚀完成的成品地形**上循坡走线（河湖只读地势不改地势，唯一例外是河床下压）：
- 干流从西北高地循最陡下坡走向东南，宽度从源头（`RiverWidthSource 6`）向河口（`RiverWidthMouth 20`）渐宽；分支河口宽 `12`。
- 沿干流低平处（水位 ≤`LakeMaxSiteLevel 1.5m`）生成 1~2 座湖泊，湖缘多正弦谐波扭曲成不规则湾汊，圈内高地自然留成湖中岛/岬角。
- **河床下压**（`CarveBed`）：水格四角顶点下压到水面以下（离岸 `BedFalloffDist 5` 格内渐深），使水面嵌入地形。
- 逐格 `WaterH` 存储水位，渲染时顶点取邻水格 `WaterH` 均值，坡河上水面连续倾斜。

### 4.6 道路网 RoadNetwork

与 `Cell.HasRoad/RoadKind` 平行维护一张寻路图。`SetRoad(c, on, kind)` 同步权重：寻路权重 = 主路速度 ÷ 该路面速度（`MovementConfig.RoadWeight`），使 AStar 最小化实际旅行时间而非几何距离——居民自发偏好走主路。桥面权重同辅路。

### 4.7 植被 TreeGenerator

世界生成期在可用地面撒树（含果树），存入 `GameState.Plants`（格索引为键，一格至多一株）。树木有树龄/血量（`PlantObj`），果树挂果（`FruitStock`），供伐木/采摘。月度生长与幼体散播走 `PlantGrowthSystem` + `TreesChanged` 事件。

---

## 5. 渲染系统 GridRenderer

### 5.1 分块增量渲染

地图按 **64×64 分块**组织，每块独立 `ArrayMesh`。脏标机制订阅 EventBus：
- `MapChanged` → 全部分块脏。
- `CellChanged` → 所在分块脏。
- `RectChanged` → 覆盖矩形的分块脏。
- `BuildingsChanged` → 只重建建筑层。
- `TreesChanged` → 只刷新各分块树木 MultiMesh（`TreesDirty`）。

每帧限量重建（约 12 块整块 + 32 块树层），避免大改动时一次性百万格重建卡顿（4x 频繁建房曾是间歇卡顿主源，经事件细分 + 分块限额解决）。

### 5.2 渲染图层

每格按优先级生成网格（同一格可叠多层，如桥格＝桥下水面＋桥体板）：

- **地形**：draped quad，四角采顶点高（`AddDrapedQuad`），坡面自然倾斜；顶点色。
- **水面**（`AddWaterQuad`）：四角取邻水格 `WaterH` 均值；四边外扩 `WaterEdgeOverlap 0.7m` 嵌入邻格，水平面从高岸下方穿过被岸地遮住，消除水陆交界锯齿/空隙；贴岸陆格（顶点被河床下压）也补一片水面，水线落在交线上。半透明 + 双面材质。
- **道路**（`AddDrapedQuad` + `AddRoadFoundation`）：三类道路按种类区分明度（主路最亮/小路最暗），整体抬升 `RoadSurfaceLift 0.1m` 避免 z-fighting；路面外边缘（邻非路/非桥格）垂一圈 `RoadFoundationDepth 1m` 地基立面，路面读作坐在高台基上，斜坡不镂空。双面材质。
- **桥（扁平拱桥）**：见 §5.3。
- **树**：独立脏标层，`MultiMesh` 批量渲染。
- **建筑**：房体 + 屋顶（`NoRoof` 者只地面）+ 地基（下延 `FoundationDepth 2m` 遮斜坡悬空底）+ 门。整体抬 `BuildingBaseLift 0.1m`。半透明房体可透见屋内库存堆。
- **门**：大门/后门以颜色与宽度区分，大门居中朝最高等级路。

### 5.3 桥梁渲染（拱桥 + 引桥 + 桥体 + 桥下水）

桥面高度模型集中在 `MapGrid`（渲染与村民站面**同源**，二者严丝合缝）：

- `BridgeSpan(c)`：沿两轴各探两岸最近陆格，取水面跨度更短的一轴为桥跨向，输出 `axis/distA/distB/bankA/bankB`。
- `BridgeDeckTopAt(c)`（**扁平拱桥**）：整段跨水为一座拱——`t = distA/(distA+distB)`，弦 `chord = lerp(bankA,bankB,t)`，拱高 `archH = max(0, min(两岸)+BridgeArchApexRise(1m) − 弦中点)`，抛物鼓包 `bump = 4·archH·t·(1-t)`，`deck = chord + bump`；拱顶（河中央）= 两岸较低者 + 1m，两端落岸；探不到岸退化为水面+抬升。
- `NearBridge(x,y)`：桥旁 ±`BridgeRampCells 3` 格窗内含桥格 → 属引桥过渡带。
- `DeckVertexTop(vx,vy)`：某顶点向外扫最近桥格（格距 d），在桥面高（`BridgeDeckTopAt`）与岸路高（顶点地高+`RoadSurfaceLift`）间按 `t=d/BridgeRampCells` 插值——桥心平抬于水上、向岸逐格渐降接岸路，既遮住被河床下压的岸际锯齿又与普通路无缝相接。
- `DeckSurfaceY(wx,wz)`：双线性插值四邻顶点 `DeckVertexTop`（村民过桥/上下引桥坡站面贴合桥面不下沉）。
- 渲染：`AddDeckBox` 铺实体桥体板（顶面四角取 `DeckVertexTop` + 向下拉 `BridgeBodyThickness 0.2m` 作底面与四侧壁），桥格另先铺桥下水面；桥旁引桥陆地路格同桥体板渲染。

### 5.4 卷轴背景（Main.BuildScrollBackdrop）

游戏世界坐在一幅横卷「画」上，层次自上而下：地形/裙板 → **白底**（地图四周外扩 `MapEdgeExtend 10m` 的近白色平面，齐裙板底高）→ **纸面**（绢帛暖米色，东西向加宽约 2 倍成横卷比例，下移 0.4m）→ 东西两端各一根**深色漆木卷轴圆柱**（轴向南北，底部与纸面画布相切）。图缘镂空由 GridRenderer 裙板遮住。

---

## 6. 建筑系统

### 6.1 数据驱动定义 BuildingDef + mod

建筑静态定义从 `res://data/buildings.json` 加载（`BuildingDef.LoadAll`），随后扫描游戏根目录 `mods/<模组名>/buildings.json` 按目录名升序合并——同 id 覆盖、新 id 追加，玩家放入文件夹即生效无需改代码。

`BuildingDef` 主要字段：`Id/Name/Category(official|grown)/SizeX/SizeY/Cost/Upkeep/Color/Height/NoRoof/Unique/DesirabilityBonus/DesirabilityRadius/Pollution/PollutionRadius/FoodOutput/Capacity/CapacityMax/MaxLevel/Natural/TaxBonus/JobSlots/Salary/StorageCapacity/HarvestMonths/YieldPerWorker/ProduceGoods/TaxBoostPerWorker/MintPerWorkerDay/MenuOrder/MenuGroup/MilestoneRequired`。`CapacityAt(level)` 按等级在 `Capacity~CapacityMax` 线性插值。

### 6.2 建筑目录（data/buildings.json）

> 造价/维护单位为**文**（铜钱）。当前 `data/buildings.json` 共 **23** 项，类别含 `official`/`public`/`field`/`court`/`grown`。

**官府/公共设施（official/public，玩家建造）：**

| id | 名称 | 尺寸 | 造价 | 维护 | 里程碑 | 关键属性 |
|---|---|---|---|---|---|---|
| `prince_mansion` | 王爷府 | 12×12 | 0 | 1500 | 0 | 唯一；宜居+4/r48；库容400；**开局首建** |
| `well` | 水井 | 2×2 | 5000 | 100 | 0 | 宜居+2/r24（公共，供水） |
| `refugee_camp` | 流民营 | 6×6 | 5000 | 200 | 0 | 容量16；库容60（寄居落位） |
| `farmland` | 农田 | 6×6 | 0 | 0 | 0 | 无屋顶；产粮；收获3月1次，人均产50；薪800；满级4（岗1/2/3/4，技能门槛0/50/150/300） |
| `repairhouse` | 修缮房 | 8×8 | 20000 | 400 | 2 | 岗3 薪1000；派修缮匠维护公共设施 |
| `yamen` | 衙门 | 8×8 | 50000 | 1000 | 3 | 宜居+2/r40；岗3 薪3000 |
| `barracks` | 军营 | 12×12 | 80000 | 1500 | 3 | 宜居+1/r32；岗4 薪2500 |
| `taxoffice` | 税所 | 8×8 | 40000 | 800 | 3 | 岗3 薪1200；每吏员 +10% 全城税 |
| `mint` | 铸币局 | 8×8 | 60000 | 1000 | 5 | 岗4 薪1200；每工匠 50 文/日铸钱 |
| `mine` | 采矿场 | 12×12 | 50000 | 600 | 5 | 岗4 薪1000；产铁矿石；污染2/r16；税+2 |
| `saltworks` | 制盐厂 | 8×8 | 50000 | 600 | 5 | 岗3 薪1000；产盐；税+3 |
| `lumber_camp` | 林场 | 8×8 | 30000 | 300 | 3 | 岗3 薪800；产木材(log) |
| `quarry` | 采石场 | 8×8 | 35000 | 400 | 3 | 岗3 薪900；产石料(stone) |
| `yeast_bureau` | 酒曲司 | 6×6 | 25000 | 300 | 3 | 岗2 薪900；产酒曲(yeast) |
| `charcool_si` | 柴炭司 | 6×6 | 40000 | 500 | 3 | 朝廷收购：木/木材/薪炭（court） |
| `shiyi_wu` | 市易务 | 8×8 | 50000 | 600 | 3 | 朝廷收购：粮/果/野味（court） |
| `palace` | 宫殿 | 16×16 | 200000 | 2000 | 7 | 宜居+3/r32；岗5 薪4000 |

**自发生长建筑（grown，居民自建，免费，占地 2×2 起，`MaxLevel 3`）：**

| id | 名称 | 基础色 | 关键属性 |
|---|---|---|---|
| `house` | 民居 | 米黄 | 容量4→满级6；库容32 |
| `mansion` | 宅邸 | 浅褐 | 容量6→满级10；库容80 |
| `shop` | 商铺 | 蓝 | 岗1 薪1500；库容80；转业专营一种货品 |
| `workshop` | 工坊 | 褐 | 岗2/4/6(随级) 薪1200；效率1/1.5/2；技能门槛0/200/600；库容80；专营加工成品 |

### 6.3 建筑实例 BuildingInstance

继承 `Obj`（含 `X/Y/Origin`）。关键：`Level`、实例占地 `SizeX/SizeY`（0 则沿用 Def；`FootX/FootY` 取实际）、`Condition`（完好度 0-100，逐月老化归零坍塌）、`Abandoned`、`BuiltYear/Month`、`Specialty`（专营货品）、`MonthsSinceHarvest`、`Inv`（库存，容量 `StorageCap` 随占地面积等比伸缩）、`Doors`（懒算缓存不入存档）。
- `HousingCapacity`：grown 建筑 = 占地格数（房体=占地，居住与打工共用同一格池）；官营沿用定义容量。

### 6.4 放置流程（GameState.PlaceBuilding）

1. 创建实例、写 `Buildings`。
2. **自动整平垫基**：`FlattenRect` 将占地顶点压平成台面（取占地顶点平均高），建筑立面天然水平（读档重建不经此方法，不会二次整平）。
3. 逐格标 `BuildingId`、施工砍伐、官营覆盖坊区（grown 保留坊区便于拆后重生）。
4. official 扣 `Cost` 并记账；grown 免费。
5. **附属小路环** `LayLaneRing`：沿 footprint 外一圈空地铺小路（`GrowthConfig.LaneRing 1` 格宽），已有任意路保留。
6. 广播 `RectChanged` + `StatsChanged` + `BuildingPlaced`。

### 6.5 门计算（ComputeDoors，懒算）

按边分组收集临路候选：**大门**开在临路等级最高的边上**居中**（主路3/辅路2/小路1/桥面1）；**后门**优先开在屋后偏侧（偏左/右按建筑 Id 奇偶错落），数量 `max(1, 占地格数/CellsPerBackDoor(64))`，相邻门保持 `MinDoorGap 2` 格间距；仅一边临路则无后门；四面无路返回空（走邻路锚点兜底）。占地/临路变化时 `Doors=null` 令其失效重算。

### 6.6 道路 / 桥梁画笔（GameState）

- `PlaceRoadStamp(center, kind)`：w×w 方形画笔（主路 `MainRoadWidth 4`、辅路 `SideRoadWidth 2`）。陆地空格铺路，遇水面格**自动架同宽小桥**（拖拉一次画成不断档）；岸上按道路单价、跨水段按桥梁单价，各按等效延米（新格数÷宽）计费（重叠不多扣）。
- `PlaceBridgeStamp(center)`：独立桥工具，`BridgeWidth 4` 方形画笔，只在无桥水面格架设。
- 单价（WorldConfig）：主路 `MainRoadCost 18`、辅路 `SideRoadCost 10`、桥 `BridgeCost 30`（文/延米）。
- 桥面 `LayBridgeCell`：`HasBridge=HasRoad=true`，`RoadKind=None`，寻路权重同辅路，可通行。

### 6.7 拆除（GameState.DemolishAt）

逐层优先级：桥梁 > 道路 > 建筑 > 坊区 > 树木（河水不可拆）。拆建筑时清空占地并清理其附属小路环——仅移除「不再紧贴任何其它建筑」的独占小路，共享小路保留以免切断邻居通路。

### 6.8 建造交互 BuildController + PlacementValidator

`BuildController` 处理放置流程：悬停高亮 `UpdateHoverCell`（对地形高度场做射线求交定位）、放置居中 `BuildingOrigin`（鼠标对准占地中心）、道路/桥画笔的拖拉连铺、拆除工具。`PlacementValidator` 校验占用/坡度/里程碑解锁/唯一性。**王爷府首建门槛**：`PrinceMansionBuilt` 为假前锁定其它一切营造；王爷府落成触发开基拨款（`PrinceMansionConfig`：钱/粮/府库货品）并随迁三对富裕年轻夫妻暂居（`SettleNobleFamilies`）。

---

## 7. 人口与社会

### 7.1 居民个体模型（Citizen）

`Citizen` 为纯数据类（可直接 JSON 序列化），每个个体有真实生命周期：

| 分组 | 字段 | 说明 |
|---|---|---|
| 身份 | `Id` / `Surname` / `Name` / `Gender` | 姓名由 `NameGenerator` 按姓氏+名库生成 |
| 年龄 | `AgeMonths` | 按游戏月计（每月结算 +1）；`AgeYears = AgeMonths/12` |
| 社会关系 | `FamilyId` / `SpouseId` / `FatherId` / `MotherId` / `ChildrenIds` / `FriendIds` | 全部存 Id 而非对象引用，避免对象图循环，序列化友好 |
| 居住与工作 | `HomeId` / `JobKind` / `WorkplaceId` | `JobKind`：`None`（无业）/ `Employed`（受雇）/ `Logger`（进山伐木采猎） |
| 资产 | `Money` | 个人私产（文，铜钱） |
| 状态值 | `Fatigue` / `Fun` / `Health` | 均 0-100；`Health` 默认满值，预埋健康系统接口 |
| 背包 | `Pack` | `Inventory`，容量一担（`LoadUnits` 5 份）；`PackGoodsId` 兼容旧单货品判断 |
| 短缺计数 | `FoodShortDays` / `FuelShortDays` / `HomelessMonths` | 面板展示 + 迁出判定 |
| 供货认领 | `ClaimBuildingId` / `ClaimGoodsId` | 出发为某建筑采集/补料时登记，需求判定扣除在途量防多人扎堆 |
| 履历 | `LifeEvents` | 重大人生事件（迁入/出生/成婚/得子女/分家/迁居/就业变动/丧偶），上限 `LifeEventCap 40` |
| 扩展 | `Extra` | 字典，为 mod 与未来系统（教育/官职/声望）预留 |

派生属性：`IsChild`（<16 岁）/ `IsAdult`（16-60）/ `IsElder`（≥60）/ `HasJob` / `IsMarried`。
`GetIdentity(gs)` 按年龄与职业返回身份名：孩童 / 山民 / 官吏（yamen）/ 士兵（barracks）/ 仆役（palace）/ 商贩（shop）/ 工匠（workshop）/ 农夫（farm）/ 修缮匠（repairhouse）/ 税吏（taxoffice）/ 铸钱匠（mint）/ 矿工（mine）/ 盐工（saltworks）/ 长者 / 平民 / 雇工。

### 7.2 活动状态机（ActivityType）

居民当前活动是表现层状态机驱动的枚举，随存档保存（**枚举新值只能尾部追加**，防老档错位）：

| 活动 | 含义 |
|---|---|
| `RestHome` | 在家休息（夜间/疲劳） |
| `Working` | 在岗工作（含上下工通勤） |
| `Shopping` | 外出采购（主妇/补货） |
| `Playing` / `Strolling` | 玩耍 / 闲逛散心 |
| `Logging` / `Gathering` / `Hunting` | 伐木 / 采摘 / 打猎（含 `Logger` 山民） |
| `Trading` | 市集交易 |
| `Repairing` | 参与修缮 |
| `Hauling` | 把背的货物挑去目标建筑入库（自家或田仓） |
| `PickingUp` | 走到地面物资堆拾货入背包 |
| `FetchingWater` | 在水井/河岸打水入背包（水仅家用，背回家入库） |

### 7.3 家庭（Family）

- `Family`：共享住所与公产。`MemberIds`（成员 Id）、`HomeId`、`SharedAssets`（公产，婚嫁/迁入时注入，日常开销优先扣此）。
- `TotalAssets = SharedAssets + Σ成员 Money`——「家庭人均资产」是富裕判定（退休分流/高胎次抑制）的依据。
- 典型形态：夫妻 + 子女 + 父母，也可为单身户；无家可归累计 6 个月携幼迁出。

### 7.4 生命周期（LifecycleSystem）

**每旬（TickDay）——人口增长靠「迁入 + 分家建房」驱动：**
> 注：结算口径已由「日」改为**旬**（一游戏旬 ≈ 1 现实分钟，每月 3 旬；批次九十一把旧的日频概率 ×7/3 折算为旬频，年流入量不变）。

| 事务 | 概率 / 参数 | 说明 |
|---|---|---|
| 迁入（夫妻户 + 单身） | 0.2333/旬 | 四类流民按权重抽一（归民 0.35 / 寓商 0.30 / 散勇 0.20 / 客士 0.15），需流民营/店坊有寄居空位；成人年龄 18~36 岁；随身现金（文）：归民 800~3000、寓商 6000~12000、散勇 300~1500、客士 0~300 |
| 单身男性比例 | 0.6 | 男性概率 0.6；资产 ≥ `SelfBuildAssets 5000` 文且有落位才自建，否则寄居店坊当暂住雇工 |
| 婚配 | 0.0233/旬 | 每次抽样 8 名候选（近亲跳过），抽满未果本旬作罢 |
| 生育 | 0.007/旬 基础值 | 系数 = 胎次 × 母龄 × 富裕，住房容量超 1.5 倍停生；满员住户每月 `CrowdEventChance 0.15` 触发扩建/分家疏解 |
| 交友 | 0.0233/旬 | 上限 `MaxFriends 5`（社交预留） |

**生育系数公式（PopulationConfig）：**
- 胎次：1~2 胎 1.0 → 3 胎 0.6 → 4 胎 0.3 → 5 胎起 `0.12 × 0.5^(胎-5)`（指数衰减永不归零）；
- 母龄：30 岁起每年 ×(1-0.05)，下限 0.2；
- 富裕：人均资产达 `WealthEase 40000` 文时降至下限 0.3。

**每月（TickMonth）——老化与生死：**
- 年龄 +1 月；成年门槛 16 岁。
- 死亡：Gompertz 曲线年死亡率 = `0.005（底噪）+ 0.03 × e^((age-55)/8)`，经 `MonthlyFromAnnual` 复利换算月率；主要死亡区间约 55-65 岁；饥荒（官粮见底）月死亡率附加 0.03；最大寿数 120 岁必亡。健康放大系数（满值 100 为 1.0，越低越易亡，封顶 4 倍）已预埋，当前健康恒满不生效。
- 分家：成年子女成婚分家立户，新家庭注入公产 `SplitFamilyAssets 1500` 文。
- `SettleNobleFamilies`：王爷府落成时随迁 3 对 20~27 岁富裕夫妻（公产 40000 文），暂居府中，待玩家划好可建坊区后由「寄居→自建」逻辑迁出、在府邸周边自建新宅。

### 7.5 就业（JobSystem）

每日结算固定五步（顺序敏感）：

1. **清理失效岗位**：工作单位被拆 → 失业并记履历。
2. **自营优先**：工坊/商铺（grown + 有岗位）的岗位先由本楼居民承担——在外就业者辞外职回自家；岗位被外来雇工占满时辞退外人给东家让位。家族产业内的人可干到 60 岁（普通雇工 50 岁退休）。
3. **退休致仕**：到龄退出当前岗位；退休后不再受雇，只参与采集等轻活（行为层按家资分流：人均资产 >`WealthyPerCapitaAssets 20000` 文闲逛，否则采集）。
4. **求职**：适龄（<50 岁）无业者应聘建筑岗位（官营建筑面向全城招工；grown 工商满员不对外招——外来雇工占一个居住格）；已婚且丈夫在业的主妇留家采购不求职；无空缺时 `JoblessForageChance 0.83` 概率上山谋生（`Logger`：伐木/采摘/打猎），其余闲逛/待业。
5. **家计开销**：每人每月 `LivingCostPerCapita 200` 文，逐旬按 1/月旬数扣——先扣公产，不足再由成员分摊私产。

> **工钱不走数据层**：由表现层 `CitizenAgent` 在每班下工时按动作即时结算（月俸/30 一班）。工商业「一直营业只退休」，作息与疲劳由表现层实时驱动。

### 7.6 居民表现层（AgentManager / CitizenAgent + VillagerConfig / MovementConfig）

- **代理上限**：`MaxAgents 300`——超出的居民只参与数据模拟，不上屏（防万人同屏）。
- **模型与移动**：成人缩放 0.25，儿童从 0.4 起随龄线性长大；基础速度 5 m/s × 路面系数（主路 1.2 / 辅路 1.0 / 小路 0.7 / 桥面 1.0），脱路 ×0.35 惩罚 → 居民自发沿路行走；转身角速度 10 rad/s；路格内随机偏移 0.45m 防排成一线；人群分离（半径 0.9m / 推力 3）防重叠。
- **作息**：6~18 时上工（`WorkStartHour/WorkEndHour`），每 5 天轮休 1 天（按 `AbsoluteDay` 错峰，不全城同日停工）。
- **需求驱动决策**：疲劳 >80 回家歇息；兴致 <25 出门散心；家庭储备目标 食物 3 / 柴 1 / 水 3 份每人（约一月用量），低于一半触发补货/打水；就近采集半径 64m（伐木/采摘/拾堆/打猎，水不受限——刚需且无替代）。
- **砍伐**：每斧 25 点伤害（幼树一斧倒、老树多斧），血量 → 柴薪按 0.2 份/血折算；海拔 >4.5m 的高山景观树不派工采集。
- **贴地（SurfaceYAt）**：桥格与引桥坡取 `MapGrid.DeckSurfaceY`（与渲染同源，过桥不下沉）；普通路格站路面（地面 + `RoadSurfaceLift 0.1`）；其余双线性采高度场直接贴地。
- **买卖与供货**：采买半径 160m 内找备货市集/铺面，否则自主采集；供货认领（`ClaimBuildingId/GoodsId`）防多人扎堆同一目标。

---

## 8. 经济与生产

### 8.1 货品与价格（Goods）

> 基价为**整数文/份**（`Goods.BasePrice`，`long`）；当前共 23 种货品，覆盖食物/燃料、原料、中间品、成品、流民随身物、废料。

| 货品 | 基价（文） | 类别 | 来源 |
|---|---|---|---|
| `grain` 粮食 | 10 | 食物 | 农田收获 / 市集 |
| `wood` 柴薪 | 3 | 燃料 | 伐木 |
| `fruit` 果品 | 6 | 食物 | 果树挂果采摘 |
| `game` 野味 | 18 | 食物 | 打猎 |
| `flatbread` 烧饼 | 15 | 食物 | 加工：粮食 → 烧饼 |
| `charcoal` 炭 | 8 | 燃料 | 加工：柴薪 → 炭（薪炭） |
| `log` 木材 | 5 | 原料 | 林场 |
| `hide` 皮 | 22 | 原料 | 打猎 |
| `herb` 药材 | 25 | 原料 | 采集 |
| `raw_salt` 盐 | 15 | 原料 | 制盐厂 |
| `iron_ore` 矿石 | 20 | 原料 | 采矿场 |
| `stone` 石料 | 8 | 原料 | 采石场 |
| `yeast` 酒曲 | 12 | 原料 | 酒曲司 |
| `planks` 板材 | 18 | 中间品 | 加工：木材 → 板材 |
| `leather` 皮革 | 40 | 中间品 | 加工：皮 → 皮革 |
| `refined_salt` 精盐 | 40 | 中间品 | 加工：盐 → 精盐 |
| `iron_ingot` 铁锭 | 55 | 中间品 | 加工：矿石 → 铁锭 |
| `timber` 木器 | 50 | 成品 | 加工：板材 → 木器 |
| `wine` 酒 | 45 | 成品 | 加工：粮食 + 酒曲 → 酒 |
| `ironware` 铁器 | 140 | 成品 | 加工：铁锭 → 铁器 |
| `cured` 腌货 | 60 | 成品 | 加工：野味 + 精盐 → 腌货 |
| `furniture` 家具 | 100 | 成品 | 加工 |
| `clothing` 衣物 | 150 | 成品 | 加工：皮革 → 衣物 |
| `medicine` 药材制成品 | 80 | 成品 | 加工：药材 → 成药 |
| `water` 水 | — | 非买卖 | 井/河岸打水，**不设市价、不入铺面、不参与买卖**，仅供家用 |
| `weapon`/`book`/`scrap` | 80/30/2 | 流民物/废料 | 仅折入资产或当柴烧，不入市 |

- **定价**：居民卖出价 = 基价；买入价 = 基价 × `BuyMarkup 1.5`（去商铺购买比自产贵）。库存联动定价按占用率浮动（高库存降价、低库存涨价，见 `Goods.StockPriceFactor`）。
- **配方**：`Recipes` 字典（成品 → 原料列表），每产 1 份成品耗每种原料各 1 份。
- **一担 = `LoadUnits 5` 份**（居民单次搬运量，`Goods.LoadUnits` 转发）。

### 8.2 统一仓储（Inventory）

`GoodsStack`（货品 id / 份数 / 入库天数）+ `Inventory`（容量 / 堆列表），建筑仓房、居民背包、地面物资堆共用同一套容量与存取规则：

- 同一货品并为一堆；`Store` 受容量限制返回实际入库量；`StoreForce` **超限收下**（上限只作「继续派人采集/进货」的闸门，不作硬墙——村民背回的货不浪费）；`Take` 取空即移除；`AgeOneDay` 全堆计龄（并堆取较早龄期），为后期变质/鲜度系统铺垫。
- 典型案例：居民背包（容量一担）、建筑库存（仓房）、地面物资堆（散落地图的收成/猎物/落果）。

### 8.3 家庭消费与市场（GoodsSystem）

**每旬结算：**
- 口粮 0.2333 份/人/旬：先掏家中存粮（优先级 **粮→果→野味**），不够上市购买（当旬即耗，不入家库），买不到则 `FoodShortDays++` 且兴致 −1/旬（断炊一天比一天丧气）。
- 柴薪 0.07 份/人/旬：同流程，缺柴兴致 −0.5/旬。
- 饮水 0.2333 份/人/旬：只扣家存（水不上市无处可买），缺水无惩罚，由储备阈值驱动居民去井/河边打水。
- 分级需求（见 12.2）：家中无存则上市购买——为加工成品打通消费端。
- 购买规则：从「专营该货」的铺面或市集（通卖各货）直接买走，货款**平分给铺面雇工**（工资的市场化通道），无雇工的官营铺面收入入官库记「市易收入」。
- 全部库存（建筑/背包/地面堆）计龄 1 天。

**月结：** 产业建筑（粮田/采矿场/制盐厂）按 `HarvestMonths` 周期到期收获——产量 = 在岗工人 × `YieldPerWorker` × `TechFactor("harvest")`（农学科技加成）；产物由定义 `ProduceGoods` 指定（空串默认产粮）。收成**集中成最多 8 堆**随机散落在田格上（1m 格下逐格散会生成上百小堆拖垮拾运与渲染），单堆容量 40 份，满堆装不下的烂在地里；由农夫拾运（`PickingUp` + `Hauling`）入仓。

### 8.4 加工链（CraftingSystem）

每日结算：仅「专营可加工成品」且有在岗工人的 grown 建筑（工坊/商铺）加工——产量 = 工人数 × `CraftPerWorkerDay 1.8667`（份/工/旬）× `TechFactor("craft")`，受最紧缺原料存量限制；扣原料、入成品（超限入库：加工前后总占用只减不增，不会因库容永久停工）。一座建筑当前只加工一种成品（`Specialty`，转业时随机专营其一，随存档保存）。

### 8.5 官库（EconomySystem）

> 货币内部单位已统一为**铜钱（文）**，大额以白银（两 / 万两）展示：1 两 = 1000 文，1 万两 = 10000 两 = 10,000,000 文（见 `CurrencyConfig`）。**黄金单位已废除**（批次九十三）。以下金额均为文。

- 官粮日耗：`OfficialFoodPerCapita 0.05` 份/人/旬（官府赈济/公务用度，区别于家中口粮）；朝廷每月按 `CourtFoodAmmoPerCapitaMonth 3` 份/人拨入官仓，官仓按 `CourtFoodCapPerCapita 0.9` 份/人封顶；农田收成按 `GrainTaxShare 0.1` 比例入官粮（余下归村民）。
- 开局（匹配 `WorldConfig.StartMoney` / `EconomyConfig.SettlementGrant`）：官库钱 **100000**、官粮 **500** 份。
- 王爷月俸：`PrinceMonthlySalary 8000` 文/月（前期核心现金流，逐月入官库）。
- 收入来源：税收（月征）、里程碑晋级拨款、市易收入（无雇工铺面）、朝廷赏赐、王爷府开基拨款（**100000 文** + 400 份粮 + 府库货品：粮120/木80/果40/盐30/矿30）。

### 8.6 老化与修缮（MaintenanceSystem，双线并行）

- **老化**：人造建筑逐日老化（`BuildingAgingPerMonth 0.7`/月按 1/旬数逐旬结算），天然建筑固定不变。
- **官府线**（公共设施）：修缮匠（受雇于修缮房）逐座抢修**最破**的 official 建筑，每人每月修复量 25，官府出料钱 **100 文**/匠/月（记账）。
- **私宅线**（住宅/工商）：grown 建筑由居住者按人头集资自修——每人每月摊派 **15 文**，集得修复量 5/月（「以税养屋」）；无人居住任其荒废。
- **坍塌**：完好度归零 → `DemolishBuilding` 拆除，居民失所由 LifecycleSystem 的无家流程接管（6 个月未安家则携幼迁出）。

---

## 9. 宜居度（DesirabilitySystem）

- **模型**：每格 `Cell.Desirability` = 道路临街加成 + 建筑正负覆盖（`desirabilityBonus` 正向 / `pollution` 负向，半径线性衰减，均来自 buildings.json 数据驱动）。
- **性能设计**：道路项数量大（数千格 × 半径 12m 圆盘）且逐格增量变化——道路吸引力场用**独立缓存增量维护**（只泼溅新增/变种/拆除的路格差额，变种补差额），重算时整场拷入再叠建筑项；避免 4x 下村民频繁铺小路/建房触发全量重泼（曾是间歇卡顿源之一）。
- **数值**（GrowthConfig）：主路 1.0、辅路 0.4，除以密度归一系数 16（1m 格密度是旧版 4m 格的 16 倍）后沿半径 12m 线性衰减泼溅；小路与桥面不加成。
- **消费方**：`ZoneGrowthSystem` 选址打分（见下章）与住宅升级门槛（每级需吸引力系数 `LevelUpDesirPerLevel 1.2`）。
- 订阅 `MapChanged/CellChanged/RectChanged/BuildingsChanged` 置脏，`EnsureUpdated` 在每日结算首步（`OnDayPassed` 第一行）惰性重算。

---

## 10. 坊区生长（ZoneGrowthSystem + GrowthConfig）

居民自发建房成坊的完整链条（每日结算，在 `BuildableCells` 索引集内挑点，免全图扫描）：

1. **选址打分**（扫描半径 4m）：主路 +3 / 辅路 +2 / 河道 +1.5 / 邻居密度 +1.2×每栋（封顶 3 栋 = 3.6 分）/ **近王爷府 +8**（半径 32m 内随距线性衰减，`PrinceMansionConfig.SiteScore`）——可叠加（河边十字路口 = 主路+辅路+河道 分最高）。
2. **加权抽签**：达阈值 3 分的候选按 `分数^2` 加权抽签（权重 = `max(0.1, 分)^2`，向热闹地段集中但仍给小概率）；无达标者退而选可负担候选中分最高处。
3. **地价与建造**：地价 = `HouseBaseCost 20 + 5 × 分数`（好地段更贵）；资产不足者寄居店坊，攒够后再自建。
4. **小路环与布门**：住宅四周自动生成 1 格宽小路环（`LayLaneRing`，与外界路网连通）；`ComputeDoors` 布门——大门恒 1 个，后门数 = max(1, 占地格数/64)，按最小间距 2 格（切比雪夫）分散布置，凑不足再放宽。
5. **升级**：每日 0.02 概率，需完好度 ≥60 且吸引力达标（每级 ×1.2 系数），等级受里程碑限级（`Milestones.MaxHouseLevel`）；升级改变楼高外观（`BuildingsChanged` 只刷建筑层）。
6. **转业**：符合条件的路边住宅每日 0.03 概率转工商——占地 ≥6 平米且距主/辅路 ≤6m，按临路档位取分布：贴主路 → 商铺 0.5 / 工坊 0.3（余 0.2 更高级住宅）；贴辅路 → 工坊 0.4 / 商铺 0.1（余 0.5 住宅升级）；仅小路 → 工坊 0.15（余 0.85 住宅升级，不出商铺）。全城工商占比封顶 0.3（约十间住宅出两三家）。
7. **扩建与分家**：满员住户每月 0.15 概率触发拥挤事件——扩建（边长上限 8m，改实例占地并重布门）或成年子女分家迁出；住宅用地释放后由新家庭接手。

> 王爷府（official）不参与自发生长；「寄居 → 攒资 → 自建」是坊区扩张的主通道，玩家只需划好坊区（`SetZone`），民居生长全自动。

---

## 11. 政策与财政

### 11.1 税制（TaxDefs / TaxPolicy / TaxSystem）

**三税种模型**（`TaxPolicy` 纯数据随档序列化、`TaxSystem` 结算；批次五十六重写，**已废弃旧版四税种注册表**）。货币单位为**文**：

| 税种 | 名称 | 计税方式 |
|---|---|---|
| `land` | 土地税 | 每栋建筑按类型与等级的固定税基（`TaxSystem.BuildingTaxBase`，如民居 Lv1/2/3 = 10/25/50 文）× 土地税率，每月逐旬收缴，从住户/店主家庭公产实扣入官库 |
| `trade` | 商税 | 交易发生时由买方按成交额另付（税率见 `TradeTaxRate`），自动入账 |
| `poll` | 人口税 | 可选开关（默认关）；开启时从雇工每旬薪资扣 20%，每月降幸福 |

- **税率区间（引用 `EconomyConfig`）**：土地税 **1%~10%（默认 3%）**、商税 **2%~15%（默认 5%）**；人口税固定 **20%**（开/关）。
- **档位兼容**：`LandTaxLevel`/`TradeTaxLevel`（0-3，免征/轻/中/重）仅为旧 UI 兼容，底层全部换算为百分比率。
- **民怨**：土地税高于重税线（6%）触发「重敛伤民」、商税高于重税线（10%）触发「关市苛征」，每月各扣全城成人兴致 −2；人口税开启每月扣 −1.5（关闭后每月恢复 +0.5）。
- 月度实收入官库并记账（`Ledger`）。

### 11.2 官库账本（Ledger）

- 本月 / 上月两张分类流水字典（收入正、支出负），每月 `Ledger.Rotate()` 把本月转上月（v3 起随档保存）。
- 记项示例：晋级拨款（+）、王爷府开基（+）、市易收入（+）、修缮料钱（−）、税收（+）、铺路架桥造价（−）、研习经费（−）等；`FinancePanel` 按分类展示本月/上月对比。

### 11.3 财政循环总览

| 收入 | 支出 |
|---|---|
| 三税种月度实收（土地/商/人口） | 道路/桥梁每延米造价（主路 18 / 辅路 10 / 桥 30 文） |
| 里程碑晋级拨款（150~3000 文） | 修缮匠料钱（100 文/匠/月） |
| 市易收入（无雇工官营铺面） | 主动科技研习经费（逐日从官库拨付） |
| 王爷府开基拨款（100000 文一次性） | 官粮日耗（0.05 份/人/旬，朝廷粮饷 3 份/人/月补给） |

---

## 12. 里程碑与科技

### 12.1 城市里程碑（Milestones）

> 注：里程碑体系自批次起由 5 级扩展为 **8 级**（村落→乡里→集镇→县城→郡城→州城→府城→京城），人口阈值与拨款以下表为准。货币单位见 §8.5（内部以铜钱「文」结算，白银/万两仅用于显示，黄金已废除）。

| 级 | 名称 | 晋级人口 | 拨款（文） | 住宅限级 | 全城兴致 |
|---|---|---|---|---|---|
| 0 | 村落 | 0 | 0 | 1 | — |
| 1 | 乡里 | 8 | 150 | 1 | +3 |
| 2 | 集镇 | 20 | 300 | 2 | +4 |
| 3 | 县城 | 45 | 600 | 2 | +5 |
| 4 | 郡城 | 90 | 1000 | 3 | +6 |
| 5 | 州城 | 160 | 1500 | 3 | +8 |
| 6 | 府城 | 260 | 2200 | 3 | +9 |
| 7 | 京城 | 400 | 3000 | 3 | +10 |

- 条件达成即晋级（一日至多晋一级；读档大城逐日补晋）：官库记账拨款、全城成人兴致小涨、广播 `MilestoneReached`（建造菜单刷新解锁、HUD 弹报）。
- 预留融合口：`MoneyRequired`（方案 b，官库钱）与 `RequiredBuildingId`（方案 c，标志建筑）——`Reached` 判定已包含三项，填值即生效。

### 12.2 分级需求（TierNeeds）

| 需求 | 门槛 | 候选货品 | 每人每旬 | 断供兴致扣减（每旬） |
|---|---|---|---|---|
| 烧饼 | 集镇（2） | 烧饼（flatbread） | 0.0467 份 | −0.4 |
| 薪炭 | 郡城（4） | 薪炭（charcoal） | 0.035 份 | −0.4 |
| 副食 | 州城（5） | 果品（fruit） | 0.07 份 | −0.5 |
| 酒馔 | 府城（6） | 酒（wine）/ 腌货（cured） | 0.035 份 | −0.5 |
| 器用 | 京城（7） | 木器（timber）/ 铁器（ironware） | 0.0187 份 | −0.3 |

> 候选货品任一满足即可（按序尝试），家中无存则上市购买——集镇起的烧饼需求为工坊打开早期销路，郡城起的薪炭、州城起的副食、府城起的酒馔、京城起的器用逐级为加工链补上消费端出口（数据驱动表，增改直接改表）。

候选货品任一满足即可（按序尝试），家中无存则上市购买——**州城起的成品需求为加工链补上消费端出口**（数据驱动表，增改直接改表）。

### 12.3 科技（TechDefs / TechSystem）

- **加载**：`res://data/techs.json` 基础定义 + `mods/<名>/techs.json` 按目录名升序合并覆盖（机制同建筑定义）。
- **两种解锁模式**：
  - `passive`：条件（里程碑等级 + 前置科技全研成）达成后**自动研成**，不花钱；
  - `active`：玩家在研习面板主动立项，逐日从官库拨研习经费（总经费逐日均摊），天数攒满研成；官库断供则暂停推进。
- **效果**：`Effects` 效果键 → 加成值（`harvest` 收成 / `craft` 加工 / `tax` 税收 / `mint` 铸币），由 `GameState.TechFactor(key) = 1 + Σ加成` 汇总供各系统取用；mod 新科技可复用现有效果键，新效果键需代码侧接线。
- 每日结算：被动科技自动研成 + 主动项目逐日推进（`TechSystem.TickDay`）。

---

## 13. 存档系统

### 13.1 存储介质（LightningDB）

- 一份存档一个库：`saves/<slot>/data.mdb`（一个环境一个目录）；保存时在**单个写事务**内写入全部 key 后提交——要么全部落盘要么全部回滚，天然原子。
- key 划分：`meta / world / map / buildings / citizens / families / plants / animals`——便于未来 mod 追加自己的数据段。
- `FormatVersion 25`（早期开发不做跨版本兼容，版本不符直接拒读）；环境映射 32MB。

### 13.2 序列化模型（SaveData）

- JSON（`IncludeFields`，Citizen/Family 用公共字段）；`SaveMeta` 记录版本/日期/城市名/存档名/真实时间戳。
- 地图紧凑存储（一维索引 y×Size+x）：`RoadCells+RoadKinds`、`ZoneCells+ZoneTypes`、`WaterCells+WaterFlow+WaterLevels`（v19/v21 逐格水位）、`BridgeCells`；高度场为 uint16 量化灰度 blob（`HeightMin`/`HeightStep`，替代旧整数层稀疏表）。
- 建筑 DTO：DefId/坐标/等级/完好度/建造日期/专营货品/统一库存/农时计数/废弃标志/实例占地（扩建）。

### 13.3 保存流程

- **异步原子**（`SaveAsync`）：主线程快照 + 序列化 → **后台线程写盘**（免卡帧）→ 完成回调经 `CallDeferred` marshal 回主线程再碰 HUD；`_asyncSaving` 防并发写同一库。
- 槽位：`QuickSlot "quick"`（F5 快速）/ `AutoSlot "autosave"`（自动）/ `SlotFor(名)` 命名槽（同名覆盖，非法字符替换为下划线）。
- **自动保存**：游戏未暂停时累计真实时间，达 `AutoSaveMinutes`（默认 5 分钟，0 关闭）触发。

### 13.4 读档流程

版本校验 → 重建 `GameState`（恢复 Id 自增器、税档、账本、里程碑、科技、公告）→ 恢复时钟日期 → 广播 `GameLoaded` 全量刷新（渲染器按 `MapChanged` 全量重建）。**读档重建不触发 `BuildingPlaced`**——王爷府开基拨款与随迁夫妻只在实时放置时发生一次（防重复）。

### 13.5 用户设置（GameSettings）

`settings.cfg`（游戏根目录，绿色便携）：分辨率（默认 1280×720）/ 全屏 / 垂直同步 / 自动保存间隔 / 无限钱开关；`Apply()` 应用窗口与显示模式，`stretch=canvas_items + aspect=expand` 保证窗体缩放自适应。

---

## 14. 相机与交互

### 14.1 RTS 轨道相机（RtsCameraRig）

- **平移**：WASD + 屏幕边缘 8px 触发带；速度 = 当前距离 × 0.9（拉近慢、拉远快）；边界 ±552m。
- **缩放**：滚轮 ×0.87 / ×1.15，距离 2.5~450m（最远约览半城，省渲染资源）；远裁剪面 2000m（深度精度更好）。
- **旋转**：Q/E 或中键拖动，偏航无限制、俯仰 −1.45 ~ −0.35（近乎垂直俯视 ~ 低角度平视）。
- **防穿山**：镜头低于脚下地形 + 1.5m 净空时抬升整个云台——上抬立即到位（防穿山不能慢），回落渐进（免下山时镜头磕地感）。

### 14.2 键位总表

| 键 | 功能 |
|---|---|
| 空格 | 暂停 / 恢复（`GameClock._Input` 优先于 UI，文本框聚焦时放行防误触） |
| 1 / 2 / 3 | 1x / 2x / 4x 速度 |
| W / A / S / D | 平移 |
| Q / E | 旋转（或中键拖动） |
| 滚轮 | 缩放 |
| F5 / F9 | 快速存档 / 快速读档（`Main._UnhandledKeyInput`） |
| ESC | 暂停菜单（GameMenu） |

### 14.3 建造与点选交互

建造交互见 6.8（悬停高亮/放置居中/画笔拖拉/拆除）；点选居民广播 `CitizenSelected`（-1 取消），绘制目标路线；`Hud.ShowCellInfo` 统一信息提示条。

---

## 15. UI 层

### 15.1 面板总表

| 面板 | 职责 |
|---|---|
| `Hud` | 主 HUD 容器：顶栏、建造菜单、检查面板、公告栏、科技/新闻弹报、信息提示条 |
| `TopBar` | 金钱/官粮、人口、里程碑名、日期与时辰、时间流速按钮 |
| `BuildMenu` | 建筑分类列表（解锁条件显示），点击进入放置模式 |
| `InspectPanel` | 点选建筑/居民详情：建筑（等级/完好度/库存/岗位/居住）、居民（身份/年龄/家庭/资产/需求面板/年龄履历） |
| `FinancePanel` | 官库账本：本月/上月分类流水（收入/支出） |
| `PolicyPanel` | 四大税种档位调节（免征~重税） |
| `NewsPanel` | 公告栏（数据层封顶 200 条，读档续接旧事） |
| `TechPanel` | 科技面板：被动（条件显示）/ 主动研习立项与进度 |
| `GameMenu` | 主菜单 / 暂停菜单：新游戏、命名存档、读档列表、返回标题 |
| `LoadingScreen` | 后台世界生成的进度轮询与阶段文案（「初入汴京 · 正在生成世界」） |

### 15.2 流程要点

- **主菜单**：`GameMenu` 最后加入场景树并自行暂停全树（`ProcessMode=Always` 的 LoadingScreen 不受影响）；ESC 呼出暂停菜单；返回标题 = 重载当前场景（`ReloadCurrentScene`）。
- **新游戏**：重置 `GameState`（复用 Defs）→ 整树暂停 → 挂 LoadingScreen 后台生成 → 完成回调恢复暂停、归零日历、广播 `MapChanged/ZonesChanged/StatsChanged/GameLoaded` 全量刷新。
- **通知体系**：`ShowCellInfo` 提示条（建造反馈/保存结果）、`NewsPosted` 公告栏、`MilestoneReached`/`TechUnlocked` HUD 弹报。

---

## 16. 配置总表（scripts/configs/）

**调参纪律：** 所有常量与公式集中于此，业务系统只引用不硬编码；每个配置类头注释写明业务归属（哪个系统使用）。

| 配置类 | 业务归属 | 关键数值 |
|---|---|---|
| `WorldConfig` | 道路/桥梁造价与宽度、抬升与地基、开局资源、记录上限 | 主路 18 文/延米宽 4、辅路 10 文宽 2、桥 30 文宽 4；小路 10 文/格；路面抬升 0.1m、地基深 1m；建筑抬升 0.1m、地基深 2m；拱顶抬升 2m、桥体厚 0.2m、引桥 3 格；白边 32m；开局钱 100000/粮 500；履历 40 条、公告 200 条 |
| `TerrainConfig` | 高度场生成与坡度规则 | 步高 0.5m、坡度 30°、垫基高差 1m；海拔 [-3, 64]m；草图 128² 映射 8m/格；大势 6m；峰 10~14 座 30~62m（半径 60~120）；山区带 440m、避心圆 280m；独立山 3~6 座 3~7m；山脊 2 链/鞍部 0.62/半宽 46m；侵蚀 25 万滴 + 6 千滴；热侵蚀安息角 ≈33°；采集豁免 4.5m |
| `WaterConfig` | 河流湖泊生成 | 水位下限 0、平滑窗 21；河 4~6 条；源头宽 6 → 河口宽 20（支流 12）；外扩 0.7m；河床下压 0.25/1.0m、满深距岸 5 格；湖 1~2 座、成湖水位 ≤1.5m、容差 1.2m、半径 30~52、湖缘扭曲 0.42 |
| `TimeConfig` | 日历与作息 | 24h/12 天/12 月；1 游戏时 ≈0.833 真实秒；上工 6~18 时；轮休 5 天 |
| `EconomyConfig` | 货担/价差/消耗/产能/堆容量 + 家计/修缮/税制（单位：文） | 一担 5 份、买价 ×1.5；官粮 0.05/人/旬、朝廷粮饷 3/人/月、田赋 0.1×粮收成；口粮 0.2333、柴 0.07、水 0.2333 份/人/日；断炊 −1/缺柴 −0.5 兴致每旬；收成限 8 堆、堆容 40 份；加工 1.8667 份/工/旬、买半径 160；家计 200 文/人/月；老化 0.7/月、匠修 25/月/料钱 100、集资修 5/月/摊派 15；土地税 1~10%(默认3%)、商税 2~15%(默认5%)、人口税 20% 开关、重税民怨 −2；安家银 100000、王爷月俸 8000、创业资产门槛 8000/技能120 |
| `PopulationConfig` | 迁入/婚配/生育/交友/迁出（频率为每旬） | 迁入 0.2333/旬（四类流民权重 归民0.35/寓商0.30/散勇0.20/客士0.15，资产 800~3000 / 6000~12000 / 300~1500 / 0~300 文）；单身男 0.6；婚配 0.0233/旬（候选 8 人）、生育 0.007/旬、交友 0.0233/旬；无家 6 月迁出；拥挤 0.15/月；自建门槛 5000、分家公产 1500、空房继承门槛 1000/过户费 600、年龄 18~36；富裕 WealthEase 40000 |
| `LifeConfig` | 年龄门槛与死亡曲线 | 成年 16、老年 60、婚配上限 50、生育上限 45；退休 50/家族产业 60；富裕线 `WealthyPerCapitaAssets 20000`（退休分流）；年死亡率 = 0.005 + 0.03×e^((age-55)/8)；饥荒月附加 0.03；健康放大上限 4；最大寿数 120 |
| `GrowthConfig` | 坊区生长与吸引力 | 小路环 1 格；基价 20、地价 5/分；打分：主路 3/辅路 2/河道 1.5/邻居 1.2×3 栋；阈值 3、抽签幂 2；升级 0.02/日（完好度 ≥60、吸引力 1.2/级）；工商占比封顶 0.3；扩建上限 8m；转业 0.03/日（占地 ≥6、距路 ≤6，分布见 10 章）；吸引力：主 1.0/辅 0.4 ÷16、半径 12；后门 = 占地格数/64、门距 2 |
| `MovementConfig` | 移动速度与寻路权重 | 基础 5 m/s；脱路 ×0.35；转向 10 rad/s；路面系数 主 1.2/辅 1.0/小 0.7/桥 1.0；寻路权重 = 主路速度 ÷ 该路面速度 |
| `VillagerConfig` | 表现层参数 | 缩放 0.25、儿童 0.4 起；代理上限 300；分离半径 0.9/推力 3；疲劳阈值 80、兴致阈值 25；车道偏移 0.45；斧伤 25（0.2 份柴/血）；储备目标 食 3/柴 1/水 3；采集半径 64；主妇采购 0.6、老人闲逛 0.5 |
| `PlantConfig` | 植被生成与消长 | 全图上限 8800；密度阈值 0.55、密核 0.2 棵/格、初始目标 5000；散播 0.03/月/4m；挂果 0.1/日、落果 0.1、果上限 3；恢复延迟 3 天/2/日；成熟 12 月；血量 20+80 渐进（半饱和 24 月） |
| `WildlifeConfig` | 动物种群 | 种群 = min(240, 树数/15)；月刷新 0.5（半径 24m 内无动物）；繁育 0.12/月、自然死亡 0.01/月；日游走 4m |
| `PrinceMansionConfig` | 王爷府 | DefId `prince_mansion`；开基 100000 文/400 粮 + 货品（粮120/木80/果40/盐30/矿30）；随迁 3 对 20~27 岁夫妻（公产 40000）；选址加分 8/半径 32m |
| `CameraConfig` | 相机 | 距离 2.5~450、远裁剪 2000；俯仰 −1.45~−0.35；边缘 8px；净空 1.5m |

---

## 17. 扩展性与 mod

### 17.1 数据驱动清单

| 内容 | 载体 | 扩展方式 |
|---|---|---|
| 建筑定义 | `data/buildings.json` | 增改建筑/产物/岗位/加成直接改表 |
| 科技定义 | `data/techs.json` | 增改科技/效果键（新效果键需代码接线） |
| 税种 | `TaxDefs.All`（代码注册表） | mod 可注册新税种，存档只存档位不受影响 |
| 里程碑 / 分级需求 | `Milestones.Levels` / `TierNeeds` | 数据驱动表，填值即生效 |

### 17.2 mod 机制

- 目录：`mods/<模组名>/`（游戏根目录，绿色便携；按目录名升序加载）。
- 覆盖合并：`buildings.json`、`techs.json` 以 def id 为键合并——基础定义先载，mod 后载覆盖/追加；解析失败仅警告不崩游戏。
- 存档 key 分段设计（meta/world/map/…）为 mod 追加自己的数据段预留位置。

### 17.3 预留扩展口

- `Citizen.Extra` / `BuildingInstance.Extra` 字典：mod 与后续系统（教育/官职/声望）扩展位。
- 枚举新值只能尾部追加（防老档错位）；存档结构不兼容时提升 `FormatVersion`（旧档拒读）。
- 健康系统预埋：`Health` 字段 + `HealthMortalityFactor` 放大系数已就位，接入即自动生效。
- 里程碑方案 b/c 融合口：`MoneyRequired` / `RequiredBuildingId` 填值即生效。
- 变质/鲜度：`Inventory.AgeOneDay` 计龄已跑通，效果在仓储层挂接即可。

---

## 17.5 当前实现补充（批次五十五之后新增 / 调整）

> 以下为「截至批次五十五」冻结快照之后的关键演进，与 §1–§17 互补。逐批次细节见 `CHANGELOG.md`。

### 17.5.1 美术进阶：宋风原语 + 骨骼村民 + glb 资产管线

- **宋风建筑原语**（`scripts/render/BuildingModelFactory.cs`）：用 `MultiMeshInstance3D` + 基础几何（Box/Prism/Cylinder/Sphere）程序化拼装亭台楼阁剪影，替代早期纯色方块；`MakePreview(def, groundY, scale)` 同时供建造预览复用，保证「预览 = 实际剪影」。
- **骨骼村民**（`scripts/agents/CitizenAgent.cs` + `CitizenAnim.cs`）：
  - **关键事实：Godot 4.7 的 C# 没有 `Bone3D` 类**。骨骼全部走 `Skeleton3D` 索引 API——`AddBone(name)`/`SetBoneParent`/`SetBoneRest(idx,T)`/`SetBonePose(idx,T)`/`FindBone(name)`；网格部件经 `BoneAttachment3D`（`BoneName`）挂到骨头，其 Transform 跟随骨头（含 pose）。
  - 层级：`root → spine → { head, armL, armR }`；rest 纯平移（腰/颈/臂位置）、pose 纯旋转 → 绕骨头原点旋转即理想关节枢轴。
  - `CitizenAnim.ApplyPose(state, phase, skel, 5 个骨索引)` 纯函数姿态库，每帧 `SetBonePose` 驱动（**逐帧代码驱动，非 AnimationPlayer**，便于 300 个 agent 的性能）；4 态：`Idle` / `Walk` / `Carry` / `Working`。
- **glb 建筑资产管线**（`scripts/build/BuildingAssetLoader.cs` + `BuildingDef.ModelPath`/`HasModel`）：
  - 静态缓存 `Dictionary<string,PackedScene>`；`LoadScene(path)` 空路径/失败返回 null；`FitAndPlace` 递归遍历 `VisualInstance3D` 子节点取 `GetAabb()`（Node3D 无 GetAabb）+ 父链 `RelativeTo` 合成局部 AABB，按 `Min(sx, Min(sy, sz))` 均匀缩放、底座落 `baseY`。
  - `GridRenderer.RebuildBuildings` 优先加载 glb，失败自动回落 `BuildingModelFactory` 原语；`CleanupStaleAssetInstances` 回收过期实例。

### 17.5.2 经济与平衡关键修复（摘要）

- **钱流闭环**（批次七十六–七十九、八十七）：除朝廷直属机构外，所有金钱在官库↔村民家庭公产间循环；建造费/铺路架桥费/修缮料钱发当日无业者（营造工钱），生活开销/土地税/修缮摊派入官库，绝户公产折入官库；官营售货入官库、商税落地。
- **官粮补给链**（批次七十八）：日耗 0.2→0.05 份/人/旬（赈济定位）；新增朝廷粮饷 3 份/人/月入官仓，农田田赋 `GrainTaxShare=0.1` 为额外增收，饥荒不再必然。
- **农田收入修复**（批次八十五）：farmland 加 `salary:800`、`yieldPerWorker` 30→50；农民发固定工钱（`official||field`）。
- **民居转业卡点**（批次八十三）：`ConvertMinArea` 6→4；在业者可创业、无职谋生半速涨经验；烧饼需求里程碑 3→2。
- **空房低价继承**（批次八十六）：`InheritVacantHomes`，空置 house/mansion 由寄居家庭低价过户（house 1000/600、mansion 3000/1500）。

### 17.5.3 时间体系定稿（批次九十一）

- 日历：每月 **3 旬**（上/中/下旬），一旬 ≈ 1 现实分钟，一游戏年 = 36 旬 = 36 分钟；`DaysPerMonth` 7→3、日频概率整体 ×7/3（年流入量不变）。
- NPC 一年两岁：`Citizen.AgeYears` 独立字段，1 月/7 月各 +1；grow 动画改用 `AgeYears/AdultAgeYears`（16 岁成年 ≈ 8 游戏年 ≈ 4.8 现实小时）。
- 存档 `FormatVersion` 24→25（旬历语义 + AgeYears 新字段）。

### 17.5.4 存档依赖回退（批次九十）

- LightningDB 0.23.0 改变文件布局致旧档不可读 → 回退 **0.22.0**（勿再升级，升级前须验证旧档兼容性）。

### 17.5.5 模块增减（相对冻结快照）

- 新增：`agents/CitizenAnim.cs`（骨骼姿态库）、`build/BuildingAssetLoader.cs`（glb 管线）、`render/BuildingModelFactory.cs`（宋风原语）、`render/ScrollBackdrop.cs`/`RenderLayers.cs`、`sim/RecipeDef.cs`（配方三级化）、`configs/GeneticsConfig.cs`（技能遗传）。
- 目录调整：`objects/` 自 `Obj` 派生的实体，`render/` 收纳宋风/卷轴表现；`map/` 仍含各 `MultiMesh` 渲染器。

## 18. 附录

```
GameState（唯一真源）
├── MapGrid（1024² Cell + HeightField 1025²）── Cell: 水面/道路/桥梁/树/建筑/坊区/吸引力
├── RoadNetwork（寻路图，权重=旅行时间）
├── Buildings：BuildingInstance（Def 引用 + 占地/等级/完好度/专营/库存/农时）
├── Citizens：Citizen（生命周期个体）── Family（公产/成员）
├── Plants：PlantObj（树龄/血量/挂果）── Animals：AnimalObj ── Piles：ItemPileObj
├── Money/Food/Ledger/Taxes/MilestoneLevel/TechsUnlocked/Research
└── News（公告栏）
EventBus ← 模拟系统层（12 个 Tick 系统，无长期状态）→ 表现层（渲染器/代理/相机/UI）
configs/*（只读参数） + data/*.json + mods/*（静态定义）
```

### 18.2 术语表

| 术语 | 含义 |
|---|---|
| 格 / Cell | 1m×1m 的地表最小单元（世界 1024×1024 格） |
| 坊区 | 玩家划定的可建设区（`Zone=Buildable`），居民只在其中自建住宅 |
| 官库 | 玩家财政：Money（文，铜钱）+ Food（官粮份）+ Ledger 账本 |
| 公产 / 私产 | 家庭共享资产 / 居民个人 Money |
| grown 建筑 | 居民自发长出的住宅/商铺/工坊（区别于 official 官营建筑） |
| 一担 | 居民单次搬运量（5 份） |
| 引桥 | 桥面两端向岸上陆地路格延伸的过渡坡（桥面高渐降到岸路面高） |
| 里程碑 | 村落→集镇→县城→州城→京城（按人口晋级） |
| 分级需求 | 里程碑解锁的居民日常消耗（副食/酒馔/器用） |
| 吸引力（宜居度） | 逐格数值：道路/建筑/王爷府正负覆盖之和，驱动选址与升级 |

### 18.3 相关文档

| 文档 | 内容 |
|---|---|
| [CHANGELOG.md](CHANGELOG.md) | 单一权威迭代记录（批次 25→91，逐批次调整要点） |
| [CODEMAP.md](CODEMAP.md) | 业务 ↔ 代码对照表（系统/概念 → 文件/类/方法/configs，AI 快速寻址） |
| [ECONOMY.md](ECONOMY.md) | 经济系统专项规格（货币/注入/就业/税收/交易链/物价，合并自 `.qoder` 需求文档） |
| [GAME_DESIGN.md](GAME_DESIGN.md) | 玩法设计与第一阶段开发计划原案（汴京盛卷框架） |
| [RULES.md](RULES.md) | 项目开发规范（技术栈/阶段兼容/注释率/全英文/停问规则/常量归口） |
| `README.md` | 项目入口说明 |
