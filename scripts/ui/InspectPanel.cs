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

    /// <summary>建造控制器（批次七十：面板定位镜头走 Build.Rig/Build.Agents，由 Hud 注入）。</summary>
    public BuildController Build { get; set; }

    private HBoxContainer _locateRow; // 定位按钮行（定位本人/住所/工作，仅居民页显示）
    private HBoxContainer _familyLocateRow; // 定位按钮行（仅定位住所，家庭页显示，批次七十一）
    private Button _backButton; // 返回按钮（批次七十二：面板内跳转时显示，点按回来源面板）

    // 返回目标（批次七十二）：面板内链接跳转（个人页↔家庭页等）前快照当前面板目标，GoBack 恢复
    private enum BackKind { None, Citizen, Building, Family }
    private BackKind _backKind = BackKind.None;
    private int _backId = -1;

    private int _citizenId = -1;
    private int _buildingId = -1;
    private int _familyId = -1; // 家庭页目标（批次七十一：个人/建筑页点家庭链接进入）
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
        // 返回按钮（批次七十二）：从面板内链接跳转（如个人页→家庭页）时显示，点按回到来源面板
        _backButton = new Button { Text = "← 返回", Flat = true, Visible = false };
        _backButton.AddThemeFontSizeOverride("font_size", 12);
        _backButton.Pressed += GoBack;
        head.AddChild(_backButton);
        _title = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _title.AddThemeFontSizeOverride("font_size", 18);
        head.AddChild(_title);
        var close = new Button { Text = "×", Flat = true };
        close.Pressed += Close;
        head.AddChild(close);

        // 定位按钮行（批次七十）：居民页显示，点按把镜头拉向本人/住所/工作地
        _locateRow = new HBoxContainer();
        _locateRow.AddThemeConstantOverride("separation", 6);
        var locateSelf = new Button { Text = "定位本人", Flat = true };
        locateSelf.AddThemeFontSizeOverride("font_size", 12);
        locateSelf.Pressed += LocateSelf;
        var locateHome = new Button { Text = "定位住所", Flat = true };
        locateHome.AddThemeFontSizeOverride("font_size", 12);
        locateHome.Pressed += LocateHome;
        var locateWork = new Button { Text = "定位工作", Flat = true };
        locateWork.AddThemeFontSizeOverride("font_size", 12);
        locateWork.Pressed += LocateWork;
        _locateRow.AddChild(locateSelf);
        _locateRow.AddChild(locateHome);
        _locateRow.AddChild(locateWork);
        _locateRow.Visible = false;
        box.AddChild(_locateRow);

        // 家庭页定位按钮行（批次七十一）：仅「定位住所」——把镜头拉到家庭住房
        _familyLocateRow = new HBoxContainer();
        _familyLocateRow.AddThemeConstantOverride("separation", 6);
        var locateFamilyHome = new Button { Text = "定位住所", Flat = true };
        locateFamilyHome.AddThemeFontSizeOverride("font_size", 12);
        locateFamilyHome.Pressed += LocateFamilyHome;
        _familyLocateRow.AddChild(locateFamilyHome);
        _familyLocateRow.Visible = false;
        box.AddChild(_familyLocateRow);

        _body = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true, // 高度随内容适应（不出滚动条，行为同旧 Label）
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _body.AddThemeFontSizeOverride("normal_font_size", 13);
        _body.MetaClicked += OnMetaClicked; // 批次七十：人名链接展开个人面板
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
        ClearBack(); // 外部入口（点选世界目标）：无返回目标
        _citizenId = c.Id;
        _bioExpanded = false; // 换人重置为折叠
        Visible = true;
        EventBus.RaiseCitizenSelected(c.Id);
        Refresh();
    }

    public void ShowBuilding(BuildingInstance b)
    {
        ClearTargets();
        ClearBack(); // 外部入口（点选世界目标）：无返回目标
        _buildingId = b.Id;
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    /// <summary>展示家庭页（批次七十一）：成员清单 + 家产模块 + 住所，成员名可点回个人页。</summary>
    public void ShowFamily(Family fam)
    {
        ClearTargets();
        ClearBack(); // 外部入口；面板内跳转由 OnMetaClicked 在调用后恢复返回目标
        _familyId = fam.Id;
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    public void ShowTree(PlantObj p)
    {
        ClearTargets();
        ClearBack(); // 外部入口（点选世界目标）：无返回目标
        _plantCell = GameState.CellIndex(new Vector2I(p.X, p.Y));
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    public void ShowAnimal(AnimalObj a)
    {
        ClearTargets();
        ClearBack(); // 外部入口（点选世界目标）：无返回目标
        _animalId = a.Id;
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    public void ShowPile(ItemPileObj pile)
    {
        ClearTargets();
        ClearBack(); // 外部入口（点选世界目标）：无返回目标
        _pileCell = GameState.CellIndex(new Vector2I(pile.X, pile.Y));
        Visible = true;
        EventBus.RaiseCitizenSelected(-1);
        Refresh();
    }

    // ---- 富文本链接与定位（批次七十）----

    /// <summary>富文本链接点击（citizen:ID）：建筑面板点人名即切换为该居民的个人页。</summary>
    private void OnMetaClicked(Variant meta)
    {
        // 批次七十二：先快照当前面板目标（跳转会清空目标字段），跳转后恢复为返回目标
        var back = SnapshotBack();
        string s = meta.AsString();
        const string citizenPrefix = "citizen:";
        if (s.StartsWith(citizenPrefix) && int.TryParse(s[citizenPrefix.Length..], out int cid)
            && GameState.I.Citizens.TryGetValue(cid, out var c))
        {
            ShowCitizen(c);
            RestoreBack(back);
            return;
        }
        const string familyPrefix = "family:"; // 批次七十一：家庭链接进入家庭页
        if (s.StartsWith(familyPrefix) && int.TryParse(s[familyPrefix.Length..], out int fid)
            && GameState.I.Families.TryGetValue(fid, out var fam))
        {
            ShowFamily(fam);
            RestoreBack(back);
        }
    }

    // ---- 面板内跳转与返回（批次七十二）----

    /// <summary>快照当前面板目标为返回目标（跳转方法会清空目标字段，故先存）。</summary>
    private (BackKind Kind, int Id) SnapshotBack()
    {
        if (_citizenId >= 0)
            return (BackKind.Citizen, _citizenId);
        if (_buildingId >= 0)
            return (BackKind.Building, _buildingId);
        if (_familyId >= 0)
            return (BackKind.Family, _familyId);
        return (BackKind.None, -1);
    }

    /// <summary>清空返回目标并隐藏返回按钮（外部入口/返回后调用）。</summary>
    private void ClearBack()
    {
        _backKind = BackKind.None;
        _backId = -1;
        _backButton.Visible = false;
    }

    /// <summary>跳转完成后恢复返回目标（OnMetaClicked 用，来源面板可能已随跳转 ClearBack）。</summary>
    private void RestoreBack((BackKind Kind, int Id) back)
    {
        _backKind = back.Kind;
        _backId = back.Id;
        _backButton.Visible = _backKind != BackKind.None;
    }

    /// <summary>返回来源面板；来源目标已失效（亡故/拆除/散伙）则关闭面板。</summary>
    private void GoBack()
    {
        var (kind, id) = (_backKind, _backId);
        ClearBack();
        switch (kind)
        {
            case BackKind.Citizen:
                if (GameState.I.Citizens.TryGetValue(id, out var c))
                    ShowCitizen(c);
                else
                    Close();
                break;
            case BackKind.Building:
                if (GameState.I.Buildings.TryGetValue(id, out var b))
                    ShowBuilding(b);
                else
                    Close();
                break;
            case BackKind.Family:
                if (GameState.I.Families.TryGetValue(id, out var f))
                    ShowFamily(f);
                else
                    Close();
                break;
            default:
                Close();
                break;
        }
    }

    /// <summary>定位本人：优先代理实时坐标，其次数据层坐标（读档恢复），再退住所中心。</summary>
    private void LocateSelf()
    {
        if (!TryGetSelectedCitizen(out var c))
            return;
        var world = Build?.Agents?.AgentPosition(c.Id);
        world ??= c.PosValid ? new Vector3(c.PosX, 0f, c.PosZ) : BuildingCenterOf(c.HomeId);
        FocusWorld(world, "该居民暂不在城");
    }

    /// <summary>定位住所（无家可归时提示）。</summary>
    private void LocateHome()
    {
        if (!TryGetSelectedCitizen(out var c))
            return;
        FocusWorld(BuildingCenterOf(c.HomeId), c.HomeId < 0 ? "该居民无家可归" : "住所已拆除");
    }

    /// <summary>定位工作地（无业/失地时提示）。</summary>
    private void LocateWork()
    {
        if (!TryGetSelectedCitizen(out var c))
            return;
        FocusWorld(BuildingCenterOf(c.WorkplaceId), c.WorkplaceId < 0 ? "该居民暂无工作" : "工作地已失");
    }

    /// <summary>定位家庭住所（家庭页，批次七十一）。</summary>
    private void LocateFamilyHome()
    {
        if (!GameState.I.Families.TryGetValue(_familyId, out var fam))
            return;
        FocusWorld(BuildingCenterOf(fam.HomeId), fam.HomeId < 0 ? "该家庭居无定所" : "住所已拆除");
    }

    private bool TryGetSelectedCitizen(out Citizen c)
    {
        c = null;
        return _citizenId >= 0 && GameState.I.Citizens.TryGetValue(_citizenId, out c);
    }

    private Vector3? BuildingCenterOf(int bid)
        => bid >= 0 && GameState.I.Buildings.TryGetValue(bid, out var b) ? BuildingCenter(b) : null;

    /// <summary>建筑占地中心（世界坐标，定位镜头落点）。</summary>
    private static Vector3 BuildingCenter(BuildingInstance b)
        => MapGrid.CellToWorld(new Vector2I(b.X, b.Y))
           + new Vector3(b.Def.SizeX * MapGrid.CellSize / 2f, 0f, b.Def.SizeY * MapGrid.CellSize / 2f);

    private void FocusWorld(Vector3? world, string fallback)
    {
        if (world == null)
        {
            Build?.Hud?.ShowCellInfo(fallback);
            return;
        }
        Build?.Rig.FocusOn(world.Value);
    }

    /// <summary>清空全部选中目标（切页前调用，保证同时只有一个目标生效）。</summary>
    private void ClearTargets()
    {
        _citizenId = -1;
        _buildingId = -1;
        _familyId = -1;
        _plantCell = -1;
        _pileCell = -1;
        _animalId = -1;
    }

    public void Close()
    {
        Visible = false;
        ClearTargets();
        ClearBack(); // 批次七十二：关闭即丢弃返回目标
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

        if (_familyId >= 0)
        {
            // 批次七十一：家庭页——家庭散伙（成员尽去）自动关闭
            if (gs.Families.TryGetValue(_familyId, out var fam))
                RenderFamily(gs, fam);
            else
                Close();
        }
        else if (_citizenId >= 0)
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
        _locateRow.Visible = Build != null; // 批次七十：定位按钮仅居民页显示（无控制器则隐藏）
        _familyLocateRow.Visible = false; // 批次七十一：家庭页专属定位行

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{(c.Gender == Gender.Female ? "女" : "男")}  {c.AgeYears}岁  {c.GetIdentity(gs)}");

        // 履历
        sb.AppendLine("—— 履历 ——");
        // 批次七十一：家庭行改可点击链接（点击进入家庭面板）；家产独立成模块，不再缀在行尾
        if (gs.Families.TryGetValue(c.FamilyId, out var fam))
        {
            sb.AppendLine($"家庭：{FamilyLink(gs, fam)}");
            sb.AppendLine("—— 家产 ——");
            sb.AppendLine(CurrencyHelper.FormatWen(fam.SharedAssets));
        }
        else
        {
            sb.AppendLine("家庭：无（无家庭）");
        }
        sb.AppendLine($"住所：{BuildingName(gs, c.HomeId, "无家可归")}");
        sb.AppendLine($"生计：{JobLine(gs, c)}");
        if (c.Skill != SkillType.None)
            sb.AppendLine($"技能：{SkillName(c.Skill)}（{SkillLevelName(c.SkillExp)}，{c.SkillExp:F0} 经验）");
        if (c.CarriedItems.Count > 0)
            sb.AppendLine($"携带：{string.Join("、", c.CarriedItems.Select(Goods.NameOf))}");
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

    /// <summary>家庭户主（批次七十一）：成员中最年长成年，成年优先/男优先/年长者优先（同 HouseholdHead 规则）。</summary>
    private static Citizen FamilyHead(GameState gs, Family fam)
    {
        Citizen head = null;
        foreach (int mid in fam.MemberIds)
        {
            if (!gs.Citizens.TryGetValue(mid, out var m))
                continue;
            if (head == null
                || (!head.IsChild, head.Gender == Gender.Male, head.AgeMonths)
                    .CompareTo((!m.IsChild, m.Gender == Gender.Male, m.AgeMonths)) < 0)
                head = m;
        }
        return head;
    }

    /// <summary>家庭链接文本（批次七十一）：「户主名一家（n口）」/「户主名（独居）」，点击进入家庭面板。</summary>
    private static string FamilyLink(GameState gs, Family fam)
    {
        var head = FamilyHead(gs, fam);
        string name = head != null ? ColorName(head) : $"（家庭{fam.Id}）";
        return fam.MemberIds.Count >= 2
            ? $"[url=family:{fam.Id}]{name}一家（{fam.MemberIds.Count}口）[/url]"
            : $"[url=family:{fam.Id}]{name}（独居）[/url]";
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
        ActivityType.Trading => "上市交易",
        ActivityType.Repairing => "修缮",
        ActivityType.Hauling => "挑担入库",
        ActivityType.PickingUp => "拾取物资",
        ActivityType.FetchingWater => "打水",
        _ => "不明",
    };

    // ---- 家庭页（批次七十一）：成员清单 + 家产模块 + 住所 ----

    private void RenderFamily(GameState gs, Family fam)
    {
        var head = FamilyHead(gs, fam);
        _title.Text = head != null ? $"{head.Name}一家" : "家庭";
        _bioToggle.Visible = false;
        _bioBody.Visible = false;
        _locateRow.Visible = false;
        _familyLocateRow.Visible = Build != null; // 定位住所按钮仅家庭页显示（无控制器则隐藏）

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"成员 {fam.MemberIds.Count} 口");
        foreach (var c in fam.MemberIds
            .Where(id => gs.Citizens.ContainsKey(id))
            .OrderByDescending(id => gs.Citizens[id].AgeMonths)
            .Select(id => gs.Citizens[id]))
        {
            // 户主置顶（按年龄降序自然排首位），其余标与户主关系
            string rel = head != null && c.Id == head.Id ? "户主" : head != null ? RelationTo(head, c) : "";
            sb.AppendLine($"{UrlName(c)} {c.AgeYears}岁  {rel}".TrimEnd());
        }
        sb.AppendLine("—— 家产 ——");
        sb.AppendLine(CurrencyHelper.FormatWen(fam.SharedAssets));
        sb.AppendLine($"住所：{BuildingName(gs, fam.HomeId, "居无定所")}");
        _body.Text = sb.ToString().TrimEnd();
    }

    // ---- 建筑页：建造时间/等级/人员/储存 ----

    private void RenderBuilding(GameState gs, BuildingInstance b)
    {
        _title.Text = b.Def.Name;

        // 建筑页隐藏居民专属的履历折叠区与定位按钮
        _bioToggle.Visible = false;
        _bioBody.Visible = false;
        _locateRow.Visible = false; // 批次七十：定位按钮仅居民页显示
        _familyLocateRow.Visible = false; // 批次七十一：家庭页专属定位行

        var sb = new System.Text.StringBuilder();
        // 王爷府地标（批次八十）：不设健康度、永不老化，不显示等级/完好
        if (b.Def.Id == PrinceMansionConfig.DefId)
            sb.AppendLine("王府地标：不设健康度，永不老化");
        else
            sb.AppendLine($"等级 {b.Level}/{b.Def.MaxLevel}  完好 {b.Condition:F0}%");
        sb.AppendLine($"建于：{(b.BuiltYear > 0 ? $"第{b.BuiltYear}年 {b.BuiltMonth}月" : "不详")}");
        if (b.Specialty != "")
        {
            sb.AppendLine($"专营：{Goods.NameOf(b.Specialty)}");
            if (b.ExtraGoods.Count > 0)
                sb.AppendLine($"兼营：{string.Join("、", b.ExtraGoods.Select(Goods.NameOf))}");
        }
        if (b.Def.HarvestMonths > 0)
        {
            if (b.Def.Category == "field")
            {
                // 批次七十四一年两熟：收获窗口外（含冬季 10-12 月）休整不产出
                if (gs.CurMonth < FarmlandConfig.HarvestStartMonth || gs.CurMonth > FarmlandConfig.HarvestEndMonth)
                    sb.AppendLine("农时：冬歇休整（来年开春重新播种）");
                else
                    sb.AppendLine($"农时：{b.Def.HarvestMonths - b.MonthsSinceHarvest} 月后收获（一年两熟）");
            }
            else
                sb.AppendLine($"农时：{b.Def.HarvestMonths - b.MonthsSinceHarvest} 月后收获");
        }

        // 人员：居民按户主/关系展示（名字按性别着色），雇工仍列名
        var residents = new List<Citizen>();
        var workers = new List<Citizen>();
        foreach (var c in gs.Citizens.Values)
        {
            if (c.HomeId == b.Id)
                residents.Add(c);
            // 批次七十：农田雇工名单不含田主本人（田主单独一行展示）
            if (c.WorkplaceId == b.Id && c.JobKind == JobKind.Employed
                && (b.Def.Category != "field" || c.Id != b.OwnerCitizenId))
                workers.Add(c);
        }
        if (b.HousingCapacity > 0)
        {
            sb.AppendLine($"—— 居民 {residents.Count}/{b.HousingCapacity} ——");
            var head = gs.HouseholdHead(b.Id);
            if (head == null)
            {
                sb.AppendLine("（无人居住）");
            }
            else if (b.Def.Category == "grown")
            {
                // 批次七十一：屋主行只挂人名链接；家产单独模块展示（不再缀在屋主名后）
                sb.AppendLine($"屋主：{UrlName(head)}");
                AppendFamilyAssets(sb, gs, head);
                // 成员按年龄降序逐行：名（性别色）+ 年龄 + 与屋主关系
                foreach (var c in residents.OrderByDescending(r => r.AgeMonths))
                    sb.AppendLine($"{UrlName(c)} {c.AgeYears}岁 {RelationTo(head, c)}");
            }
            else
            {
                // 公共建筑（流民营/王爷府等）：只列住户名单，不设屋主（人名可点击）
                foreach (var c in residents.OrderByDescending(r => r.AgeMonths))
                    sb.AppendLine($"{UrlName(c)} {c.AgeYears}岁");
            }
        }
        // 批次七十：农田页——田主与雇工分列（田主名可点击展开个人面板）
        if (b.Def.Category == "field")
        {
            string owner = b.OwnerCitizenId >= 0 && gs.Citizens.TryGetValue(b.OwnerCitizenId, out var o)
                ? UrlName(o) : "待指派";
            sb.AppendLine($"田主：{owner}");
            // 批次七十一：田主家产单独模块展示
            if (b.OwnerCitizenId >= 0 && gs.Citizens.TryGetValue(b.OwnerCitizenId, out var oc))
                AppendFamilyAssets(sb, gs, oc);
        }
        if (b.Def.JobSlotsAt(b.Level) > 0)
        {
            sb.AppendLine($"—— 雇工 {workers.Count}/{b.Def.JobSlotsAt(b.Level)} ——");
            sb.AppendLine(workers.Count > 0 ? string.Join("、", workers.Select(UrlName)) : "（暂无雇工）");
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

        // 生产需求：加工建筑（工坊）的按级配方原料/燃料/副产品，及产业建筑（粮田/林场/矿场等）的收成信息
        // 批次八十四：仅工坊展示配方（商铺不加工，只从工坊收货售卖，不显示生产需求防误读为工坊）
        if (b.Def.Id == "workshop" && Goods.IsCraftable(b.Specialty))
        {
            sb.AppendLine("—— 生产需求 ——");
            var inputs = Goods.InputsAt(b.Specialty, b.Level);
            int fuelAt = Goods.FuelAt(b.Specialty, b.Level);
            double byp = Goods.ByproductAt(b.Specialty, b.Level);
            var parts = new List<string>();
            foreach (var kv in inputs)
                parts.Add($"{Goods.NameOf(kv.Key)}×{kv.Value}");
            if (fuelAt > 0)
                parts.Add($"{Goods.NameOf(Goods.Wood)}×{fuelAt}"); // 燃料
            string arrow = string.Join(" + ", parts) + $" → {Goods.NameOf(b.Specialty)}";
            if (byp > 0)
                arrow += $" + {Goods.NameOf(Goods.Scrap)}×{byp:0.##}"; // 副产品
            sb.AppendLine($"配方（{b.Level}级）：{arrow}");
            sb.AppendLine($"需料：{string.Join("、", inputs.Select(kv => $"{Goods.NameOf(kv.Key)} {b.Inv.AmountOf(kv.Key):F1}份（{Goods.PriceOf(kv.Key)}文）"))}");
            sb.AppendLine($"产出：{Goods.NameOf(b.Specialty)} {b.Inv.AmountOf(b.Specialty):F1}份（{Goods.PriceOf(b.Specialty)}文）");
            double eff = b.Def.EfficiencyAt(b.Level);
            if (eff != 1.0)
                sb.AppendLine($"效率：{eff:0.0}×（{b.Level}级坊铺）");
        }
        else if (b.Def.HarvestMonths > 0)
        {
            // 产业建筑直采产出（空 ProduceGoods 默认产粮），无配方链
            string goodsId = string.IsNullOrEmpty(b.Def.ProduceGoods) ? Goods.Grain : b.Def.ProduceGoods;
            sb.AppendLine("—— 生产需求 ——");
            sb.AppendLine($"产出：{Goods.NameOf(goodsId)}（每工每收 {b.Def.YieldPerWorker:F0} 份，{b.Def.HarvestMonths} 月一收）");
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
        _locateRow.Visible = false; // 批次七十：定位按钮仅居民页显示
        _familyLocateRow.Visible = false; // 批次七十一：家庭页专属定位行

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

    /// <summary>野物页：月龄 + 生命阶段（按月龄阈值派生）+ 习性。</summary>
    private void RenderAnimal(AnimalObj a)
    {
        _title.Text = "野物";
        _bioToggle.Visible = false;
        _bioBody.Visible = false;
        _locateRow.Visible = false; // 批次七十：定位按钮仅居民页显示
        _familyLocateRow.Visible = false; // 批次七十一：家庭页专属定位行

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(a.AgeMonths >= 12 ? $"月龄：{a.AgeMonths / 12} 岁零 {a.AgeMonths % 12} 月" : $"月龄：{a.AgeMonths} 个月");
        sb.AppendLine($"阶段：{LifeStageOf(a.AgeMonths)}");
        sb.AppendLine("习性：倚林而栖，日间小范围游走觅食");
        sb.AppendLine("可由猎户捕获，倒地化为野味供拾取");
        _body.Text = sb.ToString().TrimEnd();
    }

    /// <summary>月龄 → 生命阶段：半岁内幼崽，一岁内亚成年，满一岁成年。</summary>
    private static string LifeStageOf(int ageMonths) =>
        ageMonths < 6 ? "幼崽"
        : ageMonths < 12 ? "亚成年"
        : "成年";

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
        _locateRow.Visible = false; // 批次七十：定位按钮仅居民页显示
        _familyLocateRow.Visible = false; // 批次七十一：家庭页专属定位行

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
        SkillType.Farming => "农艺",
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

    /// <summary>名字按性别着色并挂点击链接（批次七十）：建筑面板点人名即在面板展开该居民个人页。</summary>
    private static string UrlName(Citizen c)
        => $"[url=citizen:{c.Id}]{ColorName(c)}[/url]";

    /// <summary>家产模块（批次七十一）：独立小节「—— 家产 ——」+ 金额，不再缀在户主/田主名后。</summary>
    private static void AppendFamilyAssets(System.Text.StringBuilder sb, GameState gs, Citizen c)
    {
        sb.AppendLine("—— 家产 ——");
        sb.AppendLine(gs.Families.TryGetValue(c.FamilyId, out var fam)
            ? CurrencyHelper.FormatWen(fam.SharedAssets) : "（无家庭）");
    }

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
