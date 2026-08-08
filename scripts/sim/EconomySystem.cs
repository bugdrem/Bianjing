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
            if (b.Def.Category == "court")
                continue; // 朝廷机构朝廷自理维护（批次七十七），不占官库
            totalUpkeep += b.Def.Upkeep;
            foodNet += b.Def.FoodOutput;
        }

        long dailyUpkeep = Math.Max(1, (long)totalUpkeep / GameClock.DaysPerMonth);
        // 批次七十九：维护费发当日无业者（官府营造杂役用工，按实扣款）——
        // 旧版维护费只扣不付，是官库每月凭空流失的黑洞；发工钱后钱在玩家↔村民循环内
        long upkeepPaid = gs.PayBuildWages(dailyUpkeep);
        gs.Money -= upkeepPaid;
        gs.Ledger.Add("建筑维护", -upkeepPaid);
        gs.Food = Math.Max(0, gs.Food + foodNet / GameClock.DaysPerMonth);

        MintCoins(gs);

        EventBus.RaiseStatsChanged();
    }

    /// <summary>每月结算：王爷月俸入国库（前期核心现金流）+ 朝廷粮饷按人口拨官仓。由 Main.OnMonthPassed 调用。</summary>
    public void PayMonthlySalary(GameState gs)
    {
        long salary = EconomyConfig.PrinceMonthlySalary;
        gs.Money += salary;
        gs.Ledger.Add("王爷月俸", salary);

        // 朝廷粮饷（批次七十八）：朝廷按人口每月拨粮入官仓（赈济储备，凭空生成）——
        // 旧版官粮只靠开局存量 + 农田 20% 田赋，而消耗 0.2 份/人/日 远超补给，耗尽即永久饥荒
        gs.Food += (long)(gs.Population * EconomyConfig.CourtFoodAmmoPerCapitaMonth);
    }

    /// <summary>月结工钱（批次七十四）：雇工下工只记账（Citizen.WagesOwed），月底统一发放——
    /// 官库一次性出账总额并记一条流水，逐人入家庭公产；亡故/迁出者未领部分自然作废。
    /// 批次七十八：朝廷衙门（court）员工俸禄由朝廷拨款（凭空生成），不占官库——
    /// 与朝廷机构营造/维护豁免同口径，此前衙门员工工资从官库扣是官库失血点。</summary>
    public void PayWages(GameState gs)
    {
        long total = 0;
        foreach (var c in gs.Citizens.Values)
            if (c.WagesOwed > 0)
            {
                if (c.WorkplaceId >= 0 && gs.Buildings.TryGetValue(c.WorkplaceId, out var wp)
                    && wp.Def.Category == "court")
                    continue; // 朝廷衙门员工：俸禄由朝廷凭空生成，不走官库
                total += c.WagesOwed;
            }
        if (total > 0)
        {
            gs.Money -= total;
            gs.Ledger.Add("雇工俸禄", -total);
        }
        foreach (var c in gs.Citizens.Values)
            if (c.WagesOwed > 0)
            {
                gs.PayToFamily(c, c.WagesOwed);
                c.WagesOwed = 0;
            }
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
