# 变更日志（specs）

按批次记录每次调整的要点（新规则起始于批次二十五；更早批次的详情见计划文档归档）。

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
