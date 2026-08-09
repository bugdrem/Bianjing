using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Bianjing;

/// <summary>
/// 科技静态定义，从 res://data/techs.json 加载（mods/&lt;模组&gt;/techs.json 可覆盖/追加，机制同建筑定义）。
/// 两种解锁模式：
///   passive——条件（里程碑+前置科技）达成后自动研成，不花钱；
///   active——玩家在研习面板主动立项，逐旬从官库拨研习经费，旬数攒满研成。
/// Effects 为效果键→加成值（如 "harvest":0.2 表示收成 +20%），由 GameState.TechFactor 汇总供各系统取用；
/// mod 新科技可复用现有效果键，新效果键则需代码侧接线。
/// </summary>
public class TechDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>解锁模式：active=主动研习 / passive=条件达成自动研成。</summary>
    public string Mode { get; set; } = "passive";

    /// <summary>所需里程碑等级（两种模式通用门槛）。</summary>
    public int MilestoneRequired { get; set; }

    /// <summary>前置科技 id 列表（全部研成才可解锁/立项）。</summary>
    public List<string> Prerequisites { get; set; } = new();

    /// <summary>主动模式：总研习经费（文，逐旬均摊拨付）与所需旬数（techs.json 的 researchDays，单位：旬）。</summary>
    public double CostMoney { get; set; }
    public int ResearchDays { get; set; } = 13;

    /// <summary>效果键→加成值（harvest 收成 / craft 加工 / tax 税收 / mint 铸币）。</summary>
    public Dictionary<string, double> Effects { get; set; } = new();

    public bool IsActive => Mode == "active";
}

/// <summary>科技注册表：游戏启动时加载一次（基础 + mod 合并）。</summary>
public static class TechDefs
{
    private static Dictionary<string, TechDef> _all;

    /// <summary>全部科技（保持定义顺序，面板按此排列）。</summary>
    public static IReadOnlyCollection<TechDef> All => Load().Values;

    public static TechDef Find(string id) => Load().GetValueOrDefault(id);

    private static Dictionary<string, TechDef> Load()
    {
        if (_all != null)
            return _all;
        _all = new Dictionary<string, TechDef>();

        // 1) 基础定义：随游戏发行
        using (var f = Godot.FileAccess.Open("res://data/techs.json", Godot.FileAccess.ModeFlags.Read))
        {
            if (f != null)
                MergeInto(_all, f.GetAsText());
            else
                Godot.GD.PushError("缺少 res://data/techs.json，科技树为空。");
        }

        // 2) mod 追加/覆盖：mods/<模组>/techs.json 按目录名升序
        string modsDir = GamePaths.ModsDir;
        if (Directory.Exists(modsDir))
        {
            foreach (string dir in Directory.GetDirectories(modsDir).OrderBy(d => d))
            {
                string file = Path.Combine(dir, "techs.json");
                if (!File.Exists(file))
                    continue;
                try
                {
                    MergeInto(_all, File.ReadAllText(file));
                    Godot.GD.Print($"[mod] 载入科技定义 {Path.GetFileName(dir)}（现共 {_all.Count} 项）");
                }
                catch (System.Exception e)
                {
                    Godot.GD.PushWarning($"[mod] 解析 {file} 失败：{e.Message}");
                }
            }
        }
        return _all;
    }

    private static void MergeInto(Dictionary<string, TechDef> dict, string json)
    {
        var list = JsonSerializer.Deserialize<List<TechDef>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (list == null)
            return;
        foreach (var def in list)
            dict[def.Id] = def;
    }
}
