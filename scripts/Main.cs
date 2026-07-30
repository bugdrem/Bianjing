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

    private float _autoSaveTimer;

    public override void _Ready()
    {
        EventBus.Reset();
        GameSettings.Load();
        GameSettings.Apply();

        GameState.I = new GameState(BuildingDef.LoadAll());
        SeedWorld();

        SetupEnvironment();

        var renderer = new GridRenderer();
        AddChild(renderer);
        AddChild(new AnimalRenderer());
        AddChild(new PileRenderer()); // 地面物资堆（收成/猎物/落果）
        AddChild(new BuildingStockRenderer()); // 屋内库存堆（透过半透明房体可见，耗尽才消）

        var cameraRig = new RtsCameraRig();
        AddChild(cameraRig);

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
        _lifecycle.TickMonth(gs);
        _taxes.TickMonth(gs);
        _goods.TickMonth(gs); // 农田到期收获，收成散落田格
        _plants.TickMonth(gs);
        _wildlife.TickMonth(gs);
        gs.Ledger.Rotate();
    }

    public override void _Process(double delta)
    {
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

    // ---- 新游戏 / 存读档 / 返回主菜单 ----

    /// <summary>新地图初始化：开凿河道、隆起山体、随机铺树、投放野物。</summary>
    private static void SeedWorld()
    {
        var rng = new Random();
        RiverGenerator.Carve(GameState.I.Map, rng);
        MountainGenerator.Raise(GameState.I.Map, rng); // 河后山前：山形定了再决定哪长树
        TreeGenerator.Scatter(GameState.I, rng);
        new WildlifeSystem().SeedInitial(GameState.I);
    }

    /// <summary>新建城池：重置世界、重新生成地表、归零日历。</summary>
    private void NewGame(string cityName)
    {
        GameState.I = new GameState(GameState.I.Defs) { CityName = cityName };
        SeedWorld();
        _clock.SetDate(1, 1);
        _autoSaveTimer = 0f;

        GameState.I.CurYear = 1;
        GameState.I.CurMonth = 1;

        EventBus.RaiseMapChanged();
        EventBus.RaiseZonesChanged();
        EventBus.RaiseStatsChanged();
        EventBus.RaiseGameLoaded();
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
        };
        AddChild(sun);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.7f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        // 地面背景平面：落在水面（y=-0.5）之下作河床/图外背景；陆地靠逐格土柱立于其上（顶 0），故河道自然下凹
        float extent = MapGrid.Size * MapGrid.CellSize + 80f;
        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(extent, extent) },
            Position = new Vector3(0f, -0.6f, 0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.45f, 0.5f, 0.32f),
            },
        };
        AddChild(ground);
    }
}
