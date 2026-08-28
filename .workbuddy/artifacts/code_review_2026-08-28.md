# 汴京盛卷（Bianjing）全量代码审查报告

> 日期：2026-08-28 ｜ 范围：96 个 `.cs` 文件，约 21,000 行 ｜ 引擎：Godot 4.7.1 Mono (C#)
> 方法：按子系统分 5 簇并行审查 + 重大发现抽验（存档线程、太阳朝向）
> 严重度：Critical（必崩/必卡死）＞ High（功能严重错误）＞ Medium（质量/性能/局部错误）＞ Low（规范/洁净度）

---

## 一、总体结论

架构扎实、无 NuGet、无 GDScript、注释率达标、配置集中化良好；已知的 10 个 Godot 4.7 API 坑在 UI 层已正确规避，**未复现"黑太阳(vec4/Color)"**。

但有 **1 个 Critical 线程越界**、**5 个 High 级功能错误**，以及一批 RULES.md 漂移（魔法数字、死代码）。最影响体验的是：存档读档可能跨线程触碰 UI、太阳渲染成细条、地图外硬边重现、小贩永不离开导致访客系统软锁。

---

## 二、Critical（发布前必须修）

### C1. 读档在后台线程触发 EventBus → 跨线程触碰 HUD
- 位置：`scripts/save/SaveService.cs:325`（`Task.Run` → `LoadData`）、`scripts/core/GameState.cs:181-194`（`RegisterEdgeCell` → `EventBus.RaiseRoadReachedEdge`）、`scripts/Main.cs:132,158-162`
- 现象：`LoadData` 在后台线程重建道路时会调用 `RegisterEdgeCell`，一旦边缘路连通就 `RaiseRoadReachedEdge`；若 `Main.OnRoadReachedEdge` 已订阅，会在**工作线程**执行 `GameState.I.PostNews(...)`（且是即将被丢弃的旧 state）并 `RaiseNewsPosted()` 触碰 HUD 节点。
- 触发条件：任何"地图边缘有路"的存档在 F9 / 菜单读档 / 游戏内读档时必现。
- 修复：读档重建期关闭边沿事件（`RegisterRoadCellSilent` 或重建后由 `Connected` 推导，不广播）；或 `LoadData` 全程不调 `RegisterEdgeCell`。

---

## 三、High（功能严重错误）

### H1. `EnterWorld` 重入后 VisitorSystem 持有旧 state
- `scripts/Main.cs:373-374` 在 `_inWorld` 时提前 return，导致 F9 / 菜单读档 / 游戏内新开局 后 `_visitors.Setup(_clock, GameState.I)`（行 115）不再执行，`VisitorSystem._gs` 仍指向已销毁的 state，其 `_Process` 会对已删除的建筑/路格刷访客。
- 修复：在 `GameState.I = gs` 之后重新注入（`_visitors.Setup(...)`），或让 `VisitorSystem` 直接读 `GameState.I` 而非缓存 `_gs`。

### H2. 太阳 Sprite 未广告牌化 → 渲染成细条/被剔除
- `scripts/sky/SkyBodies.cs:35-53,78-79`：`Sprite3D` 默认 `billboard=Disabled` 且未旋转，`MaterialOverride` 也会绕过 `Sprite3D.Billboard`；太阳四边形恒朝世界 +Z，而相机俯视，dot≈负 → 多数时刻呈 ~20% 宽度的侧条甚至背面剔除。
- 修复：在着色器 `vertex()` 里做广告牌变换，或每帧 `_sunCore.LookAt(GlobalPosition + sunDir)` 使 +Z 朝向原点（即相机方向）。

### H3. 太阳着色器未禁用雾 → 红黄被洗白
- `scripts/sky/SkyBodies.cs:31-33,102-125`：注释称"unshaded 即豁免雾"，但 Godot 4 对 spatial 着色器**无论 unshaded 都应用距离雾**（这正是 `fog_disabled` 存在的理由）。`Dist=700`、密度 0.00025 下约 16% 混雾，且 `FogAerialPerspective=0.5`，早晚红黄太阳被洗淡。
- 修复：在 `render_mode` 加 `fog_disabled`（核心与光晕都加）。

### H4. 地图外硬边重现（雾密度过低）
- `scripts/render/ScrollBackdrop.cs:38-54` + `WorldConfig.cs:94`：当前 `HorizonFogDensity=0.00025` 指数雾在 `FarClip=2000` 仅约 39% 不透明度；`tableSide=4400` 在相机平移到极限（±552）时真实桌缘距相机约 1648m，仍处远平面内 → 一道 ~35% 雾化的木质硬边对着天空可见。
- 修复（推荐）：雾改用 **`FogMode=Depth` + `FogDepthEnd≈FarClip`**，使雾在远平面处恰好饱和、城市内基本无雾——同时满足你"俯瞰城池不该有烟雾"的要求；或把 `HorizonFogDensity` 提到 ≥0.0025。

### H5. 无摊位小贩永不离开 → 访客系统软锁
- `scripts/visitors/ForeignVisitor.cs:100-108`（及 `Init:62-64`）：`Entering→Dwell` 时 `_dwell = Kind==Peddler ? float.MaxValue : …`，忽略了 `HasStall`。找不到路旁空位（无摊位）的小贩拿到 `float.MaxValue`，`_dwell-=dt` 永不归零且不会被 `ForceLeave()` 召回 → 访客泄漏，最终耗尽 `MaxConcurrentVisitors=60`，**所有新访客不再生成**。
- 修复：内层三元改为 `(Kind==Peddler && HasStall) ? float.MaxValue : VisitorConfig.DwellSecondsMin + …`。

---

## 四、Medium（质量 / 性能 / 局部错误）

| # | 位置 | 类别 | 问题 | 建议 |
|---|------|------|------|------|
| M1 | `GameState.cs:1166-1176` + `PlacementValidator` + `BuildController.UpdatePreview` 每帧 | Perf | `PrinceMansionBuilt→CountByDef` 是 O(建筑数) 字符串比较，被预览每帧调用 | GameState 上缓存 `bool`，`PlaceBuilding`/`DemolishBuilding` 时置位，读档后重建一次 |
| M2 | `SaveService.cs:113-126,301-349` | Quality | `Save()`/`Load()`/`ApplyLoaded()` 零调用者；`ApplyLoaded` 还复制了 Main 的逻辑却漏掉 `EnterWorld`/`FocusEnterView`，是陷阱 | 删除 |
| M3 | `SaveService.cs:520-526` | RULES | v24"私钱→家产"迁移不可达（行 391 仅放行 25），且每读档扫描全体居民；RULES §2 禁止存档兼容代码 | 删除 |
| M4 | `SaveService.cs:26` | Bug | `MapSizeBytes=32MB` 是 LMDB 硬上限；1024² 高度图+道路/水域 JSON 列表后期超 2× 写时余量 → `MDB_MAP_FULL`，仅 `PushWarning` | 提到 ~512MB（稀疏文件无实耗），或捕获 MapFull 重试 |
| M5 | `Main.cs:260,283,486-489,502-505,526-530` + `Milestones.cs` | RULES | 光照/里程碑逻辑魔法数字（`*100f`、`/0.5f`、`0.55f`、`0.82/0.97`、`RotationDegrees(-55,-35,0)`、`Color(1,.96,.88)`、`Min(100,Fun)`）；里程碑数据表放在 `core/` 而非 `configs/` | 常量搬 `WorldConfig`；数据表搬 `configs/` |
| M6 | `Main.cs:294-299` + `WorldConfig.cs` + `CameraConfig.cs` | Perf/Quality | `UpdateFog` 每帧重写常量 `FogDensity`；注释仍描述已删除的"按拉距升满"；`CameraConfig.FogEnableDistance` 零引用 | 在 `SetupEnvironment` 设一次，删 `UpdateFog` 与死常量 |
| M7 | `Main.cs:57-60` | Godot API/Quality | 注释称 `Root.Theme` 传播到 3 个 CanvasLayer（实际 Godot 4 主题链不穿透 CanvasLayer，这正是各 `UiTheme.Apply(this)` 存在的原因）；且 `UiTheme.Build()` 每次新建 Theme（4+ 活副本） | 删此行或缓存单一 Theme |
| M8 | `FrostedPanel.cs:45` | Godot API（新坑） | `vec2 half = panel_size * 0.5;` — `half` 是 GLSL 保留字，桌面 GL 多容忍，但 Metal/Vulkan 下编译失败导致所有毛玻璃面板黑屏 | 改名 `center` |
| M9 | `LoadingScreen.cs:50-56` | Perf | 加了全屏 `BackBufferCopy` 但本屏无 `FrostedPanel`、`SCREEN_TEXTURE` 不被采样 → 每帧白做一次全屏回拷 | 删除该 `BackBufferCopy`（Hud/GameMenu 的保留） |
| M10 | `FrostedPanel.cs:81` | Quality | `_mat.SetShaderParameter("panel_size", Size)` 在构造期 `Size==Zero` 求值，圆角遮罩首帧错误，仅靠 `Resized` 纠正 | `_Ready()` 内（布局后）再设 `panel_size`/`texel` |
| M11 | `ScrollBackdrop.cs:129-133,146-150` | Godot API | `AlbedoColor` 的 alpha（诗条 0.25、印章 0.85）被忽略（`StandardMaterial3D` 需 `Transparency=Alpha`）→ 占位全不透明 | 设 `Transparency=Alpha` |
| M12 | `ScrollBackdrop.cs:166,302` | Perf | `Image.CreateEmpty(useMipmaps:false)` 却在 ~32×/~73× 平铺大平面 → 远处严重摩尔纹 | 调用 `img.GenerateMipmaps()` 再 `CreateFromImage` |
| M13 | `HydraulicEroder.cs:79-87` | Bug | 笔刷偏移是 1-D 且只校验 `h.Length`，半径 3 内左右边缘会卷绕到相邻行对侧 → 侵蚀跨缝涂抹、质量不守恒 | 存 `(ox,oy)` 并校验列 |
| M14 | `RiverGenerator.cs:94-98` | Bug | 前向扇被堵/`guard>64` 时 `break` 并把 `cur=target` 瞬移最多 8 格（源宽仅 6m）→ 河面出现 2 格干洞 | 用 Bresenham 直线补完剩余段 |
| M15 | `Stall.cs:84-89` | Bug | 城市有农夫但无店铺/客栈时 `FirstShopOrInn` 返回 null，货物 `Inv.Take` 被移除却未入任何库存，仍扣 `gs.Money -= cost` → 无对应库存的财库流失 | 仅当真实入库 `amt>0` 才扣钱，或入 `gs.Food` 等汇 |
| M16 | `TaxSystem.cs:18-25` | RULES | 各建筑税额（10/25/50、80/150/300…）硬编码在逻辑里 | 搬 `EconomyConfig` 或 JSON |
| M17 | `GridRenderer.cs:152-172,984` | Godot API | 材质设 `Uv1Scale`（作用于 UV1），但网格提供的是 `TexUV`(UV0)，albedo 采样 UV0 → `Uv1Scale` 无效；且 `ImageTexture.CreateFromImage` 默认 `Repeat=Disabled`，道路世界坐标 UV 0..数百会钳到边缘色 → 路面可能纯色 | 二选一：保留世界坐标 TexUV 并删 `Uv1Scale`；或供 UV1 通道再保留。并显式 `tex.Repeat=Enabled` |
| M18 | `BuildController.cs:853-857` | Bug | `StampCenter` 公式对偶数宽度得 0.5·cs，与预览中心公式不一致 → 偶数宽街区/桥刷预览相对落点偏移半格 | 改用与 `BuildingOrigin`/`SetPreviewBox` 同源居中偏移 |

---

## 五、Low（规范 / 洁净度，批量）

- **魔法数字（RULES §5）**：`VisitorSystem.cs:56,94` / `ForeignVisitor.cs:68` / `VisitorSystem.cs:253`（方向数 4、年龄 `18+Next(0,43)`、货类 `1+Next(0,2)`）；`GoodsSystem.cs:104`/`EconomySystem.cs:91`（tech 键字符串 `"harvest"`/`"mint"`）；`BuildController.cs:397,935`（射线步进上界 4096）、`:629,681`（油漆桶上限 400_000）；`CitizenAgent.cs:1463`（BFS 上限 24000）；`PlacementValidator.cs:100-104`（仅 `official` 免单，未含注释声明的 `court`）。→ 提取为命名常量 / 入 `configs/`。
- **死代码/注释**：`Main.cs:294-299` 注释与现逻辑不符；`RtsCameraRig.cs` `Distance`/`FogEnableDistance` 已无用；`RtsCameraRig.cs:164-181` 注释称 intro 忽略输入但 `_UnhandledInput` 仍改写状态（应 early-return）；`WorldGenerator.cs:141-143` 斜坡采样用 `i+1`/`i+vps` 越界读相邻行首顶点、`maxH` 未播种易打印 `-3.4E+38`；`RiverGenerator.cs:24-30` 以 `i==0` 判定主河/湖泊，若河 0 失败则永不布湖。
- **SkyBodies.cs:91-92** `LookAt(Vector3.Zero)` 默认 up 在 `moonDir` 近垂直时会抛错，当前仅靠 `Main` 硬编码 `z=0.2` 偏移兜底 → 显式传安全 up 或 `|Y|>0.999` 时跳过。

---

## 六、已验证健康（无需改）

- **Godot 4.7 已知坑全部规避**：UI 层 `vec3 tint+float alpha`、`render_mode blend_*`、`Image.CreateEmpty`、递归 `UiTheme.Apply`、OptionButton `GetPopup` 钉主题、`BackBufferCopy` 首子节点+`Rect` 全屏、ItemList `font_hover_color`+`hovered` 样式盒、`RichTextLabel` 走 `default_color` —— 均无复现。
- **互市供需闭环闭合无 NPE**：`MakeCargo`/`SettleVenueTrade` 守卫充分（`PickVenue` null 早退、`holder` null 跳过、`gs.Demand` 非空、进口先快照再重置）；除 M15 财库泄漏外逻辑自洽。
- **大型文件稳健**：`CitizenAgent`(1849) 路网 A*+脱路 BFS 绕水、车道抖动避叠、状态机防死循环；`LifecycleSystem`(874) 用 `ToList()` 防字典并发改；`BuildController`(1116) 射线步长>0 不死循环；`GridRenderer`(1255) 分块增量重建、MultiMesh 分层设计佳。
- **无 NuGet、无 GDScript、注释率 >30%**。

---

## 七、建议修复顺序

1. **C1 存档线程越界**（跨线程碰 UI，必崩隐患）
2. **H5 小贩软锁** + **H1 EnterWorld 重入旧 state**（二者同源"state 重绑"，一并加 `RebindWorld(gs)`）
3. **H2+H3 太阳**：广告牌化 + `fog_disabled`（几行改动，直接修复可见天体）
4. **H4 地图外硬边**：雾改 `FogMode=Depth` + `FogDepthEnd≈FarClip`（兼顾"城池无雾"诉求）
5. **RULES 清理**：删死存档 API(M2)/v24 迁移(M3)、LMDB 上限(M4)、魔法数字(M5/M16/Low 批)
6. **性能**：`CountByDef` 缓存(M1)、`GridRenderer.OnCellChanged` 过度标脏、ScrollBackdrop 回拷(M9)
7. **视觉细节**：`FrostedPanel.half` 保留字(M8)、ScrollBackdrop `Transparency`/mipmap(M11/M12)、`GridRenderer` 道路纹理 `Repeat`(M17)、`BuildController.StampCenter`(M18)、地形边缘采样(M13/M14/Low)
