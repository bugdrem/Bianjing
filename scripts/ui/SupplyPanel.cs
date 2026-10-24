using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 城市供需面板（REVIEW_2026-10 P0-③）：把「中央需求账本」第一次呈现给玩家。
///
/// **这个面板为什么是最高优先级的可玩性修复**：<see cref="DemandLedger"/> 的类注释原文写着
/// 「作为全局可查阅的被动参考（上帝视角，**暂不显示给玩家**）」，而它驱动了全部 NPC 决策
/// （择业/建田/产业规划都查它）。结果是**玩家是全场唯一的信息盲人**——NPC 看得见城市缺什么，
/// 玩家看不见，于是 30 分钟后就进入"看着城市自己长"的无事可做状态。
/// 而数据早就是齐的（DailyDemand / Stock / DaysOfStock / IsShort 四个字段都在），
/// 差的只是一个面板。这就是「性价比最高的单点修复」的含义。
///
/// 呈现规则：
///   · 只列「有需求」的货品（无需求项不占版面）；
///   · 排序：短缺在前，其余按可支撑天数升序（最危急的永远在最上面）；
///   · 状态分四档，颜色走 <see cref="DesignTokens"/> 语义色：
///       短缺（&lt; 6.5 日）→ 朱红；偏低（&lt; 15 日）→ 暗金；充裕（&lt; 90 日）→ 青；滞销（≥ 90 日）→ 弱文灰。
/// </summary>
public partial class SupplyPanel : FrostedPanel
{
    private const float RefreshInterval = 0.5f;

    private VBoxContainer _list;
    private Label _summary;
    private float _refresh;

    /// <summary>滞销线（日）：超过它就说明城里这种货堆着没人用，转弱文灰提示玩家别再扩产。</summary>
    private const double SurplusDays = 90.0;
    /// <summary>偏低线（日）：低于它转暗金提醒。</summary>
    private const double LowDays = 15.0;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterRight);
        GrowHorizontal = Control.GrowDirection.Begin;
        GrowVertical = Control.GrowDirection.Both;
        Position -= new Vector2(12, 0);

        var margin = new MarginContainer();
        margin.Margin(DesignTokens.GapL);
        AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", DesignTokens.GapM);
        margin.AddChild(box);

        box.AddChild(DesignTokens.Text("城市供需", DesignTokens.FontTitle, UiTheme.Ink,
            HorizontalAlignment.Center));
        box.AddChild(DesignTokens.Section("全城每日需求与库存可支撑天数（缺什么先建什么）"));

        _summary = DesignTokens.Text("", DesignTokens.FontCaption, DesignTokens.StatusMuted);
        box.AddChild(_summary);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(360, 380),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", DesignTokens.GapXs);
        scroll.AddChild(_list);
        box.AddChild(scroll);

        box.AddChild(DesignTokens.Footer("村民择业与建田都参考这本账——缺货正是扩张的方向"));

        Refresh();
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
        var ledger = GameState.I.Demand;
        if (ledger == null)
            return;

        // 收集「有需求」的项，按危急度排序（短缺优先，其余按可撑天数升序）
        var rows = new List<DemandEntry>();
        foreach (var e in ledger.Entries.Values)
            if (e.DailyDemand > 0)
                rows.Add(e);
        rows.Sort(static (a, b) =>
        {
            if (a.IsShort != b.IsShort)
                return a.IsShort ? -1 : 1;
            return a.DaysOfStock.CompareTo(b.DaysOfStock);
        });

        // 列表是纯文本行、数量 ~30，整量重建比做 diff 更简单也够快（0.5s 一次）
        foreach (var child in _list.GetChildren())
            child.QueueFree();
        if (rows.Count == 0)
        {
            _list.AddChild(DesignTokens.Text("尚无市民需求——先有居民才有供需", DesignTokens.FontBody,
                DesignTokens.StatusMuted));
            _summary.Text = "";
            return;
        }

        int shortCount = 0;
        foreach (var e in rows)
        {
            if (e.IsShort)
                shortCount++;
            _list.AddChild(MakeRow(e));
        }

        _summary.Text = shortCount > 0
            ? $"⚠ {shortCount} 种货品短缺（可撑天数不足 {EconomyConfig.DemandShortDays:0.#} 日）"
            : $"供应充足，共 {rows.Count} 种货品在流通";
        _summary.AddThemeColorOverride("font_color",
            shortCount > 0 ? DesignTokens.StatusBad : DesignTokens.StatusGood);
    }

    /// <summary>一行：货品名 + 日需求 + 库存 + 可撑天数 + 状态标签。</summary>
    private static Control MakeRow(DemandEntry e)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, DesignTokens.RowHeight) };
        row.AddThemeConstantOverride("separation", DesignTokens.GapM);

        // 货品名（占主要宽度）
        var name = DesignTokens.Text(Goods.NameOf(e.GoodsId), DesignTokens.FontBody, UiTheme.Ink);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(name);

        string days = double.IsPositiveInfinity(e.DaysOfStock) ? "∞" : e.DaysOfStock.ToString("F1");
        var demand = DesignTokens.Text($"需 {e.DailyDemand:F1}/日", DesignTokens.FontCaption, DesignTokens.StatusMuted);
        demand.CustomMinimumSize = new Vector2(88, 0);
        row.AddChild(demand);

        var stock = DesignTokens.Text($"存 {e.Stock:F0}", DesignTokens.FontCaption, DesignTokens.StatusMuted);
        stock.CustomMinimumSize = new Vector2(72, 0);
        row.AddChild(stock);

        var daysLbl = DesignTokens.Text($"{days} 日", DesignTokens.FontBody);
        daysLbl.CustomMinimumSize = new Vector2(64, 0);
        daysLbl.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(daysLbl);

        // 状态标签：四档语义色
        var (tag, color) = StageOf(e);
        var status = DesignTokens.Text(tag, DesignTokens.FontCaption, color);
        status.CustomMinimumSize = new Vector2(44, 0);
        status.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(status);

        // 整行按最危急的档位着色「可撑天数」，让远看也能扫出红行
        daysLbl.AddThemeColorOverride("font_color", color);
        return row;
    }

    /// <summary>四档状态：短缺 / 偏低 / 充裕 / 滞销。</summary>
    private static (string, Color) StageOf(DemandEntry e)
    {
        if (e.IsShort)
            return ("短缺", DesignTokens.StatusBad);
        if (e.DaysOfStock < LowDays)
            return ("偏低", DesignTokens.StatusWarn);
        if (e.DaysOfStock >= SurplusDays || double.IsPositiveInfinity(e.DaysOfStock))
            return ("滞销", DesignTokens.StatusMuted);
        return ("充裕", DesignTokens.StatusGood);
    }
}
