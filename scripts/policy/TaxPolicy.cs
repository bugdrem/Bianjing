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

    /// <summary>土地税档位 0-3（兼容旧 UI：免征/轻/中/重），档位与税率全部引用 EconomyConfig 常量
    /// （批次八十七：旧版 getter 写死 0.03/0.06 字面量，配置调整后两处口径分裂）。</summary>
    public int LandTaxLevel
    {
        get
        {
            if (LandTaxRate <= EconomyConfig.LandTaxRateMin) return 0;
            if (LandTaxRate <= EconomyConfig.LandTaxRateDefault) return 1;
            if (LandTaxRate <= EconomyConfig.LandTaxRateHeavy) return 2;
            return 3;
        }
        set => LandTaxRate = value switch
        {
            0 => EconomyConfig.LandTaxRateMin,
            1 => EconomyConfig.LandTaxRateDefault,
            2 => EconomyConfig.LandTaxRateHeavy,
            _ => EconomyConfig.LandTaxRateMax,
        };
    }

    /// <summary>商税档位 0-3（兼容旧 UI），档位与税率全部引用 EconomyConfig 常量（批次八十七：同土地税）。</summary>
    public int TradeTaxLevel
    {
        get
        {
            if (TradeTaxRate <= EconomyConfig.TradeTaxRateMin) return 0;
            if (TradeTaxRate <= EconomyConfig.TradeTaxRateDefault) return 1;
            if (TradeTaxRate <= EconomyConfig.TradeTaxRateHeavy) return 2;
            return 3;
        }
        set => TradeTaxRate = value switch
        {
            0 => EconomyConfig.TradeTaxRateMin,
            1 => EconomyConfig.TradeTaxRateDefault,
            2 => EconomyConfig.TradeTaxRateHeavy,
            _ => EconomyConfig.TradeTaxRateMax,
        };
    }

    public static readonly string[] LevelNames = { "免征/极低", "轻税", "中税", "重税" };

    /// <summary>旧档兼容：老版税种档位字典（v≤55），新档不再写入。
    /// 反序列化时如存在则尝试迁移到新字段（见 SaveService 迁移逻辑）。</summary>
    public Dictionary<string, int> Levels = new();
}
