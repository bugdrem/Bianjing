using System;
using Godot;

namespace Bianjing;

/// <summary>公告栏：右下角常驻「公告」按钮 + 可开合的滚动公告列表，
/// 实时播报迁入迁出/出生离世等全城大事（数据源 GameState.News，可拓展新类别）。
/// 面板收合仅由按钮控制——点击游戏世界不收起（与政策/财政/科技面板不同，公告栏可常驻）；
/// 收合期间新公告在按钮上累计未读数，展开即清零。</summary>
public partial class NewsPanel : VBoxContainer
{
    /// <summary>列表最多渲染条数（数据层另有 200 条上限，展示端再截一刀防长文卡顿）。</summary>
    private const int MaxShown = 100;

    private PanelContainer _panel;
    private Label _body;
    private Button _toggle;
    private int _unread;

    public override void _Ready()
    {
        // 右下角竖排：公告列表在上、按钮在下，向左上生长（让开底部建造菜单与格子信息条）
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        GrowHorizontal = Control.GrowDirection.Begin;
        GrowVertical = Control.GrowDirection.Begin;
        Position -= new Vector2(12, 84);
        AddThemeConstantOverride("separation", 6);

        _panel = new PanelContainer { Visible = false };
        AddChild(_panel);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 10);
        _panel.AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        margin.AddChild(box);

        var title = new Label { Text = "城中公告", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 16);
        box.AddChild(title);

        // 滚动区：正文单 Label 逐行拼接（新在前），自动换行
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(320, 260) };
        box.AddChild(scroll);

        _body = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(300, 0),
        };
        _body.AddThemeFontSizeOverride("font_size", 13);
        scroll.AddChild(_body);

        _toggle = new Button { Text = "公告", SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
        _toggle.Pressed += Toggle;
        AddChild(_toggle);

        EventBus.NewsPosted += OnNewsPosted;
        EventBus.GameLoaded += OnGameLoaded;
    }

    public override void _ExitTree()
    {
        EventBus.NewsPosted -= OnNewsPosted;
        EventBus.GameLoaded -= OnGameLoaded;
    }

    /// <summary>新公告入栏：展开时即时刷新，收合时按钮累计未读数（封顶 99）。</summary>
    private void OnNewsPosted()
    {
        if (_panel.Visible)
        {
            Refresh();
        }
        else
        {
            _unread = Math.Min(_unread + 1, 99);
            _toggle.Text = $"公告({_unread})";
        }
    }

    /// <summary>读档/新开局：公告不入存档从头重记，清空未读并重刷。</summary>
    private void OnGameLoaded()
    {
        _unread = 0;
        _toggle.Text = "公告";
        Refresh();
    }

    private void Toggle()
    {
        _panel.Visible = !_panel.Visible;
        if (!_panel.Visible)
            return;
        _unread = 0;
        _toggle.Text = "公告";
        Refresh();
    }

    private void Refresh()
    {
        var news = GameState.I.News;
        if (news.Count == 0)
        {
            _body.Text = "（尚无公告）";
            return;
        }

        var sb = new System.Text.StringBuilder();
        int n = Math.Min(news.Count, MaxShown);
        for (int i = 0; i < n; i++)
            sb.AppendLine($"{news[i].Year}年{news[i].Month}月  {news[i].Text}");
        _body.Text = sb.ToString().TrimEnd();
    }
}
