using System;

namespace Bianjing;

/// <summary>经济系统：每月扣建筑维护费；粮田产粮、人口耗粮。税收由 TaxSystem 按政策结算。</summary>
public class EconomySystem
{
    private const double FoodPerCapita = 0.2;

    public void Tick(GameState gs)
    {
        double upkeep = 0;
        double foodNet = -gs.Population * FoodPerCapita;

        foreach (var b in gs.Buildings.Values)
        {
            upkeep += b.Def.Upkeep;
            foodNet += b.Def.FoodOutput;
        }

        gs.Money -= upkeep;
        gs.Food = Math.Max(0, gs.Food + foodNet);

        EventBus.RaiseStatsChanged();
    }
}
