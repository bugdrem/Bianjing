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
        };
        AddChild(new WorldEnvironment { Environment = env });

                // 卷轴背景：游戏世界坐在一幅横卷「画」上——大于地图的长方形纸面垫在地形之下，
        // 东西两侧各横一根卷轴圆柱（轴向南北）；图内地表由地形三角网格覆盖，
        // 图缘镂空由 GridRenderer 裙板遮住（裙板底与纸面同高）
        BuildScrollBackdrop();
    }

    /// <summary>卷轴背景布景：白底（地图四周外扩 MapEdgeExtend 的白边）+ 纸面（长方形，东西向更宽）
    /// + 两根横卧卷轴圆柱。层次自上而下：地形/裙板 → 白底 → 纸面；圆柱底部与纸面画布相切。</summary>
    private void BuildScrollBackdrop()
    {
        float mapSize = MapGrid.Size * MapGrid.CellSize;
        float baseY = TerrainConfig.MinTerrainHeight - 0.2f;   // 白底 = 裙板底，地形断面→裙板→白底无缝
        float paperY = baseY - 0.4f;                            // 纸面垫在白底之下
        float paperX = (mapSize + 440f) * 2f;  // 东西向（卷轴圆柱所在方向）加宽到旧版 2 倍，卷轴画更宽展
        float paperZ = mapSize + 180f;  // 南北向留窄白边，成横卷比例

        // 白底：地图四周外扩 MapEdgeExtend 的纯白底色，垫在地图与卷轴纸面之间
        float baseSize = mapSize + 2f * WorldConfig.MapEdgeExtend;
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(baseSize, baseSize) },
            Position = new Vector3(0f, baseY, 0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.96f, 0.96f, 0.94f), // 白底（略暖白）
            },
        });

        var paper = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(paperX, paperZ) },
            Position = new Vector3(0f, paperY, 0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.84f, 0.78f, 0.62f), // 绢帛暖米色（宋画手卷纸面）
            },
        };
        AddChild(paper);

        // 两侧卷轴：深色漆木圆柱横卧东西两端（轴向南北，即绕 X 轴旋 90°），底部与纸面画布相切
        const float rollerR = 14f;
        var rollerMesh = new CylinderMesh
        {
            TopRadius = rollerR,
            BottomRadius = rollerR,
            Height = paperZ + 60f, // 两端微出纸面，像轴头
        };
        var rollerMat = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.20f, 0.12f) }; // 深色漆木
        foreach (float sx in new[] { -1f, 1f })
        {
            AddChild(new MeshInstance3D
            {
                Mesh = rollerMesh,
                MaterialOverride = rollerMat,
                // 圆柱默认轴向 Y：绕 X 轴旋 90° 后轴向 Z（南北横卧）
                RotationDegrees = new Vector3(90f, 0f, 0f),
                // 底部与纸面相切：轴心抬高一个半径（圆柱底刚好落在 paperY）
                Position = new Vector3(sx * (paperX / 2f - rollerR * 0.4f), paperY + rollerR, 0f),
            });
        }
    }
}
