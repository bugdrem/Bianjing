using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 税收政策（批次五十六重写：三税种模型——土地税、商税、人口税）。
/// 纯数据，随存档 JSON 序列化。老档 Levels 字段保留兼容反序列化（新档不再使用）。
/// </summary>
public class TaxPolicy
{
    /// <summary>土地税税率（1%~10% 可调，默认 3%），作用于每栋建筑的定额税基（见 TaxSystem.BuildingTaxBase）。</summary>
    public double LandTaxRate = EconomyConfig.LandTaxRateDefault;

    /// <summary>商税税率（2%~15% 可调，默认 5%），交易发生时自动扣除。</summary>
    public double TradeTaxRate = EconomyConfig.TradeTaxRateDefault;

    /// <summary>人口税是否开启（默认关闭）；开启时从雇工工资中扣 20%，持续降幸福。</summary>
    public bool PollTaxEnabled;

    /// <summary>土地税档位 0-3（兼容旧 UI：免征/轻/中/重），0→1%, 1→3%, 2→6%, 3→10%。</summary>
    public int LandTaxLevel
    {
        get
        {
            if (LandTaxRate <= EconomyConfig.LandTaxRateMin) return 0;
            if (LandTaxRate <= 0.03) return 1;
            if (LandTaxRate <= 0.06) return 2;
            return 3;
        }
        set => LandTaxRate = value switch
        {
            0 => EconomyConfig.LandTaxRateMin,
            1 => 0.03,
            2 => 0.06,
            _ => EconomyConfig.LandTaxRateMax,
        };
    }

    /// <summary>商税档位 0-3（兼容旧 UI），0→2%, 1→5%, 2→10%, 3→15%。</summary>
    public int TradeTaxLevel
    {
        get
        {
            if (TradeTaxRate <= EconomyConfig.TradeTaxRateMin) return 0;
            if (TradeTaxRate <= 0.05) return 1;
            if (TradeTaxRate <= 0.10) return 2;
            return 3;
        }
        set => TradeTaxRate = value switch
        {
            0 => EconomyConfig.TradeTaxRateMin,
            1 => 0.05,
            2 => 0.10,
            _ => EconomyConfig.TradeTaxRateMax,
        };
    }

    public static readonly string[] LevelNames = { "免征/极低", "轻税", "中税", "重税" };

    /// <summary>旧档兼容：老版税种档位字典（v≤55），新档不再写入。
    /// 反序列化时如存在则尝试迁移到新字段（见 SaveService 迁移逻辑）。</summary>
    public Dictionary<string, int> Levels = new();
}
