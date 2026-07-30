namespace Bianjing;

/// <summary>
/// 野生动物配置：种群承载、刷新、繁育与减员（业务归属：WildlifeSystem）。
/// 种群上限 = min(HardCap, 树木数 ÷ TreesPerAnimal)，与林地量成正比。
/// </summary>
public static class WildlifeConfig
{
    /// <summary>每多少格树林支撑一只动物（总数与林地成正比）。</summary>
    public const int TreesPerAnimal = 15;

    /// <summary>种群硬上限（防极端密林爆量）。</summary>
    public const int HardCap = 240;

    /// <summary>每月补充新个体的触发概率（种群未满时在无动物的树林边刷新）。</summary>
    public const float SpawnChancePerMonth = 0.5f;

    /// <summary>刷新点该半径（米）内无动物才算「此处动物少于一」。</summary>
    public const int LonelyRadius = 24;

    /// <summary>月繁育概率（全图两只以上即可，不分性别）/ 月自然死亡概率。</summary>
    public const float BreedChance = 0.12f;
    public const float NaturalDeathChance = 0.01f;

    /// <summary>日游走半径（米，倾向树林、远离人烟）。</summary>
    public const int WanderRadius = 4;
}
