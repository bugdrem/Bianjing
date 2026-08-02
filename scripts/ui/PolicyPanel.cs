using Godot;

namespace Bianjing;

/// <summary>
/// 税收政策面板（批次五十六重写：三税种模型——土地税、商税、人口税）。
/// 土地税/商税各有四档（免征/轻税/中税/重税），人口税为开关。
/// 实时预估月入并展示税率。
/// </summary>
public partial class PolicyPanel : PanelContainer
{
    private const float RefreshInterval = 0.5f;

    private OptionButton _landOption;
    private OptionButton _tradeOption;
    private CheckButton _pollToggle;
    private Label _landRevenue;
    private Label _tradeRevenue;
    private Label _pollRevenue;
    private float _refresh;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterRight);
        GrowHorizontal = Control.GrowDirection.Begin;
        GrowVertical = Control.GrowDirection.Both;
        Position -= new Vector2(12, 0);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        margin.AddChild(box);

        var title = new Label { Text = "税收政策", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(title);

        // 土地税
        box.AddChild(MakeSectionLabel("土地税（按建筑类型/等级定额，税率作用于税基）"));
        var landRow = new HBoxContainer { CustomMinimumSize = new Vector2(0, 32) };
        landRow.AddThemeConstantOverride("separation", 12);
        _landOption = MakeLevelOption(TaxPolicy.LevelNames, 0);
        _landOption.ItemSelected += OnLandLevelChanged;
        landRow.AddChild(_landOption);
        _landRevenue = MakeRevenueLabel();
        landRow.AddChild(_landRevenue);
        box.AddChild(landRow);

        // 商税
        box.AddChild(MakeSectionLabel("商税（交易发生时按成交额扣除）"));
        var tradeRow = new HBoxContainer { CustomMinimumSize = new Vector2(0, 32) };
        tradeRow.AddThemeConstantOverride("separation", 12);
        _tradeOption = MakeLevelOption(TaxPolicy.LevelNames, 0);
        _tradeOption.ItemSelected += OnTradeLevelChanged;
        tradeRow.AddChild(_tradeOption);
        _tradeRevenue = MakeRevenueLabel();
        tradeRow.AddChild(_tradeRevenue);
        box.AddChild(tradeRow);

        // 人口税
        box.AddChild(MakeSectionLabel("人口税（从雇工工资扣 20%，持续降幸福）"));
        var pollRow = new HBoxContainer { CustomMinimumSize = new Vector2(0, 32) };
        pollRow.AddThemeConstantOverride("separation", 12);
        _pollToggle = new CheckButton { Text = "开征人口税" };
        _pollToggle.Toggled += OnPollToggled;
        pollRow.AddChild(_pollToggle);
        _pollRevenue = MakeRevenueLabel();
        pollRow.AddChild(_pollRevenue);
        box.AddChild(pollRow);

        var footer = new Label { Text = "税入并入国库，用于俸禄维护与朝廷采买" };
        footer.AddThemeFontSizeOverride("font_size", 12);
        footer.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        box.AddChild(footer);

        SyncFromState();
    }

    private static Label MakeSectionLabel(string text)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", 12);
        lbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f));
        return lbl;
    }

    private static OptionButton MakeLevelOption(string[] names, int selected)
    {
        var opt = new OptionButton();
        foreach (string n in names)
            opt.AddItem(n);
        opt.Selected = selected;
        return opt;
    }

    private static Label MakeRevenueLabel()
    {
        var lbl = new Label
        {
            CustomMinimumSize = new Vector2(100, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return lbl;
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
            Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible)
            return;
        _refresh -= (float)delta;
        if (_refresh <= 0f)
            Refresh();
    }

    private void SyncFromState()
    {
        var gs = GameState.I;
        _landOption.Selected = gs.Taxes.LandTaxLevel;
        _tradeOption.Selected = gs.Taxes.TradeTaxLevel;
        _pollToggle.ButtonPressed = gs.Taxes.PollTaxEnabled;
    }

    private void OnLandLevelChanged(long index)
    {
        GameState.I.Taxes.LandTaxLevel = (int)index;
        Refresh();
    }

    private void OnTradeLevelChanged(long index)
    {
        GameState.I.Taxes.TradeTaxLevel = (int)index;
        Refresh();
    }

    private void OnPollToggled(bool on)
    {
        GameState.I.Taxes.PollTaxEnabled = on;
        Refresh();
    }

    private void Refresh()
    {
        _refresh = RefreshInterval;
        var gs = GameState.I;
        SyncFromState();

        long landEst = TaxSystem.EstimateLandTax(gs);
        long tradeEst = TaxSystem.EstimateTradeTax(gs);

        _landRevenue.Text = $"{CurrencyHelper.FormatWen(landEst)}/月";
        _tradeRevenue.Text = $"{CurrencyHelper.FormatWen(tradeEst)}/月";

        if (gs.Taxes.PollTaxEnabled)
        {
            long pollEst = 0;
            foreach (var c in gs.Citizens.Values)
                if (c.JobKind == JobKind.Employed && !c.IsChild)
                    pollEst += (long)(gs.Buildings.TryGetValue(c.WorkplaceId, out var b)
                        ? b.Def.Salary * EconomyConfig.PollTaxRate : 0);
            _pollRevenue.Text = $"{CurrencyHelper.FormatWen(pollEst)}/月";
        }
        else
        {
            _pollRevenue.Text = "—";
        }
    }
}
