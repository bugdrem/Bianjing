using System;

namespace Bianjing;

/// <summary>
/// 税收系统：按政策档位对各税种计征入国库（每日按月基数的 1/30 结算并记账）；
/// 重税引发民怨（每月结算，成人兴趣值下降），为后续满意度/民变系统留接口。
/// </summary>
public class TaxSystem
{
    /// <summary>每项重税每月造成的民怨（成人兴趣值扣减）。</summary>
    private const float HeavyTaxFunPenalty = 2f;

    /// <summary>每日征税：月基数 ÷ 30，逐税种记入账本；税所在岗吏员与钱法科技提供全局加成。</summary>
    public void TickDay(GameState gs)
    {
        double boost = TaxBoost(gs) * gs.TechFactor("tax");
        foreach (var def in TaxDefs.All)
        {
            int level = gs.Taxes.LevelOf(def.Id);
            double amount = def.MonthlyBase(gs) * TaxPolicy.RateOf(level) * boost / GameClock.DaysPerMonth;
            if (amount <= 0)
                continue;
            gs.Money += amount;
            gs.Ledger.Add(def.Name, amount);
        }
    }

    /// <summary>税收加成倍率 = 1 + Σ(税所类建筑在岗吏员 × 每人加成)，数据驱动自 buildings.json。</summary>
    public static double TaxBoost(GameState gs)
    {
        double boost = 1.0;
        foreach (var c in gs.Citizens.Values)
            if (c.JobKind == JobKind.Employed && gs.Buildings.TryGetValue(c.WorkplaceId, out var wp))
                boost += wp.Def.TaxBoostPerWorker;
        return boost;
    }

    /// <summary>每月结算：重税民怨。</summary>
    public void TickMonth(GameState gs)
    {
        int heavyCount = 0;
        foreach (var def in TaxDefs.All)
            if (gs.Taxes.LevelOf(def.Id) >= TaxPolicy.MaxLevel)
                heavyCount++;

        if (heavyCount == 0)
            return;

        foreach (var c in gs.Citizens.Values)
            if (!c.IsChild)
                c.Fun = Math.Max(0f, c.Fun - HeavyTaxFunPenalty * heavyCount);
    }

    /// <summary>当前档位下某税种的预估月入（政策面板展示用）。</summary>
    public static double Estimate(GameState gs, TaxDef def) =>
        def.MonthlyBase(gs) * TaxPolicy.RateOf(gs.Taxes.LevelOf(def.Id));
}
