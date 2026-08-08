using Godot;
using System.Collections.Generic;
using System.Linq;

namespace Bianjing;

/// <summary>底部建造菜单：整合为「基础设施 / 公共设施 / 官府设施 / 分区」四大类。
/// 下排点分类切换，上排显示该类可建项；「分区」展开后上排展示建筑区（原可建造区）/耕种区。
/// 建筑按定义的 MenuGroup 自动落组、MenuOrder 组内升序；
/// 王爷府为开局选位落成的首建地标（批次八十一），不进入建造栏；
/// 首建门槛：未落成王爷府前，基础设施整组置灰、其余分类建筑折叠提示，落成后自动点亮。
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
        ("court", "朝廷"), // 批次七十七：朝廷直属机构（柴炭司/市易务）独立分组
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

        // 批次七十二：点「选择」也清当前分组标记，与分类 toggle 状态一致
        AddButton(catRow, "选择", () => { _build.SetModeNone(); _currentGroup = ""; ClearItems(); });
        catRow.AddChild(new VSeparator());

        foreach (var (key, label) in GroupsInOrder())
            AddButton(catRow, label, () => ToggleGroup(key));

        catRow.AddChild(new VSeparator());
        // 批次七十：可建造区+耕种区整合为「分区」菜单——点开在上排展示建筑区/耕种区两个子项
        AddButton(catRow, "分区", () => ToggleGroup("zone"));
        AddButton(catRow, "拆除", () => { _build.SetDemolishMode(); ClearItems(); });

        // 默认展开首个分类（王爷府首建未落成前，基础设施整组置灰引导）
        ShowGroup(BaseGroups[0].Key);

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
        // 王爷府建成（BuildingPlaced）：首建门槛解锁，刷新当前分组重新点亮基础设施/官府建筑
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

    /// <summary>一级分类按钮 toggle（批次七十二）：点一次展开该分类；再点已展开的分类回到选择状态
    /// （退出当前建造模式并收起上排）。展开「分区」时即进入分区模式——规划色块立显，无需再点子项。</summary>
    private void ToggleGroup(string key)
    {
        if (_currentGroup == key && _itemRow.GetChildCount() > 0)
        {
            _build.SetModeNone(); // 再点已展开的分类：默认为选择状态
            _currentGroup = "";
            ClearItems();
            return;
        }
        ShowGroup(key);
        if (key == "zone")
            _build.SetZoneTool(_build.ZoneTool); // 已选工具不变，未在分区模式则进入（色块立显）
    }

    /// <summary>切换上排为指定分组的可建项：基础设施组附带道路/桥梁/树木内置模式；
    /// 分区组展示建筑区/耕种区两个划区子项（进入划区模式后才显示规划色块）；
    /// 王爷府未落成（首建门槛）或里程碑未到的建筑折叠为一个提示按钮（悬停逐条列出），免未解锁项多到超出屏幕。</summary>
    private void ShowGroup(string key)
    {
        _currentGroup = key;
        ClearItems();

        // 首建门槛（批次八十一）：王爷府未落成前，基础设施/公共/官府/朝廷一切营造锁定（置灰或折叠）
        bool mansionBuilt = GameState.I.PrinceMansionBuilt;

        if (key == "zone")
        {
            // 批次七十一：分区页三组互斥按钮——类型（建筑区/耕种区）| 工具（油漆桶/笔刷/拖拽）| 操作（规划/删除）
            // 类型组：决定划区内容；点选即进入分区模式（默认建筑区）
            var zoneGroup = new ButtonGroup();
            var bBuild = new Button
            {
                Text = "建筑区", ToggleMode = true, ButtonGroup = zoneGroup,
                ButtonPressed = _build.Zone == ZoneType.Buildable,
                TooltipText = "居民在此建造房屋/工坊的划定范围",
            };
            bBuild.Pressed += () => _build.SetZoneMode(ZoneType.Buildable);
            _itemRow.AddChild(bBuild);
            var bFarm = new Button
            {
                Text = "耕种区", ToggleMode = true, ButtonGroup = zoneGroup,
                ButtonPressed = _build.Zone == ZoneType.Farmland,
                TooltipText = "农艺村民在此开垦农田（需自有住所，一人一田）",
            };
            bFarm.Pressed += () => _build.SetZoneMode(ZoneType.Farmland);
            _itemRow.AddChild(bFarm);
            _itemRow.AddChild(new VSeparator());
            // 工具组：左键交互方式（默认拖拽拉矩形）
            var toolGroup = new ButtonGroup();
            var tBucket = new Button
            {
                Text = "油漆桶", ToggleMode = true, ButtonGroup = toolGroup,
                ButtonPressed = _build.ZoneTool == ZoneTool.Bucket,
                TooltipText = "单击填充/清除封闭区域（主/辅路与河流为界）",
            };
            tBucket.Pressed += () => _build.SetZoneTool(ZoneTool.Bucket);
            _itemRow.AddChild(tBucket);
            var tBrush = new Button
            {
                Text = "笔刷", ToggleMode = true, ButtonGroup = toolGroup,
                ButtonPressed = _build.ZoneTool == ZoneTool.Brush,
                TooltipText = "按住左键沿路径涂抹",
            };
            tBrush.Pressed += () => _build.SetZoneTool(ZoneTool.Brush);
            _itemRow.AddChild(tBrush);
            var tRect = new Button
            {
                Text = "拖拽", ToggleMode = true, ButtonGroup = toolGroup,
                ButtonPressed = _build.ZoneTool == ZoneTool.Rect,
                TooltipText = "按住左键拖出矩形区域（默认）",
            };
            tRect.Pressed += () => _build.SetZoneTool(ZoneTool.Rect);
            _itemRow.AddChild(tRect);
            _itemRow.AddChild(new VSeparator());
            // 操作组：规划落区 / 删除清除（删除与分区类型无关，三工具通用）
            var opGroup = new ButtonGroup();
            var oPlan = new Button
            {
                Text = "规划", ToggleMode = true, ButtonGroup = opGroup,
                ButtonPressed = !_build.ZoneErase,
                TooltipText = "把所选区域规划为当前类型",
            };
            oPlan.Pressed += () => _build.SetZoneErase(false);
            _itemRow.AddChild(oPlan);
            var oErase = new Button
            {
                Text = "删除", ToggleMode = true, ButtonGroup = opGroup,
                ButtonPressed = _build.ZoneErase,
                TooltipText = "清除所选区域的一切分区规划",
            };
            oErase.Pressed += () => _build.SetZoneErase(true);
            _itemRow.AddChild(oErase);
            return;
        }

        // 基础设施组：道路/桥梁/树木（批次八十一：王爷府未落成前整组置灰，落成后由 OnBuildingPlaced 刷新点亮）
        if (key == "infrastructure")
        {
            // 道路/桥按长度计价（每延米，不计宽度），标价带单位一目了然
            AddGateButton(_itemRow, $"主路 {CurrencyHelper.FormatWen(GameState.RoadCostOf(RoadKind.Main))}/米", () => _build.SetRoadMode(RoadKind.Main), mansionBuilt);
            AddGateButton(_itemRow, $"辅路 {CurrencyHelper.FormatWen(GameState.RoadCostOf(RoadKind.Side))}/米", () => _build.SetRoadMode(RoadKind.Side), mansionBuilt);
            AddGateButton(_itemRow, $"桥梁 {CurrencyHelper.FormatWen(GameState.BridgeCost)}/米", () => _build.SetBridgeMode(), mansionBuilt);
            _itemRow.AddChild(new VSeparator());
            // 道路绘制工具（批次八十）：直线（默认）/贝塞尔曲线/手绘，主路辅路桥梁通用
            var roadToolGroup = new ButtonGroup();
            AddGateToolButton(_itemRow, "直线", _build.RoadTool == RoadTool.Straight, roadToolGroup,
                () => _build.SetRoadTool(RoadTool.Straight), "按住左键定起点，拖到终点松开，两点间画一条直线道路", mansionBuilt);
            AddGateToolButton(_itemRow, "曲线", _build.RoadTool == RoadTool.Bezier, roadToolGroup,
                () => _build.SetRoadTool(RoadTool.Bezier), "按住左键定起点，拖动控制弯曲，松开落笔（曲线弯向拖动方向右侧）", mansionBuilt);
            AddGateToolButton(_itemRow, "手绘", _build.RoadTool == RoadTool.Freehand, roadToolGroup,
                () => _build.SetRoadTool(RoadTool.Freehand), "按住左键沿鼠标轨迹自由涂抹（原版绘制方式）", mansionBuilt);
            _itemRow.AddChild(new VSeparator());
            AddGateButton(_itemRow, "树木", () => _build.SetTreeMode(), mansionBuilt);
        }

        int milestone = GameState.I.MilestoneLevel;
        var locked = new List<BuildingDef>(); // 未解锁项先收拢，组末折叠成一个提示按钮
        foreach (var def in GameState.I.Defs.Values
            .Where(d => d.MenuOrder > 0 && GroupOf(d) == key)
            .OrderBy(d => d.MenuOrder))
        {
            var captured = def; // 闭包捕获当前定义

            // 王爷府开局选位落成：不进建造栏（不展示、不锁定、不置灰）
            if (captured.Id == PrinceMansionConfig.DefId)
                continue;
            // 全局唯一且已建成：置灰标“已建”
            if (captured.Unique && GameState.I.CountByDef(captured.Id) > 0)
            {
                AddLockedButton(_itemRow, $"{captured.Name}（已建）", "全城唯一，不可再建");
                continue;
            }
            // 首建门槛（批次八十一）：王爷府未落成前，其余建筑一律视为未解锁（折叠提示，落成后点亮）
            if (!mansionBuilt)
            {
                locked.Add(captured);
                continue;
            }
            // 未解锁：收拢到折叠提示，避免官府设施等组未解锁项逐条置灰导致超出屏幕
            if (captured.MilestoneRequired > milestone)
            {
                locked.Add(captured);
                continue;
            }
            AddButton(_itemRow, $"{captured.Name} {CurrencyHelper.FormatWen(captured.Cost)}", () => _build.SetBuildingMode(captured));
        }
        if (locked.Count > 0)
        {
            // 折叠按钮悬停列出全部未解锁项：首建门槛未过统一提示，否则逐条列名称 + 所需等级 + 人口门槛
            var detail = mansionBuilt
                ? string.Join("\n",
                    locked.OrderBy(d => d.MilestoneRequired)
                        .Select(d => $"{d.Name}（需{Milestones.NameOf(d.MilestoneRequired)}·人口{Milestones.Of(d.MilestoneRequired).PopulationRequired}）"))
                : "需先落成王爷府（开局点击地图落成，随后解锁一切营造）";
            AddLockedButton(_itemRow, $"另有 {locked.Count} 项未解锁", detail);
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

    /// <summary>互斥 toggle 工具按钮（直线/曲线/手绘）：按下态与控制器当前工具同步，点击即切换。</summary>
    private static void AddToolButton(HBoxContainer box, string text, bool pressed, ButtonGroup group,
        System.Action onPressed, string tooltip)
    {
        var btn = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonGroup = group,
            ButtonPressed = pressed,
            TooltipText = tooltip,
        };
        btn.Pressed += () => onPressed();
        box.AddChild(btn);
    }

    /// <summary>首建门槛按钮（批次八十一）：王爷府未落成前基础设施一律置灰（悬停提示先决条件），落成后正常可点。</summary>
    private static void AddGateButton(HBoxContainer box, string text, System.Action onPressed, bool mansionBuilt)
    {
        if (mansionBuilt)
            AddButton(box, text, onPressed);
        else
            AddLockedButton(box, text, "需先落成王爷府（开局点击地图落成）");
    }

    /// <summary>首建门槛版工具按钮（直线/曲线/手绘）：与 AddGateButton 同规则。</summary>
    private static void AddGateToolButton(HBoxContainer box, string text, bool pressed, ButtonGroup group,
        System.Action onPressed, string tooltip, bool mansionBuilt)
    {
        if (mansionBuilt)
            AddToolButton(box, text, pressed, group, onPressed, tooltip);
        else
            AddLockedButton(box, text, "需先落成王爷府（开局点击地图落成）");
    }

    /// <summary>置灰（不可点）按钮：用于未解锁/需前置条件的建造项，鼠标悬停标提示原因。</summary>
    private static void AddLockedButton(HBoxContainer box, string text, string tooltip)
    {
        box.AddChild(new Button { Text = text, Disabled = true, TooltipText = tooltip });
    }
}
