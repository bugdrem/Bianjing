using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>游戏入口：装配全部系统与场景节点，按序驱动每日/每月结算。
/// 启动直进标题菜单（不建 GameState/不生成世界），地图只在新建/读档时生成并挂加载面板。</summary>
public partial class Main : Node3D
{
    private GameClock _clock;
    private DesirabilitySystem _desirability;
    private ZoneGrowthSystem _growth;
    private FarmlandSystem _farmland;
    private LifecycleSystem _lifecycle;
    private JobSystem _jobs;
    private TaxSystem _taxes;
    private EconomySystem _economy;
    private MaintenanceSystem _maintenance;
    private GoodsSystem _goods;
    private CraftingSystem _crafting;
    private PlantGrowthSystem _plants;
    private WildlifeSystem _wildlife;
    private MilestoneSystem _milestones;
    private TechSystem _techs;
    private DemandSystem _demand;
    private VisitorSystem _visitors; // 道路通边 → 四向邻城来人
    private Hud _hud;
    private BuildController _build; // 建造交互控制器：开局王爷府选位放置用
    private GameMenu _menu;
    private Dictionary<string, BuildingDef> _defs; // 建筑定义：启动即载入（纯数据，不涉地图）

    /// <summary>世界是否已装配（EnterWorld 守卫：F9 游戏中读档不二次装配）。</summary>
    private bool _inWorld;

    /// <summary>异步读档完成标志（LoadingScreen 轮询源：后台线程写、主线程读）。</summary>
    private bool _loadDone;

    /// <summary>异步读档结果（主线程应用；null 表示失败）。</summary>
    private (GameState gs, SaveMeta meta)? _loadResult;

    private Godot.Environment _env;   // 世界环境：按相机拉距动态开关深度雾化
    private DirectionalLight3D _sun;  // 主光源（批次七十四）：夜间联动变暗
    private RtsCameraRig _cameraRig;  // 相机云台：取拉距判定视角是否在地图内

    private float _autoSaveTimer;

    public override void _Ready()
    {
        EventBus.Reset();
        GameSettings.Load();
        GameSettings.Apply();

        // 全局挂载新中式 UI 主题（宣纸白毛玻璃 + 书法黑字 + 青辅 + 红印章）：
        // 根 Window 的 Theme 向下传播到所有 Control 子节点（含 Hud/GameMenu/LoadingScreen），
        // 无需逐文件改样式（设计见 .workbuddy/artifacts/UI评审与优化方案.md）。
        GetTree().Root.Theme = UiTheme.Build();

        // 启动直进标题菜单：不建 GameState、不生成世界（地图只在新建/读档时生成并挂加载面板）
        _defs = BuildingDef.LoadAll();
        _menu = new GameMenu(NewGame, SaveNamed, LoadSlotAsync, ReturnToTitle);
        AddChild(_menu); // 标题菜单自行暂停全树
    }

    /// <summary>进入世界（主线程）：装配环境/渲染器/相机/时钟/系统/HUD。
    /// 由新游戏/读档的加载完成回调调用；_inWorld 守卫防 F9 游戏中读档二次装配。
    /// 启动时不再创建 GameMenu——标题菜单已在 _Ready 创建，进入世界后由流程 MarkInGame。</summary>
    private void EnterWorld()
    {
        if (_inWorld)
            return;
        _inWorld = true;

        SetupEnvironment();

        var renderer = new GridRenderer();
        AddChild(renderer);
        AddChild(new AnimalRenderer());
        AddChild(new PileRenderer()); // 地面物资堆（收成/猎物/落果）
        AddChild(new BuildingStockRenderer()); // 屋内库存堆（透过半透明房体可见，耗尽才消）

        var cameraRig = new RtsCameraRig();
        AddChild(cameraRig);
        _cameraRig = cameraRig;

        _clock = new GameClock();
        AddChild(_clock);
        _clock.DayPassed += OnDayPassed;
        _clock.MonthPassed += OnMonthPassed;
        _desirability = new DesirabilitySystem();
        _growth = new ZoneGrowthSystem();
        _farmland = new FarmlandSystem();
        _lifecycle = new LifecycleSystem();
        _jobs = new JobSystem();
        _taxes = new TaxSystem();
        _economy = new EconomySystem();
        _maintenance = new MaintenanceSystem();
        _goods = new GoodsSystem();
        _crafting = new CraftingSystem();
        _plants = new PlantGrowthSystem();
        _wildlife = new WildlifeSystem();
        _milestones = new MilestoneSystem();
        _techs = new TechSystem();
        _demand = new DemandSystem();
        _visitors = new VisitorSystem();
        AddChild(_visitors);
        _visitors.Setup(_clock, GameState.I);

        _build = new BuildController(cameraRig, renderer);
        AddChild(_build);

        var agents = new AgentManager(_clock);
        AddChild(agents);
        _build.Agents = agents;

        _hud = new Hud(_build, _clock, SaveGame, LoadGame);
        AddChild(_hud);
        _build.Hud = _hud;
        _build.Visitors = _visitors; // 点选外城来人

        // 王爷府建成钩子：实时放置才触发（读档重建不经 PlaceBuilding，不会重复拨款/重生夫妻）
        EventBus.BuildingPlaced += OnBuildingPlaced;
        // 道路通边 → 四向邻城来人：首次通边播报
        EventBus.RoadReachedEdge += OnRoadReachedEdge;
    }

    /// <summary>建筑建成钩子：王爷府落成时一次性拨给开基资源（官库钱/粮 + 府库货品），
    /// 并携三对富裕年轻夫妻暂居府中（待玩家划区后自建新宅迁出）。</summary>
    private void OnBuildingPlaced(BuildingInstance b)
    {
        if (b.Def.Id != PrinceMansionConfig.DefId)
            return;
        var gs = GameState.I;

        // 一次性开基资源：钱/粮入官库，各类货品入王爷府库存
        gs.Money += PrinceMansionConfig.GrantMoney;
        gs.Ledger.Add("王爷府开基", PrinceMansionConfig.GrantMoney);
        gs.Food += PrinceMansionConfig.GrantFood;
        foreach (var (goodsId, amount) in PrinceMansionConfig.GrantGoods)
            b.StoreGoodsForce(goodsId, amount);

        // 随迁三对富裕年轻夫妻暂居府中
        _lifecycle.SettleNobleFamilies(gs, b);

        gs.PostNews("milestone", "王爷府落成，王爷携眷开府建衙，开基家底入库");
        EventBus.RaiseStatsChanged();
    }

    /// <summary>道路首次通到某方向地图边缘：播报对应邻城商旅往来。</summary>
    private void OnRoadReachedEdge(MapDir dir)
    {
        var gs = GameState.I;
        gs.PostNews("trade", $"{gs.Neighbors[(int)dir].Name} 道路已通，商旅往来渐多");
    }

    /// <summary>每日结算：日常事务（生长/民生/财政/物产/动物游走）。</summary>
    private void OnDayPassed()
    {
        var gs = GameState.I;
        gs.CurYear = _clock.Year;
        gs.CurMonth = _clock.Month;

        _desirability.EnsureUpdated(gs);
        _growth.TickDay(gs);
        _farmland.TickDay(gs); // 耕种区：农艺村民自动开垦/升级田块
        _lifecycle.TickDay(gs);
        _jobs.TickDay(gs);
        _taxes.TickDay(gs);
        _economy.TickDay(gs);
        _maintenance.TickDay(gs);
        _goods.TickDay(gs);
        _crafting.TickDay(gs); // 工坊/商铺把原料加工成成品
        _plants.TickDay(gs); // 挂果生长与落果
        _wildlife.TickDay(gs);
        _milestones.TickDay(gs); // 人口达标即晋级（解锁建筑/需求/限级）
        _techs.TickDay(gs); // 被动科技自动研成 + 主动项目逐日推进
        _demand.TickDay(gs); // 中央需求账本：城市级供需统计 + 内部广播（置于日结末，捕获当日终态）
        _visitors.TickDay(gs); // 外来访客：四向邻城来人调度（置于日结末，读取当日需求终态）
    }

    /// <summary>每月结算：大事（老化生死/重税民怨/植物生长/动物繁育）、月结工钱与账本轮转。</summary>
    private void OnMonthPassed()
    {
        var gs = GameState.I;
        _economy.PayMonthlySalary(gs); // 王爷月俸先入账
        _economy.PayWages(gs); // 批次七十四：雇工工钱改月结（下工只记账，月底统一发放）
        _lifecycle.TickMonth(gs);
        _taxes.TickMonth(gs);
        _goods.TickMonth(gs); // 农田到期收获，收成散落田格
        _plants.TickMonth(gs);
        _wildlife.TickMonth(gs);
        gs.Ledger.Rotate();
    }

    public override void _Process(double delta)
    {
        UpdateFog();
        UpdateDayNight((float)delta);

        // 自动保存：仅在游戏进行中（已进世界且未暂停）累计真实时间
        if (_hud == null || GameSettings.AutoSaveMinutes <= 0 || GetTree().Paused)
            return;
        _autoSaveTimer += (float)delta;
        if (_autoSaveTimer < GameSettings.AutoSaveMinutes * 60f)
            return;
        _autoSaveTimer = 0f;
        // 异步原子保存：主线程快照+序列化，后台线程写盘免卡帧；完成回调在后台线程，用 CallDeferred marshal 回主线程再碰 HUD
        SaveService.SaveAsync(_clock, SaveService.AutoSlot, "自动保存", ok =>
            Callable.From(() => _hud.ShowCellInfo(ok ? "已自动保存" : "自动保存失败（详见日志）")).CallDeferred());
    }

    /// <summary>昼夜光照联动（批次七十四）：夜晚主光与环境光调暗（保持可操作），平滑过渡；
    /// 白天/夜晚边界见 TimeConfig（DayStartHour=6 天亮 / NightStartHour=18 天黑）。</summary>
    private void UpdateDayNight(float delta)
    {
        if (_sun == null || _env == null)
            return;
        bool night = _clock != null && _clock.IsNight;
        float targetSun = night ? WorldConfig.NightSunEnergy : WorldConfig.DaySunEnergy;
        float targetAmb = night ? WorldConfig.NightAmbientEnergy : WorldConfig.DayAmbientEnergy;
        float k = 1f - Mathf.Exp(-delta * WorldConfig.DayNightSmoothPerSec);
        _sun.LightEnergy = Mathf.Lerp(_sun.LightEnergy, targetSun, k);
        _env.AmbientLightEnergy = Mathf.Lerp(_env.AmbientLightEnergy, targetAmb, k);
    }

    /// <summary>深度雾化当前已关闭：每帧确保 FogEnabled=false（保留按拉距开关的骨架，后续如需重启用回下方逻辑）。
    /// 原逻辑：拉距 > CameraConfig.FogEnableDistance（视角扩到地图外）才开雾，凑近地图内关雾省一次雾 pass。</summary>
    private void UpdateFog()
    {
        if (_env != null && _env.FogEnabled)
            _env.FogEnabled = false;
    }

    // ---- 新游戏 / 存读档 / 返回主菜单 ----

    /// <summary>新建城池（预览页确认后携种子调用）：此刻才建 GameState（1024² 地图），
    /// 全树暂停挂加载画面，后台以同种子重新生成——与预览地形完全一致；
    /// 完成回调（主线程）装配世界、归零日历并广播刷新（渲染器据 MapChanged 全量重建）。</summary>
    private void NewGame(string cityName, int seed)
    {
        GameState.I = new GameState(_defs) { CityName = cityName };

        // 生成期间整树暂停（LoadingScreen 自身 ProcessMode=Always 不受影响），防系统碰半成品数据
        GetTree().Paused = true;
        var loading = new LoadingScreen(cityName)
        {
            OnFinished = () =>
            {
                EnterWorld();
                StartFirstMansionPlacement(); // 开局即进入王爷府选位放置（点击地图落成），建成钩子拨款+安置随迁夫妻
                _clock.SetDate(1, 1);
                _autoSaveTimer = 0f;
                GameState.I.CurYear = 1;
                GameState.I.CurMonth = 1;

                EventBus.RaiseMapChanged();
                EventBus.RaiseZonesChanged();
                EventBus.RaiseStatsChanged();
                EventBus.RaiseGameLoaded();
                GetTree().Paused = false;
            },
            OnError = () =>
            {
                // 世界生成失败：不再装配世界，回到标题菜单并提示原因（GameState 保持未建，下次新建重来）
                GameState.I = null;
                GetTree().Paused = false;
                _menu.NotifyLoadFailed($"世界生成失败，已回到主菜单：{WorldGenerator.Error}");
            },
        };
        AddChild(loading);
        WorldGenerator.GenerateAsync(GameState.I, seed);
    }

    private void SaveNamed(string saveName)
    {
        SaveService.SaveAsync(_clock, SaveService.SlotFor(saveName), saveName, ok =>
            Callable.From(() => _hud.ShowCellInfo(ok ? $"已保存：{saveName}" : $"保存失败：{saveName}（详见日志）")).CallDeferred());
    }

    /// <summary>异步读档：挂加载面板（自定义完成源），后台线程读 LMDB；
    /// 完成回调（主线程）应用存档并装配世界；失败按来源提示——菜单发起留在读档页（树保持暂停），
    /// F9 快速读档仅 HUD 提示并恢复游戏。</summary>
    private void LoadSlotAsync(string slot)
    {
        _loadDone = false;
        _loadResult = null;
        GetTree().Paused = true;
        var loading = new LoadingScreen("读档中", () => _loadDone, () => "复原山河", () => 0.5f)
        {
            OnFinished = () =>
            {
                if (_loadResult == null)
                {
                    // 读档失败：菜单发起 → 留在读档页提示；F9 → 仅 HUD 提示并恢复游戏
                    if (_menu.Visible)
                        _menu.NotifyLoadFailed("读取失败：存档不完整或版本不符");
                    else
                    {
                        _hud.ShowCellInfo("读档失败（详见日志）");
                        GetTree().Paused = false;
                    }
                    return;
                }
                var (gs, meta) = _loadResult.Value;
                _loadResult = null;
                GameState.I = gs; // 先替换世界数据：主菜单读档时 _clock 尚不存在，EnterWorld 会新建
                EnterWorld();     // 装配渲染/系统/时钟（_inWorld 守卫：F9 游戏中读档不二次装配）
                FocusEnterView(); // 读档视角定位：王爷府（缺失时兜底地图中心）
                _clock.SetDate(meta.Year, meta.Month, meta.Day, meta.Hour);
                _autoSaveTimer = 0f;

                EventBus.RaiseMapChanged();
                EventBus.RaiseZonesChanged();
                EventBus.RaiseStatsChanged();
                EventBus.RaiseGameLoaded();
                _menu.MarkInGame();
                _menu.Resume();
                _hud.ShowCellInfo("已读档");
            },
        };
        AddChild(loading);
        SaveService.LoadAsync(slot, _defs, (gs, meta) =>
            Callable.From(() => { _loadResult = gs != null ? (gs, meta) : null; _loadDone = true; }).CallDeferred());
    }

    /// <summary>进入世界后的进场落点（批次八十二）：读档与新建同一套进场动画（俯冲节奏完全一致），
    /// 仅落点不同——读档重放动画落王爷府（缺失时兜底地图中心）；新地图不调用，由相机默认落点（中心）自然完成。</summary>
    private void FocusEnterView()
    {
        if (_cameraRig == null)
            return;
        var mansion = FindMansion();
        _cameraRig.RestartIntro(mansion != null ? MansionCenter(mansion) : Vector3.Zero); // 无王府（旧档/移除定义）兜底中心
    }

    /// <summary>查全局唯一王爷府实例（读档定位用）。</summary>
    private static BuildingInstance FindMansion()
    {
        foreach (var b in GameState.I.Buildings.Values)
            if (b.Def.Id == PrinceMansionConfig.DefId)
                return b;
        return null;
    }

    /// <summary>王爷府占地中心（世界坐标，镜头落点；与 InspectPanel.BuildingCenter 同口径）。</summary>
    private static Vector3 MansionCenter(BuildingInstance b) =>
        MapGrid.CellToWorld(b.Origin)
        + new Vector3(b.Def.SizeX * MapGrid.CellSize / 2f, 0f, b.Def.SizeY * MapGrid.CellSize / 2f);

    /// <summary>开局进入王爷府选位放置模式（批次八十一：王爷府由玩家手动首建，不再自动落成）：
    /// 预览跟随鼠标，点击地图即落成；建成钩子 OnBuildingPlaced 自动拨给开基资源并安置随迁夫妻，
    /// 落成后 TryPlaceBuilding 自动退出建造模式，首建门槛随之解锁一切营造。
    /// 读档不调用（旧档王爷府已建，PrinceMansionBuilt 自然跳过；mod 移除定义同样跳过）。</summary>
    private void StartFirstMansionPlacement()
    {
        var gs = GameState.I;
        if (gs.PrinceMansionBuilt || !gs.Defs.TryGetValue(PrinceMansionConfig.DefId, out var def))
            return;
        _build.SetBuildingMode(def);
        _hud.ShowCellInfo("请点击地图落成王爷府——落成后解锁道路/桥梁/坊区与一切营造");
    }

    private void ReturnToTitle()
    {
        GetTree().Paused = false;
        GetTree().ReloadCurrentScene();
    }

    // ---- 快速存读档（F5/F9）----

    private void SaveGame()
    {
        SaveService.SaveAsync(_clock, SaveService.QuickSlot, "快速存档", ok =>
            Callable.From(() => _hud.ShowCellInfo(ok ? "已快速保存 (F5)" : "快速保存失败（详见日志）")).CallDeferred());
    }

    private void LoadGame()
    {
        if (!SaveService.SaveExists(SaveService.QuickSlot))
        {
            _hud.ShowCellInfo("没有快速存档");
            return;
        }
        LoadSlotAsync(SaveService.QuickSlot);
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (_hud == null)
            return; // 标题菜单阶段 F5/F9 无副作用
        if (e is not InputEventKey key || !key.Pressed || key.Echo)
            return;

        switch (key.Keycode)
        {
            case Key.F5: SaveGame(); break;
            case Key.F9: LoadGame(); break;
        }
    }

    private void SetupEnvironment()
    {
        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55f, -35f, 0f),
            ShadowEnabled = true,
            LightColor = new Color(1f, 0.96f, 0.88f), // 微暖阳光，去冷白感
            LightEnergy = WorldConfig.DaySunEnergy,
            // 批次九十二：卷轴装裱层材质一律 Unshaded（不受光照），光照效果仅作用于地图内
        };
        AddChild(_sun);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial
            {
                // 地平线暖灰（参考宋画素净色调），降低天空蓝对环境光的染色
                SkyHorizonColor = new Color(0.78f, 0.75f, 0.67f),
                GroundHorizonColor = new Color(0.78f, 0.75f, 0.67f),
            } },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.55f,
            // Filmic 色调映射压高光 + 全局降饱和：整体去卡通鲜艳、归素雅
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            AdjustmentEnabled = true,
            AdjustmentSaturation = 0.85f,
            AdjustmentBrightness = 0.97f,
            // 深度雾化：拉远看卷轴/桌面外缘时远端融入暖雾（默认关，由 _Process 按相机拉距动态开关，性能优先）
            FogEnabled = false,
            FogLightColor = new Color(0.80f, 0.77f, 0.70f), // 暖米雾色，融入卷轴纸调与天际
            FogDensity = 0.001f,
            FogAerialPerspective = 0.4f,
        };
        _env = env;
        AddChild(new WorldEnvironment { Environment = env });

        // 卷轴装裱（地图外）独立成层：白底/绢帛纸面/卷轴圆柱/祥云置于 RenderLayers.Scroll，
        // 与地图内（RenderLayers.Map）分层渲染；图缘裙板由 GridRenderer 生成但同归卷轴层，
        // 地形断面→裙板→白底无缝衔接（详见 ScrollBackdrop）
        AddChild(new ScrollBackdrop());
    }
}
