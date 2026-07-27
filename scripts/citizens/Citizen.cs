using System.Collections.Generic;

namespace Bianjing;

public enum Gender
{
    Male,
    Female,
}

/// <summary>就业形态：无业 / 受雇于建筑 / 进山自谋生路（伐木采猎）/ 修缮匠（维护公共设施）。</summary>
public enum JobKind
{
    None,
    Employed,
    Logger,
    Repairer,
}

/// <summary>居民当前活动（表现层状态机驱动，随存档保存；新值只能尾部追加，防老档枚举错位）。</summary>
public enum ActivityType
{
    RestHome,
    Working,
    Shopping,
    Playing,
    Strolling,
    Logging,
    Gathering,
    Hunting,
    Trading,
    Repairing,
    /// <summary>把携带的货物搬回自家入库。</summary>
    Hauling,
}

/// <summary>
/// 真实居民：从迁入/出生到迁出/死亡的完整生命周期个体。
/// 纯数据类（不含 Godot 类型），可直接 JSON 序列化入存档；
/// Extra 字典为 mod 与后续系统（教育/官职/声望等）预留扩展位。
/// </summary>
public class Citizen
{
    public int Id;
    public string Surname = "";
    public string Name = "";
    public Gender Gender;

    /// <summary>年龄按游戏月计（每月结算 +1）。</summary>
    public int AgeMonths;

    // ---- 社会关系（存 Id，避免对象图循环）----
    public int FamilyId = -1;
    public int SpouseId = -1;
    public int FatherId = -1;
    public int MotherId = -1;
    public List<int> ChildrenIds = new();
    public List<int> FriendIds = new();

    // ---- 居住与工作 ----
    public int HomeId = -1;
    public JobKind JobKind = JobKind.None;
    public int WorkplaceId = -1;

    /// <summary>个人资产。</summary>
    public double Money;

    // ---- 状态值（表现层实时驱动，随存档保存）----
    /// <summary>疲劳值 0-100，工作累积，休息消解。</summary>
    public float Fatigue;

    /// <summary>兴趣值 0-100，闲逛/玩耍积攒。</summary>
    public float Fun = 50f;

    /// <summary>当前活动（表现层同步，读档后恢复）。</summary>
    public ActivityType Activity = ActivityType.RestHome;

    /// <summary>世界坐标（表现层每帧同步，PosValid 为真时读档可恢复）。</summary>
    public float PosX;
    public float PosZ;
    public bool PosValid;

    /// <summary>携带的货物（grain/wood/fruit/game，一担；待搬回家或去市集交易；空串表示空手）。</summary>
    public string Carrying = "";

    /// <summary>无家可归的持续月数，过久则迁出。</summary>
    public int HomelessMonths;

    /// <summary>连续缺粮/缺柴天数（家中无存货且买不到，需求面板展示）。</summary>
    public int FoodShortDays;
    public int FuelShortDays;

    /// <summary>mod / 未来系统扩展字段。</summary>
    public Dictionary<string, string> Extra = new();

    // ---- 派生属性 ----
    public int AgeYears => AgeMonths / 12;
    public bool IsChild => AgeYears < 16;
    public bool IsAdult => AgeYears >= 16 && AgeYears < 60;
    public bool IsElder => AgeYears >= 60;
    public bool HasJob => JobKind != JobKind.None;
    public bool IsMarried => SpouseId >= 0;

    /// <summary>身份：由年龄与职业派生（后续官职/爵位系统可覆盖）。</summary>
    public string GetIdentity(GameState gs)
    {
        if (IsChild)
            return "孩童";
        if (JobKind == JobKind.Logger)
            return "山民";
        if (JobKind == JobKind.Repairer)
            return "修缮匠";
        if (JobKind == JobKind.Employed && gs.Buildings.TryGetValue(WorkplaceId, out var b))
        {
            return b.Def.Id switch
            {
                "yamen" => "官吏",
                "barracks" => "士兵",
                "palace" => "仆役",
                "shop" => "商贩",
                "workshop" => "工匠",
                "farm" => "农夫",
                _ => "雇工",
            };
        }
        return IsElder ? "长者" : "平民";
    }
}
