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
        _policy = new PolicyPanel();
        _finance = new FinancePanel();
        _tech = new TechPanel();
        _inspect = new InspectPanel();
        AddChild(new TopBar(_clock, _onSave, _onLoad, _policy.Toggle, _finance.Toggle, _tech.Toggle));
        AddChild(new BuildMenu(_build));
        AddChild(_policy);
        AddChild(_finance);
        AddChild(_tech);
        AddChild(_inspect);
        AddChild(new NewsPanel()); // 右下角常驻公告栏（迁入迁出/生死实时播报）

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
        ShowCellInfo($"城市晋级为【{def.Name}】！官库拨款 {def.Reward} 贯，新建筑已解锁");
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

    public void ShowBuilding(BuildingInstance b) => _inspect.ShowBuilding(b);

    public void CloseInspect() => _inspect.Close();

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
