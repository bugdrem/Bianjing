using System;
using Godot;

namespace Bianjing;

/// <summary>顶栏：钱/粮/人口/里程碑/日期 + 时间控制、政策与研习、存读档按钮。数据每帧轮询刷新；点击钱可查收支明细。</summary>
public partial class TopBar : PanelContainer
{
    private readonly GameClock _clock;
    private readonly Action _onSave;
    private readonly Action _onLoad;
    private readonly Action _onPolicy;
    private readonly Action _onFinance;
    private readonly Action _onTech;

    private Button _money;
    private Label _cityWealth; // 城市总金额（批次七十二）：官库 + 全城家庭公产之和，后期作为政策/事件依据
    private Label _food;
    private Label _pop;
    private Label _date;
    private OptionButton _speedBox; // 速率下拉（批次七十六）：暂停/0.5x/1x/2x/4x，默认 1x
    private float[] _speeds;

    public TopBar(GameClock clock, Action onSave, Action onLoad, Action onPolicy, Action onFinance, Action onTech)
    {
        _clock = clock;
        _onSave = onSave;
        _onLoad = onLoad;
        _onPolicy = onPolicy;
        _onFinance = onFinance;
        _onTech = onTech;
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
        // 批次七十二：城市总金额 = 官库 + 全城家庭公产（家底越厚城市越富，供后续机制参考）
        _cityWealth = MakeLabel(box);
        _cityWealth.TooltipText = "城市总金额 = 官库 + 全城家庭公产之和";
        _food = MakeLabel(box);
        _pop = MakeLabel(box);
        _date = MakeLabel(box);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(spacer);

        // 速率下拉（批次七十六）：暂停/0.5x/1x/2x/4x，默认 1x；键盘 1/2/3 与空格暂停仍有效，_Process 里同步选中项
        _speedBox = new OptionButton { TooltipText = "游戏速率" };
        _speeds = new[] { 0f, 0.5f, 1f, 2f, 4f };
        foreach (float s in _speeds)
            _speedBox.AddItem(s <= 0f ? "暂停" : $"{s:0.#}x");
        _speedBox.Selected = 2; // 默认 1x
        _speedBox.ItemSelected += idx => _clock.Speed = _speeds[idx];
        box.AddChild(_speedBox);

        box.AddChild(new VSeparator());
        AddActionButton(box, "政策", _onPolicy);
        AddActionButton(box, "研习", _onTech);
        AddActionButton(box, "保存", _onSave);
        AddActionButton(box, "读档", _onLoad);
    }

    private void AddActionButton(HBoxContainer box, string text, Action action)
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

    public override void _Process(double delta)
    {
        var gs = GameState.I;
        _money.Text = CurrencyHelper.FormatWen(gs.Money);
        _money.AddThemeColorOverride("font_color", gs.Money < 0 ? new Color(1f, 0.3f, 0.3f) : new Color(1f, 0.9f, 0.5f));
        long cityTotal = gs.Money;
        foreach (var f in gs.Families.Values)
            cityTotal += Math.Max(0, f.SharedAssets);
        _cityWealth.Text = $"城 {CurrencyHelper.FormatWen(cityTotal)}";
        _food.Text = $"粮 {gs.Food:F0}";
        _pop.Text = $"人口 {gs.Population}";
        string speedText = _clock.Speed == 0 ? "已暂停" : $"{_clock.Speed:0.#}x";
        string rank = Milestones.NameOf(gs.MilestoneLevel); // 当前城市里程碑称号
        _date.Text = $"{gs.CityName}【{rank}】 第{_clock.Year}年 {_clock.Month}月 {_clock.DisplayDay}日 {(_clock.IsNight ? "夜晚" : "白天")}  [{speedText}]";
        // 键盘快捷键/暂停改速后同步下拉选中项（未知速率不强制选中，仅文本展示）
        for (int i = 0; i < _speeds.Length; i++)
            if (Math.Abs(_clock.Speed - _speeds[i]) < 0.001f && _speedBox.Selected != i)
                _speedBox.Selected = i;
    }
}
