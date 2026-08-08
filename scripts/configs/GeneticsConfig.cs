namespace Bianjing;

/// <summary>
/// 技能遗传配置：城内出生的新生儿技能继承与变异规则
/// （业务归属：LifecycleSystem.Births 的 RollInheritedSkill——遗传算法：
/// 随机继承父或母的主技能，经验按比例衰减继承；小概率变异换型；父母皆无技能时偶发开蒙）。
/// </summary>
public static class GeneticsConfig
{
    /// <summary>新生儿继承父亲技能的概率（余量 = 继承母亲）；父母皆无技能时不适用。</summary>
    public const double InheritFatherChance = 0.5;

    /// <summary>出生变异概率：命中则技能类型与经验全部重新随机（不继承）。</summary>
    public const double MutationChancePerBirth = 0.05;

    /// <summary>继承经验的比例系数区间（父/母经验 × 随机系数）：下限 + 随机跨度 → 0.3~0.7。</summary>
    public const float ExpInheritMin = 0.3f;
    public const float ExpInheritSpan = 0.4f;

    /// <summary>变异/开蒙后的经验区间（起步 + 随机跨度）。</summary>
    public const float MutationExpMin = 20f;
    public const float MutationExpSpan = 100f;

    /// <summary>父母皆无技能时新生儿随机开蒙获得技能的概率（余量 = 随父母无技能）。</summary>
    public const double SkilllessRandomChance = 0.1;
}
