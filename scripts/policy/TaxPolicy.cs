using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 单个税种定义：计税基数由回调从当前局面算出。
/// 数据驱动设计——mod 可向 TaxDefs.All 注册新税种，存档只记录档位不受影响。
/// </summary>
public class TaxDef
{
    public string Id = "";
    public string Name = "";
    public string Description = "";

    /// <summary>月度计税基数（贯），实收 = 基数 × 档位税率。</summary>
    public Func<GameState, double> MonthlyBase = _ => 0;
}

/// <summary>内置四大税种注册表：田赋 / 商税 / 专卖税 / 市舶税。</summary>
public static class TaxDefs
{
    public static readonly List<TaxDef> All = new()
    {
        new TaxDef
        {
            Id = "land",
            Name = "田赋（两税）",
            Description = "基础经济层：按粮田与在籍户数征收",
            MonthlyBase = gs => gs.CountByDef("farm") * TaxConfig.FarmBasePerFarm
                + gs.Families.Count * TaxConfig.FarmBasePerFamily,
        },
        new TaxDef
        {
            Id = "trade",
            Name = "商税（过税+住税）",
            Description = "贸易与经济层：按城中商铺经营规模征收",
            MonthlyBase = gs => SumTaxBonus(gs, "shop"),
        },
        new TaxDef
        {
            Id = "monopoly",
            Name = "专卖税（盐/茶/酒）",
            Description = "资源垄断层：按工坊与矿盐官产的榷货专卖征收",
            MonthlyBase = gs => SumTaxBonus(gs, "workshop") + SumTaxBonus(gs, "saltworks") + SumTaxBonus(gs, "mine"),
        },
        new TaxDef
        {
            Id = "maritime",
            Name = "市舶税（海外关税）",
            Description = "探索与扩张层：需开设港口市舶司（待后续版本）",
            MonthlyBase = gs => SumTaxBonus(gs, "port") + gs.CountByDef("port") * TaxConfig.PortBasePerPort,
        },
    };

    private static double SumTaxBonus(GameState gs, string defId)
    {
        double sum = 0;
        foreach (var b in gs.Buildings.Values)
            if (b.Def.Id == defId)
                sum += b.Def.TaxBonus;
        return sum;
    }
}

/// <summary>税收政策（纯数据，随存档保存）：每税种一个档位。</summary>
public class TaxPolicy
{
    public const int MaxLevel = 3;
    public static readonly string[] LevelNames = { "免征", "轻税", "中税", "重税" };

    /// <summary>税种 Id -&gt; 档位(0-3)，未设置的税种默认轻税。</summary>
    public Dictionary<string, int> Levels = new();

    public int LevelOf(string taxId) => Levels.GetValueOrDefault(taxId, 1);

    public void SetLevel(string taxId, int level) => Levels[taxId] = Math.Clamp(level, 0, MaxLevel);

    /// <summary>档位税率倍数（步长取自 TaxConfig）：免征0 / 轻0.5 / 中1.0 / 重1.5。</summary>
    public static double RateOf(int level) => level * TaxConfig.RatePerLevel;
}
