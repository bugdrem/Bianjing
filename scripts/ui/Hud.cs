using System;
using Godot;

namespace Bianjing;

/// <summary>HUD 根：顶栏 + 底部建造菜单 + 政策/财政/点选详情面板 + 右下角格子信息 + 帧率显示(Ctrl+F)。</summary>
public partial class Hud : CanvasLayer
{
    private readonly BuildController _build;
    private readonly GameClock _clock;
    private readonly Action _onSave;
    private readonly Action _onLoad;

    private Label _cellInfo;
    private Label _fps;
    private float _infoTimer;

    private InspectPanel _inspect;
    private PolicyPanel _policy;
    private FinancePanel _finance;
    private TechPanel _tech;
    private SupplyPanel _supply;

    public Hud(BuildController build, GameClock clock, Action onSave, Action onLoad)
    {
        _build = build;
        _clock = clock;
        _onSave = onSave;
        _onLoad = onLoad;
    }

    public override void _Ready()
    {
        // 毛玻璃桥接：作为本 CanvasLayer 第一个子节点，先于各玻璃面板合成，
        // 把已画好的世界（地形/建筑/小人）抓进后台缓冲；之后 FrostedPanel 读 SCREEN_TEXTURE 即采到背后世界，
        // 而非本层透明内容（Godot 4 中 CanvasLayer 内 SCREEN_TEXTURE 默认只采到本层）。
        // —— 必须显式扩 Rect 覆盖整屏（默认 256×256 不够）：否则 FrostedPanel 大部分屏幕 UV 采到 backbuffer 外、返回无效色，毛玻璃退化为实色带。
        var win = GetWindow();
        var bbc = new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Viewport };
        if (win != null)
        {
            bbc.Rect = new Rect2(Vector2.Zero, win.Size);
            win.SizeChanged += () => bbc.Rect = new Rect2(Vector2.Zero, win.Size); // 改窗口/分辨率/F11 全屏切换时跟新
        }
        AddChild(bbc);

        _policy = new PolicyPanel();
        _finance = new FinancePanel();
        _tech = new TechPanel();
        _supply = new SupplyPanel();
        _inspect = new InspectPanel();
        _inspect.Build = _build; // 批次七十：注入建造控制器（面板定位镜头用）
        var news = new NewsPanel(); // 公告栏：列表右下角弹出，开关按钮交给底部操作栏摆在最右
        AddChild(new TopBar(_clock, _onSave, _onLoad,
            () => OpenExclusive(_policy, _policy.Toggle),
            () => OpenExclusive(_finance, _finance.Toggle),
            () => OpenExclusive(_tech, _tech.Toggle),
            () => OpenExclusive(_supply, _supply.Toggle)));
        AddChild(new BuildMenu(_build, news.ToggleButton));
        AddChild(_policy);
        AddChild(_finance);
        AddChild(_tech);
        AddChild(_supply);
        AddChild(_inspect);
        AddChild(news);

        UiTheme.Apply(this); // 根 Window 主题不穿透 CanvasLayer，需手动挂到本子树

        // 里程碑晋级与科技研成：右下角弹报（复用格子信息条）
        EventBus.MilestoneReached += OnMilestone;
        EventBus.TechUnlocked += OnTechUnlocked;

        // 批次一百零一：古风 BGM 起播（未放音频文件时静默空转）。
        // 曲目随昼夜切换：白天市井丝竹、夜间低缓慢板——由 _Process 低频检查触发。
        GameAudio.I?.PlayMusic("market_day");

        _cellInfo = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.Begin,
        };
        _cellInfo.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        _cellInfo.Position -= new Vector2(12, 48);
        AddChild(_cellInfo);

        // 帧率显示：右上角（顶栏下方），Ctrl+F 开关
        _fps = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            GrowHorizontal = Control.GrowDirection.Begin,
        };
        _fps.AddThemeColorOverride("font_color", new Color(0.5f, 1f, 0.5f));
        _fps.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        _fps.Position += new Vector2(-12, 44);
        AddChild(_fps);
    }

    public override void _ExitTree()
    {
        EventBus.MilestoneReached -= OnMilestone;
        EventBus.TechUnlocked -= OnTechUnlocked;
    }

    private void OnMilestone(int level)
    {
        var def = Milestones.Of(level);
        ShowCellInfo($"城市晋级为【{def.Name}】！官库拨款 {def.Reward} 文，新建筑已解锁");
        GameAudio.I?.Sfx("milestone");
    }

    private void OnTechUnlocked(string techId)
    {
        var def = TechDefs.Find(techId);
        if (def != null)
            ShowCellInfo($"【{def.Name}】研成：{def.Description}");
            GameAudio.I?.Sfx("tech");
    }

    public void ShowCellInfo(string text)
    {
        _cellInfo.Text = text;
        _infoTimer = 6f;
    }

    // ---- 点选反馈：选中什么响什么（批次一百零一）----
    // 音色按「人 / 材质」分：村民按年龄分三档，建筑与物品按材质（木/石/瓦/货/水/活物/树）分。
    // 未配对应音效时静默，不影响点选本身。

    public void ShowCitizen(Citizen c)
    {
        _inspect.ShowCitizen(c);
        GameAudio.I?.Sfx(c.IsChild ? "pick_child" : c.IsElder ? "pick_elder" : "pick_citizen");
    }

    public void ShowVisitor(ForeignVisitor v)
    {
        _inspect.ShowVisitor(v);
        GameAudio.I?.Sfx("pick_citizen"); // 客商同人声档
    }

    public void ShowBuilding(BuildingInstance b)
    {
        _inspect.ShowBuilding(b);
        GameAudio.I?.Sfx(MaterialCueOf(b));
    }

    public void ShowTree(PlantObj p)
    {
        _inspect.ShowTree(p);
        GameAudio.I?.Sfx("pick_tree");
    }

    public void ShowAnimal(AnimalObj a)
    {
        _inspect.ShowAnimal(a);
        GameAudio.I?.Sfx("pick_animal");
    }

    public void ShowPile(ItemPileObj p)
    {
        _inspect.ShowPile(p);
        GameAudio.I?.Sfx("pick_goods");
    }

    public void CloseInspect() => _inspect.Close();

    /// <summary>按建筑类别推断点选音效的材质档。</summary>
    private static string MaterialCueOf(BuildingInstance b) => b.Def.Category switch
    {
        "field"  => "pick_goods",              // 农田：土/粮
        "court"  => "pick_wood",
        _ when b.Def.Id is "well" => "pick_water",
        _ when b.Def.Id is "mine" or "quarry" or "saltworks" => "pick_stone",
        _ when b.Def.Id is "mint" or "workshop" => "pick_stone",
        _ => "pick_wood",                      // 木构为主
    };

    /// <summary>侧面板互斥：目标未开则先关掉供需/政策/财政/研习其余几个再开它（各自 Toggle 保留 Refresh）；目标已开则关闭。</summary>
    private void OpenExclusive(Control panel, Action toggle)
    {
        if (panel.Visible)
        {
            panel.Visible = false;
            return;
        }
        _policy.Visible = false;
        _finance.Visible = false;
        _tech.Visible = false;
        _supply.Visible = false;
        toggle();
    }

    /// <summary>收起供需/政策/财政/科技侧面板（点击游戏世界时由 BuildController 调用）；
    /// 公告栏常驻不收，ESC 菜单另有遮罩拦截仅手动返回。</summary>
    public void CloseSidePanels()
    {
        _policy.Visible = false;
        _finance.Visible = false;
        _tech.Visible = false;
        _supply.Visible = false;
    }

    private float _bgmCheck;
    private float _crowdScan;

    public override void _Process(double delta)
    {
        if (_fps.Visible)
            _fps.Text = $"FPS {Engine.GetFramesPerSecond():F0}";

        // BGM 昼夜选曲：2s 一检（曲目切换本身有交叉淡变，低频检查足够）
        _bgmCheck -= (float)delta;
        if (_bgmCheck <= 0f)
        {
            _bgmCheck = 2f;
            GameAudio.I?.PlayMusic(IsNight() ? "night_calm" : "market_day");
        }

        // 人群环境音（批次一百零四）：4Hz 扫描相机焦点周边市民密度，交给音频导演平滑。
        // 音量平滑在 GameAudio 里逐帧做，这里只负责报数——低频扫描 + 高频平滑，
        // 比每帧遍历全城市民（上千个）便宜得多。
        _crowdScan -= (float)delta;
        if (_crowdScan <= 0f)
        {
            _crowdScan = 0.25f;
            GameAudio.I?.SetCrowdLevel(CrowdLevel());
        }

        if (_infoTimer <= 0f)
            return;
        _infoTimer -= (float)delta;
        if (_infoTimer <= 0f)
            _cellInfo.Text = "";
    }

    /// <summary>是否夜间（供 BGM 选曲用）：直接用 GameClock.IsNight（光照昼夜联动同一判据，
    /// 不另抄一套时段名匹配——时段名是「平旦/隅中/日中/晡时/黄昏/夜半」六时制，硬匹配必漏）。</summary>
    private bool IsNight() => _clock != null && _clock.IsNight;

    /// <summary>
    /// 相机焦点周边的人群密度（0~1）：半径内市民数 ÷ 满额数，夜间打折（宵禁、人多在屋内）。
    ///
    /// 参照点取**相机云台的 Position**（焦点）而不是相机本身——相机挂在焦点后上方
    /// （默认拉距下水平偏移约 26m），用相机会让人声圈整体偏离玩家正在看的地方。
    /// </summary>
    private float CrowdLevel()
    {
        var gs = GameState.I;
        var rig = _build?.Rig;
        if (gs == null || rig == null)
            return 0f;
        var focus = rig.Position;
        float r2 = AudioConfig.CrowdRadius * AudioConfig.CrowdRadius;
        int n = 0;
        foreach (var p in gs.Citizens.Values)
        {
            if (!p.PosValid)
                continue;
            float dx = p.PosX - focus.X;
            float dz = p.PosZ - focus.Z;
            if (dx * dx + dz * dz <= r2)
                n++;
        }
        float level = Mathf.Min(1f, n / (float)AudioConfig.CrowdFullCount);
        if (IsNight())
            level *= AudioConfig.CrowdNightFactor;
        return level;
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F, CtrlPressed: true })
        {
            _fps.Visible = !_fps.Visible;
            GetViewport().SetInputAsHandled();
        }
    }
}
