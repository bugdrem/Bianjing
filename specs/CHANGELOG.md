# 变更日志（specs）

按批次记录每次调整的要点（新规则起始于批次二十五；更早批次的详情见计划文档归档）。

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
