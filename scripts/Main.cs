using System;
using Godot;

namespace Bianjing;

/// <summary>游戏入口：装配全部系统与场景节点，按序驱动每日/每月结算。</summary>
public partial class Main : Node3D
{
    private GameClock _clock;
    private DesirabilitySystem _desirability;
    private ZoneGrowthSystem _growth;
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
    private Hud _hud;
    private GameMenu _menu;

    private Godot.Environment _env;   // 世界环境：按相机拉距动态开关深度雾化
    private RtsCameraRig _cameraRig;  // 相机云台：取拉距判定视角是否在地图内

    private float _autoSaveTimer;

    public override void _Ready()
    {
        EventBus.Reset();
        GameSettings.Load();
        GameSettings.Apply();

        GameState.I = new GameState(BuildingDef.LoadAll());

        // 世界生成走后台线程（此时渲染节点未建，生成只动纯数据 Map/Plants/Animals，线程安全）；
        // 加载画面主线程轮询进度，完成回调 FinishSetup 装配全部节点与系统。
        // 启动阶段确实在生成世界（主菜单背后的城市），标题如实描述——阶段文案由 WorldGenerator 实时上报
        var loading = new LoadingScreen("初入汴京 · 正在生成世界") { OnFinished = FinishSetup };
        AddChild(loading);
        WorldGenerator.GenerateAsync(GameState.I);
    }

    /// <summary>世界生成完毕后的装配收尾（主线程）：环境/渲染器/相机/时钟/系统/HUD/菜单。
    /// GameMenu 最后加入并自行暂停全树展示主菜单。</summary>
    private void FinishSetup()
    {
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

        var build = new BuildController(cameraRig, renderer);
        AddChild(build);

        var agents = new AgentManager(_clock);
        AddChild(agents);
        build.Agents = agents;

        _hud = new Hud(build, _clock, SaveGame, LoadGame);
        AddChild(_hud);
        build.Hud = _hud;

        // 游戏菜单最后加入：_Ready 时暂停全树展示主菜单，ESC 呼出暂停菜单
        _menu = new GameMenu(NewGame, SaveNamed, LoadSlot, ReturnToTitle);
        AddChild(_menu);

        // 王爷府建成钩子：实时放置才触发（读档重建不经 PlaceBuilding，不会重复拨款/重生夫妻）
        EventBus.BuildingPlaced += OnBuildingPlaced;
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

    /// <summary>每日结算：日常事务（生长/民生/财政/物产/动物游走）。</summary>
    private void OnDayPassed()
    {
        var gs = GameState.I;
        gs.CurYear = _clock.Year;
        gs.CurMonth = _clock.Month;

        _desirability.EnsureUpdated(gs);
        _growth.TickDay(gs);
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
    }

    /// <summary>每月结算：大事（老化生死/重税民怨/植物生长/动物繁育）与账本轮转。</summary>
    private void OnMonthPassed()
    {
        var gs = GameState.I;
        _economy.PayMonthlySalary(gs); // 王爷月俸先入账
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

        // 自动保存：仅在游戏进行中（未暂停）累计真实时间
        if (GameSettings.AutoSaveMinutes <= 0 || GetTree().Paused)
            return;
        _autoSaveTimer += (float)delta;
        if (_autoSaveTimer < GameSettings.AutoSaveMinutes * 60f)
            return;
        _autoSaveTimer = 0f;
        // 异步原子保存：主线程快照+序列化，后台线程写盘免卡帧；完成回调在后台线程，用 CallDeferred marshal 回主线程再碰 HUD
        SaveService.SaveAsync(_clock, SaveService.AutoSlot, "自动保存", ok =>
            Callable.From(() => _hud.ShowCellInfo(ok ? "已自动保存" : "自动保存失败（详见日志）")).CallDeferred());
    }

    /// <summary>深度雾化当前已关闭：每帧确保 FogEnabled=false（保留按拉距开关的骨架，后续如需重启用回下方逻辑）。
    /// 原逻辑：拉距 > CameraConfig.FogEnableDistance（视角扩到地图外）才开雾，凑近地图内关雾省一次雾 pass。</summary>
    private void UpdateFog()
    {
        if (_env != null && _env.FogEnabled)
            _env.FogEnabled = false;
    }

    // ---- 新游戏 / 存读档 / 返回主菜单 ----

    /// <summary>新建城池：重置世界数据后全树暂停，挂加载画面走后台生成；
    /// 完成回调（主线程）恢复暂停、归零日历并广播刷新（渲染器据 MapChanged 全量重建）。</summary>
    private void NewGame(string cityName)
    {
        GameState.I = new GameState(GameState.I.Defs) { CityName = cityName };

        // 生成期间整树暂停（LoadingScreen 自身 ProcessMode=Always 不受影响），防系统碰半成品数据
        GetTree().Paused = true;
        var loading = new LoadingScreen(cityName)
        {
            OnFinished = () =>
            {
                GetTree().Paused = false;
                _clock.SetDate(1, 1);
                _autoSaveTimer = 0f;
                GameState.I.CurYear = 1;
                GameState.I.CurMonth = 1;

                EventBus.RaiseMapChanged();
                EventBus.RaiseZonesChanged();
                EventBus.RaiseStatsChanged();
                EventBus.RaiseGameLoaded();
            },
        };
        AddChild(loading);
        WorldGenerator.GenerateAsync(GameState.I);
    }

    private void SaveNamed(string saveName)
    {
        SaveService.SaveAsync(_clock, SaveService.SlotFor(saveName), saveName, ok =>
            Callable.From(() => _hud.ShowCellInfo(ok ? $"已保存：{saveName}" : $"保存失败：{saveName}（详见日志）")).CallDeferred());
    }

    private bool LoadSlot(string slot) => SaveService.Load(_clock, slot);

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
        if (SaveService.Load(_clock, SaveService.QuickSlot))
        {
            _menu.MarkInGame();
            _hud.ShowCellInfo("已读档 (F9)");
        }
        else
        {
            _hud.ShowCellInfo("没有快速存档");
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
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
        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55f, -35f, 0f),
            ShadowEnabled = true,
            LightColor = new Color(1f, 0.96f, 0.88f), // 微暖阳光，去冷白感
            LightEnergy = 0.95f,
        };
        AddChild(sun);

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
