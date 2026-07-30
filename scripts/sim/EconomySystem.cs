using System;

namespace Bianjing;

/// <summary>经济系统（每日结算）：按月值的 1/30 扣建筑维护费并记账；铸币局在岗工匠逐日铸钱入官库；
/// 粮田产粮、人口耗粮。税收由 TaxSystem 按政策结算。</summary>
public class EconomySystem
{
    /// <summary>人均日耗官粮：转发自 EconomyConfig。</summary>
    private static double FoodPerCapita => EconomyConfig.OfficialFoodPerCapita;

    public void TickDay(GameState gs)
    {
        double upkeep = 0;
        double foodNet = -gs.Population * FoodPerCapita;

        foreach (var b in gs.Buildings.Values)
        {
            upkeep += b.Def.Upkeep;
            foodNet += b.Def.FoodOutput;
        }

        upkeep /= GameClock.DaysPerMonth;
        gs.Money -= upkeep;
        gs.Ledger.Add("建筑维护", -upkeep);
        gs.Food = Math.Max(0, gs.Food + foodNet / GameClock.DaysPerMonth);

        MintCoins(gs);

        EventBus.RaiseStatsChanged();
    }

    /// <summary>铸币：铸币局每名在岗工匠每日铸钱入官库（数据驱动自 buildings.json，冶铸科技加成）。</summary>
    private static void MintCoins(GameState gs)
    {
        double minted = 0;
        foreach (var c in gs.Citizens.Values)
            if (c.JobKind == JobKind.Employed && gs.Buildings.TryGetValue(c.WorkplaceId, out var wp))
                minted += wp.Def.MintPerWorkerDay;
        minted *= gs.TechFactor("mint");
        if (minted <= 0)
            return;
        gs.Money += minted;
        gs.Ledger.Add("铸币收入", minted);
    }
}
