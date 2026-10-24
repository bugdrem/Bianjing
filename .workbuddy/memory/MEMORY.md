# 汴京盛卷 (Bianjing) 项目记忆

## 开发约定（用户明确要求，务必遵守）
- 早期开发阶段：**无需存档兼容**，可大幅重构；新功能直接用代码原始体/骨骼化，不追求资产管线完备。
- 逻辑全部 C#；常量集中 `configs/`；每次调整在 `specs/` 更新日志（CHANGELOG.md = 唯一迭代记录，DESIGN.md = 技术/业务权威，ECONOMY.md = 经济权威，TIMELINESS.md = 时效权威，CODEMAP.md = 业务↔代码速查）。
- 构建：`dotnet build Bianjing.csproj -nologo -p:UseSharedCompilation=false`（不加此参数偶发卡死/exit 1）。
- Bash 工具 PATH 缺 `/usr/bin`，命令前先 `export PATH="/usr/bin:/bin:$PATH"`。

## 最容易踩的坑（按踩雷频率排序）
- **时间尺度**：`GameClock.Day` 是「旬」= 24 游戏时 = 60 现实秒；`RealDaysPerGameDay=10` ⇒ 1 旬≙10 真实日、1 月≙1 真实月、1 年=36 分钟。**「日值」配置一律是"每真实日"**（结算处经 `TimeConfig.PerRealDay` 换算）；「月值」= 真实月。曾把旬当成"天"，经济尺度整体错 10 倍。
- **Godot 4.7 C# 没有 `Bone3D`**：骨以索引管理（`Skeleton3D.AddBone/SetBoneParent/SetBoneRest/SetBonePose/FindBone`），网格挂骨用 `BoneAttachment3D`，动画走 `SetBonePose`（pose 取纯旋转即绕 rest 原点，正好作支点）。
- **着色器 `uniform vec4` 不接受 `Color`**（`SetShaderParameter` 传 Color 被静默丢弃 → 黑盘）：拆成 `vec3 tint + float alpha`。
- **`RichTextLabel` 正文字色走主题 `default_color`，不是 `font_color`**；`ItemList` 漏设 `font_hover_color` 时 hover 白字在浅米面板上不可读。
- **`Sprite3D` 无贴图 = 零尺寸四边形**（自定义 shader 也要挂 Texture）；真毛玻璃需 `BackBufferCopy` 且显式 Rect 覆盖 viewport（默认 256×256）。
- **`Cell` 是 struct 且活在百万格 dense 数组（`MapGrid`）→ 不能加字段**；道路状态必须走 `GameState.RoadStates` 稀疏字典。
- `MathF` 无 `Clamp` 重载（用 `Math.Clamp`）；`const bool` 配置开关被编译器折叠致 CS0162，须 `static readonly`。
- `GoodsStack.Fresh` / `AnimalObj.Fresh` 字段初始化器 `= 100f` 不可省；`HousingCapacity` 在 BuildingInstance 上而非 Def 上。

## 架构要点
- 地图渲染：`GridRenderer` 只是协调器（脏标 + 预算仲裁 12+32 + 裙板），内容在 `scripts/map/layers/` 六图层（Terrain/Water/Road/Vegetation/Building/Overlay，基类 MapLayer）。**图层不订阅 EventBus、不写 _Process**（否则预算翻六倍）。新增地表层 = 缓冲进 ChunkGeometry + Builder 产出 + GridRenderer 分发。
- 水系 = 汇流求解制（FlowRouter→RiverNetwork→LakeGenerator→`RiverGenerator.BuildWaterSystem`），参数在 `WaterConfig`，勿回退旧「草图引导线」。山体 2 座 88~105m 主峰，MaxHeight=110。
- glTF 资产（`GltfMeshMerger` 共用装载/重组/归一化，Tree/AnimalModelFactory 为薄层）：① 含 JOINTS_0/animation 的蒙皮模型**不能走 MultiMesh**（静态批渲丢动画），须逐实例 + AnimationPlayer；② 蒙皮模型包围盒以 `Skeleton3D` 为基准（Sheep/Pug 骨架缩放≠网格缩放）；③ glTF 根节点 Transform 别直接覆盖，套包装节点；④ `.glb` 需编辑器导入生成 `.import`，未导入 `ResourceLoader` 返 null 静默回退（开发期可用 `GltfDocument` 直接解析）；⑤ 底色 = baseColorFactor × 顶点色 COLOR_0，必须相乘。
## 经济（权威 = specs/ECONOMY.md）
- 钱：外部注入→官库→家庭→铺面/工坊→回流官库；货：自然→原料→中间品→成品→消费单向流。
- 三条守恒律：① 官库该付的先发放、按实扣款（`PayBuildWages`）；② 家庭该交的实扣公产、不足少收；③ **价只有一个出口**（`PriceOf`/`RetailPrice`/`BuyerPrice`），调用点不得自乘倍率。
- 物价锚（批次96）：一升米=10 文=一人一天用量；成品价≈原料+燃料+22 文附加值（各链每工日毛利 35~56 文）。家计 30 文/人/月；官库收入由土地税接管（BuildingTaxBase 民居 60 / 商铺 300）。
- 工资分制：`official`/`field` 固定月俸；`grown` 店坊无固定工钱（售货分账、自负进料货款 ≈ 合伙人）；`court` 朝廷凭空出。
- 实测：官库 20 人 +25k/月、400 人 +170k/月；无业者有维护费+营造工钱+朝廷采购三重保底不会归零；**铸币局官库净亏 −4000/月**（唯一货币发动机却是漏卮）；飞轮最紧一环 = 流民营 16 空位 + 玩家划坊区。
- 未做：分品类库存定价、外贸不受城内库存倍率影响、农田开垦 0 成本、朝廷采购无配额。

## 时效系统（权威 = specs/TIMELINESS.md，四批已完成）
- 双轴独立计时：Fresh(0~100，≥60 全效 / 15~60 渐损 / <15 失效可 `Salvage` 0.3 降 Scrap) + Lifespan（归零消亡）。状态只存 Fresh/UsedLifespan/RenewCount，Span 与衰减速率**一律现算**（`TimedRules.Resolve`）。
- **稳定期 `PlateauDays`**：期内 Fresh 恒 100，之后才线性下滑——「多久完全不变」与「之后坏多快」是两个独立旋钮，只调后者会把一切退化成易耗品。粮食 (112,480,48)：仓房 2.96 年打折 / 6.32 年不可食。
- 翻新 = 拉回固定值 + `寿限×0.65^n` / `衰减×1.8^n`（递减收益自然涌现）；**执行后必须重新归档**，直接改字段会赖在错误档位。NPC 自动翻新线：鲜度<40 且翻新<2 次。
- 效果打折 = 消耗时有效量折算（取用量 × EffectOf(Fresh)），系数 <0.02 不参与消耗；环境修正走 `ITimedModifier` + `TimedRegistry`（乘算，基准=露天+春）。合并键不含环境、用「已耗寿限」而非剩余。
- 未做：配方状态条件、加工输入按鲜度折算。

## 可玩性评估结论（2026-10 全代码 review，详见 specs/REVIEW_2026-10.md）
- **「模拟活着，但游戏未成形」**：无目标/终局/失败/危机；开局仅王爷府一句话引导；**`DemandLedger` 驱动全部 NPC 决策却"暂不显示给玩家"**（最大信息断点）；朝廷赏赐/任务链（死常量）/瘟疫/灾害/特产均不存在；里程碑到「京城」封顶且拨款 150~3000 文相对造价无感。
- 反馈环不会死：饥饿螺旋、就业贫困、衰减维护均有多重刹车，未发现必然硬锁；财政螺旋是条件性软锁（官库可无限负，但月俸恒定 + 可拆楼还债）。
- **教训：引用历史批次的核算结论必须重算**——批次九十八自称铸币局"净赚 6,000/月"，实为漏算维护费 10,000，真实净额 −4,000/月。
