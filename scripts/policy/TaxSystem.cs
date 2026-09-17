using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 税收系统（批次五十六重写：三税种模型）。
/// 土地税：按建筑类型与等级的固定税额，每月逐旬 1/DaysPerMonth 收缴入国库。
/// 商税：交易发生时由买方按成交额另付税入官库（批次七十五落地：GoodsSystem.BuyGoods 自动购粮/购柴、
/// CitizenAgent.Shopping 带单采买两处买点收税；税率见 TaxPolicy.TradeTaxRate）。
/// 人口税：可选开启，从雇工每旬薪资中扣除 20%，每月降幸福。
/// </summary>
public class TaxSystem
{
    /// <summary>
    /// 每栋建筑的土地税月计税额（文，对应默认税率 3% 下的实收额）。
    ///
    /// <b>批次九十五整体放大 6~10 倍</b>（旧税基与物价完全脱节：一栋商铺月税 50 文，
    /// 而它雇一个店员就要 1500 文/月）；<b>批次九十八再随"1 游戏旬 = 10 真实日"的新尺度 ×10</b>。
    ///
    /// 现行基准：民居 L1 月税 600 文 ≈ 一月口粮（300 文）的两倍；
    /// 一栋住三口的民居连同家计合计约 1500 文/月，占双职工家庭收入（≈7000 文）的 21%。
    /// </summary>
    public static long BuildingTaxBase(BuildingDef def, int level)
    {
        return def.Id switch
        {
            "house"    => level switch { 1 => 600, 2 => 1500, 3 => 3000, _ => 600 },
            "mansion"  => level switch { 1 => 4000, 2 => 8000, 3 => 16000, _ => 4000 },
            "workshop" => level switch { 1 => 2000, 2 => 4000, 3 => 8000, _ => 2000 },
            "shop"     => level switch { 1 => 3000, 2 => 6000, 3 => 12000, _ => 3000 },
            _ => 0,
        };
    }

    /// <summary>每旬征税：土地税逐栋向住户/店主家庭实扣 + 人口税在薪资发放时扣（见 CitizenAgent），此处仅处理土地税。</summary>
    public void TickDay(GameState gs)
    {
        double rateFactor = gs.Taxes.LandTaxRate / EconomyConfig.LandTaxRateDefault;
        int days = GameClock.DaysPerMonth;

        foreach (var b in gs.Buildings.Values)
        {
            long baseAmount = BuildingTaxBase(b.Def, b.Level);
            if (baseAmount <= 0)
                continue;
            // 批次八十七：四舍五入取旬额（旧版 Math.Max(1) 截断——低税率档每旬 0.14 文被放大成 1 文，
            // 免税档反而多收）；记账按实收额（旧版记全额，公产不足时账实不符）
            long daily = Math.Max(0, (long)Math.Round(baseAmount * rateFactor / days, MidpointRounding.AwayFromZero));
            // 批次七十二：税款从住户/店主家庭公产实扣入官库（旧版凭空造钱，家庭财富不回官库）
            long paid = gs.TakeLandTax(b, daily);
            if (paid > 0)
                gs.Ledger.Add("土地税", paid);
        }
    }

    /// <summary>每月结算：重税民怨 + 人口税幸福度影响。</summary>
    public void TickMonth(GameState gs)
    {
        // 土地税重税（高于重税线视为重税，见 EconomyConfig.LandTaxRateHeavy）
        if (gs.Taxes.LandTaxRate > EconomyConfig.LandTaxRateHeavy)
            ApplyMoralePenalty(gs, "重敛伤民");

        // 商税重税（高于重税线视为重税，见 EconomyConfig.TradeTaxRateHeavy）
        if (gs.Taxes.TradeTaxRate > EconomyConfig.TradeTaxRateHeavy)
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
