using Godot;

namespace Bianjing;

/// <summary>
/// 设计令牌（间距 / 字号 / 圆角 / 状态色）——UI 层统一的度量与语义来源。
///
/// **为什么要有这个文件**：Review 摸底发现 UI 目录有 47 处硬编码颜色（其中 10 处是
/// `new Color(0.7f,0.7f,0.7f)` 三档灰的各自为政）、34 处 `font_size` 覆写、
/// 22 处 `separation` 常量、30 处 theme constant 覆写——UiTheme 只管颜色与字体，
/// **不管间距与字号**，于是每个面板都自己发明一套度量，视觉上必然不齐。
///
/// 本类把「度量」也收敛成具名常量，并补上 UiTheme 色板缺失的**状态语义色**
/// （完好/渐损/失效、短缺/偏低/充裕）。新写的面板一律用令牌；旧面板的重填见
/// specs/REVIEW_2026-10.md P1-4，属后续批次，不在本次范围内。
/// </summary>
public static class DesignTokens
{
    // ---- 间距（px）----
    public const int GapXs = 4;    // 同组内紧邻元素
    public const int GapS = 8;     // 组内默认间隔
    public const int GapM = 12;    // 组间默认间隔
    public const int GapL = 16;    // 面板内边距（MarginContainer 四边）
    public const int GapXl = 24;   // 顶栏/底栏与屏幕边缘的距离

    // ---- 字号 ----
    public const int FontCaption = 12;  // 脚注/次要说明
    public const int FontBody = 14;     // 正文（= UiTheme.DefaultFontSize）
    public const int FontSection = 16;  // 分组小标题
    public const int FontTitle = 20;    // 面板标题
    public const int FontDisplay = 36;  // 主菜单「汴京」大字

    // ---- 圆角（px，与 UiTheme 内 MakeFlat 的取值一致，改样式时两处同步）----
    public const int RadiusControl = 10; // 按钮
    public const int RadiusPanel = 14;   // 面板

    // ---- 控件高度 ----
    public const int RowHeight = 32;     // 一行控件的基准高

    // ---- 语义状态色（UI 层可见的"好坏"信号，全部派生自 UiTheme 色板）----

    /// <summary>短缺/告警：取印章朱（同告警色，全项目一致）。</summary>
    public static Color StatusBad => UiTheme.Seal;

    /// <summary>偏低/渐损：暗金（旧代码里散落的 #b8860b，收拢到一处）。</summary>
    public static Color StatusWarn => new("#B8860B");

    /// <summary>完好/充裕：青（辅色，不抢主体）。</summary>
    public static Color StatusGood => UiTheme.Celadon;

    /// <summary>中性/无数据：弱文灰（取代旧代码 7 处 `new Color(0.7f,0.7f,0.7f)`）。</summary>
    public static Color StatusMuted => UiTheme.Muted;

    /// <summary>把 MarginContainer 四边设成同一值（旧代码 6 个面板各写一遍 for 循环）。</summary>
    public static void Margin(this MarginContainer mc, int all)
    {
        mc.AddThemeConstantOverride("margin_left", all);
        mc.AddThemeConstantOverride("margin_right", all);
        mc.AddThemeConstantOverride("margin_top", all);
        mc.AddThemeConstantOverride("margin_bottom", all);
    }

    /// <summary>快捷造一个带字号/颜色的标签（旧代码 34 处 font_size 覆写的收敛写法）。</summary>
    public static Label Text(string content, int size = FontBody, Color? color = null,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var lbl = new Label
        {
            Text = content,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
        };
        lbl.AddThemeFontSizeOverride("font_size", size);
        if (color.HasValue)
            lbl.AddThemeColorOverride("font_color", color.Value);
        return lbl;
    }

    /// <summary>快捷造一个分组小标题（旧 Policy/Tech/Finance 三处同模式复制的收敛写法）。</summary>
    public static Label Section(string content) => Text(content, FontCaption, StatusMuted);

    /// <summary>快捷造一个面板脚注（旧代码 5 处"标题+脚注灰字"骨架的收敛写法）。</summary>
    public static Label Footer(string content) => Text(content, FontCaption, StatusMuted);
}
