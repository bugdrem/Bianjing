using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Bianjing;

/// <summary>
/// 点选详情面板（屏幕左侧）：点居民看个人履历与需求，点建筑看建造时间/等级/人员/储存，
/// 另支持树木（树龄/长势/挂果）、野物（月龄）与地面物资堆（堆内明细）。
/// 面板常驻刷新（每 0.5s 重读数据层），目标消失（死亡/拆除/砍倒/拾空）自动关闭。
/// </summary>
public partial class InspectPanel : PanelContainer
{
    private const float RefreshInterval = 0.5f;

    private Label _title;
    private RichTextLabel _body; // 富文本：成员名按性别着色（BBCode），纯文本页同样兼容
    private Button _bioToggle;
    private Label _bioBody;
    private bool _bioExpanded; // 年龄履历默认折叠
    private float _refresh;

    private int _citizenId = -1;
    private int _buildingId = -1;
    private int _plantCell = -1; // 植物/地面堆均以格索引为键（见 GameState.Plants/Piles）
    private int _pileCell = -1;
    private int _animalId = -1;

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

        _body = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true, // 高度随内容适应（不出滚动条，行为同旧 Label）
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _body.AddThemeFontSizeOverride("normal_font_size", 13);
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
        ClearTargets();
        _citizenId = c.Id;
        _bioExpanded = false; // 换人重置为折叠
        Visible = true;
        EventBus.RaiseCitizenSelected(c.Id);
        Refresh();
    }

    public void ShowBuilding(BuildingInstance b)
    {
        ClearTargets();
        _buildingId = b.Id;
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    public void ShowTree(PlantObj p)
    {
        ClearTargets();
        _plantCell = GameState.CellIndex(new Vector2I(p.X, p.Y));
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    public void ShowAnimal(AnimalObj a)
    {
        ClearTargets();
        _animalId = a.Id;
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    public void ShowPile(ItemPileObj pile)
    {
        ClearTargets();
        _pileCell = GameState.CellIndex(new Vector2I(pile.X, pile.Y));
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    /// <summary>清空全部选中目标（切页前调用，保证同时只有一个目标生效）。</summary>
    private void ClearTargets()
    {
        _citizenId = -1;
        _buildingId = -1;
        _plantCell = -1;
        _pileCell = -1;
        _animalId = -1;
    }

    public void Close()
    {
        Visible = false;
        ClearTargets();
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
        else if (_plantCell >= 0)
        {
            if (gs.Plants.TryGetValue(_plantCell, out var p))
                RenderTree(p);
            else
                Close(); // 已被砍倒
        }
        else if (_animalId >= 0)
        {
            if (gs.Animals.TryGetValue(_animalId, out var a))
                RenderAnimal(a);
            else
                Close(); // 已被猎获/自然减员
        }
        else if (_pileCell >= 0)
        {
            if (gs.Piles.TryGetValue(_pileCell, out var pile))
                RenderPile(pile);
            else
                Close(); // 已拾空
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
        if (c.Skill != SkillType.None)
            sb.AppendLine($"技能：{SkillName(c.Skill)}（{SkillLevelName(c.SkillExp)}，{c.SkillExp:F0} 经验）");
        if (c.CarriedItems.Count > 0)
            sb.AppendLine($"携带：{string.Join("、", c.CarriedItems.Select(Goods.NameOf))}");
        sb.AppendLine($"积蓄：{CurrencyHelper.FormatWen(c.Money)}");
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

        // 人员：居民按户主/关系展示（名字按性别着色），雇工仍列名
        var residents = new List<Citizen>();
        var workers = new List<string>();
        foreach (var c in gs.Citizens.Values)
        {
            if (c.HomeId == b.Id)
                residents.Add(c);
            if (c.WorkplaceId == b.Id && c.JobKind == JobKind.Employed)
                workers.Add(c.Name);
        }
        if (b.HousingCapacity > 0)
        {
            sb.AppendLine($"—— 居民 {residents.Count}/{b.HousingCapacity} ——");
            var head = gs.HouseholdHead(b.Id);
            if (head == null)
            {
                sb.AppendLine("（无人居住）");
            }
            else
            {
                sb.AppendLine($"屋主：{ColorName(head)}");
                // 成员按年龄降序逐行：名（性别色）+ 年龄 + 与屋主关系
                foreach (var c in residents.OrderByDescending(r => r.AgeMonths))
                    sb.AppendLine($"{ColorName(c)}　{c.AgeYears}岁　{RelationTo(head, c)}");
            }
        }
        if (b.Def.JobSlots > 0)
        {
            sb.AppendLine($"—— 雇工 {workers.Count}/{b.Def.JobSlots} ——");
            sb.AppendLine(workers.Count > 0 ? string.Join("、", workers) : "（暂无雇工）");
        }

        // 储存（逐堆列出，入库天数为后期变质系统铺垫）
        if (b.Def.StorageCapacity > 0)
        {
            sb.AppendLine($"—— 储存 {b.StorageTotal:F1}/{b.StorageCap:F0} 份 ——");
            if (b.Inv.IsEmpty)
                sb.AppendLine("（空仓）");
            else
                foreach (var s in b.Inv.Stacks)
                    sb.AppendLine($"{Goods.NameOf(s.GoodsId)}  {s.Amount:F1} 份（存 {s.AgeDays} 日）");
        }

        _body.Text = sb.ToString().TrimEnd();
    }

    // ---- 树木/野物/地面堆页 ----

    /// <summary>树木页：树龄/长势/木质血量，果树另列挂果存量。</summary>
    private void RenderTree(PlantObj p)
    {
        _title.Text = p.IsFruitTree ? "果树" : "树木";
        _bioToggle.Visible = false;
        _bioBody.Visible = false;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"树龄：{p.GrowthMonths / 12} 年 {p.GrowthMonths % 12} 月");
        sb.AppendLine(p.Mature ? "长势：已成树" : $"长势：幼树（{p.GrowthRatio * 100:F0}%）");
        sb.AppendLine($"木质：{p.Hp:F0}/{p.MaxHp:F0}（砍伐扣减，久不被砍缓慢恢复）");
        if (p.IsFruitTree)
            sb.AppendLine(p.Mature
                ? $"挂果：{p.FruitStock:F1}/{PlantObj.FruitCap:F0} 份（挂满过熟会落果）"
                : "挂果：尚未到挂果树龄");
        _body.Text = sb.ToString().TrimEnd();
    }

    /// <summary>野物页：月龄与习性。</summary>
    private void RenderAnimal(AnimalObj a)
    {
        _title.Text = "野物";
        _bioToggle.Visible = false;
        _bioBody.Visible = false;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(a.AgeMonths >= 12 ? $"月龄：{a.AgeMonths / 12} 岁零 {a.AgeMonths % 12} 月" : $"月龄：{a.AgeMonths} 个月");
        sb.AppendLine("习性：倚林而栖，日间小范围游走觅食");
        sb.AppendLine("可由猎户捕获，倒地化为野味供拾取");
        _body.Text = sb.ToString().TrimEnd();
    }

    /// <summary>地面物资堆页：堆内逐货明细（标题随主要货品，果堆即显「果品堆」）。</summary>
    private void RenderPile(ItemPileObj pile)
    {
        // 取份数最多的货品作标题（与地面堆渲染的主色逻辑同源）
        string domId = "";
        double domAmt = 0;
        foreach (var s in pile.Inv.Stacks)
        {
            if (s.Amount > domAmt)
            {
                domAmt = s.Amount;
                domId = s.GoodsId;
            }
        }
        _title.Text = domId == "" ? "地面物资" : $"{Goods.NameOf(domId)}堆";
        _bioToggle.Visible = false;
        _bioBody.Visible = false;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"堆存 {pile.Inv.Total:F1}/{ItemPileObj.PileCapacity:F0} 份（任何居民可拾取）");
        foreach (var s in pile.Inv.Stacks)
            sb.AppendLine($"{Goods.NameOf(s.GoodsId)}  {s.Amount:F1} 份（落地 {s.AgeDays} 日）");
        _body.Text = sb.ToString().TrimEnd();
    }

    private static string SkillName(SkillType s) => s switch
    {
        SkillType.Labor => "体力",
        SkillType.Craft => "手艺",
        SkillType.Commerce => "商业",
        SkillType.Scholarship => "文化",
        _ => "无",
    };

    private static string SkillLevelName(float exp) => exp switch
    {
        >= EconomyConfig.SkillExpExpert => "高级",
        >= EconomyConfig.SkillExpSkilled => "熟练",
        _ => "学徒",
    };

    /// <summary>名字按性别着色（BBCode）：男青蓝、女红。</summary>
    private static string ColorName(Citizen c)
        => $"[color={(c.Gender == Gender.Male ? "#6fa8dc" : "#e0708a")}]{c.Name}[/color]";

    /// <summary>与屋主的关系称谓（由配偶/父母/子女链推导）：本人/妻/夫/子/女/父/母/
    /// 兄弟姐妹/孙辈/儿媳女婿，其余笼统称亲眷。</summary>
    private static string RelationTo(Citizen head, Citizen c)
    {
        if (c.Id == head.Id)
            return "本人";
        if (c.Id == head.SpouseId)
            return c.Gender == Gender.Female ? "妻" : "夫";
        if (head.ChildrenIds.Contains(c.Id))
            return c.Gender == Gender.Male ? "子" : "女";
        if (c.ChildrenIds.Contains(head.Id))
            return c.Gender == Gender.Male ? "父" : "母";
        // 同胞：同父或同母，按年龄分长幼
        bool sibling = (c.FatherId >= 0 && c.FatherId == head.FatherId)
            || (c.MotherId >= 0 && c.MotherId == head.MotherId);
        if (sibling)
            return c.Gender == Gender.Male
                ? (c.AgeMonths > head.AgeMonths ? "兄" : "弟")
                : (c.AgeMonths > head.AgeMonths ? "姐" : "妹");
        // 孙辈：父或母是屋主的子女
        if (head.ChildrenIds.Contains(c.FatherId) || head.ChildrenIds.Contains(c.MotherId))
            return c.Gender == Gender.Male ? "孙" : "孙女";
        // 儿媳/女婿：配偶是屋主的子女
        if (c.SpouseId >= 0 && head.ChildrenIds.Contains(c.SpouseId))
            return c.Gender == Gender.Female ? "儿媳" : "女婿";
        return "亲眷";
    }
}
