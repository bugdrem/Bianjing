using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 税收系统（批次五十六重写：三税种模型）。
/// 土地税：按建筑类型与等级的固定税额，每月逐日 1/DaysPerMonth 收缴入国库。
/// 商税：交易发生时由买方按成交额另付税入官库（批次七十五落地：GoodsSystem.BuyGoods 自动购粮/购柴、
/// CitizenAgent.Shopping 带单采买两处买点收税；税率见 TaxPolicy.TradeTaxRate）。
/// 人口税：可选开启，从雇工每日薪资中扣除 20%，每月降幸福。
/// </summary>
public class TaxSystem
{
    /// <summary>每栋建筑的土地税月计税额（文，对应 3% 默认税率下的实收额；需求 §4.3）。</summary>
    public static long BuildingTaxBase(BuildingDef def, int level)
    {
        return def.Id switch
        {
            "house"    => level switch { 1 => 10, 2 => 25, 3 => 50, _ => 10 },
            "mansion"  => level switch { 1 => 80, 2 => 150, 3 => 300, _ => 80 },
            "workshop" => level switch { 1 => 40, 2 => 80, 3 => 150, _ => 40 },
            "shop"     => level switch { 1 => 50, 2 => 100, 3 => 200, _ => 50 },
            _ => 0,
        };
    }

    /// <summary>每日征税：土地税逐栋向住户/店主家庭实扣 + 人口税在薪资发放时扣（见 CitizenAgent），此处仅处理土地税。</summary>
    public void TickDay(GameState gs)
    {
        double rateFactor = gs.Taxes.LandTaxRate / EconomyConfig.LandTaxRateDefault;
        int days = GameClock.DaysPerMonth;

        foreach (var b in gs.Buildings.Values)
        {
            long baseAmount = BuildingTaxBase(b.Def, b.Level);
            if (baseAmount <= 0)
                continue;
            long daily = Math.Max(1, (long)(baseAmount * rateFactor / days));
            // 批次七十二：税款从住户/店主家庭公产实扣入官库（旧版凭空造钱，家庭财富不回官库）
            if (gs.TakeLandTax(b, daily))
                gs.Ledger.Add("土地税", daily);
        }
    }

    /// <summary>每月结算：重税民怨 + 人口税幸福度影响。</summary>
    public void TickMonth(GameState gs)
    {
        // 土地税重税（高于 6% 视为重税）
        if (gs.Taxes.LandTaxRate > 0.06)
            ApplyMoralePenalty(gs, "重敛伤民");

        // 商税重税（高于 10% 视为重税）
        if (gs.Taxes.TradeTaxRate > 0.10)
            ApplyMoralePenalty(gs, "关市苛征");

        // 人口税
        if (gs.Taxes.PollTaxEnabled)
        {
            foreach (var c in gs.Citizens.Values)
                if (!c.IsChild)
                    c.Fun = Math.Max(0f, c.Fun - EconomyConfig.PollTaxMoraleDrop);
        }
        else
        {
            // 关闭后缓慢恢复
            foreach (var c in gs.Citizens.Values)
                if (!c.IsChild && c.Fun < 50f)
                    c.Fun = Math.Min(50f, c.Fun + EconomyConfig.PollTaxMoraleRecover);
        }
    }

    private static void ApplyMoralePenalty(GameState gs, string reason)
    {
        foreach (var c in gs.Citizens.Values)
            if (!c.IsChild)
                c.Fun = Math.Max(0f, c.Fun - EconomyConfig.HeavyTaxFunPenalty);
    }

    /// <summary>土地税月入预估（政策面板展示用，文）。</summary>
    public static long EstimateLandTax(GameState gs)
    {
        long total = 0;
        double rateFactor = gs.Taxes.LandTaxRate / EconomyConfig.LandTaxRateDefault;
        foreach (var b in gs.Buildings.Values)
            total += (long)(BuildingTaxBase(b.Def, b.Level) * rateFactor);
        return total;
    }

    /// <summary>商税月入预估（粗略，文）。</summary>
    public static long EstimateTradeTax(GameState gs)
    {
        long total = 0;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Id is "shop" or "workshop" or "saltworks" or "mine")
                total += (long)(b.Def.Salary * b.Def.JobSlotsAt(b.Level) * 12 * gs.Taxes.TradeTaxRate);
        }
        return total;
    }
}
