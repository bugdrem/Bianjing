using System;

namespace Bianjing;

/// <summary>经济系统（批次五十六：单位切文、增加月俸与朝廷采购）。
/// 每日结算：建筑维护费、官粮消耗、铸币；月俸由 Main.OnMonthPassed 触发；朝廷采购待后续对接。</summary>
public class EconomySystem
{
    /// <summary>人均日耗官粮：转发自 EconomyConfig。</summary>
    private static double FoodPerCapita => EconomyConfig.OfficialFoodPerCapita;

    public void TickDay(GameState gs)
    {
        // 建筑维护费（文/月 → 文/日）
        int totalUpkeep = 0;
        double foodNet = -gs.Population * FoodPerCapita;

        foreach (var b in gs.Buildings.Values)
        {
            totalUpkeep += b.Def.Upkeep;
            foodNet += b.Def.FoodOutput;
        }

        long dailyUpkeep = Math.Max(1, (long)totalUpkeep / GameClock.DaysPerMonth);
        gs.Money -= dailyUpkeep;
        gs.Ledger.Add("建筑维护", -dailyUpkeep);
        gs.Food = Math.Max(0, gs.Food + foodNet / GameClock.DaysPerMonth);

        MintCoins(gs);

        EventBus.RaiseStatsChanged();
    }

    /// <summary>每月结算：王爷月俸入国库（前期核心现金流）。由 Main.OnMonthPassed 调用。</summary>
    public void PayMonthlySalary(GameState gs)
    {
        long salary = EconomyConfig.PrinceMonthlySalary;
        gs.Money += salary;
        gs.Ledger.Add("王爷月俸", salary);
    }

    /// <summary>铸币：铸币局每名在岗工匠每日铸钱入官库（文，数据驱动自 buildings.json，冶铸科技加成）。</summary>
    private static void MintCoins(GameState gs)
    {
        double minted = 0;
        foreach (var c in gs.Citizens.Values)
            if (c.JobKind == JobKind.Employed && gs.Buildings.TryGetValue(c.WorkplaceId, out var wp))
                minted += wp.Def.MintPerWorkerDay;
        minted *= gs.TechFactor("mint");
        if (minted <= 0)
            return;
        long amount = (long)minted;
        gs.Money += amount;
        gs.Ledger.Add("铸币收入", amount);
    }
}
