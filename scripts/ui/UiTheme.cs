using Godot;

namespace Bianjing;

/// <summary>
/// 中央 UI 主题（新中式·国潮极简）：宣纸白毛玻璃面板 + 黑色书法字 + 青为辅 + 红色印章点睛。
/// 通过 GetTree().Root.Theme 全局挂载，所有 Control 子节点自动继承，无需逐文件改样式。
/// 字体走系统书法字链（楷体→宋体→黑体兜底），运行时在玩家机器解析（开发机 Windows 自带楷体）。
/// 设计令牌见 .workbuddy/artifacts/UI评审与优化方案.md。
/// </summary>
public static class UiTheme
{
    // —— 语义色板（与设计令牌一致）——
    public static readonly Color Ink        = new("#1F1B16"); // 墨字（标题/正文）
    public static readonly Color Muted      = new("#8A8276"); // 弱文（次要/提示）
    public static readonly Color Celadon    = new("#2C8C82"); // 青（辅色：选中/高亮/图标）
    public static readonly Color CeladonD    = new("#1F6B63"); // 青·深（按下/强选中）
    public static readonly Color CeladonL    = new("#CFE6E2"); // 青·浅（列表选中底）
    public static readonly Color Divider    = new("#E2D9C8"); // 黛（分隔线）
    public static readonly Color PaperEdge  = new("#E7DFCF"); // 面板极淡边
    public static readonly Color Seal       = new("#C0392B"); // 朱（印章/告警点睛）
    // 宣纸白毛玻璃：带 alpha 透出 3D 场景形成玻璃感；墨字高对比，可读性无忧
    public static readonly Color Paper      = new(0.984f, 0.973f, 0.949f, 0.94f); // ≈ #FBF8F2@0.94
    public static readonly Color PaperSolid = new("#FCFAF5"); // 输入框/下拉等不透明底
    public static readonly Color BtnNormal   = new("#EDE7DA"); // 按钮常规填充
    public static readonly Color BtnHover    = new("#F3EEE3"); // 按钮悬停填充
    public static readonly Color BtnDisabled = new("#E0D9C8"); // 按钮禁用填充
    public static readonly Color Track       = new("#EDE7DA"); // 进度条/滑条底

    private static Theme _cached;

    public static Theme Build()
    {
        if (_cached != null)
            return _cached;

        var t = new Theme();
        t.DefaultFontSize = 14;

        // —— 字体：系统书法字链（楷体优先，国潮识别度来源），缺失逐级兜底 ——
        var font = new SystemFont
        {
            AllowSystemFallback = true,
            Oversampling = 1.5f, // 超采样：窗口放大/高分屏下文字保持清晰不发虚
        };
        font.FontNames = new[]
        {
            "KaiTi", "STKaiti", "Kaiti SC", "Kaiti TC",
            "Noto Serif SC", "Source Han Serif SC", "Source Han Serif CN",
            "SimSun", "Songti SC", "Microsoft YaHei", "SimHei", "sans-serif",
        };
        t.DefaultFont = font;

        // —— 文字色：墨字为主，青/朱仅点缀 ——
        t.SetColor("font_color", "Label", Ink);
        t.SetColor("font_color", "LineEdit", Ink);
        t.SetColor("font_placeholder_color", "LineEdit", Muted);
        t.SetColor("font_color", "Button", Ink);
        t.SetColor("font_disabled_color", "Button", Muted);
        t.SetColor("font_color", "ItemList", Ink);
        t.SetColor("font_color", "PopupMenu", Ink);
        // 悬停项：浅青底 + 墨字（白字落浅面板看不清，改高对比墨字）
        t.SetColor("font_hover_color", "PopupMenu", Ink);
        t.SetColor("font_color", "RichTextLabel", Ink);
        t.SetColor("font_color", "OptionButton", Ink);
        t.SetColor("font_color", "CheckButton", Ink);

        // —— 面板：宣纸白毛玻璃 + 极淡边 + 极轻投影（漂浮感）——
        t.SetStylebox("panel", "PanelContainer",
            MakePanel(Paper, 14, PaperEdge, 1, 0.12f, 6, new Vector2(0, 2)));
        t.SetStylebox("panel", "PopupMenu",
            MakePanel(PaperSolid, 6, PaperEdge, 1, 0.18f, 10, new Vector2(0, 3)));
        t.SetStylebox("panel", "ItemList", MakePanel(PaperSolid, 8, PaperEdge, 1));

        // —— 按钮：无边框圆角矩形，浅灰填充墨字；悬停微亮；按下=青填充（选中态）；禁用=弱化 ——
        t.SetStylebox("normal", "Button", MakeFlat(BtnNormal, 10));
        t.SetStylebox("hover", "Button", MakeFlat(BtnHover, 10));
        t.SetStylebox("pressed", "Button", MakeFlat(Celadon, 10));
        t.SetStylebox("disabled", "Button", MakeFlat(BtnDisabled, 10));
        // 焦点：青描边（键盘导航可辨，不喧宾夺主）
        var btnFocus = MakeFlat(BtnNormal, 10);
        Border(btnFocus, Celadon, 1);
        t.SetStylebox("focus", "Button", btnFocus);

        // —— 复选框：未选=素框，选中=青填 ——
        var chk = MakeFlat(PaperSolid, 4);
        Border(chk, PaperEdge, 1);
        var chkOn = MakeFlat(Celadon, 4);
        Border(chkOn, CeladonD, 1);
        t.SetStylebox("check", "CheckButton", chk);
        t.SetStylebox("check", "CheckBox", chk);
        t.SetStylebox("checked", "CheckButton", chkOn);
        t.SetStylebox("checked", "CheckBox", chkOn);

        // —— 输入框 ——
        t.SetStylebox("panel", "LineEdit", MakeLine(PaperSolid, PaperEdge, 1f));
        t.SetStylebox("focus", "LineEdit", MakeLine(PaperSolid, Celadon, 1.5f));

        // —— 列表选中 ——
        t.SetStylebox("selected", "ItemList", MakeFlat(CeladonL, 4));
        t.SetStylebox("selected_focus", "ItemList", MakeFlat(CeladonL, 4));

        // —— 进度条（加载/其它）——
        t.SetStylebox("fill", "ProgressBar", MakeFlat(Celadon, 4));
        t.SetStylebox("background", "ProgressBar", MakeFlat(Track, 4));

        // —— 下拉菜单悬停（浅青底，墨字高对比）——
        t.SetStylebox("hover", "PopupMenu", MakeFlat(CeladonL, 2));

        // —— 分隔线（黛色细线）——
        var sep = new StyleBoxFlat { BgColor = Divider };
        t.SetStylebox("separator", "VSeparator", sep);
        t.SetStylebox("separator", "HSeparator", sep);

        // —— 滚动条（列表滑条，浅色与整体协调）——
        t.SetStylebox("scroll", "VScrollBar", MakeFlat(Track, 4));
        t.SetStylebox("scroll", "HScrollBar", MakeFlat(Track, 4));
        var grab = MakeFlat(CeladonL, 4);
        t.SetStylebox("grabber", "VScrollBar", grab);
        t.SetStylebox("grabber", "HScrollBar", grab);
        t.SetStylebox("grabber_highlight", "VScrollBar", grab);
        t.SetStylebox("grabber_highlight", "HScrollBar", grab);

        _cached = t;
        return t;
    }

    /// <summary>将主题递归挂载到某节点下所有 Control。
    /// 根 Window 主题不会可靠传播到 CanvasLayer 内部的 Control（Godot 4 行为），
    /// 故每个 UI（Hud/GameMenu/LoadingScreen 均为 CanvasLayer）需各自把主题应用到自身子树。</summary>
    public static void Apply(Node root)
    {
        var theme = Build();
        ApplyWalk(root, theme);
    }

    private static void ApplyWalk(Node node, Theme theme)
    {
        if (node is Control control)
            control.Theme = theme;
        foreach (var child in node.GetChildren())
            ApplyWalk(child, theme);
    }

    /// <summary>将主题显式钉到 OptionButton 延迟创建的弹出层（PopupMenu）。
    /// OptionButton 的弹出层在运行时才生成、且不保证继承父节点主题，
    /// 若只靠 Apply 遍历，会漏掉它，回退成 Godot 默认皮肤（蓝底白字、悬浮文字不可读）。</summary>
    public static void StyleOptionPopup(OptionButton opt)
    {
        var popup = opt.GetPopup();
        if (popup != null)
            popup.Theme = Build();
    }

    /// <summary>红色印章点睛：小红方（圆角）内嵌白色书法字，作标题/里程碑点睛（装饰，不可点）。</summary>
    public static Control MakeSeal(string text)
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(38, 38) };
        var sb = MakeFlat(Seal, 6);
        Border(sb, Seal, 0);
        panel.AddThemeStyleboxOverride("panel", sb);

        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        label.AddThemeFontSizeOverride("font_size", 20);
        panel.AddChild(label);
        return panel;
    }

    // ---- 样式助手 ----

    private static StyleBoxFlat MakePanel(Color bg, float radius, Color border, int borderW,
        float shadowAlpha = 0f, int shadowSize = 0, Vector2 shadowOffset = default)
    {
        var s = MakeFlat(bg, radius);
        Border(s, border, borderW);
        if (shadowSize > 0)
        {
            s.ShadowColor = new Color(0f, 0f, 0f, shadowAlpha);
            s.ShadowSize = shadowSize;
            s.ShadowOffset = shadowOffset == default ? new Vector2(0, 2) : shadowOffset;
        }
        return s;
    }

    private static StyleBoxFlat MakeFlat(Color bg, float radius)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            ContentMarginLeft = 10, ContentMarginRight = 10,
            ContentMarginTop = 5, ContentMarginBottom = 5,
        };
        Radius(s, (int)radius);
        return s;
    }

    private static StyleBoxFlat MakeLine(Color bg, Color border, float borderW)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            ContentMarginLeft = 6, ContentMarginRight = 6,
            ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        Radius(s, 8);
        Border(s, border, (int)borderW);
        return s;
    }

    private static void Radius(StyleBoxFlat s, int r)
    {
        s.CornerRadiusTopLeft = r;
        s.CornerRadiusTopRight = r;
        s.CornerRadiusBottomLeft = r;
        s.CornerRadiusBottomRight = r;
    }

    private static void Border(StyleBoxFlat s, Color c, int w)
    {
        s.BorderWidthLeft = w;
        s.BorderWidthRight = w;
        s.BorderWidthTop = w;
        s.BorderWidthBottom = w;
        s.BorderColor = c;
    }
}
