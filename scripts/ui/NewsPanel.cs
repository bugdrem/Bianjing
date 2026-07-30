using System;
using Godot;

namespace Bianjing;

/// <summary>公告栏：底部操作栏最右侧的「公告」按钮（按钮实体由 BuildMenu 摆入操作栏）+ 可开合的滚动公告列表，
/// 实时播报迁入迁出/出生离世等全城大事（数据源 GameState.News，随存档保存、读档续接）。
/// 面板收合仅由按钮控制——点击游戏世界不收起（与政策/财政/科技面板不同，公告栏可常驻）；
/// 收合期间新公告在按钮上累计未读数，展开即清零。</summary>
public partial class NewsPanel : VBoxContainer
{
    /// <summary>列表最多渲染条数（数据层另有 200 条上限，展示端只取最新 99 条）。</summary>
    private const int MaxShown = 99;

    /// <summary>公告栏固定宽度（像素）：长文在栏内自动换行，不撑宽面板。</summary>
    private const float PanelWidth = 340f;

    private PanelContainer _panel;
    private Label _body;
    private readonly Button _toggle;
    private int _unread;

    /// <summary>「公告」开关按钮：未读数/开合逻辑仍由本面板自持，按钮本体交由 BuildMenu 摆进操作栏最右。</summary>
    public Button ToggleButton => _toggle;

    public NewsPanel()
    {
        // 按钮在构造期创建：BuildMenu._Ready 先于本面板 _Ready 运行，需提前拿到按钮实体挂入操作栏
        _toggle = new Button { Text = "公告" };
        _toggle.Pressed += Toggle;
    }

    public override void _Ready()
    {
        // 右下角公告列表：向左上生长，让开底部操作栏（公告按钮已入栏，面板从栏上方展开）
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        GrowHorizontal = Control.GrowDirection.Begin;
        GrowVertical = Control.GrowDirection.Begin;
        Position -= new Vector2(12, 96);
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

        // 滚动区：固定宽高，禁横向滚动——长文在定宽内自动换行，面板宽度不随内容撑开
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(PanelWidth, 260),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        box.AddChild(scroll);

        _body = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, // 铺满定宽滚动区以触发换行
        };
        _body.AddThemeFontSizeOverride("font_size", 13);
        scroll.AddChild(_body);

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

    /// <summary>读档/新开局：公告随存档恢复（见 SaveService），清空未读并重刷列表续接旧事。</summary>
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
