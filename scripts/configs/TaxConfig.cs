namespace Bianjing;

/// <summary>
/// 税制配置：税率步长与各税基公式系数（业务归属：TaxPolicy.RateOf、TaxDefs 税基回调、TaxSystem 民怨）。
/// 税种注册表本体在 TaxDefs（数据驱动，mod 可追加），此处只放数值系数。
/// </summary>
public static class TaxConfig
{
    /// <summary>档位税率步长：税率 = 档位 × 此值（免征0 / 轻0.5 / 中1.0 / 重1.5）。</summary>
    public const double RatePerLevel = 0.5;

    /// <summary>田赋税基：每块粮田 / 每户在籍的月计税额（贯）。</summary>
    public const double FarmBasePerFarm = 4.0;
    public const double FarmBasePerFamily = 0.5;

    /// <summary>市舶税基：每座港口的月计税额（贯，另加建筑 TaxBonus）。</summary>
    public const double PortBasePerPort = 8.0;

    /// <summary>每项重税每月造成的民怨（成人兴趣值扣减）。</summary>
    public const float HeavyTaxFunPenalty = 2f;
}
