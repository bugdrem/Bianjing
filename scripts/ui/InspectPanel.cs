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
    }

    public void ShowCitizen(Citizen c)
    {
        _citizenId = c.Id;
        _buildingId = -1;
        Visible = true;
        Refresh();
    }

    public void ShowBuilding(BuildingInstance b)
    {
        _buildingId = b.Id;
        _citizenId = -1;
        Visible = true;
        Refresh();
    }

    public void Close()
    {
        Visible = false;
        _citizenId = -1;
        _buildingId = -1;
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
        _title.Text = $"{c.Surname}{c.Name}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{(c.Gender == Gender.Female ? "女" : "男")}  {c.AgeYears}岁  {c.GetIdentity(gs)}");

        // 履历
        sb.AppendLine("—— 履历 ——");
        sb.AppendLine($"家庭：{FamilyLine(gs, c)}");
        sb.AppendLine($"住所：{BuildingName(gs, c.HomeId, "无家可归")}");
        sb.AppendLine($"生计：{JobLine(gs, c)}");
        sb.AppendLine($"积蓄：{c.Money:F1} 钱");
        sb.AppendLine($"正在：{ActivityName(c.Activity)}" +
            (c.Carrying != "" ? $"（携 {Goods.NameOf(c.Carrying)} 一担）" : ""));
        sb.AppendLine($"疲劳 {c.Fatigue:F0} / 兴致 {c.Fun:F0}");

        // 需求
        sb.AppendLine("—— 需求 ——");
        if (gs.Buildings.TryGetValue(c.HomeId, out var home))
        {
            double food = home.Storage.GetValueOrDefault(Goods.Grain)
                + home.Storage.GetValueOrDefault(Goods.Fruit)
                + home.Storage.GetValueOrDefault(Goods.Game);
            double fuel = home.Storage.GetValueOrDefault(Goods.Wood);
            sb.AppendLine($"家中存粮 {food:F1} 份 / 存柴 {fuel:F1} 份");
        }
        else
        {
            sb.AppendLine("急需住所");
        }
        sb.AppendLine(c.FoodShortDays > 0 ? $"已断炊 {c.FoodShortDays} 天！" : "口粮无虞");
        sb.AppendLine(c.FuelShortDays > 0 ? $"已缺柴 {c.FuelShortDays} 天" : "柴薪无虞");

        _body.Text = sb.ToString().TrimEnd();
    }

    private static string FamilyLine(GameState gs, Citizen c)
    {
        string spouse = c.SpouseId >= 0 && gs.Citizens.TryGetValue(c.SpouseId, out var s)
            ? $"配偶 {s.Surname}{s.Name}" : (c.IsChild ? "" : "未婚");
        string kids = c.ChildrenIds.Count > 0 ? $"子女 {c.ChildrenIds.Count} 人" : "";
        string line = string.Join("，", new[] { spouse, kids }.Where(x => x != ""));
        return line == "" ? "孑然一身" : line;
    }

    private static string JobLine(GameState gs, Citizen c) => c.JobKind switch
    {
        JobKind.Employed => $"受雇于{BuildingName(gs, c.WorkplaceId, "（工作地已失）")}",
        JobKind.Logger => "进山伐木采猎",
        JobKind.Repairer => "修缮公共屋舍",
        _ => c.IsChild ? "尚幼" : c.IsElder ? "颐养" : "无业",
    };

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
        ActivityType.Hauling => "挑担回家",
        _ => "不明",
    };

    // ---- 建筑页：建造时间/等级/人员/储存 ----

    private void RenderBuilding(GameState gs, BuildingInstance b)
    {
        _title.Text = b.Def.Name;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"等级 {b.Level}/{b.Def.MaxLevel}  完好 {b.Condition:F0}%");
        sb.AppendLine($"建于：{(b.BuiltYear > 0 ? $"第{b.BuiltYear}年 {b.BuiltMonth}月" : "不详")}");
        if (b.Specialty != "")
            sb.AppendLine($"专营：{Goods.NameOf(b.Specialty)}");

        // 人员
        var residents = new List<string>();
        var workers = new List<string>();
        foreach (var c in gs.Citizens.Values)
        {
            if (c.HomeId == b.Id)
                residents.Add($"{c.Surname}{c.Name}");
            if (c.WorkplaceId == b.Id && c.JobKind == JobKind.Employed)
                workers.Add($"{c.Surname}{c.Name}");
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

        // 储存
        if (b.Def.StorageCapacity > 0)
        {
            sb.AppendLine($"—— 储存 {b.StorageTotal:F1}/{b.Def.StorageCapacity} 份 ——");
            if (b.Storage.Count == 0)
                sb.AppendLine("（空仓）");
            else
                foreach (var (id, amt) in b.Storage)
                    sb.AppendLine($"{Goods.NameOf(id)}  {amt:F1} 份");
        }

        _body.Text = sb.ToString().TrimEnd();
    }
}
