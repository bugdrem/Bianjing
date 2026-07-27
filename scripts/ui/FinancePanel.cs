using Godot;

namespace Bianjing;

/// <summary>财政面板：官库本月/上月分类收支流水（点顶栏钱数呼出）。</summary>
public partial class FinancePanel : PanelContainer
{
    private const float RefreshInterval = 0.5f;

    private Label _body;
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

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        box.AddThemeConstantOverride("separation", 10);
        margin.AddChild(box);

        var title = new Label { Text = "官库收支", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(title);

        _body = new Label();
        _body.AddThemeFontSizeOverride("font_size", 13);
        box.AddChild(_body);

        var footer = new Label { Text = "收入为正、支出为负；月界轮转留存上月" };
        footer.AddThemeFontSizeOverride("font_size", 12);
        footer.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        box.AddChild(footer);
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

    private void Refresh()
    {
        _refresh = RefreshInterval;
        var ledger = GameState.I.Ledger;

        var sb = new System.Text.StringBuilder();
        AppendSection(sb, "本月", ledger.Current);
        sb.AppendLine();
        AppendSection(sb, "上月", ledger.Previous);
        _body.Text = sb.ToString().TrimEnd();
    }

    private static void AppendSection(System.Text.StringBuilder sb, string label,
        System.Collections.Generic.Dictionary<string, double> rows)
    {
        double total = 0;
        sb.AppendLine($"—— {label} ——");
        if (rows.Count == 0)
        {
            sb.AppendLine("（尚无流水）");
            return;
        }
        foreach (var (cat, amt) in rows)
        {
            sb.AppendLine($"{cat}  {(amt >= 0 ? "+" : "")}{amt:F1}");
            total += amt;
        }
        sb.AppendLine($"小计  {(total >= 0 ? "+" : "")}{total:F1}");
    }
}
