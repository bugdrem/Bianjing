using Godot;
using System.Collections.Generic;
using System.Linq;

namespace Bianjing;

/// <summary>底部建造菜单：整合为「基础设施 / 公共设施 / 官府设施 / 可建造区」四大类。
/// 下排点分类切换，上排显示该类可建项。建筑按定义的 MenuGroup 自动落组、MenuOrder 组内升序；
/// mod 只需在 mods/&lt;模组&gt;/buildings.json 给建筑填 menuGroup/menuOrder 即自动出现在对应分类，
/// 填未知组名会自动新增一个分类页——四类同样支持 mod 式扩展。</summary>
public partial class BuildMenu : PanelContainer
{
    /// <summary>基础分组的固定顺序与中文名（mod 自定义组按其最小 MenuOrder 追加在其后）。</summary>
    private static readonly (string Key, string Label)[] BaseGroups =
    {
        ("infrastructure", "基础设施"),
        ("public", "公共设施"),
        ("official", "官府设施"),
    };

    /// <summary>建筑未指定分组时的默认归属。</summary>
    private const string DefaultGroup = "official";

    private readonly BuildController _build;
    private readonly Button _newsToggle;
    private HBoxContainer _itemRow;
    private string _currentGroup = ""; // 当前展开的分组（里程碑晋级时刷新解锁态）

    /// <summary>newsToggle：公告栏开关按钮（由 NewsPanel 自持逻辑），摆在操作栏最右侧。</summary>
    public BuildMenu(BuildController build, Button newsToggle = null)
    {
        _build = build;
        _newsToggle = newsToggle;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        GrowVertical = GrowDirection.Begin;

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        AddChild(col);

        // 上排：当前分类下的可建项（点下排分类按钮切换）
        _itemRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _itemRow.AddThemeConstantOverride("separation", 8);
        col.AddChild(_itemRow);

        // 下排：选择 | 三大分类 | 可建造区 | 拆除
        var catRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        catRow.AddThemeConstantOverride("separation", 8);
        col.AddChild(catRow);

        AddButton(catRow, "选择", () => { _build.SetModeNone(); ClearItems(); });
        catRow.AddChild(new VSeparator());

        foreach (var (key, label) in GroupsInOrder())
            AddButton(catRow, label, () => ShowGroup(key));

        catRow.AddChild(new VSeparator());
        AddButton(catRow, "可建造区", () => { _build.SetZoneMode(ZoneType.Buildable); ClearItems(); });
        AddButton(catRow, "拆除", () => { _build.SetDemolishMode(); ClearItems(); });

        // 默认展开首个分类；开局未建王爷府时改展官府设施页，让王爷府一眼可见
        ShowGroup(GameState.I.PrinceMansionBuilt ? BaseGroups[0].Key : "official");

        // 公告按钮：叠一层右下收缩容器压在操作栏最右（PanelContainer 子节点同占整栏矩形，
        // 按钮两向 ShrinkEnd 即落在下排分类行的右端）；容器自身 Ignore 鼠标，免遮下层居中的分类按钮
        if (_newsToggle != null)
        {
            var corner = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
            corner.AddThemeConstantOverride("margin_right", 10);
            corner.AddThemeConstantOverride("margin_bottom", 4);
            _newsToggle.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            _newsToggle.SizeFlagsVertical = SizeFlags.ShrinkEnd;
            corner.AddChild(_newsToggle);
            AddChild(corner);
        }

        // 里程碑晋级：刷新当前分组，新解锁的建筑按钮即时点亮
        EventBus.MilestoneReached += OnMilestone;
        // 王爷府建成（BuildingPlaced）：首建门槛解锁，刷新当前分组重新点亮道路/其它官营建筑
        EventBus.BuildingPlaced += OnBuildingPlaced;
    }

    public override void _ExitTree()
    {
        EventBus.MilestoneReached -= OnMilestone;
        EventBus.BuildingPlaced -= OnBuildingPlaced;
    }

    private void OnBuildingPlaced(BuildingInstance _)
    {
        if (_currentGroup != "")
            ShowGroup(_currentGroup); // 建成/拆除后刷新锁态（王爷府首建、唯一置灰）
    }

    private void OnMilestone(int _)
    {
        if (_currentGroup != "")
            ShowGroup(_currentGroup);
    }

    /// <summary>分类顺序：基础分组固定在前，mod 自定义组（组名不在基础组内）按组内最小 MenuOrder 追加在末尾。</summary>
    private static IEnumerable<(string Key, string Label)> GroupsInOrder()
    {
        var result = new List<(string, string)>(BaseGroups);
        var known = new HashSet<string>(BaseGroups.Select(g => g.Key));

        var extra = GameState.I.Defs.Values
            .Where(d => d.MenuOrder > 0 && !string.IsNullOrEmpty(d.MenuGroup) && !known.Contains(d.MenuGroup))
            .GroupBy(d => d.MenuGroup)
            .OrderBy(g => g.Min(d => d.MenuOrder));
        foreach (var g in extra)
            result.Add((g.Key, g.Key)); // 自定义组直接以组名作标签
        return result;
    }

    /// <summary>切换上排为指定分组的可建项：基础设施组附带道路/桥梁/树木内置模式；
    /// 里程碑未到的建筑置灰并标注所需城市等级。</summary>
    private void ShowGroup(string key)
    {
        _currentGroup = key;
        ClearItems();

        bool mansionBuilt = GameState.I.PrinceMansionBuilt;
        if (key == "infrastructure")
        {
            if (!mansionBuilt)
            {
                // 开局首建王爷府前，道路/桥梁/树木置灰
                AddLockedButton(_itemRow, "主路", "需先建造王爷府");
                AddLockedButton(_itemRow, "辅路", "需先建造王爷府");
                AddLockedButton(_itemRow, "桥梁", "需先建造王爷府");
                AddLockedButton(_itemRow, "树木", "需先建造王爷府");
            }
            else
            {
                // 道路/桥按长度计价（每延米，不计宽度），标价带单位一目了然
                AddButton(_itemRow, $"主路 {GameState.RoadCostOf(RoadKind.Main)}/米", () => _build.SetRoadMode(RoadKind.Main));
                AddButton(_itemRow, $"辅路 {GameState.RoadCostOf(RoadKind.Side)}/米", () => _build.SetRoadMode(RoadKind.Side));
                AddButton(_itemRow, $"桥梁 {GameState.BridgeCost}/米", () => _build.SetBridgeMode());
                AddButton(_itemRow, "树木", () => _build.SetTreeMode());
            }
        }

        int milestone = GameState.I.MilestoneLevel;
        foreach (var def in GameState.I.Defs.Values
            .Where(d => d.MenuOrder > 0 && GroupOf(d) == key)
            .OrderBy(d => d.MenuOrder))
        {
            var captured = def; // 闭包捕获当前定义

            // 全局唯一且已建成（如王爷府）：置灰标“已建”
            if (captured.Unique && GameState.I.CountByDef(captured.Id) > 0)
            {
                AddLockedButton(_itemRow, $"{captured.Name}（已建）", "全城唯一，不可再建");
                continue;
            }
            // 开局首建门槛：未建王爷府前，除王爷府外一律置灰
            if (!mansionBuilt && captured.Id != PrinceMansionConfig.DefId)
            {
                AddLockedButton(_itemRow, captured.Name, "需先建造王爷府");
                continue;
            }
            if (captured.MilestoneRequired > milestone)
            {
                // 未解锁：置灰展示所需里程碑，玩家一眼知道升到什么城市才能建
                AddLockedButton(_itemRow, $"{captured.Name}（需{Milestones.NameOf(captured.MilestoneRequired)}）",
                    $"人口达 {Milestones.Of(captured.MilestoneRequired).PopulationRequired} 晋级后解锁");
                continue;
            }
            AddButton(_itemRow, $"{captured.Name} {captured.Cost}", () => _build.SetBuildingMode(captured));
        }
    }

    /// <summary>建筑归属组：空串按官府设施处理。</summary>
    private static string GroupOf(BuildingDef def) =>
        string.IsNullOrEmpty(def.MenuGroup) ? DefaultGroup : def.MenuGroup;

    /// <summary>清空上排（先移出场景树再排队释放，避免与新按钮同帧并存）。</summary>
    private void ClearItems()
    {
        foreach (var c in _itemRow.GetChildren())
        {
            _itemRow.RemoveChild(c);
            c.QueueFree();
        }
    }

    private static void AddButton(HBoxContainer box, string text, System.Action onPressed)
    {
        var btn = new Button { Text = text };
        btn.Pressed += () => onPressed();
        box.AddChild(btn);
    }

    /// <summary>置灰（不可点）按钮：用于未解锁/需前置条件的建造项，鼠标悬停标提示原因。</summary>
    private static void AddLockedButton(HBoxContainer box, string text, string tooltip)
    {
        box.AddChild(new Button { Text = text, Disabled = true, TooltipText = tooltip });
    }
}
