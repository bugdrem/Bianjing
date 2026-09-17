# 业务 ↔ 代码对照表（Code Map）

> 目的：让 AI / 开发者在「业务概念」与「代码实体」之间快速双向寻址。本文为速查索引，细节以 `DESIGN.md`（架构与实现）与 `CHANGELOG.md`（迭代记录）为准。
>
> 约定：所有逻辑用 **C#**；常量集中在 `scripts/configs/`；静态数据在 `data/*.json`；早期开发阶段，**功能实现或重构无需考虑旧版本兼容**（枚举新值尾部追加、存档 `FormatVersion` 不符直接拒读即可）。

## 0. 顶层入口与装配

| 业务概念 | 代码实体 | 位置 |
|---|---|---|
| 主场景 / 根节点 | `Main`（`_Ready`/`EnterWorld`/`_Process`） | `scripts/Main.cs` |
| 世界 / 游戏状态唯一真源 | `GameState.I` | `scripts/core/GameState.cs` |
| 事件总线（解耦模拟层与表现层） | `EventBus`（+ `Raise*`/`Reset`） | `scripts/core/EventBus.cs` |
| 时钟 / 日历 / 倍速 | `GameClock` + `TimeConfig` | `scripts/core/GameClock.cs` · `configs/TimeConfig.cs` |
| 用户设置（绿色便携 cfg） | `GameSettings` | `scripts/core/GameSettings.cs` |
| 存档（LMDB 异步原子） | `SaveService` / `SaveData` | `scripts/save/SaveService.cs` · `SaveData.cs` |
| 账本（本月/上月流水） | `Ledger` | `scripts/core/Ledger.cs` |

## 1. 地图 / 地形 / 渲染

| 业务概念 | 代码实体 | 位置 |
|---|---|---|
| 网格 + 单元 | `MapGrid` / `Cell` | `scripts/map/MapGrid.cs` · `Cell.cs` |
| 顶点高度场（灰度地图） | `HeightField` | `scripts/map/HeightField.cs` |
| 地形生成管线（草图→侵蚀→主峰补回→水系） | `WorldGenerator` / `WorldSketch` / `HydraulicEroder` / `ValueNoise` | `scripts/map/` |
| 水系汇流求解（填洼/D8/汇流累积） | `FlowRouter` + `FlowField` | `scripts/map/FlowRouter.cs` |
| 河网走线（选源/蛇曲/河口外推） | `RiverNetwork` + `RiverPath` | `scripts/map/RiverNetwork.cs` |
| 湖泊生成（洼地塘 + 三档选址湖） | `LakeGenerator` + `LakeShape` | `scripts/map/LakeGenerator.cs` |
| 河/湖刻盘（水位/河床下压） | `RiverGenerator.BuildWaterSystem` + `WaterConfig` | `scripts/map/RiverGenerator.cs` · `configs/WaterConfig.cs` |
| 植被生成 | `TreeGenerator` + `PlantConfig` | `scripts/map/TreeGenerator.cs` · `configs/PlantConfig.cs` |
| 道路寻路图 | `RoadNetwork` + `MovementConfig` | `scripts/map/RoadNetwork.cs` · `configs/MovementConfig.cs` |
| 渲染协调器（脏标 + 预算仲裁 + 裙板） | `GridRenderer` | `scripts/map/GridRenderer.cs` |
| 渲染六图层（地形/水/路/植被/建筑/叠加） | `TerrainLayer`/`WaterLayer`/`RoadLayer`/`VegetationLayer`/`BuildingLayer`/`OverlayLayer`（基类 `MapLayer`）+ `ChunkGeometryBuilder`/`LayerKit` | `scripts/map/layers/` |
| 动物 / 物资堆 / 屋内库存 渲染 | `AnimalRenderer` / `PileRenderer` / `BuildingStockRenderer` | `scripts/map/` |
| 货品配色 | `GoodsColors` | `scripts/map/GoodsColors.cs` |
| 卷轴背景 | `ScrollBackdrop` / `RenderLayers` | `scripts/render/` |
| **宋风建筑模型（原语）** | `BuildingModelFactory`（含 `MakePreview`） | `scripts/render/BuildingModelFactory.cs` |
| **glb 资产管线（加载/回落）** | `BuildingAssetLoader`（`LoadScene`/`FitAndPlace`） | `scripts/build/BuildingAssetLoader.cs` |
| 地形 / 坡度 / 垫基规则 | `TerrainConfig`（步高/坡度/垫基/采集豁免） | `configs/TerrainConfig.cs` |
| 道路 / 桥梁造价与宽度 | `WorldConfig`（主/辅/桥单价与宽、抬升/地基） | `configs/WorldConfig.cs` |

## 2. 建筑 / 建造

| 业务概念 | 代码实体 | 位置 |
|---|---|---|
| 建筑静态定义（数据驱动 + mod） | `BuildingDef` + `data/buildings.json` | `scripts/build/BuildingDef.cs` |
| 建筑实例（运行时） | `BuildingInstance`（继承 `Obj`） | `scripts/build/BuildingDef.cs` · `objects/Obj.cs` |
| 放置 / 拆除 / 道路画笔 / 门 | `GameState.PlaceBuilding` / `DemolishAt` / `PlaceRoadStamp` / `PlaceBridgeStamp` / `ComputeDoors` | `scripts/core/GameState.cs` |
| 建造交互 / 放置预览 | `BuildController` + `PlacementValidator` | `scripts/build/` |
| 建筑目录（23 项）与数值 | `data/buildings.json` | `data/` |
| 王爷府（开局首建 / 开基拨款 / 随迁） | `PrinceMansionConfig` | `configs/PrinceMansionConfig.cs` |

## 3. 人口 / 社会

| 业务概念 | 代码实体 | 位置 |
|---|---|---|
| 居民个体（纯数据，可序列化） | `Citizen`（年龄/家庭/职业/资产/活动/履历） | `scripts/citizens/Citizen.cs` |
| 家庭（共享公产） | `Family` | `scripts/citizens/Family.cs` |
| 生命周期（迁入/婚育/生死/分家） | `LifecycleSystem` + `PopulationConfig` + `LifeConfig` | `scripts/citizens/` · `configs/` |
| 就业 / 退休 / 家计 | `JobSystem` | `scripts/citizens/JobSystem.cs` |
| 技能遗传 / 变异 | `GeneticsConfig` | `configs/GeneticsConfig.cs` |
| 姓名生成 | `NameGenerator` | `scripts/citizens/NameGenerator.cs` |
| 坊区自发生长（选址/升级/转业/扩建） | `ZoneGrowthSystem` + `GrowthConfig` | `scripts/zone/ZoneGrowthSystem.cs` · `configs/GrowthConfig.cs` |
| **居民 3D 表现（骨骼村民）** | `CitizenAgent`（挂 `Skeleton3D` + `BoneAttachment3D`） | `scripts/agents/CitizenAgent.cs` |
| **骨骼姿态库（4 套动画：Idle/Walk/Carry/Working）** | `CitizenAnim.ApplyPose`（索引式 `Skeleton3D` API） | `scripts/agents/CitizenAnim.cs` |
| 代理管理（上限 300 / 贴地 / 决策） | `AgentManager` + `VillagerConfig` | `scripts/agents/AgentManager.cs` · `configs/VillagerConfig.cs` |

> 注：Godot 4.7 的 C# **无 `Bone3D` 类**；骨骼只用 `Skeleton3D` 索引 API（`AddBone`/`SetBoneParent`/`SetBoneRest`/`SetBonePose`/`FindBone`），网格部件经 `BoneAttachment3D`（`BoneName`）挂载，逐帧代码驱动 pose（非 AnimationPlayer）。

## 4. 经济 / 生产

| 业务概念 | 代码实体 | 位置 |
|---|---|---|
| 货币换算与显示 | `CurrencyConfig` / `CurrencyHelper` | `scripts/configs/CurrencyConfig.cs` · `core/CurrencyHelper.cs` |
| 货品 / 基价 / 配方 | `Goods`（+ `Goods.Recipes`） | `scripts/sim/Goods.cs` |
| 定价三出口（基价 / 零售 / 批发）+ 库存联动倍率 | `Goods.PriceOf` · `Goods.RetailPrice` · `Goods.BuyerPrice` · `Goods.StockPriceFactor` · `Goods.FillRateOf` | `scripts/sim/Goods.cs` · 档位见 `configs/EconomyConfig.cs` |
| 配方三级化 | `RecipeDef` + `Goods.InputsAt/FuelAt/ByproductAt` | `scripts/sim/RecipeDef.cs` |
| 统一仓储（建筑仓/背包/地面堆） | `Inventory` / `GoodsStack`（按属性分批，同档才并堆） | `scripts/sim/Inventory.cs` |
| **物品时效系统**（新鲜度软轴 + 有效期硬轴） | `TimedState` / `TimedRules` / `TimedRegistry` / `TimedModifier` / `TimelinessSystem` | `scripts/sim/timeliness/` · 阈值见 `configs/TimelinessConfig.cs` · 设计见 `specs/TIMELINESS.md` |
| 时效环境修正源（位置/季节/临水，可注册扩展） | `ITimedModifier` 实现类 + `TimedRegistry.Register` | `scripts/sim/timeliness/TimedRegistry.cs` |
| 家庭消费 / 市场 / 分级需求 | `GoodsSystem` + `EconomyConfig` | `scripts/sim/GoodsSystem.cs` · `configs/EconomyConfig.cs` |
| 商税代扣（买方为建筑） | `GameState.PayFromBuildingTaxed` | `scripts/core/GameState.cs` |
| 加工链（工坊专营） | `CraftingSystem` | `scripts/sim/CraftingSystem.cs` |
| 官库 / 月俸 / 朝廷粮饷 / 开基 | `EconomySystem` | `scripts/sim/EconomySystem.cs` |
| 老化 / 修缮（官修 + 私宅集资） | `MaintenanceSystem` | `scripts/sim/MaintenanceSystem.cs` |
| 农田（耕种区 / 田主 / 两熟） | `FarmlandSystem` + `FarmlandConfig` | `scripts/sim/FarmlandSystem.cs` · `configs/FarmlandConfig.cs` |
| 中央需求账本（pull 被动参考） | `DemandLedger` / `DemandSystem` | `scripts/sim/DemandLedger.cs` |

## 5. 政策 / 财政 / 里程碑 / 科技

| 业务概念 | 代码实体 | 位置 |
|---|---|---|
| 三税种模型（土地/商/人口） | `TaxPolicy` / `TaxSystem` | `scripts/policy/` |
| 税率区间 / 家计 / 产能参数 | `EconomyConfig`（引用 `LandTaxRateHeavy`/`TradeTaxRateHeavy`） | `configs/EconomyConfig.cs` |
| 城市里程碑（8 级） | `Milestones.Levels` / `TierNeeds` | `scripts/core/Milestones.cs` |
| 科技（passive/active + 效果键） | `TechDef` / `TechSystem` + `data/techs.json` | `scripts/tech/` · `data/` |

## 6. 宜居度 / 吸引力

| 业务概念 | 代码实体 | 位置 |
|---|---|---|
| 逐格吸引力场（增量维护） | `DesirabilitySystem` + `GrowthConfig` | `scripts/sim/DesirabilitySystem.cs` · `configs/GrowthConfig.cs` |

## 7. 自然生态

| 业务概念 | 代码实体 | 位置 |
|---|---|---|
| 树木生长 / 挂果 / 散播 | `PlantGrowthSystem` + `PlantObj` | `scripts/sim/PlantGrowthSystem.cs` · `objects/Obj.cs` |
| 野生动物种群 | `WildlifeSystem` + `AnimalObj` + `WildlifeConfig` | `scripts/sim/WildlifeSystem.cs` · `configs/WildlifeConfig.cs` |

## 8. 相机 / 交互 / UI

| 业务概念 | 代码实体 | 位置 |
|---|---|---|
| RTS 轨道相机 | `RtsCameraRig` + `CameraConfig` | `scripts/camera/RtsCameraRig.cs` · `configs/CameraConfig.cs` |
| 建造菜单 / 分区 | `BuildMenu` | `scripts/ui/BuildMenu.cs` |
| 顶栏（钱/粮/人口/日期/倍速） | `TopBar` | `scripts/ui/TopBar.cs` |
| 检查面板（建筑/居民/家庭/田/树） | `InspectPanel` | `scripts/ui/InspectPanel.cs` |
| 官库账本面板 | `FinancePanel` | `scripts/ui/FinancePanel.cs` |
| 税率调节面板 | `PolicyPanel` | `scripts/ui/PolicyPanel.cs` |
| 公告栏 | `NewsPanel` / `NewsItem` | `scripts/ui/` · `core/NewsItem.cs` |
| 科技面板 | `TechPanel` | `scripts/ui/TechPanel.cs` |
| 主菜单 / 暂停 / 随机地图预览 | `GameMenu` | `scripts/ui/GameMenu.cs` |
| 加载画面（后台生成轮询） | `LoadingScreen` | `scripts/ui/LoadingScreen.cs` |
| HUD 容器 | `Hud` | `scripts/ui/Hud.cs` |

## 9. 配置总表（scripts/configs/ 全集）

| 配置类 | 业务归属 | 关键内容 |
|---|---|---|
| `WorldConfig` | 道路/桥造价与宽、抬升/地基、开局资源、记录上限 | 主路 18 文/延米宽 4、辅路 10/宽 2、桥 30/宽 4；路面抬升 0.1m、地基深 1m；建筑抬升 0.1m/地基 2m；拱顶 1m、桥体厚 0.2m、引桥 3 格；白边 10m；开局钱 100000/粮 500；履历 40、公告 200 |
| `TerrainConfig` | 高度场生成 / 坡度 / 垫基 / 采集豁免 | 步高 0.5m、坡度 30°、垫基高差 1m；海拔 [-5,110]m；草图 128²；主峰 2 座 88~105m（半径 160~210m）；小山 12~20 座 2~14m 成簇；侵蚀 25 万滴；热侵蚀安息角≈33°；采集豁免 4.5m |
| `WaterConfig` | 河网 / 湖泊 / 河床 | 路由格 2m；河道 ≥45 汇流格、≤13 条；河宽 0.075×√汇流面积（5~34m）；蛇曲波长 210m；河口外推 56m×展宽 2.1；湖 3~6 座（大 70~110 / 中 32~58 / 小 12~26m）、总水面 5.5% 封顶；河床深 1.0~3.5m 按离岸距离；水面外扩 0.7m |
| `TimeConfig` | 日历 / 作息 / **两历换算层** | 前台压缩历：24h/旬 · 3 旬/月 · 12 月/年（**1 旬 = 60 秒现实**、1 年 = 36 分钟）；**后台真实历：1 年 = 12 月 = 360 日，映射 1 游戏旬 ≙ 10 真实日 ⇒ 1 游戏月 ≙ 1 真实月、1 游戏年 ≙ 1 真实年**（`RealDaysPerGameDay`，改前台节奏只改此值）；上工 6~18 时；轮休 2 旬 |
| `TimelinessConfig` | **物品时效**（软/硬双轴阈值与全部倍率） | 软轴阶段 60 / 15；分档鲜度 10 点、寿限 30 日；翻新惩罚 寿限 ×0.65^n、速率 ×1.8^n；位置 有顶 ×0.45速率/×1.6寿限；季节 夏 ×1.6 / 秋 ×0.85 / 冬 ×0.55；临水 ×1.35/×0.9；硬轴归零转废料 0.3；货品基线 野味 10 日 / 烧饼 15 日 / 果品 24 日 / 粮食 120 日（硬轴 480）/ 柴薪 240（无硬轴），未登记即不腐 |
| `EconomyConfig` | 货担/价差/库存与鲜度定价/消耗/产能/税制（文） | **锚：一升米 10 文 = 一人一天**；一担 30 份（= 一月口粮）、零售加价 ×2.0（铺面 20 文上下）；库存定价 ≥95% ×0.7 / ≥80% ×0.9 / ≤20% ×1.1；鲜度折价下限 ×0.5；**「日值」一律为"每真实日"**（口粮 1.0 / 柴 0.3 / 水 1.0，结算经 `TimeConfig.PerRealDay` 换算成每旬）；加工 1.8667 份/真实日/工；家计 300 文/人/月；官粮 0.5/人/月、朝廷粮饷 30/人/月、田赋 0.1；老化 0.7/月；土地税 1~10%(默认3%)/商税 2~15%(默认5%)/人口税 20%；安家银 1,000,000、王爷月俸 60,000 |
| `PopulationConfig` | 迁入/婚配/生育/交友/迁出（旬频） | 迁入 0.2333/旬（四类权重 归民0.35/寓商0.30/散勇0.20/客士0.15）；婚配 0.0233/旬、生育 0.007/旬、交友 0.0233/旬；无家 6 月迁出；拥挤 0.15/月；自建门槛 5000、分家公产 1500、空房继承 1000/600 |
| `LifeConfig` | 年龄门槛 / 死亡曲线 | 成年 16、老年 60、婚配上限 50、生育上限 45；退休 50/家族产业 60；Gompertz 年死亡率 0.005+0.03×e^((age-55)/8)；最大寿数 120 |
| `GrowthConfig` | 坊区生长 / 吸引力 | 小路环 1 格；基价 20、地价 5/分；打分 主3/辅2/河道1.5/邻居1.2×3栋；阈值 3、抽签幂 2；升级 0.02/日（完好≥60、吸引力1.2/级）；工商占比封顶 0.3；转业 0.03/日；吸引力 主1.0/辅0.4÷16、半径 12 |
| `MovementConfig` | 移动速度 / 寻路权重 | 基础 5 m/s；脱路 ×0.35；转向 10 rad/s；路面系数 主1.2/辅1.0/小0.7/桥1.0 |
| `VillagerConfig` | 表现层参数 | 缩放 0.25、儿童 0.4 起；代理上限 300；分离半径 0.9/推力 3；疲劳阈值 80、兴致阈值 25；储备目标 食3/柴1/水3；采集半径 64 |
| `PlantConfig` | 植被生成 / 消长 | 全图上限 8800；密度阈值 0.55；散播 0.03/月；挂果 0.1/日、落果 0.1；成熟 12 月；血量 20+80 渐进 |
| `WildlifeConfig` | 动物种群 | 种群 = min(240, 树数/15)；月刷新 0.5；繁育 0.12/月、自然死亡 0.01/月；日游走 4m |
| `PrinceMansionConfig` | 王爷府 | DefId `prince_mansion`；开基 100000 文/400 粮 + 货品；随迁 3 对 20~27 岁夫妻；选址加分 8/半径 32m |
| `CameraConfig` | 相机 | 距离 2.5~450、远裁剪 2000；俯仰 −1.45~−0.35；边缘 8px；净空 1.5m |
| `GeneticsConfig` | 技能遗传 | 继承 50/50 父/母；经验衰减 0.3~0.7；变异 5%；开蒙 10% |
| `CurrencyConfig` | 货币换算 | 1 两=1000 文；1 万两=10000 两=10,000,000 文（**黄金已废除**） |
| `FarmlandConfig` | 农田 | 收获周期 3 月、每工 50 份、两熟（[4,9]月窗口）；田赋 0.1；OwnerYieldBonus/SkillYieldMaxBonus |

## 10. 数据文件（data/）

| 文件 | 内容 | 扩展方式 |
|---|---|---|
| `data/buildings.json` | 23 项建筑定义（占地/造价/颜色/岗位/加成/产物/税率） | 增改即生效；mod 可覆盖/追加 |
| `data/techs.json` | 科技定义（passive/active + 效果键） | 同 buildings.json |

## 11. 模块 ↔ 目录速查

| 目录 | 职责 |
|---|---|
| `scripts/core/` | 骨架：`GameState`/`EventBus`/`GameClock`/`GamePaths`/`GameSettings`/`Ledger`/`NewsItem`/`Milestones`/`CurrencyHelper` |
| `scripts/configs/` | 全部常量与公式（21 个静态类） |
| `scripts/map/` | 网格/地形/水系/植被/道路/渲染（`FlowRouter`/`RiverNetwork`/`LakeGenerator`/`GridRenderer` 及 `layers/` 六图层/`AnimalRenderer`/`PileRenderer`/`BuildingStockRenderer`/`GoodsColors`） |
| `scripts/render/` | 宋风模型工厂 / 卷轴背景 / 渲染图层 |
| `scripts/build/` | 建造：`BuildingDef`/`BuildingInstance`/`BuildController`/`PlacementValidator`/`BuildingAssetLoader` |
| `scripts/citizens/` | `Citizen`/`Family`/`LifecycleSystem`/`JobSystem`/`NameGenerator` |
| `scripts/agents/` | `AgentManager`/`CitizenAgent`/`CitizenAnim`（骨骼村民） |
| `scripts/sim/` | `Goods`/`GoodsSystem`/`CraftingSystem`/`EconomySystem`/`MaintenanceSystem`/`DesirabilitySystem`/`PlantGrowthSystem`/`WildlifeSystem`/`Inventory`/`Obj`/`DemandLedger`/`RecipeDef`/`FarmlandSystem` |
| `scripts/zone/` | `ZoneGrowthSystem`（坊区自发生长） |
| `scripts/policy/` | `TaxPolicy`/`TaxSystem` |
| `scripts/tech/` | `TechDef`/`TechSystem` |
| `scripts/save/` | `SaveData`/`SaveService`（LMDB） |
| `scripts/camera/` | `RtsCameraRig` |
| `scripts/ui/` | 全部界面 |
| `scripts/objects/` | `Obj`（世界实体基类：PlantObj/AnimalObj/ItemPileObj） |
