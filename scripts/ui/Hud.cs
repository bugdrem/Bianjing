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
        _inspect = new InspectPanel();
        _inspect.Build = _build; // 批次七十：注入建造控制器（面板定位镜头用）
        var news = new NewsPanel(); // 公告栏：列表右下角弹出，开关按钮交给底部操作栏摆在最右
        AddChild(new TopBar(_clock, _onSave, _onLoad,
            () => OpenExclusive(_policy, _policy.Toggle),
            () => OpenExclusive(_finance, _finance.Toggle),
            () => OpenExclusive(_tech, _tech.Toggle)));
        AddChild(new BuildMenu(_build, news.ToggleButton));
        AddChild(_policy);
        AddChild(_finance);
        AddChild(_tech);
        AddChild(_inspect);
        AddChild(news);

        UiTheme.Apply(this); // 根 Window 主题不穿透 CanvasLayer，需手动挂到本子树

        // 里程碑晋级与科技研成：右下角弹报（复用格子信息条）
        EventBus.MilestoneReached += OnMilestone;
        EventBus.TechUnlocked += OnTechUnlocked;

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
    }

    private void OnTechUnlocked(string techId)
    {
        var def = TechDefs.Find(techId);
        if (def != null)
            ShowCellInfo($"【{def.Name}】研成：{def.Description}");
    }

    public void ShowCellInfo(string text)
    {
        _cellInfo.Text = text;
        _infoTimer = 6f;
    }

    public void ShowCitizen(Citizen c) => _inspect.ShowCitizen(c);

    public void ShowVisitor(ForeignVisitor v) => _inspect.ShowVisitor(v);

    public void ShowBuilding(BuildingInstance b) => _inspect.ShowBuilding(b);

    public void ShowTree(PlantObj p) => _inspect.ShowTree(p);

    public void ShowAnimal(AnimalObj a) => _inspect.ShowAnimal(a);

    public void ShowPile(ItemPileObj p) => _inspect.ShowPile(p);

    public void CloseInspect() => _inspect.Close();

    /// <summary>侧面板互斥：目标未开则先关掉政策/财政/研习其余两个再开它（各自 Toggle 保留 Refresh）；目标已开则关闭。</summary>
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
        toggle();
    }

    /// <summary>收起政策/财政/科技侧面板（点击游戏世界时由 BuildController 调用）；
    /// 公告栏常驻不收，ESC 菜单另有遮罩拦截仅手动返回。</summary>
    public void CloseSidePanels()
    {
        _policy.Visible = false;
        _finance.Visible = false;
        _tech.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_fps.Visible)
            _fps.Text = $"FPS {Engine.GetFramesPerSecond():F0}";

        if (_infoTimer <= 0f)
            return;
        _infoTimer -= (float)delta;
        if (_infoTimer <= 0f)
            _cellInfo.Text = "";
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
