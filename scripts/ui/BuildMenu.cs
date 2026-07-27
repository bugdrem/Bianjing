using Godot;

namespace Bianjing;

/// <summary>底部建造菜单：道路/官方建筑/坊区/拆除。</summary>
public partial class BuildMenu : PanelContainer
{
    // 水井暂时下架（定义保留以兼容老存档）
    private static readonly string[] OfficialOrder = { "palace", "yamen", "barracks", "farm" };

    private readonly BuildController _build;

    public BuildMenu(BuildController build)
    {
        _build = build;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        GrowVertical = GrowDirection.Begin;

        var box = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);

        AddButton(box, "选择", () => _build.SetModeNone());
        AddButton(box, $"道路 {GameState.RoadCost}", () => _build.SetRoadMode());
        AddButton(box, $"桥梁 {GameState.BridgeCost}", () => _build.SetBridgeMode());

        var defs = GameState.I.Defs;
        foreach (var id in OfficialOrder)
        {
            var def = defs[id];
            AddButton(box, $"{def.Name} {def.Cost}", () => _build.SetBuildingMode(def));
        }

        AddButton(box, "住宅坊", () => _build.SetZoneMode(ZoneType.Residential));
        AddButton(box, "市坊", () => _build.SetZoneMode(ZoneType.Market));
        AddButton(box, "工坊区", () => _build.SetZoneMode(ZoneType.Workshop));
        AddButton(box, "树木", () => _build.SetTreeMode());
        AddButton(box, "拆除", () => _build.SetDemolishMode());
    }

    private static void AddButton(HBoxContainer box, string text, System.Action onPressed)
    {
        var btn = new Button { Text = text };
        btn.Pressed += () => onPressed();
        box.AddChild(btn);
    }
}
