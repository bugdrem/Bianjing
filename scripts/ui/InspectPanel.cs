using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Bianjing;

/// <summary>
/// 点选详情面板（屏幕左侧）：点居民看个人履历与需求，点建筑看建造时间/等级/人员/储存。
/// 面板常驻刷新（每 0.5s 重读数据层），目标消失（死亡/拆除）自动关闭。
/// </summary>
public partial class InspectPanel : PanelContainer
{
    private const float RefreshInterval = 0.5f;

    private Label _title;
    private Label _body;
    private Button _bioToggle;
    private Label _bioBody;
    private bool _bioExpanded; // 年龄履历默认折叠
    private float _refresh;

    private int _citizenId = -1;
    private int _buildingId = -1;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterLeft);
        GrowHorizontal = Control.GrowDirection.End;
        GrowVertical = Control.GrowDirection.Both;
        Position += new Vector2(12, 0);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 14);
        AddChild(margin);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(260, 0) };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        var head = new HBoxContainer();
        box.AddChild(head);
        _title = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _title.AddThemeFontSizeOverride("font_size", 18);
        head.AddChild(_title);
        var close = new Button { Text = "×", Flat = true };
        close.Pressed += Close;
        head.AddChild(close);

        _body = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _body.AddThemeFontSizeOverride("font_size", 13);
        box.AddChild(_body);

        // 年龄履历折叠区（仅居民页可见，默认收起）
        _bioToggle = new Button { Flat = true, Alignment = HorizontalAlignment.Left };
        _bioToggle.AddThemeFontSizeOverride("font_size", 13);
        _bioToggle.Pressed += () =>
        {
            _bioExpanded = !_bioExpanded;
            Refresh();
        };
        box.AddChild(_bioToggle);

        _bioBody = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _bioBody.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(_bioBody);
    }

    public void ShowCitizen(Citizen c)
    {
        _citizenId = c.Id;
        _buildingId = -1;
        _bioExpanded = false; // 换人重置为折叠
        Visible = true;
        EventBus.RaiseCitizenSelected(c.Id);
        Refresh();
    }

    public void ShowBuilding(BuildingInstance b)
    {
        _buildingId = b.Id;
        _citizenId = -1;
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    public void Close()
    {
        Visible = false;
        _citizenId = -1;
        _buildingId = -1;
        EventBus.RaiseCitizenSelected(-1);
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
        var gs = GameState.I;

        if (_citizenId >= 0)
        {
            if (gs.Citizens.TryGetValue(_citizenId, out var c))
                RenderCitizen(gs, c);
            else
                Close(); // 已亡故/迁出
        }
        else if (_buildingId >= 0)
        {
            if (gs.Buildings.TryGetValue(_buildingId, out var b))
                RenderBuilding(gs, b);
            else
                Close(); // 已拆除/坍塌
        }
    }

    // ---- 居民页：履历 + 需求 ----

    private void RenderCitizen(GameState gs, Citizen c)
    {
        _title.Text = c.Name; // Name 已含姓，不叠加 Surname

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{(c.Gender == Gender.Female ? "女" : "男")}  {c.AgeYears}岁  {c.GetIdentity(gs)}");

        // 履历
        sb.AppendLine("—— 履历 ——");
        sb.AppendLine($"家庭：{FamilyLine(gs, c)}");
        sb.AppendLine($"住所：{BuildingName(gs, c.HomeId, "无家可归")}");
        sb.AppendLine($"生计：{JobLine(gs, c)}");
        sb.AppendLine($"积蓄：{c.Money:F1} 钱");
        sb.AppendLine($"正在：{ActivityName(c.Activity)}{PackLine(c)}");
        sb.AppendLine($"疲劳 {c.Fatigue:F0} / 兴致 {c.Fun:F0}");

        // 需求
        sb.AppendLine("—— 需求 ——");
        if (gs.Buildings.TryGetValue(c.HomeId, out var home))
        {
            double food = home.Inv.AmountOf(Goods.Grain)
                + home.Inv.AmountOf(Goods.Fruit)
                + home.Inv.AmountOf(Goods.Game);
            double fuel = home.Inv.AmountOf(Goods.Wood);
            sb.AppendLine($"家中存粮 {food:F1} 份 / 存柴 {fuel:F1} 份");
        }
        else
        {
            sb.AppendLine("急需住所");
        }
        sb.AppendLine(c.FoodShortDays > 0 ? $"已断炊 {c.FoodShortDays} 天！" : "口粮无虞");
        sb.AppendLine(c.FuelShortDays > 0 ? $"已缺柴 {c.FuelShortDays} 天" : "柴薪无虞");

        _body.Text = sb.ToString().TrimEnd();
        RenderBiography(c);
    }

    /// <summary>年龄履历折叠段：收起时只显示条数，展开后倒序（最新在前）列出重大事件。</summary>
    private void RenderBiography(Citizen c)
    {
        _bioToggle.Visible = true;
        _bioToggle.Text = _bioExpanded ? "▾ 年龄履历" : $"▸ 年龄履历（{c.LifeEvents.Count} 条）";
        _bioBody.Visible = _bioExpanded;
        if (!_bioExpanded)
            return;

        if (c.LifeEvents.Count == 0)
        {
            _bioBody.Text = "（尚无大事记）";
            return;
        }

        var sb = new System.Text.StringBuilder();
        for (int i = c.LifeEvents.Count - 1; i >= 0; i--)
        {
            var e = c.LifeEvents[i];
            sb.AppendLine($"第{e.Year}年{e.Month}月　{e.Text}");
        }
        _bioBody.Text = sb.ToString().TrimEnd();
    }

    private static string FamilyLine(GameState gs, Citizen c)
    {
        string spouse = c.SpouseId >= 0 && gs.Citizens.TryGetValue(c.SpouseId, out var s)
            ? $"配偶 {s.Name}" : (c.IsChild ? "" : "未婚");
        string kids = c.ChildrenIds.Count > 0 ? $"子女 {c.ChildrenIds.Count} 人" : "";
        string line = string.Join("，", new[] { spouse, kids }.Where(x => x != ""));
        return line == "" ? "孑然一身" : line;
    }

    private static string JobLine(GameState gs, Citizen c) => c.JobKind switch
    {
        JobKind.Employed => $"受雇于{BuildingName(gs, c.WorkplaceId, "（工作地已失）")}",
        JobKind.Logger => "进山伐木采猎",
        _ => c.IsChild ? "尚幼" : c.IsElder ? "颐养" : "无业",
    };

    /// <summary>背包携带明细（空背包返回空串）。</summary>
    private static string PackLine(Citizen c)
    {
        if (c.Pack.IsEmpty)
            return "";
        var parts = c.Pack.Stacks.Select(s => $"{Goods.NameOf(s.GoodsId)} {s.Amount:F1}份");
        return $"（背 {string.Join("、", parts)}）";
    }

    private static string BuildingName(GameState gs, int id, string fallback) =>
        gs.Buildings.TryGetValue(id, out var b) ? $"{b.Def.Name}（{b.X},{b.Y}）" : fallback;

    private static string ActivityName(ActivityType a) => a switch
    {
        ActivityType.RestHome => "在家歇息",
        ActivityType.Working => "上工",
        ActivityType.Shopping => "上市采买",
        ActivityType.Playing => "玩耍",
        ActivityType.Strolling => "闲逛",
        ActivityType.Logging => "伐木",
        ActivityType.Gathering => "采摘",
        ActivityType.Hunting => "打猎",
        ActivityType.Trading => "市集交易",
        ActivityType.Repairing => "修缮",
        ActivityType.Hauling => "挑担入库",
        ActivityType.PickingUp => "拾取物资",
        ActivityType.FetchingWater => "打水",
        _ => "不明",
    };

    // ---- 建筑页：建造时间/等级/人员/储存 ----

    private void RenderBuilding(GameState gs, BuildingInstance b)
    {
        _title.Text = b.Def.Name;

        // 建筑页隐藏居民专属的履历折叠区
        _bioToggle.Visible = false;
        _bioBody.Visible = false;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"等级 {b.Level}/{b.Def.MaxLevel}  完好 {b.Condition:F0}%");
        sb.AppendLine($"建于：{(b.BuiltYear > 0 ? $"第{b.BuiltYear}年 {b.BuiltMonth}月" : "不详")}");
        if (b.Specialty != "")
            sb.AppendLine($"专营：{Goods.NameOf(b.Specialty)}");
        if (b.Def.HarvestMonths > 0)
            sb.AppendLine($"农时：{b.Def.HarvestMonths - b.MonthsSinceHarvest} 月后收获");

        // 人员
        var residents = new List<string>();
        var workers = new List<string>();
        foreach (var c in gs.Citizens.Values)
        {
            if (c.HomeId == b.Id)
                residents.Add(c.Name);
            if (c.WorkplaceId == b.Id && c.JobKind == JobKind.Employed)
                workers.Add(c.Name);
        }
        if (b.HousingCapacity > 0)
        {
            sb.AppendLine($"—— 居民 {residents.Count}/{b.HousingCapacity} ——");
            sb.AppendLine(residents.Count > 0 ? string.Join("、", residents) : "（无人居住）");
        }
        if (b.Def.JobSlots > 0)
        {
            sb.AppendLine($"—— 雇工 {workers.Count}/{b.Def.JobSlots} ——");
            sb.AppendLine(workers.Count > 0 ? string.Join("、", workers) : "（暂无雇工）");
        }

        // 储存（逐堆列出，入库天数为后期变质系统铺垫）
        if (b.Def.StorageCapacity > 0)
        {
            sb.AppendLine($"—— 储存 {b.StorageTotal:F1}/{b.Def.StorageCapacity} 份 ——");
            if (b.Inv.IsEmpty)
                sb.AppendLine("（空仓）");
            else
                foreach (var s in b.Inv.Stacks)
                    sb.AppendLine($"{Goods.NameOf(s.GoodsId)}  {s.Amount:F1} 份（存 {s.AgeDays} 日）");
        }

        _body.Text = sb.ToString().TrimEnd();
    }
}
