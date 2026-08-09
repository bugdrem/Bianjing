namespace Bianjing;

/// <summary>
/// 耕种区配置：农田开垦与升级门槛
/// （业务归属：FarmlandSystem 全流程——区块连通分组、田块开垦尺寸阶梯、升级资产门槛与扣款）。
/// 数据驱动方向：田块本体为 buildings.json 的 farmland 定义（占地/收获周期/岗位/产量），
/// 后续果树/桑田等新田种只需新增定义并按 Def.ProduceGoods 区分，无需改本类逻辑。
/// </summary>
public static class FarmlandConfig
{
    /// <summary>田块开垦尺寸阶梯（边长米，按序尝试：优先大块，块内放不下依次退小块）。</summary>
    public static readonly int[] FieldSizeTiers = { 6, 4, 2 };

    /// <summary>田块升级资产门槛（文，索引 0 = 一级升二级；升到 N 级需 N-1 项），升级同时扣款入官库
    /// （土地相关交王爷，批次七十八起——旧注释“入家庭公产”与实现不符）。</summary>
    public static readonly long[] UpgradeAssets = { 5_000, 20_000, 50_000 };

    /// <summary>田块升级扣款（文，索引与 UpgradeAssets 对齐）：农户扩大生产投入，家庭公产不足则不升（款入官库）。</summary>
    public static readonly long[] UpgradeCosts = { 5_000, 20_000, 50_000 };

    /// <summary>田块升级的额外硬条件：全城存在此数以上的闲置农艺劳动力（升级才有意义，防白升）。</summary>
    public const int UpgradeSpareFarmers = 1;

    /// <summary>开垦/升级的每旬结算概率（批次九十一：日值×7/3）：给全城农场推进留出呼吸感，避免划区瞬间齐刷刷长满。</summary>
    public const float FarmChancePerDay = 0.5833f;

    // ---- 一年两熟与产量加成（批次七十四/八十五）：农田只在收获窗口内产出——每年 6 月、9 月两熟，
    // 窗口外（含冬季 10-12 月）累计月数归零重新播种；一工两熟年产 100 份（田赋 10% 后 90）≈ 供 2.5 人年食
    // （口粮 0.2333 份/人/旬，旧注释 60 份标 5 人年食与实际口径脱节），配合家庭采果/野味补足一 4-5 口之家；
    // 批次八十五：农田岗位发固定工钱（salary 800），农民家庭现金收入不再只靠卖粮（≈40 文/月，不足开销 1/5）----

    /// <summary>农田收获月份窗口 [HarvestStartMonth, HarvestEndMonth]（含）：窗口外归零重播，冬季（10-12 月）休整。</summary>
    public const int HarvestStartMonth = 4;
    public const int HarvestEndMonth = 9;

    /// <summary>田主在岗产量加成（田主亲自下地多收两成）。</summary>
    public const double OwnerYieldBonus = 0.2;

    /// <summary>技能产量加成：在岗农夫平均经验达 SkillYieldFullExp（高级技工）时封顶加此值。</summary>
    public const double SkillYieldMaxBonus = 0.5;
    public const float SkillYieldFullExp = 600f;

    // ---- 种植需求度（批次七十四）：全城缺粮（需求账本 grain 短缺）时开垦加速、升级门槛放宽，
    // 保证人口缓慢增长时全局存粮也缓慢增长（需求越高门槛越低）----

    /// <summary>缺粮时开垦/升级日概率倍率。</summary>
    public const float ScarcityReclaimBoost = 3f;

    /// <summary>缺粮时田块升级资产门槛折扣（门槛 ×(1-本值)）。</summary>
    public const double UpgradeScarcityDiscount = 0.5;

    /// <summary>田块面积与岗位的换算（岗位已由 JobSlotsByLevel 数据驱动，此处仅作存档/展示说明位）。</summary>
    public const int BaseFieldArea = 36;
}
