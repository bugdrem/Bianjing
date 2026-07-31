namespace Bianjing;

/// <summary>
/// 村民配置：模型缩放与行为层参数（代理数量、分离推力、决策阈值、家庭储备目标与关键概率）
/// （业务归属：AgentManager 代理管理、CitizenAgent 渲染与日常决策；
/// 各活动的疲劳/兴致速率与驻留时长属表现手感参数，仍内联在 CitizenAgent.ApplyActivityNeeds）。
/// </summary>
public static class VillagerConfig
{
    /// <summary>成年人模型整体缩放（1.0 为原始大小；儿童在此基础上再按年龄折算）。</summary>
    public const float ModelScale = 0.25f;

    /// <summary>新生儿体型占成人的比例（体型从此值线性生长到成年门槛处的 1.0）。</summary>
    public const float ChildMinScale = 0.4f;

    // ---- 行为层（原 AgentConfig 并入）----

    /// <summary>表现层代理上限（超出的居民只参与数据模拟，不上屏）。</summary>
    public const int MaxAgents = 300;

    /// <summary>人群分离半径（米，小于邻桶覆盖距离）/ 推力强度。</summary>
    public const float SeparationRadius = 0.9f;
    public const float SeparationStrength = 3f;

    /// <summary>疲劳达此回家歇息 / 兴致低于此出门散心。</summary>
    public const float TiredThreshold = 80f;
    public const float BoredThreshold = 25f;

    /// <summary>车道随机偏移幅度（米）：路格宽 1m，偏移后不出本格太远，避免行人排成一线。</summary>
    public const float LaneJitterRange = 0.45f;

    /// <summary>每斧砍伐伤害（幼树一斧倒，老树需多斧）。</summary>
    public const float ChopDamage = 25f;

    /// <summary>公式：血量 → 柴薪折算（份/血）：一斧 ChopDamage 血恰好一担（LoadUnits 份）。</summary>
    public const double WoodPerHp = EconomyConfig.LoadUnits / ChopDamage;

    /// <summary>家庭储备目标（份/人，约一月用量）：食物 / 柴薪 / 饮水，低于目标一半触发补货/打水。</summary>
    public const double FoodPerResident = 3.0;
    public const double WoodPerResident = 1.0;
    public const double WaterPerResident = 3.0;

    /// <summary>就近采集半径（米）：伐木/采摘/拾堆/打猎只在此范围内找目标，
    /// 附近没有就闲逛等林子长回来；打水不受此限（水是刚需且无替代来源）。</summary>
    public const int ForageRadius = 64;

    /// <summary>主妇外出采购概率 / 老人出门闲逛概率（每次决策）。</summary>
    public const float HousewifeShopChance = 0.6f;
    public const float ElderStrollChance = 0.5f;
}
