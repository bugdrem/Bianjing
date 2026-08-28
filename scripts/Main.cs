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
    private ProceduralSkyMaterial _skyMat; // 天空材质：昼夜间平滑插值配色（蓝黑夜空）
    private DirectionalLight3D _sun;  // 主光源（批次七十四）：随一日六时转动、夜间变暗
    private Vector3 _sunDir = new Vector3(0.2f, 1f, 0.2f).Normalized(); // 平滑后的“指向太阳”方向（避免整点跳变）
    private RtsCameraRig _cameraRig;  // 相机云台：取拉距判定视角是否在地图内
    private SkyBodies _sky;           // 天空天体：太阳（带光晕）/ 月亮（相位）/ 月光平行光

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

        SetupTitleBackground(); // 标题阶段主视口给个天空背景：菜单玻璃才有内容可虚化（进世界后复用，不重复建）
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

        _sky = new SkyBodies();
        AddChild(_sky);

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

    /// <summary>昼夜光照联动：太阳随一日六时走“东（地平）→ 南（正午顶）→ 西（地平）”弧线，
    /// 阴影方向随之从西扫向东（晨昏长软、正午短硬）；太阳高度角驱动阴影“浅→深→浅”；
    /// 夜间太阳沉到地平线下并关阴影。天空与光照均按 DayNightSmoothPerSec 平缓过渡。
    /// 昼夜边界见 TimeConfig（DayStartHour=5 天亮 / NightStartHour=19 天黑）。</summary>
    private void UpdateDayNight(float delta)
    {
        if (_sun == null || _env == null || _clock == null)
            return;
        int dayStart = TimeConfig.DayStartHour;     // 5（平旦，日出东方）
        int nightStart = TimeConfig.NightStartHour; // 19（黄昏，日入西方）
        int dayLen = nightStart - dayStart;         // 14 个白天时
        float h = _clock.Hour;
        bool night = h < dayStart || h >= nightStart;

        float targetSun, targetAmb;
        Vector3 targetDir;
        bool targetShadow;
        if (!night)
        {
            // theta: 0(东地平) → π(西地平)，正午 = π/2 在顶
            float theta = (h - dayStart) / (float)dayLen * Mathf.Pi;
            float elev = Mathf.Sin(theta); // 0(地平) .. 1(正午)
            // 指向太阳的方向：水平分量朝东(cos>0)→西(cos<0)，垂直分量=elev；z 给一点南偏，阴影主沿东西
            targetDir = new Vector3(Mathf.Cos(theta), elev, 0.2f).Normalized();
            // 主光：正午强、晨昏弱；环境光反相 → 晨昏阴影“浅”（长而柔）、正午“深”（短而硬）
            targetSun = Mathf.Lerp(WorldConfig.DawnSunEnergy, WorldConfig.DaySunEnergy, elev);
            targetAmb = Mathf.Lerp(WorldConfig.DawnAmbientEnergy, WorldConfig.DayAmbientEnergy, elev);
            targetShadow = true;
        }
        else
        {
            targetDir = new Vector3(0f, -1f, 0.2f).Normalized(); // 太阳沉到地平线下
            targetSun = WorldConfig.NightSunEnergy;
            targetAmb = WorldConfig.NightAmbientEnergy;
            targetShadow = false; // 夜晚不显示阴影
        }

        float k = 1f - Mathf.Exp(-delta * WorldConfig.DayNightSmoothPerSec);
        // 太阳方向平滑（避免整点跳变）；用指向太阳的方向摆位并 LookAt 原点，阴影自然落在背光侧
        _sunDir = _sunDir.Lerp(targetDir, k).Normalized();
        _sun.GlobalPosition = _sunDir * 100f;
        _sun.LookAt(Vector3.Zero);
        _sun.LightEnergy = Mathf.Lerp(_sun.LightEnergy, targetSun, k);
        _env.AmbientLightEnergy = Mathf.Lerp(_env.AmbientLightEnergy, targetAmb, k);
        _sun.ShadowEnabled = targetShadow;
        // 夜晚环境光改用固定中性色，避免采样蓝天导致地图泛蓝；白天回归天空采样（自然天光）
        _env.AmbientLightSource = night ? Godot.Environment.AmbientSource.Color : Godot.Environment.AmbientSource.Sky;
        if (night)
            _env.AmbientLightColor = WorldConfig.NightAmbientColor;
        // 天空配色昼夜平滑插值：白天淡灰蓝 → 夜晚蓝黑
        if (_skyMat != null)
        {
            Color top = night ? WorldConfig.NightSkyTop : WorldConfig.DaySkyTop;
            Color hor = night ? WorldConfig.NightSkyHorizon : WorldConfig.DaySkyHorizon;
            Color gnd = night ? WorldConfig.NightSkyGround : WorldConfig.DaySkyGround;
            _skyMat.SkyTopColor = _skyMat.SkyTopColor.Lerp(top, k);
            _skyMat.SkyHorizonColor = _skyMat.SkyHorizonColor.Lerp(hor, k);
            _skyMat.GroundHorizonColor = _skyMat.GroundHorizonColor.Lerp(gnd, k);
            // 雾色与地平天空同色 → 远端地形/卷轴融入雾色，地图外硬切线（天际线）消失、地平柔化
            _env.FogLightColor = _env.FogLightColor.Lerp(hor, k);
        }

        // 太阳颜色：地平红黄 → 正午白（同步照亮场景，营造早晚金辉）；夜间沉到地平线下、能量极低
        float sunColT = Mathf.Clamp(_sunDir.Y / 0.5f, 0f, 1f);
        _sun.LightColor = WorldConfig.SunWarmColor.Lerp(WorldConfig.SunNoonColor, sunColT);

        // 月相：随月份周期（MoonCycleDays=一个月朔望循环），连续推进；太阳方向驱动月亮升落与亮度反相
        float moonPhase = ((_clock.AbsoluteDay + _clock.Hour / 24f) / WorldConfig.MoonCycleDays) % 1f;
        if (moonPhase < 0f) moonPhase += 1f;
        _sky?.UpdateSky(_sunDir, moonPhase);
    }

    /// <summary>地平雾恒定低密度、不再按拉距升满：凭指数衰减仅在极远缘软化天际线，
    /// 城市任何视角（近景/俯瞰）都接近无雾；雾色由 UpdateDayNight 随昼夜在地平天空色间平滑过渡。</summary>
    private void UpdateFog()
    {
        if (_env == null) return;
        if (!_env.FogEnabled) _env.FogEnabled = true;
        _env.FogDensity = WorldConfig.HorizonFogDensity;
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
                _visitors?.Setup(_clock, gs); // F9/游戏内读档时 EnterWorld 提前返回，此处重绑访客系统到新 GameState，避免用旧 state 刷访客
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

    /// <summary>标题阶段背景：主视口先挂一个天空环境（无光照/无卷轴），让标题菜单的 FrostedPanel 有内容可虚化，
    /// 否则背后是空视口、玻璃面板采到的是纯白。进世界后 EnterWorld.SetupEnvironment 复用同一 _env，不重复建 WorldEnvironment。</summary>
    private void SetupTitleBackground()
    {
        if (_env != null)
            return;
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial
            {
                // 白天淡灰蓝（夜间由 UpdateDayNight 平滑插值到蓝黑）
                SkyTopColor = WorldConfig.DaySkyTop,
                SkyHorizonColor = WorldConfig.DaySkyHorizon,
                GroundHorizonColor = WorldConfig.DaySkyGround,
            } },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.55f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            AdjustmentEnabled = true,
            AdjustmentSaturation = 0.82f,
            AdjustmentBrightness = 0.97f,
        };
        _env = env;
        _skyMat = (ProceduralSkyMaterial)env.Sky.SkyMaterial;
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void SetupEnvironment()
    {
        if (_sun == null)
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
        }

        // 标题阶段已建好天空环境则复用，避免重复 WorldEnvironment
        if (_env == null)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial
                {
                    // 白天淡灰蓝（夜间由 UpdateDayNight 平滑插值到蓝黑）
                    SkyTopColor = WorldConfig.DaySkyTop,
                    SkyHorizonColor = WorldConfig.DaySkyHorizon,
                    GroundHorizonColor = WorldConfig.DaySkyGround,
                } },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.55f,
                // Filmic 色调映射压高光 + 全局降饱和：整体去卡通鲜艳、归素雅
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                AdjustmentEnabled = true,
                AdjustmentSaturation = 0.82f,
                AdjustmentBrightness = 0.97f,
                // 地平雾（天际线柔化）：拉远看地图外/卷轴时远端地形与装裱随距离融入雾色（与地平天空同色），
                // 地图外硬切线（天际线）消失、地平柔化为烟雾感；密度由 _Process 按相机拉距从 0 平滑升到 HorizonFogDensity
                // （凑近地图内几乎无雾、城市清晰；拉远看外缘才起烟）。雾色随昼夜在地平天空色间平滑过渡（见 UpdateDayNight）。
                FogEnabled = true,
                FogMode = Godot.Environment.FogModeEnum.Exponential,
                FogLightColor = WorldConfig.DaySkyHorizon, // 初始地平雾色（白天淡灰蓝，夜间由 UpdateDayNight 插值到蓝黑）
                FogDensity = 0f, // 初始 0，_Process 按拉距插值（进场俯瞰即起雾、落近城市即散）
                FogAerialPerspective = WorldConfig.HorizonFogAerial,
            };
            _env = env;
            _skyMat = (ProceduralSkyMaterial)env.Sky.SkyMaterial;
            AddChild(new WorldEnvironment { Environment = env });
        }


        // 卷轴装裱（地图外）独立成层：白底/绢帛纸面/卷轴圆柱/祥云置于 RenderLayers.Scroll，
        // 与地图内（RenderLayers.Map）分层渲染；图缘裙板由 GridRenderer 生成但同归卷轴层，
        // 地形断面→裙板→白底无缝衔接（详见 ScrollBackdrop）
        AddChild(new ScrollBackdrop());
    }
}
