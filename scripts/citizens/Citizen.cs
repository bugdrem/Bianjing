using System.Collections.Generic;

namespace Bianjing;

public enum Gender
{
    Male,
    Female,
}

/// <summary>NPC 技能类型（需求 §3.3）：体力（樵夫/矿工/建筑工）、手艺（工匠/技工）、
/// 商业（掌柜/账房）、文化（教师/医师）、战斗（散勇/兵卒）。
/// 枚举只能尾部追加，防老档整数错位。</summary>
public enum SkillType
{
    None,
    Labor,       // 体力
    Craft,       // 手艺
    Commerce,    // 商业
    Scholarship, // 文化
    Combat,      // 战斗（散勇随身，需求 §2.2）
}

/// <summary>就业形态：无业 / 受雇于建筑（含修缮房）/ 进山自谋生路（伐木采猎）。</summary>
public enum JobKind
{
    None,
    Employed,
    Logger,
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
    /// <summary>把背的货物挑去目标建筑入库（自家或田仓）。</summary>
    Hauling,
    /// <summary>走到地面物资堆拾货入背包。</summary>
    PickingUp,
    /// <summary>在水井/河岸打水入背包（水仅家用，背回家入库）。</summary>
    FetchingWater,
}

/// <summary>居民年龄履历条目：仅记录重大人生事件（迁入/出生/成婚/得子女/分家/迁居/就业变动/丧偶）。</summary>
public class LifeEvent
{
    /// <summary>事件发生的游戏年月。</summary>
    public int Year;
    public int Month;

    /// <summary>事件描述（中文短句，直接用于面板展示）。</summary>
    public string Text = "";
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

    /// <summary>个人资产（文）。</summary>
    public long Money;

    /// <summary>技能类型（批次五十六新增）。</summary>
    public SkillType Skill = SkillType.None;

    /// <summary>技能经验值（在主技能上累积，达阈值升级为 Skilled / Expert）。</summary>
    public float SkillExp;

    /// <summary>携带物品清单（迁入流民特殊携带：寓商≫散勇武器≫客士书籍），入存档。</summary>
    public List<string> CarriedItems = new();

    // ---- 状态值（表现层实时驱动，随存档保存）----
    /// <summary>疲劳值 0-100，工作累积，休息消解。</summary>
    public float Fatigue;

    /// <summary>兴趣值 0-100，闲逛/玩耍积攒。</summary>
    public float Fun = 50f;

    /// <summary>健康值 0-100（预埋接口）：默认满值；后续健康系统会随疾病/伤病/营养下降，
    /// 并经死亡率放大系数影响寿命（见 LifecycleSystem.HealthMortalityFactor）。</summary>
    public float Health = 100f;

    /// <summary>当前活动（表现层同步，读档后恢复）。</summary>
    public ActivityType Activity = ActivityType.RestHome;

    /// <summary>世界坐标（表现层每帧同步，PosValid 为真时读档可恢复）。</summary>
    public float PosX;
    public float PosZ;
    public bool PosValid;

    /// <summary>背包（统一仓储接口，典型案例二）：容量一担，后期载具货舱同接口；待搬回家/入仓或去市集交易。</summary>
    public Inventory Pack = new() { Capacity = Goods.LoadUnits };

    /// <summary>背包首堆货品 id（空背包返回空串，兼容旧逻辑的单货品判断）。</summary>
    public string PackGoodsId => Pack.Stacks.Count > 0 ? Pack.Stacks[0].GoodsId : "";

    /// <summary>无家可归的持续月数，过久则迁出。</summary>
    public int HomelessMonths;

    /// <summary>连续缺粮/缺柴天数（家中无存货且买不到，需求面板展示）。</summary>
    public int FoodShortDays;
    public int FuelShortDays;

    /// <summary>供货认领：出发为某建筑采集/补料时登记目标与货品（-1/空串=无认领），
    /// 需求判定扣除在途认领量防多人扎堆；背包腾空进入新决策时释放（随存档，旧档缺字段取默认值）。</summary>
    public int ClaimBuildingId = -1;
    public string ClaimGoodsId = "";

    /// <summary>年龄履历（重大事件按时间正序追加，随存档保存，上限见 GameState.LogLifeEvent）。</summary>
    public List<LifeEvent> LifeEvents = new();

    /// <summary>mod / 未来系统扩展字段。</summary>
    public Dictionary<string, string> Extra = new();

    // ---- 派生属性 ----
    public int AgeYears => AgeMonths / 12;
    public bool IsChild => AgeYears < LifeConfig.AdultAgeYears;
    public bool IsAdult => AgeYears >= LifeConfig.AdultAgeYears && AgeYears < LifeConfig.ElderAgeYears;
    public bool IsElder => AgeYears >= LifeConfig.ElderAgeYears;
    public bool HasJob => JobKind != JobKind.None;
    public bool IsMarried => SpouseId >= 0;

    /// <summary>身份：由年龄与职业派生（后续官职/爵位系统可覆盖）。</summary>
    public string GetIdentity(GameState gs)
    {
        if (IsChild)
            return "孩童";
        // 寄居流民营且无业的流民（受雇后按职业定身份）
        if (JobKind == JobKind.None && HomeId >= 0
            && gs.Buildings.TryGetValue(HomeId, out var camp) && camp.Def.Id == "refugee_camp")
            return "流民";
        if (JobKind == JobKind.Logger)
            return "山民";
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
                "repairhouse" => "修缮匠",
                "taxoffice" => "税吏",
                "mint" => "铸钱匠",
                "mine" => "矿工",
                "saltworks" => "盐工",
                _ => "雇工",
            };
        }
        return IsElder ? "长者" : "平民";
    }
}
