using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 税收政策面板：逐税种展示说明与档位（免征/轻税/中税/重税），实时预估月入。
/// 税种列表来自 TaxDefs 注册表，mod 新增税种自动出现在面板中。
/// </summary>
public partial class PolicyPanel : PanelContainer
{
    private const float RefreshInterval = 0.5f;

    private readonly List<(TaxDef Def, OptionButton Option, Label Revenue)> _rows = new();
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

        foreach (var def in TaxDefs.All)
            box.AddChild(MakeRow(def));

        var footer = new Label { Text = "税入并入国库，用于俸禄维护与后续扩展" };
        footer.AddThemeFontSizeOverride("font_size", 12);
        footer.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        box.AddChild(footer);
    }

    private Control MakeRow(TaxDef def)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var info = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(240, 0),
        };
        info.AddChild(new Label { Text = def.Name });
        var desc = new Label { Text = def.Description };
        desc.AddThemeFontSizeOverride("font_size", 12);
        desc.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f));
        info.AddChild(desc);
        row.AddChild(info);

        var option = new OptionButton();
        foreach (string name in TaxPolicy.LevelNames)
            option.AddItem(name);
        option.Selected = GameState.I.Taxes.LevelOf(def.Id);
        option.ItemSelected += index => GameState.I.Taxes.SetLevel(def.Id, (int)index);
        row.AddChild(option);

        var revenue = new Label
        {
            CustomMinimumSize = new Vector2(80, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.AddChild(revenue);

        _rows.Add((def, option, revenue));
        return row;
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

    /// <summary>同步档位选择（读档后可能变化）并刷新预估月入。</summary>
    private void Refresh()
    {
        _refresh = RefreshInterval;
        var gs = GameState.I;
        foreach (var (def, option, revenue) in _rows)
        {
            int level = gs.Taxes.LevelOf(def.Id);
            if (option.Selected != level)
                option.Selected = level;
            revenue.Text = $"{TaxSystem.Estimate(gs, def):F1}/月";
        }
    }
}
