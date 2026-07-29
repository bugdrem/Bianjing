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
    private HBoxContainer _itemRow;
    private string _currentGroup = ""; // 当前展开的分组（里程碑晋级时刷新解锁态）

    public BuildMenu(BuildController build)
    {
        _build = build;
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

        // 默认展开首个分类，避免上排空白
        ShowGroup(BaseGroups[0].Key);

        // 里程碑晋级：刷新当前分组，新解锁的建筑按钮即时点亮
        EventBus.MilestoneReached += OnMilestone;
    }

    public override void _ExitTree() => EventBus.MilestoneReached -= OnMilestone;

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

        if (key == "infrastructure")
        {
            // 道路/桥按长度计价（每延米，不计宽度），标价带单位一目了然
            AddButton(_itemRow, $"主路 {GameState.RoadCostOf(RoadKind.Main)}/米", () => _build.SetRoadMode(RoadKind.Main));
            AddButton(_itemRow, $"辅路 {GameState.RoadCostOf(RoadKind.Side)}/米", () => _build.SetRoadMode(RoadKind.Side));
            AddButton(_itemRow, $"桥梁 {GameState.BridgeCost}/米", () => _build.SetBridgeMode());
            AddButton(_itemRow, "树木", () => _build.SetTreeMode());
        }

        int milestone = GameState.I.MilestoneLevel;
        foreach (var def in GameState.I.Defs.Values
            .Where(d => d.MenuOrder > 0 && GroupOf(d) == key)
            .OrderBy(d => d.MenuOrder))
        {
            var captured = def; // 闭包捕获当前定义
            if (captured.MilestoneRequired > milestone)
            {
                // 未解锁：置灰展示所需里程碑，玩家一眼知道升到什么城市才能建
                var btn = new Button
                {
                    Text = $"{captured.Name}（需{Milestones.NameOf(captured.MilestoneRequired)}）",
                    Disabled = true,
                    TooltipText = $"人口达 {Milestones.Of(captured.MilestoneRequired).PopulationRequired} 晋级后解锁",
                };
                _itemRow.AddChild(btn);
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
}
