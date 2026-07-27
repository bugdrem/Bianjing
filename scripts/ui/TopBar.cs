using System;
using Godot;

namespace Bianjing;

/// <summary>顶栏：钱/粮/人口/日期 + 时间控制、政策与存读档按钮。数据每帧轮询刷新；点击钱可查收支明细。</summary>
public partial class TopBar : PanelContainer
{
    private readonly GameClock _clock;
    private readonly Action _onSave;
    private readonly Action _onLoad;
    private readonly Action _onPolicy;
    private readonly Action _onFinance;

    private Button _money;
    private Label _food;
    private Label _pop;
    private Label _date;

    public TopBar(GameClock clock, Action onSave, Action onLoad, Action onPolicy, Action onFinance)
    {
        _clock = clock;
        _onSave = onSave;
        _onLoad = onLoad;
        _onPolicy = onPolicy;
        _onFinance = onFinance;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);

        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 24);
        AddChild(box);

        _money = new Button { Flat = true, TooltipText = "点击查看收支明细" };
        _money.Pressed += () => _onFinance?.Invoke();
        box.AddChild(_money);
        _food = MakeLabel(box);
        _pop = MakeLabel(box);
        _date = MakeLabel(box);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(spacer);

        AddSpeedButton(box, "暂停", 0);
        AddSpeedButton(box, "1x", 1);
        AddSpeedButton(box, "2x", 2);
        AddSpeedButton(box, "3x", 3);

        box.AddChild(new VSeparator());
        AddActionButton(box, "政策", _onPolicy);
        AddActionButton(box, "保存", _onSave);
        AddActionButton(box, "读档", _onLoad);
    }

    private static void AddActionButton(HBoxContainer box, string text, Action action)
    {
        var btn = new Button { Text = text };
        btn.Pressed += () => action?.Invoke();
        box.AddChild(btn);
    }

    private static Label MakeLabel(HBoxContainer box)
    {
        var label = new Label { VerticalAlignment = VerticalAlignment.Center };
        box.AddChild(label);
        return label;
    }

    private void AddSpeedButton(HBoxContainer box, string text, int speed)
    {
        var btn = new Button { Text = text };
        btn.Pressed += () => _clock.Speed = speed;
        box.AddChild(btn);
    }

    public override void _Process(double delta)
    {
        var gs = GameState.I;
        _money.Text = $"钱 {gs.Money:F0}";
        _money.AddThemeColorOverride("font_color", gs.Money < 0 ? new Color(1f, 0.3f, 0.3f) : new Color(1f, 0.9f, 0.5f));
        _food.Text = $"粮 {gs.Food:F0}";
        _pop.Text = $"人口 {gs.Population}";
        string speedText = _clock.Speed == 0 ? "已暂停" : $"{_clock.Speed}x";
        _date.Text = $"{gs.CityName}  第{_clock.Year}年 {_clock.Month}月 {_clock.Day}日 {_clock.Shichen}  [{speedText}]";
    }
}
