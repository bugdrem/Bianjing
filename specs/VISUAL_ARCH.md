# 视觉与模型架构：地图 / 建筑 / 人物（VISUAL_ARCH）

> 立项时间：2026-10-24 ｜ 对应批次一百 ｜ 上游依据：`specs/REVIEW_2026-10.md`
> 阶段定位：**早期开发·纯发展期**——聚焦玩家发展、城市布局与造景；不考虑历史兼容与复杂玩法。
> 权威关系：本文是**视觉/模型/UI 度量**的专项权威；经济数值看 `ECONOMY.md`，业务↔代码寻址看 `CODEMAP.md`，
> 迭代记录看 `CHANGELOG.md`。四者不重复。

---

## 1. 为什么要有这份文档

Review 摸底（`REVIEW_2026-10.md`）暴露出三类问题，本架构逐一回应：

| 问题 | 证据 | 本文对策 |
|---|---|---|
| **人物模型是"静物"** | `CitizenAnim.cs` 自陈"阶段 C 暂未启用"，`CitizenAgent` 的 8 个原始体部件绝对定位，只有整体上下 bob | §3 程序化关节骨骼（`CitizenRig`） |
| **建筑模型是二选一** | `BuildingAssetLoader` 要么整栋走 GLB、要么整栋退回原始体；无渐进接入、无零件复用 | §4 模块化零件套件（`BuildingKit` + `BuildingModelConfig`） |
| **UI 度量各自为政** | 47 处硬编码色、34 处 `font_size`、22 处 `separation`；`UiTheme` 只管色与字体不管度量 | §5 设计令牌（`DesignTokens`） |
| **玩家是信息盲人** | `DemandLedger` 驱动全部 NPC 决策却"暂不显示给玩家" | §6 供需面板（`SupplyPanel`） |

---

## 2. 开源资产选型（结论先行）

**原则：CC0 优先（可商用、免署名、可改），风格统一优先于单体精美，模块化优先于整栋。**

| 来源 | 许可 | 用途 | 本项目状态 |
|---|---|---|---|
| **Quaternius**（quaternius.com） | CC0 | 低多边形主力：角色/动物/道具/中世纪村庄 | ✅ 动物已接入（`AnimalModelFactory`） |
| **Quaternius Medieval Village MegaKit** | CC0 | **300+ 模块化建筑零件**（墙/地板/梯/屋顶/门窗/藤蔓），网格对齐，自带 Godot 4.3+ 工程 | 🎯 建筑套件首选 |
| **VVayToyek「Chinese Four-corner Pavilion」**（itch.io / Godot Asset Store） | CC0 | 真正的中式四角亭，还原约 80% 建筑结构，多材质、UV 已展开 | 🎯 中式地标补充 |
| **Kenney**（kenney.nl） | CC0 | 2D/3D/UI/音频全家桶 | ✅ 树木已接入（`TreeModelFactory`） |
| **Poly Pizza**（poly.pizza） | CC0 / CC-BY **逐件不同，用前必查** | 8000+ 低模，补零碎道具 | ⚠️ 仅挑 CC0 件 |
| **Poly Haven / ambientCG** | CC0 | PBR 贴图 + HDRI 环境 | 备选（换写实风时） |

**明确不用的**：Sketchfab 未标 CC0 的模型（许可繁杂）、CC-BY-SA / GPL 资产（传染性许可会污染本项目）、
models-resource 等提取站（版权状态不清）。

> **风格统一的工程手段**：所有 GLB 零件进管线时统一走 `GltfMeshMerger.Merge(root, desaturate, brightness)`
> ——去饱和 + 亮度调和。`BuildingModelConfig.DesaturateOf/BrightnessOf` 按槽位给参数：
> 瓦作压灰（0.35）统一成深灰瓦、夯土墙面微降饱和（0.15）、木作略提亮。**这是把不同来源的资产
> 拉进同一套宋式灰调的关键一步**，比"挑风格相近的包"更可靠。

---

## 3. 人物模型：程序化关节骨骼

### 3.1 旧方案为什么废掉

`CitizenAnim.cs` 的类注释记录了完整失败史：

> 当前 CitizenAgent 取消 Skeleton3D + BoneAttachment3D 方案（Godot 4.7 代码构造 BA3D 反复不跟骨：
> 试过 BoneIdx 显式绑定 + ForceUpdateAllBoneTransforms 都失败），部件直接挂在 `_body` 下，
> Position = 视觉绝对位置。

后果：8 个原始体部件（袍摆/上身/腰带/双袖/头/发冠/帽）位置写死，**走路只有整体上下颠簸**，
没有摆臂、没有俯身、没有拧腰。三百个村民像三百个上下跳的盒子。

### 3.2 新方案：不要骨架，要关节数学

核心判断：**我们要的不是「骨骼动画系统」，而是「会动的人」。**

`Skeleton3D` + `BoneAttachment3D` 是为了让**蒙皮网格**跟随骨骼。但我们的村民是
「部件各自独立」的造型（袍、袖、头都是完整方块/球），根本不需要蒙皮。
既然不需要蒙皮，就不需要骨架节点——**直接在 C# 里算关节变换，写进部件的 Node3D 变换即可。**

这就是 `scripts/agents/CitizenRig.cs`。它等价于一套骨骼系统，但：

| | Skeleton3D + BA3D | CitizenRig |
|---|---|---|
| 节点开销 | 每人 1 Skeleton3D + N 个 BA3D | 0（部件本就在） |
| 蒙皮网格 | 必须 | 不需要 |
| 300 人开销 | 300 套骨架更新 | 300 × 7 次四元数运算 |
| 4.7 C# 可用性 | **BA3D 不跟骨，实测失败** | 纯 C# 数学，无引擎依赖 |
| 扩展性 | 受引擎行为约束 | 加关节 = 加一个数组下标 |

### 3.3 骨架定义（7 关节）

```
骨盆 JPelvis ──┬─ 脊柱 JSpine ──┬─ 颈 JNeck ──→ 头 / 发冠 / 冠
               │                ├─ 肩L JShoulderL ─→ 肘L JElbowL ─→ 左袖
               │                └─ 肩R JShoulderR ─→ 肘R JElbowR ─→ 右袖
               ├─→ 袍摆
               └─→ 腰带
```

部件绑定（`CitizenAgent.ApplyLook`）：

| 部件 | 关节 | 偏移 | 效果 |
|---|---|---|---|
| 袍摆 | 骨盆 | `Down × lowerH/2` | 随躯干前倾微倾（长袍下摆本就跟人走） |
| 腰带 | 骨盆 | 零 | 束在腰上 |
| 上身 | 骨盆 | `Up × upperH/2` | 与袍摆产生错位 → **拧腰效果** |
| 双袖 | **肘** | `Up × (上臂长 − 半袖长) + Z×0.06` | 袖顶锚在肩、袖身随肘屈，**一个部件同时吃到摆臂+屈肘** |
| 头/发/冠 | 颈 | 逐级上移 | 转头点头 |
| 搬运挂点 | 脊柱 | `(0, −upperH/2, 0.28)` | 胸前货堆随躯干动 |

**袖子绑肘而不绑肩**是本次的关键技巧：偏移取「上臂长 − 半个袖长」，
静止时袖顶正好落在肩上；屈肘时偏移方向随肘旋转，袖子自然变成"弯着的前臂"。

### 3.4 四段动作（全部由相位驱动，零关键帧）

| 状态 | 触发 | 姿态特征 |
|---|---|---|
| `Idle` | 站着 | 呼吸起伏 1.2cm + 双互质频率叠加；每 ~7s 一次平滑立方包络张望 |
| `Walk` | 在移动 | 前俯 5°、**躯干扭转 ±7.5°（与对侧摆臂配合）**、对侧摆臂 24°、屈肘随步态开合、起伏 6cm |
| `Carry` | 移动且背上有货 | 双臂前抱 77° + 屈肘 54°、后仰 6° 配平、起伏减半、低头看货 |
| `Working` | 原地劳作（伐木/采集/打猎/修房/在岗） | 俯身 24°、大幅交替挥臂 49°、节奏比走路快 1.6×、低头盯活 |

**服装形制决定可动性边界**：村民着及踝长袍，袍摆覆盖双腿——这既是宋画形制，也意味着**腿部摆动不可见**。
所以可见关节集中在躯干/双臂/头部/袍摆四组。若日后改短打装束，在 `JPelvis` 下挂
`hipL/R → kneeL/R → footL` 三节即可，**FK 框架不用动**。

### 3.5 坐标系约定（改姿态角前必读）

- Godot 中 **−Z 为「前」**，故「前倾/前摆」在代码里取**负**的 X 旋转；
- `Basis.FromEuler` 默认 **YXZ 序**，即 `Ry(yaw) × Rx(pitch) × Rz(roll)`——**Rz 先作用**；
- 手臂朝向 = `Rx(摆臂) × Rz(外张)`：先外张把手臂从体侧撑开 12.6°，再绕 X 前后摆。
  摆臂为正时臂端 z 分量为负 → 前摆；左右臂取相反摆臂角即成对侧步态。

### 3.6 数学验证（`tools/RigMathCheck.cs`）

姿态数学错了在代码审查里看不出来（编译通过、运行不报错，就是人姿势诡异）。
所以写了一个独立数学校验，5 个用例：

| 用例 | 验什么 | 结果 |
|---|---|---|
| 1 | 静止姿态下各部件落点 vs 旧「绝对定位」方案逐点重合 | ✅ 袍摆/腰带/上身/头/发冠零误差；双袖差 4.3cm（关节化后略贴身，更正确） |
| 2 | 行走时左袖前摆、右袖后摆、对侧幅度 >0.15 | ✅ |
| 3 | 俯身劳作：头探前 25cm、压低 | ✅ |
| 4 | 负重前抱：双手等高、同向前 | ✅ |
| 5 | 四状态 600 帧长跑，无 NaN、关节 y 有界 | ✅ |

**这套校验抓到过两个真 bug**：① 肩关节最初被放在脊柱顶端（颈高 y=1.35），袖子飞到头顶——
加了 `ShoulderDrop = 0.075` 修正；② `ShoulderHalf` 初值 0.17 小于上身半宽 0.18，袖子陷进躯干看不见
——改 0.20。**两个 bug 都不会报错，只会让人物看起来"不对劲"。**

运行方式（独立工程，引用工程产出的 `GodotSharp.dll`，已从主程序集排除）：

```bash
mkdir -p tools/rigcheck && cd tools/rigcheck
# 建一个 OutputType=Exe 的 csproj，Compile Include="../RigMathCheck.cs"，
# Reference GodotSharp → HintPath 指向 .godot/mono/temp/bin/Debug/GodotSharp.dll
dotnet run
```

---

## 4. 建筑模型：模块化零件套件

### 4.1 旧方案的结构性缺陷

旧路径是 `BuildingDef.ModelPath` 驱动的**整栋二选一**：

```
有 ModelPath 且资源可用 → 整栋实例化 PackedScene（每栋一个节点，无批渲）
否则                    → 整栋退回 BuildingModelFactory 原始体
```

三个问题：① 不同来源的整栋模型拼在一起必然风格割裂；② 必须一次性备齐所有建筑模型才能看到效果；
③ 等级/占地变化时无法复用零件（2×2 民居和 4×4 宅邸各要一个整栋模型）。

### 4.2 新方案：几何与网格解耦

**关键判断：那 531 行 `BuildingModelFactory` 是可信的，不该为了接资产而重写。**

占地换算、路向感知（房靠路 vs 院靠路）、院落围墙规则、等级缩放、招幌/灯笼点缀——
这些几何数学都验证过。而「用什么网格渲染这个角色」本来就是可换的。

于是架构变成：

```
BuildingModelFactory（几何数学，产出 13 类角色的 Transform3D + Color 列表）
        │
        ├─ 槽位未配置 GLB ──→ 原始体 MultiMesh 层（PrismMesh / BoxMesh / CylinderMesh…）
        │
        └─ 槽位配置了 GLB ──→ BuildingKit.MeshOf(slot) ──→ GLB MultiMesh 层
```

`scripts/map/layers/BuildingLayer.cs` 的 `FillRole()` 做二选一分流，
两侧都填空表即可清空该层——**不需要额外分支处理「资产被移除」**。

### 4.3 13 个零件槽位

| 槽位 | 内容 | 建议资产 |
|---|---|---|
| `Foundation` | 地基：房体下不透明基座 | — |
| `Step` | 台基：房体下方石阶基座 | — |
| `Body` | 房体：半透主体（民居可透视屋内） | `wall_plaster.glb` |
| `Roof` | 主坡屋顶：三棱柱 | `roof_gable.glb` ★首选 |
| `RoofEnd` | 庑殿端坡：威仪建筑第二重顶 | `roof_hip.glb` |
| `Eave` | 檐口：房体顶一周薄环 | `eave_band.glb` |
| `Ridge` | 屋脊：脊轴细条 | `ridge_bar.glb` |
| `Pillar` | 立柱：四角/边中/前廊 | `pillar_round.glb` |
| `Banner` | 招幌：商铺前脸竖条 | — |
| `Lantern` | 灯笼：檐下点缀 | — |
| `Window` | 门窗：大门 + 窗 | `window_lattice.glb` |
| `Wall` | 院墙：民居大院夯土围墙 | `wall_courtyard.glb` |
| `Door` | 门：沿占地边界贴墙 | `door_wood.glb` |

### 4.4 接入一份资产的完整步骤

1. 下载 CC0 包（如 Quaternius Medieval Village MegaKit Standard 版，约 100MB，OpenGameArt 有免费镜像）；
2. 把想要的模型重命名后放进 `assets/buildings/kit/`；
3. **首次要用 Godot 编辑器打开一次项目**生成 `.import`（否则 `ResourceLoader` 返 null）；
   ——开发期不必等：`GltfMeshMerger.LoadInstance` 有 `GltfDocument` 运行时解析兜底，丢进去就能看到；
4. 在 `scripts/configs/BuildingModelConfig.cs` 的 `Files` 字典里取消对应槽位注释；
5. 运行，控制台会打 `建筑零件已接入：Roof ← res://assets/buildings/kit/roof_gable.glb`。

**灰度上线**：只填 `Roof` 一行 → 全城屋顶立刻变资产，墙/柱/门照旧原始体，立即可见效果。
这就是模块化相比整栋的核心收益。

### 4.5 与「整栋资产」路径的分工

两条路径并存，各司其职：

| 路径 | 入口 | 适用 |
|---|---|---|
| **零件套件** | `BuildingModelConfig.Files` + `BuildingKit` | 大量重复建筑（民居/商铺/工坊/官署），要批渲、要风格统一 |
| **整栋资产** | `BuildingDef.ModelPath` + `BuildingAssetLoader` | 地标建筑（王府/宫殿），需要整体造型，数量个位数，实例化开销可忽略 |

---

## 5. UI 设计令牌

### 5.1 问题量化

Review 摸底的 UI 目录（12 文件 3350 行）：

- **47 处硬编码颜色**在 `UiTheme` 色板之外，其中 `new Color(0.7f,0.7f,0.7f)` 一档灰出现 **7 次**、
  `0.65f` 2 次、`0.6f` 1 次——**主题迁移残留**（`UiTheme.Muted` 早已存在但没人用）；
- **34 处** `AddThemeFontSizeOverride("font_size", N)`，字号值散成 12/13/16/18/20/24/36/40 八档；
- **22 处** `separation` 常量，值散成 4/6/8/10/12/18/24 七档；
- **18 处** BBCode 内联 `[color=#...]`（全在 `InspectPanel`，颜色语义散在字符串里）；
- `InspectPanel` 1126 行单类，53 处「批次XX」考古注释。

### 5.2 `DesignTokens` 收敛什么

`UiTheme` 管**颜色与字体**（那部分质量很好，不动）；`DesignTokens` 补**度量与状态语义**：

| 类别 | 令牌 |
|---|---|
| 间距 | `GapXs/S/M/L/Xl` = 4/8/12/16/24 |
| 字号 | `FontCaption/Body/Section/Title/Display` = 12/14/16/20/36 |
| 圆角 | `RadiusControl` 10 / `RadiusPanel` 14 |
| 控件高 | `RowHeight` 32 |
| 状态色 | `StatusBad` 朱（= `UiTheme.Seal`）/ `StatusWarn` 暗金 / `StatusGood` 青 / `StatusMuted` 弱文灰 |

并收掉三个复刻模式：`MarginContainer` 四边循环（旧 6 处逐字相同）、
「标题 + 脚注灰字」面板骨架（旧 5 处）、标签工厂（旧 4 处同模式）。

### 5.3 本轮已用令牌重写的面板

`SupplyPanel`（§6）是本轮唯一新写的面板，**全程用令牌，零硬编码色、零魔法字号**，
作为后续重填旧面板的样板。

### 5.4 旧面板重填（后续批次，不在本轮）

| 优先级 | 事项 |
|---|---|
| P1 | 替换 10 处 `0.7/0.65/0.6` 灰为 `DesignTokens.StatusMuted` |
| P1 | `InspectPanel` 1126 行拆分：7 种页面各一个渲染策略；阶段着色 switch 三处合一 |
| P1 | 抽 `SidePanel` 基类（CenterLeft/Right + Grow + Position∓12 + Margin + title/footer 骨架，四个侧面板共用） |
| P2 | `BuildMenu.ShowGroup` 139 行改数据驱动表 |
| P2 | `GameMenu` 标题行去重、Settings 页 5 个 CheckButton 表驱动、`HeightColor` 地形色阶移入 `TerrainConfig` |
| P2 | 消灭 73 处「批次XX」考古注释 |

---

## 6. 可玩性：供需面板

### 6.1 定位

`REVIEW_2026-10.md` 判定它是**性价比最高的单点修复**：

> `DemandLedger`（`scripts/sim/DemandLedger.cs:7` 注释「暂不显示给玩家」）驱动全部 NPC 决策，
> 玩家却是全场唯一信息盲人。数据四个字段已备好，只差一个面板。

### 6.2 呈现规则

- 只列**有需求**的货品（无需求项不占版面）；
- 排序：**短缺在前**，其余按可支撑天数升序（最危急永远在最上面）；
- 四档状态色：

| 档 | 条件 | 色 |
|---|---|---|
| 短缺 | `IsShort`（< 6.5 日，`EconomyConfig.DemandShortDays`） | 朱 `StatusBad` |
| 偏低 | < 15 日 | 暗金 `StatusWarn` |
| 充裕 | < 90 日 | 青 `StatusGood` |
| 滞销 | ≥ 90 日 或 ∞ | 弱文灰 `StatusMuted` |

- 「可撑天数」整行按最危急档位着色，远看也能扫出红行；
- 摘要行显示短缺种数，0.5s 节流刷新。

### 6.3 它改变了什么

**之前**：玩家 30 分钟后进入纯观看状态——该建的都建了，人口自己长，钱自己转，无事可做。
**之后**：看到「口粮 短缺 3.2 日」→ 知道该建官田/磨坊 → 观察库存回血 → 看到「木炭 滞销 120 日」
→ 知道别再扩产。**「缺什么先建什么」从 NPC 的隐形决策变成玩家的显性决策。**

入口：顶栏新增「供需」按钮（与政策/财政/研习互斥）。

---

## 7. 经济正循环修正（与视觉重构同批）

依据 `REVIEW_2026-10.md` §2.5，本轮修掉两个 P0 数值倒挂。

### 7.1 铸币局：唯一的货币发动机不该是漏卮

**旧值的问题**（4 岗、`salary=3000`、`upkeep=10000`、`mintPerWorkerDay=150`）：

```
收入 4×150×30 = 18,000/月  −  工资 12,000  −  维护 10,000  =  −4,000/月
```

满负荷运转确定失血。且批次九十八的变更记录自称「净赚 6,000」——**漏算了维护费 10,000**。

**新值 280**（`data/buildings.json`）：

| 在岗人数 | 净额/月 |
|---|---|
| 4（满负荷） | **+11,600** |
| 2（半负荷） | **+800** |
| 保本点 | **1.85 人** |

选 280 而非 230 的理由：230 要到 3 人才保本，2 人时仍亏 2,200——铸币局是唯一货币发动机，
**任何现实用工水平下都不该是陷阱**。

### 7.2 商铺：早期不该是负资产

`TaxSystem.BuildingTaxBase` 商铺一级 **3000 → 1500**（二级 6000→4000、三级 12000→9000）。

旧值 3,000 > 早期商铺月毛利 ≈2,450 → 店主净亏 ≈550/月，**早期建商铺是财政自杀**，
而商铺又是城市商业层的唯一入口。税基在批次九十五/九十八两次放大时，只校准了
「住宅 vs 一口粮」，漏了「商铺税 vs 商铺毛利」这条。

修正后早期商铺净余 ≈950/月，二级/三级仍随城市成长保持正收益。

---

## 8. 文件清单（本轮新增/改动）

### 新增

| 文件 | 行数 | 职责 |
|---|---|---|
| `scripts/agents/CitizenRig.cs` | ~300 | 人物关节骨骼（FK + 四段动作） |
| `scripts/configs/BuildingModelConfig.cs` | ~95 | 建筑零件槽位资产绑定 + 调和参数 |
| `scripts/render/BuildingKit.cs` | ~110 | 零件网格加载/合并/缓存 |
| `scripts/ui/DesignTokens.cs` | ~105 | UI 设计令牌（间距/字号/状态色） |
| `scripts/ui/SupplyPanel.cs` | ~180 | 城市供需面板 |
| `tools/RigMathCheck.cs` | ~130 | 关节数学独立校验（5 用例） |
| `specs/VISUAL_ARCH.md` | 本文 | 设计说明 |

### 改动

| 文件 | 改动 |
|---|---|
| `scripts/agents/CitizenAgent.cs` | 接入 `CitizenRig`；`ApplyLook` 改为关节绑定；`_Process` 的 bob 换为 `rig.Tick`；新增 `UpdateAnimState`/`IsToilActivity` |
| `scripts/map/layers/BuildingLayer.cs` | 新增套件层数组 + `FillRole` 二选一分流 |
| `scripts/ui/TopBar.cs` | 新增「供需」按钮与回调参数 |
| `scripts/ui/Hud.cs` | 挂载 `SupplyPanel`，纳入互斥/收起逻辑 |
| `scripts/policy/TaxSystem.cs` | 商铺税基 3000→1500 / 6000→4000 / 12000→9000 |
| `data/buildings.json` | `mintPerWorkerDay` 150→280 |
| `Bianjing.csproj` | 排除 `tools/**` 出主程序集（顶级语句工具脚本） |

---

## 9. 验证状态

| 项 | 状态 |
|---|---|
| `dotnet build` 0 warning / 0 error | ✅ |
| 关节数学 5 用例独立校验 | ✅ 全通过（抓到 2 个真 bug 并修掉） |
| 建筑套件：未配置任何 GLB 时行为与改动前完全一致 | ✅ 设计保证（`HasKitPiece=false` 不建套件层） |
| 供需面板数据源（`DemandLedger.Entries` 四字段） | ✅ 编译期已绑定 |
| 经济数值（铸币 280 / 商铺税 1500） | ✅ 算术复核 |
| **游戏内实机视觉确认** | ⏳ **待 master 跑一次**（关节姿态、套件层、面板排版都需肉眼验收） |

---

## 10. 下一步（未做，按优先级）

1. **实机验收本轮三项**（关节人物 / 供需面板 / 经济数值）——尤其关节姿态是否自然；
2. **接入第一批 GLB 零件**：建议只做 `Roof` 一个槽位，跑通 §4.4 全流程再扩；
3. 旧 UI 面板按 §5.4 重填令牌，`InspectPanel` 拆分；
4. 造景道具系统（石磨/水井/灯笼柱/石碑/摊位/柴堆/粮囷）——纯程序化，服务「造景」诉求，
   需新建道具 Def + 建造菜单分类 + 放置模式，属独立一批；
5. 轻量引导任务链（流民营 → 坊区 → 官田 → 都城），复用已有 `PostNews` 播报。
