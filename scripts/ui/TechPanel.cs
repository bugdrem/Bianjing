using Godot;

namespace Bianjing;

/// <summary>
/// 研习面板（科技树）：逐项展示科技名/说明/效果与状态——
/// 已研成 / 研习中（进度）/ 可立项（主动，点按钮开研）/ 待条件（被动或未达门槛，标注所需里程碑与前置）。
/// 主动科技同时只能在研一项，经费由官库逐日拨付；被动科技条件达成自动研成。
/// </summary>
public partial class TechPanel : PanelContainer
{
    private const float RefreshInterval = 0.5f;

    private VBoxContainer _rows;
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

        var title = new Label { Text = "研习科技", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(title);

        _rows = new VBoxContainer { CustomMinimumSize = new Vector2(340, 0) };
        _rows.AddThemeConstantOverride("separation", 8);
        box.AddChild(_rows);

        var footer = new Label { Text = "被动科技条件达成自动研成；主动科技立项后逐日拨经费" };
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

    /// <summary>整面板重建（科技数量少，全量重建足够便宜且状态永远最新）。</summary>
    private void Refresh()
    {
        _refresh = RefreshInterval;
        var gs = GameState.I;

        foreach (var child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }

        foreach (var def in TechDefs.All)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);

            var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            string mode = def.IsActive ? "主动" : "被动";
            info.AddChild(new Label { Text = $"{def.Name}（{mode}）" });
            var desc = new Label
            {
                Text = def.Description,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(220, 0),
            };
            desc.AddThemeFontSizeOverride("font_size", 12);
            desc.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f));
            info.AddChild(desc);
            row.AddChild(info);

            row.AddChild(MakeStatus(gs, def));
            _rows.AddChild(row);
        }
    }

    /// <summary>状态列：按研成/在研/可立项/待条件生成对应控件。</summary>
    private Control MakeStatus(GameState gs, TechDef def)
    {
        if (gs.TechsUnlocked.Contains(def.Id))
            return StatusLabel("已研成", new Color(0.5f, 0.9f, 0.5f));

        if (gs.ResearchTechId == def.Id)
        {
            int pct = def.ResearchDays > 0 ? (int)(gs.ResearchDays * 100 / def.ResearchDays) : 0;
            return StatusLabel($"研习中 {pct}%", new Color(0.9f, 0.85f, 0.5f));
        }

        bool ready = TechSystem.ConditionsMet(gs, def);
        if (def.IsActive)
        {
            if (ready && gs.ResearchTechId == "")
            {
                // 可立项：点按钮开始研习（经费逐日从官库拨付）
                var btn = new Button { Text = $"立项 {def.CostMoney:F0}贯/{def.ResearchDays}日" };
                string id = def.Id;
                btn.Pressed += () =>
                {
                    TechSystem.StartResearch(GameState.I, id);
                    Refresh();
                };
                return btn;
            }
            if (ready)
                return StatusLabel("待当前项目完结", new Color(0.7f, 0.7f, 0.7f));
        }
        else if (ready)
        {
            return StatusLabel("将自动研成", new Color(0.7f, 0.8f, 0.7f));
        }

        // 未达条件：标注所需里程碑/前置
        string need = gs.MilestoneLevel < def.MilestoneRequired
            ? $"需{Milestones.NameOf(def.MilestoneRequired)}"
            : "待前置科技";
        return StatusLabel(need, new Color(0.6f, 0.6f, 0.6f));
    }

    private static Label StatusLabel(string text, Color color)
    {
        var label = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(96, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
