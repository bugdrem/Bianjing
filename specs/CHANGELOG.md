# 变更日志（specs）

按批次记录每次调整的要点（新规则起始于批次二十五；更早批次的详情见计划文档归档）。

## 批次四十四（2026-07-28）调整：收紧相机最大视距省渲染资源

- 缩放距离上限 CameraConfig.MaxDist 700m→450m（约览半城为限，不再开到纵览全图）：
  同屏进入视锥的地形三角网格/建筑量明显减少；全图总览后期交给小地图方向。
- 相机远裁剪面收编 configs：新增 CameraConfig.FarClip=2000m（最远拉距+地图对角线留余，
  低角度斜望不穿帮），替换 RtsCameraRig 硬编码 Far=4000，远景更早剪掉且深度精度更好。
- 无存档格式变动。编译 0 警告 0 错误。

## 批次四十三（2026-07-28）重构：顶点地形高度场（灰度地图）+ 三角网格渲染

- 数据底层：删除 Cell.Height 整数台地，新建 HeightField 顶点高度场（1025² float 顶点，
  每格四角顶点构成 2 三角面）；格级衍生量（中心高/极值/坡角）即时由四角算出，
  MapGrid.GroundY 保留签名转发格中心高；玩家升降地形等后期塑形复用 SetVertex/FlattenRect。
- 配置米制：TerrainConfig 全面重写（删整数层体系 LayerHeight/BaseLayers 等），新增
  MaxStepHeight=0.5m、MaxBuildFlattenDiff=1m、ForageMaxHeight 等；Traversable 改 float 高差+坡角判定；
  WaterConfig 新增全图统一水位 WaterLevel=-0.5m（查询收口 WaterLevelAt 预留分段水位）与河床深度参数。
- 世界生成：顺序改为地形→水系→树木→野物。MountainGenerator 连续场重写（噪声缓丘/
  脊线山脉/超椭圆石峰直接叠加顶点，删削壁与侵蚀循环）；RiverGenerator 拓扑不变、
  新增 CarveBed 按离岸距离把顶点压到水位下（岸缘 0.3m→河心 1.6m，只降不升），
  岸形由深度梯度自然涌现（平原缓入水成浅滩、山体被切成峡谷陡岸）。
- 渲染：GridRenderer 地形段重写——每分块 65×65 顶点 ArrayMesh 三角网格（平滑法线受光，
  顶点色按高度+坡度插值、水下河床泥沙色）替换逐格土柱 MultiMesh；水面按分块生成
  统一水位半透平面；道路改采四角地形高的贴地四边形（坡道路面自然倾斜）；脏块重建
  管线保留，块缘格变更波及邻块（边界顶点共享）。
- 玩法：村民贴地改 SampleWorld 双线性（坡面平滑升降）；建筑改「自动整平垫基」：
  占地高差≤1m 可建，落位/扩建时 FlattenRect 压平成台面（读档重建不整平，高度随档恢复）；
  野物/采集/峰上落树的整数层判定全改高度阈值。
- 存档 v19→v20：MapSave 删高度稀疏表，新增 uint16 量化灰度 blob（步长 0.01m，
  height=HeightMin+v×HeightStep，约 2.1MB）；旧档拒读。
- 预留不实现：直角石砌护岸、分段水位/水源模块、玩家塑形工具 UI。
- 验证：编译 0 警告 0 错误；headless 冒烟全过（地形 -2.10~12.60m、水格 3.2 万、
  垫基压平高差 0、v20 存读回环采样一致）。

## 批次四十二（2026-07-28）重构：配置类按业务合并 + 散落控制常量收编 configs

- 配置类合并（21 → 14 个，数值全部不变）：ScheduleConfig→TimeConfig（时间+作息）、
  ImmigrationConfig→PopulationConfig（人口+迁入）、RetireConfig→LifeConfig（寿命+致仕）、
  Jobs/Maintenance/TaxConfig→EconomyConfig（经济+家计/修缮/税制）、DesirabilityConfig→GrowthConfig
  （生长+吸引力）、AgentConfig→VillagerConfig（村民模型+行为层）。改名消歧义：
  Age→RetireAge、FamilyBusinessAge→FamilyBusinessRetireAge、AssetsMin/Max→ArriveAssetsMin/Max、
  RatePerLevel→TaxRatePerLevel、AgingPerMonth→BuildingAgingPerMonth、吸引力四项加 Desir 前缀。
- 散落控制常量收编：新建 CameraConfig（相机距离/俯仰/屏缘推移五参数，原 RtsCameraRig 硬编码）；
  树林生成三参数（噪声阈值/密度上限/初始树数）入 PlantConfig；市集备货线与采买半径入
  EconomyConfig；主/辅路宽与桥宽入 WorldConfig（GameState 保留转发）。
- 修复○BuildController 拾取高度上限 22f 硬编码：改为由 TerrainConfig 最高层推导的 const 表达式
  （同值），地形参数改动后不再静默过时。
- 有意不收编（表现层/数据表）：渲染色板、UI 尺寸与刷新间隔、活动手感参数；
  Goods/Milestones/TaxDefs/NameGenerator 属数据定义模块（mod 可扩展），维持原位。
- 无存档格式变动。编译 0 警告 0 错误，CodeReview 全部检查项通过。

## 批次四十一（2026-07-28）调整：建房选址更倾向已有建筑旁（邻居密度计分 + 加权抽签）

- 邻居项由布尔加分（有无建筑一律 +1，四项垫底）改为密度计分：扫描范围内每栋建筑
  （按实例 Id 去重，防王爷府等大占地按格数灌分）加 1.2 分、计分栋数封顶 3——3 栋即满
  3.6 分与主路同档且可独立过阈值，聚落能脱离主辅路自然向外扩片（GrowthConfig 新常量
  SiteNeighborScorePerBuilding/SiteNeighborCountCap，替换原 SiteNeighborScore）。
- 达标候选由等概率随机挑改为按分数加权抽签（权重 = 分数^SitePickPower，幂次 2）：
  同样达标的两个十字路口一热闹一空旷时，大部分人挨着热闹处建、少量人仍去空旷路口落户
  （ZoneGrowthSystem 新增 WeightedPick 轮盘抽签；公式 SiteWeightOf 集中在 GrowthConfig）。
- 地价联动照旧（分越高越贵），无存档格式变动。编译 0 警告 0 错误。

## 批次四十（2026-07-28）新增：画路跨水自动架同宽小桥

- 道路与桥同步：GameState.PlaceRoadStamp 遇水面格自动架一座与路同宽的小桥（辅路→宽 2、主路→宽 4），
  拖拉一次画成、跨河不断档；岸上路段按道路单价、跨水桥段按桥梁单价，各按等效延米（新格/宽）计费（重叠不多扣）。
- 抓取与预览：PlacementValidator.CanPlaceRoad 对无桥水面格返回可放（按桥价校验余额），拖路过河预览不再变红。
- 抽出 LayBridgeCell 单格桥面铺法供道路/桥梁两处复用（桥面 kind=None、寻路权重同辅路）。
- 保留独立「桥梁」工具（固定宽 4）不变。无存档格式变动。编译 0 警告 0 错误，headless 冒烟无崩溃。

## 批次三十九（2026-07-28）优化：水系与山体地形生成更自然

- 水系重制为树状水系（RiverGenerator 重写 + 新增 configs/WaterConfig 集中调参）：
  ① 一条完整干流自西源蜿蜒东流入海口，河宽自上游向下游线性变宽，中心线由低频正弦叠随机漫步蜿蜒；
  ② 支流树——从干流中段递归分叉出支流与小溪（二叉树式），逐级变细变短，撞水即汇流止笔；
  ③ 水流方向——Cell 新增 FlowDir 字段（八方向编码，湖为静水 0），干流指向河口、支流指向汇入的母河，随存档保存。
- 湖泊优化：大湖半径增大（30~52 米），湖缘由三组随机相位正弦谐波调制半径，呈不规则湾汊；
  按概率扣出湖中岛（保留陆地环水，渲染层自动画成水中高地，无需改动）；
  2~3 座大湖坐落河网点上天然带入水口/出水口，另有 1~2 座独立小湖凿出水渠连向最近水体。
- 山体优化：MountainGenerator 新增连绵山脉（RaiseRanges）——若干条蠕蜒脊线，沿脊高度随正弦起伏、两侧二次 falloff
  降到平地，成连绵起伏的中高山体（削壁后可走）；石峰数量增至 10~18 座。
- 平地占比保障：EnforceFlatRatio 收尾——若平地（非水、高度=基准层）不足全图 FlatLandTarget（50%），
  从山缘（有更低邻格的非平地）逐轮降一层自外向内蠕食至达标，保护石峰（≥PillarLayerMin）不动，末再削壁修复台阶。
- 存档格式 v18→v19（新增水流方向，旧档无此数据拒读）。编译 0 警告 0 错误，headless 冒烟（开局即生成新水系/山脉/侵蚀）无崩溃、无警告。

## 批次三十八（2026-07-28）新增：王爷府（开局首建核心官邸）

- 新增建筑「王爷府」（buildings.json）：官营、12×12、免费、全局唯一、capacity 6（可寄居 3 对夫妻）、
  吸引力 4/半径 48、储量 400、菜单最前（menuOrder 5、里程碑 0）。BuildingDef 新增数据驱动 Unique 字段。
- 开局首建门槛：未建成王爷府前锁定一切营造（PlacementValidator 对路/桥/坊区/其它建筑一律拦，
  BuildController 左键拦截并提示「请先建造王爷府」，BuildMenu 相应项置灰）；「选择/查看」不受限。
  王爷府免临路要求（首建无路可依，自带小路环）、全局唯一不重建。
- 建成钩子（EventBus 新增 BuildingPlaced，GameState.PlaceBuilding 广播；读档重建不经此方法故不误触）：
  Main.OnBuildingPlaced 一次性拨给开基资源（官库 +3000 钱/+400 粮，府库注入粮/柴/果/盐/矿各若干），
  并由 LifecycleSystem.SettleNobleFamilies 携 3 对富裕年轻夫妻（家庭公产 1200、20~26 岁）暂居府中。
- 夫妻迁出：复用「寄居→攒够自建」逻辑（BuildUpFromLodging 由「仅 grown 店坊」放宽为「非自宅且有居住位」，
  涵盖王爷府）；玩家划好坊区后，富裕夫妻自建新宅迁出。
- 建房倾向叠加王爷府数值：ZoneGrowthSystem.TryBuildHouse 选址分新增「近王爷府」加成（SiteScore 6、
  半径 24、按距线性衰减），民居优先聚于府邸周边。
- 存档格式 v17→v18（旧城无府会被锁死营造，拒读旧档）。编译 0 警告 0 错误。

## 批次三十七（2026-07-28）批量表现/性能优化（8 项）

- 地面/水面基准（第3项）：竖直原点对齐陆地基准——TerrainConfig.LayerToWorldY 改为 (layer-BaseLayers)×LayerHeight，
  BaseLayers 2→1；平原 y=0、水面 y=-0.5（低于岸陆半米）。地面背景平面下移到 y=-0.6 作河床/图外背景，
  陆地逐格土柱（顶 0、底 -0.7）立于其上，河道自然下凹；存档格式 v16→v17。
- 桥面（第1项）：由贴水直基改为悬浮板（底 0.18、顶 0.34），高于最高道路面（主路顶≈0.24）且与水面 -0.5 留明显空隙；CitizenAgent 上桥站面 0.43→0.34。
- 野生动物缩小：AnimalRenderer 整体缩放由 0.9~1.1 → 0.48~0.60，方块猪高 ≈村民体量。
- 建筑点击优先级：村民/野物模型已很小，命中圈过大易误选人；PickCitizen 32→12px、PickAnimal 24→14px，
  只有光标几乎压在小人/小猪上才选中，否则落空交给建筑视线拾取，免点房子误选周围的人。
- 4 倍速间歇卡顿：根因是 GridRenderer._Process 同帧重建所有脏分块——建筑升级/转业每天触发 RaiseMapChanged
  使全部 256 分块标脏，下一帧一次性重扫约百万格；4x 下建筑频变→周期性尖峰。改为每帧限额
  重建（MaxChunkRebuildsPerFrame=12），把尖峰摊到多帧（约 22 帧铺完），余脏块下帧续建。
- 房屋高度减半：buildings.json 全部 height 减半（Def.Height 是渲染/预览/点击命中的唯一来源，一处改三处一致）。
- 农田无屋顶：BuildingDef 新增数据驱动 NoRoof 字段，farm 置 true，GridRenderer 跳过其斜屋顶（只留地面）。
- 异步原子保存：SaveService 拆分 BuildRecords（主线程快照+序列化，与模拟同线程免竞争）+ WriteRecords
  （后台线程 LMDB 单事务写盘+提交，卸掉阻塞磁盘 I/O 免卡帧，原子性不变）；新增 SaveAsync/IsSaving，
  Main 的自动/命名/快速存档改用之，完成回调经 Callable.CallDeferred marshal 回主线程刷 HUD。
- 全部 8 项已交付。编译 0 警告 0 错误，冒烟无崩溃。

## 批次三十六（2026-07-28）修复：住宅从不转商铺/工坊

- 病因：转业 TryConvertHouse 原本只在「住宅升级成功」那一刻调用，而升级要求
  吸引力 ≥ 1.2×等级；但小路（Lane）吸引力加成=0、辅路仅 0.025/格，而村民多沿自建
  小路环聚居（吸引力≈0）→ 永远升不了级 → 转业永不触发；叠加 ≥8㎡ 占地门槛（需扩建两次），
  多重条件同时满足的概率趋近 0，6 年下零工商户。
- 修复：转业从升级链解耦，新增 ZoneGrowthSystem.Conversions 独立日结算——对够格占地的
  路边民居按日概率（ConvertChancePerDay=3%）直接按临路档位掷签转商铺/工坊，不再依赖升级/吸引力；
  里程碑≥1 与工商占比 30% 封顶仍由 TryConvertHouse 约束。
- 占地门槛 ConvertMinArea 8→6（扩建一次即 2×3=6 即够格），路边小铺更易自然长出。
- 升级仍保留（只影响楼高观感），与转业互不依赖。编译 0 警告 0 错误。

## 批次三十五（2026-07-28）野生动物模型优化：方块猪

- AnimalRenderer 由单一棕色方块 → 低多边「方块猪」（参考猪体态：胖圆身躯 + 短四腿 +
  前伸拱嘴 + 小耳 + 卷尾），与村民/建筑的方块占位美术统一。
- 实现：手搭合成单个双表面 ArrayMesh（主体粉褐 + 拱嘴/耳/蹄深色），MultiMesh 逐只实例化；
  新增 AddBox/AddSurface 手搭盒面（双面渲染免绕序剔除）。局部 y=0 为地面，四腿贴地（基准 Y 由
  旧 +0.35 改为 +0.02），+Z 为猪头朝向。
- 个体差异：朝向按 Id 稳定散布，体型按 Id 微缩 0.9~1.1；平滑位移/地形海拔逻辑不变。
- 编译 0 警告 0 错误。

## 批次三十四（2026-07-28）点选优化：建筑沿视线拾取 + 树/野物/果品面板 + 果品挂树

- 点选修复：旧拾取只拿 Y=0 平面交点格，点建筑「身体/屋顶」实际打到其身后地面——
  新增 PickWorldObject 沿视线半格步长推进，按深度命中建筑体（含屋顶余量）/树木（冠高内）/
  落地处物资堆；无命中时用视线落地格展示格子信息（台地/缓丘上不再偏格）。
- 新增点选页（InspectPanel）：树木（树龄/长势/木质血量，果树另列挂果）、野物（月龄/习性，
  屏幕投影就近拾取 24px）、地面物资堆（逐货明细+落地天数，标题随主要货品）；
  目标砍倒/猎获/拾空自动关闭，点选优先级：居民 → 野物 → 视线深度（建筑/树/堆）→ 格子信息。
- 果品挂树（PileRenderer）：落在树格的果品堆缩小成果串块（0.16~0.26m，原 0.5m），
  吊在树冠下沿而非坠地；位置/株大小与树渲染同源哈希，果串对准树身。
- 编译 0 警告 0 错误。

## 批次三十三（2026-07-28）公告栏按钮入底部操作栏最右 + 公告随存档保存

- 村民行进转身（同批追加）：MoveAlongPath 后按本帧路径净位移平滑旋转 _body 偏航，
  模型正面（局部 +Z，胸前抱货同向）朝向行进方向；角速度 MovementConfig.TurnSpeedRadPerSec=10，
  分离推力不计入免抖头，驻留期停在最后行进朝向。
- 按钮入栏：NewsPanel 的「公告」开关按钮改在构造期创建并经 ToggleButton 暴露，
  由 Hud 交给 BuildMenu 摆到底部操作栏最右（叠一层两向 ShrinkEnd 的 MarginContainer，
  容器 Ignore 鼠标免遮居中分类按钮）；未读数/开合逻辑仍由 NewsPanel 自持，
  公告列表照旧从右下角弹出（上移至 96px 让开操作栏）。
- 公告入档：WorldSave 追加可选字段 News（旧 v16 档缺失读出空表，不破坏兼容不升版本）；
  存档浅拷 GameState.News，读档 AddRange 恢复，公告栏 OnGameLoaded 续接旧事（注释同步）。
- 编译 0 警告 0 错误。

## 批次三十二（2026-07-28）地形升级：基准抬升 1 米 + 高差随机化 + 桂林石峰（峰上生树）

- 基准体系重定（TerrainConfig）：陆地基准 BaseLayers=2 层（抬高 1 米），水面/河床恒 0 层
  （最低水面 0 米，暂不考虑水体流动）；世界最高层 30（15 米）。
- MountainGenerator 重写为三段流水线：① 基准抬升（陆地整体抬到基准层，水面不动）；
  ② 平原缓丘——双八度 value noise 阈上隆起 1~3 层（高差随机化），削壁只作用到此阶段，
  保证缓丘处处可走；③ 桂林石峰——8~14 座孤峰柱（半径 5~14m、高 16~26 层，超椭圆剖面
  1-(d/r)^k 顶平壁陡，±1 层顶面噪声），避水避图缘，陡壁天然不可攀（Traversable 拦截）。
- 峰上生树（TreeGenerator 第 3 段）：峰域格（≥7 层）按保底概率落普通树，不受林区噪声左右；
  峰上树为景观树——FindNearestTreeCell/FindNearestFruitTree 按 ForageMaxLayer 豁免（不派人去砍/摘），
  WildlifeSystem 游走/刷新同样不落峰顶。
- 水陆分界豁免（基准抬升的连带修正）：岸陆比水面/桥面高 1 米，层差 2 超 30° 坡度上限——
  StepTraversable 与 SlopeWalkable 对水邻格豁免坡度判定（上下桥属水陆分界而非陡壁），
  否则沿河铺不了路、村民上不了桥。
- 存档 v15→v16：高度稀疏表语义改为「偏离默认值（水面 0 / 陆地基准层）」才入表——
  基准抬升后若仍按「非零」存会退化成百万条全量表；读档先铺默认高度再覆盖稀疏格。
- 编译 0 警告 0 错误。

## 批次三十一（2026-07-28）review 修复三项 + 树木造型升级（树干+双形树冠）

- 修复○表现层未叠加地形海拔：PileRenderer（地面物资堆）/AnimalRenderer（动物）/
  BuildingStockRenderer（屋内库存柱）与 BuildController 全部预览框（路/建筑/坊区/树/拆除）
  的 Y 基准均改为叠加 Map.GroundY，不再半埋进山体/台地。
- 修复○迁出公告按「人」重复播报：HandleHomeless 迁出循环改按 FamilyId 去重，
  整户迁出只报一条（无家庭者以负 Id 单人成组）。
- 修复○CanPlaceBuilding 边界校验顺序：baseH 读取前先 InBounds（与 FootprintBuildable
  防御性写法对齐，越界取 0 由循环内兼底拒绝）。
- 树木造型升级（GridRenderer）：单圆锥 → 圆柱树干（上细下粗，木褐微扰动）+ 双形树冠——
  逐株伪随机选型（约两成针叶圆锥 / 八成阔叶椭球，果树恒为椭球暖黄绿）；
  分块 MultiMesh 由 2 套扩为 4 套（Boxes/Trunks/ConeCrowns/BallCrowns），位置/尺寸/颜色
  扰动均用格坐标哈希，重看不变样；树冠下压遮接缝。
- 编译 0 警告 0 错误。

## 批次三十（2026-07-27）地形高度系统（整数台地）+ 山体 + 两个交互修复

- 修复○空格暂停失灵：GameClock 由 _UnhandledKeyInput 改为 _Input（优先于 UI）+ 焦点判定：
  点过按钮后焦点留在按钮，空格被当 ui_accept 触发那个按钮并吞事件（表现为“必须按住不松”）；
  现在时钟键自己拦截并 SetInputAsHandled，仅在 LineEdit/TextEdit 聚焦时放行。
- 修复○村民过桥被遮：_Process 新增站面贴合（MoveToward 到 SurfaceYAt），桥格站桥板顶 0.43；
  MoveAlongPath 改为水平移动（target.Y 取当前 Y），垂直统一交给贴面逻辑。
- 新增地形高度系统（a 方案：整数台地，为后续 b 连续高度场/玩家塑形预留）：
  ① configs/TerrainConfig：层高 0.5m、免爬层差 1、最大坡度 30°、最高山体 12 层；坡度/可通行公式集中于此。
  ② Cell 新增 Height 整数字段；MapGrid.GroundY(c) 统一查海拔。
  ③ MountainGenerator：河后树前接入，3-6 座余弦钟形缓丘（避水），生成后削平陡壁保证全图坡度≤上限（处处可上）。
  ④ GridRenderer：逐格土柱（非水高格），水/桥/路/树/建筑/坊区色块/门的 Y 基准均叠加 GroundY；土柱色随层高渐变岩褐。
  ⑤ CitizenAgent：SurfaceYAt 叠加地形海拔；避水 BFS 新增 StepTraversable 坡度守卫（降壁不可跨）。
  ⑥ PlacementValidator：道路不可铺在陡壁（SlopeWalkable）；建筑（含 AI 自建房 FootprintBuildable）要求占地整块同高（平地）。
- 存档 v14→v15：MapSave 新增 HeightCells/HeightLayers 稀疏列表（非零高度才存），版本不符拒读旧档。
- 编译 0 警告 0 错误。

## 批次二十九（2026-07-27）村民模型优化：宋人市井装束（参考宋画风人物图）

- 轮廓重塑（CitizenAgent.ApplyLook）：男女皆改及踝长袍——袍摆比上身宽出一圈成 A 字剪影
  （女 0.62 宽裙袍/男 0.56，旧版男裤装 0.48 废弃）；新增三部件：深色腰带（束袍身交界处略凸）+
  双垂袖（自肩垂至腰际，与上衣共用材质零额外材质开销），共享盒网格。
- 冠发分化：成年男戴幞头（扁盒 0.26×0.12）；女与孩童改球体圆发髻（女 0.18/童 0.13，_hat 动态切换共享 Mesh）。
- 配色改色板 + 按人稳定（Citizen.Id 取模，重看不变色）：男五组（灰蓝/青绿/米褐/藏青/茶棕）、
  女三组（米白襦朱红裙/青襦米裙/藕荷襦灰蓝裙），下摆略深于上衣显层次；
  色调取自参考图宋画市井色板；腰带男深褐/女红褐/老人灰褐；孩童亮米黄与老人灰白袍沿用微调。
- 胸前背货挂点/拾取命中高度不受影响（整体身高量级不变）；编译 0 警告 0 错误。
- 后续调整：人物不放大——含袖总宽压回旧版体量（男 0.36+双袖 0.2=0.56 同旧肩宽，女 0.54），
  袍摆/袖/腰带同比收窄，腰带上提免遮；相机拉近下限 MinDist 6→2.5（可凑到街头看清行人，MaxDist 保持 700）。
- 发型修正（黑团问题）：新增发盖部件——略宽于头的压扁球贴头皮罩住头顶半球（与冠发共用材质），
  冠发同步缩小上提（男幞头 0.2×0.09、女发髻 0.13/童 0.1），不再是悬浮头顶的一团黑。

## 批次二十八（2026-07-27）参数配置化：configs 目录按业务拆分常量模块

- 本批同期落地两套实质行为变更（口头需求，参数已入 GrowthConfig）：
  ① 自建住宅选址改为叠加偏好打分（主路3/辅路2/河道1.5/邻居1，可叠加）+达标随机选址，地价=基价+5×分；
  ② 住宅转业改为临路档位掷签：贴主路商铺0.5/工坊0.3、贴辅路0.1/0.4、仅小路0/0.15，余量维持住宅升级。
- 新建 scripts/configs/ 目录，全工程固定参数按业务收编为 18 个静态常量类（一业务一文件，全部 const + 完整中文注释）：
  Time/Schedule/Movement/Villager/Life/Retire/Population/Immigration/Jobs/Economy/
  Maintenance/Tax/Desirability/Plant/Wildlife/World/Growth/AgentConfig。
- 纯数值公式一并入配置模块：LifeConfig.AnnualMortalityAt/HealthMortalityFactor/MonthlyFromAnnual（Gompertz 死亡曲线）、
  PopulationConfig.BirthCountFactor/BirthAgeFactor/BirthWealthFactor（胎次/年龄/富裕生育系数）、
  PlantConfig.MaxHpAt（树龄→满血上限）、GrowthConfig.LandPriceOf（选址分→地价）、
  MovementConfig.RoadSpeedFactor/RoadWeight（路面速度与寻路权重）、AgentConfig.WoodPerHp（血量→柴薪折算）；
  LifecycleSystem/ZoneGrowthSystem/PlantObj 等调用点改为引用公式函数，消除重复实现。
- 各系统散落硬编码数值全部改引配置（高频引用处保留同名短名 const 转发，改动面最小）：
  道路造价/开局钱粮/履历与公告上限→WorldConfig；老年线 60 岁→LifeConfig.ElderAgeYears；
  主妇采购 0.6/老人闲逛 0.5→AgentConfig；野物游走半径/树种散播范围等微观值同步收编。
- 废弃上一轮的 LMDB 配置库方案（删 BalanceStore.cs 与 GameBalance.cs）：参数回归编译期 const，
  降低阅读复杂度；Goods.BasePrice/Recipes、Milestones.Levels/TierNeeds 恢复 readonly。
- 未收编（有意保留）：CitizenAgent 各活动疲劳/兴致速率表与驻留时长（表现手感参数）、
  TreeGenerator/RiverGenerator 世界生成参数（一次性生成逻辑）、道路宽度（几何结构）、NewsPanel 尺寸（UI 布局）。
- 存档 v14 不变（无数据字段变动）；编译 0 警告 0 错误。评审后修正：迁入私产恢复整数分布（同旧版 10~29 贯）、
  AgentConfig.WoodPerHp 改引 EconomyConfig 消除 configs→sim 回环并恢复 const、ZoneGrowthSystem 三处缩进归位。

## 批次二十七（2026-07-27）房体=占地 + 小路附属环 + 迁入驱动建房 + 岗位非必须

- 房体=占地、容量简化：buildings.json 中 house/shop/workshop 尺寸 4×4→2×2（house 高 1.8→1.1）；
  HousingCapacity（grown）= FootX×FootY（不预留工位，居住与打工共用同一格池），删 BodyCells −−院子逻辑；
  商铺岗位 2→1、工坊 3→2（非必须可空置）；GridRenderer grown 与官营统一按占地 ~0.9 缩放整块绘制。
- BuildingOccupancy（GameState）= 本楼居民 + 外来雇工（HomeId≠b 且 WorkplaceId==b），同人只占一格；
  招工（JobSystem.FindVacancy）与寄居均以此判空格（grown 店坊居民+雇工≥容量则不对外招）。
- 小路附属环推广到所有建筑（GameState.LayLaneRing）：PlaceBuilding 末尾对含玩家放置的官营建筑
  四周环一圈小路（已临任意路则不重铺）；PlaceGrownWithLanes 化为兼容别名；拆除时清理独占小路。
- 扩建连同小路调整（ZoneGrowthSystem.TryExpandHouse/ClaimStrip）：带格允许「可建设区空地」或
  「本建筑小路环格」（ClaimCellForBuilding 内部先清小路再并入）；扩成后对新 footprint 重新环一圈小路。
- 选址偏好主路 + 地价（GameBalance.Growth.HouseBaseCost/LandPricePerDesir + ZoneGrowthSystem.TryBuildHouse）：
  地价 = 基价 + 系数×该格吸引力；预算内选吸引力最高（最靠主路/设施）的可负担点，全买不起/无落位返回 false。
- 迁入驱动建房 + 取消自动长房（GameBalance.Immigration + LifecycleSystem）：
  ZoneGrowthSystem 删“缺房自动 TryGrow”，人口靠迁入+分家建房；迁入自带随机资产（AssetsMin/Max），
  夫妻必自建房（建不起/无落位则不迁）；单身资产≥SelfBuildAssets 且能建则自建，否则寄居有空位的工坊/商铺
  占 1 居住位当暂住雇工（有空岗位则同时受雇），店满则不迁；ResolveHousing 新增“赚钱自建宅”（寄居家庭人均≥门槛则建房携家搬入）。
- 婚育以住宅为前置（LifecycleSystem）：婚配时男方需自有住宅（居于 house 且有空位）或当场建新宅（次子/寄居者），
  建不起则本轮不婚；生育母亲家须为 house（寄居店坊/无家不生），保留容量 1.5 倍略超；
  分家/新婚另立/超员疏解均改为自建新宅（TryBuildHouse），房款从家庭公产/私产扣除。
- 存档 v13→v14（默认建筑尺寸 4×4→2×2 与旧档 footprint 不兼，版本不符拒读）。

## 批次二十五（2026-07-27）门布局 + 一宅一家户主制 + 面板展示 + 拾取优化

- 门布局重做（GameState.ComputeDoors）：候选按四条边分组；大门在临路等级最高的边上居中；
  后门优先开在大门对边（屋后）偏左/偏右（按建筑 Id 奇偶定侧），屋后无路则开在侧边偏后；
  仅大门一边临路时不设后门。
- 一宅一家（LifecycleSystem）：修复一房挤住几十人——
  根因是床位制允许无关个体/家庭混住（小路环又使内格容量暴涨到 16~64 床）。
  FindVacantHouse 改为 FindEmptyHouse（只找无人居住的空宅）：外来迁入、次子分家、成年分家、
  超员疏解全部只入空宅另立门户；失宅安置按家庭分组整家同迁一宅；
  婚配同住链补第三级（夫家 → 妻家 → 空宅另立 → 分居待疏解）；
  家庭内部新增（出生、配偶并入）仍按本宅床位。
- 户主（GameState.HouseholdHead）：住户中最年长成年男 → 最年长成年女 → 最年长者推导，
  不入存档，亡故自动换代。
- 建筑面板（InspectPanel）：_body 换 RichTextLabel；居民区显示"屋主：某某"，
  成员逐行 = 名字（男青蓝 #6fa8dc / 女红 #e0708a）+ 年龄 + 与屋主关系
  （本人/妻/夫/子/女/父/母/兄/弟/姐/妹/孙/孙女/儿媳/女婿/亲眷）。
- 居民拾取（BuildController.PickCitizen）：瞄准点从 +1m 改为按模型缩放的胸口高度
  （ModelScale×1.1），命中半径 24px → 32px。
- 存档 v13 不变（无新字段，门与户主均为运行时推导）。
