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
    private PlantGrowthSystem _plants;
    private WildlifeSystem _wildlife;
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
        _plants = new PlantGrowthSystem();
        _wildlife = new WildlifeSystem();

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
        _plants.TickDay(gs); // 挂果生长与落果
        _wildlife.TickDay(gs);
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
        SaveService.Save(_clock, SaveService.AutoSlot, "自动保存");
        _hud.ShowCellInfo("已自动保存");
    }

    // ---- 新游戏 / 存读档 / 返回主菜单 ----

    /// <summary>新地图初始化：开凿河道、随机铺树、投放野物。</summary>
    private static void SeedWorld()
    {
        var rng = new Random();
        RiverGenerator.Carve(GameState.I.Map, rng);
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
        SaveService.Save(_clock, SaveService.SlotFor(saveName), saveName);
        _hud.ShowCellInfo($"已保存：{saveName}");
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
        SaveService.Save(_clock, SaveService.QuickSlot, "快速存档");
        _hud.ShowCellInfo("已快速保存 (F5)");
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

        // 地面
        float extent = MapGrid.Size * MapGrid.CellSize + 80f;
        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(extent, extent) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.45f, 0.5f, 0.32f),
            },
        };
        AddChild(ground);
    }
}
