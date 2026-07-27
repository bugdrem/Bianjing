using System;

namespace Bianjing;

/// <summary>
/// 税收系统（每月结算）：按政策档位对各税种计征入国库；
/// 重税引发民怨（成人兴趣值下降），为后续满意度/民变系统留接口。
/// </summary>
public class TaxSystem
{
    /// <summary>每项重税每月造成的民怨（成人兴趣值扣减）。</summary>
    private const float HeavyTaxFunPenalty = 2f;

    public void Tick(GameState gs)
    {
        double total = 0;
        int heavyCount = 0;

        foreach (var def in TaxDefs.All)
        {
            int level = gs.Taxes.LevelOf(def.Id);
            total += def.MonthlyBase(gs) * TaxPolicy.RateOf(level);
            if (level >= TaxPolicy.MaxLevel)
                heavyCount++;
        }

        gs.Money += total;

        if (heavyCount > 0)
        {
            foreach (var c in gs.Citizens.Values)
                if (!c.IsChild)
                    c.Fun = Math.Max(0f, c.Fun - HeavyTaxFunPenalty * heavyCount);
        }
    }

    /// <summary>当前档位下某税种的预估月入（政策面板展示用）。</summary>
    public static double Estimate(GameState gs, TaxDef def) =>
        def.MonthlyBase(gs) * TaxPolicy.RateOf(gs.Taxes.LevelOf(def.Id));
}
