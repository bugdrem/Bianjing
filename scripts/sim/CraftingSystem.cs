using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 加工系统（每日结算）：只有工坊把库中原料加工成专营成品（批次六十七：商铺不再加工，只从工坊采购售卖）。
/// 一座工坊加工专营品 + 升级增补的副营品（ExtraGoods）；配方按等级多对多耗料、耗燃料、带副产品（见 RecipeDef）。
/// 产量 = 在岗工人数 × 每人日产量 × 工坊效率，受「原料齐备」「燃料」「成品库容」多重限制；
/// 消耗每级配方指定份量的原料产出一份成品。只动库存，不涉及雇佣/买卖/搬运（那是 JobSystem/表现层的职责）。
/// </summary>
public class CraftingSystem
{
    /// <summary>每名在岗工人每日加工产量（份）：转发自 EconomyConfig。</summary>
    private static double CraftPerWorkerDay => EconomyConfig.CraftPerWorkerDay;

    public void TickDay(GameState gs)
    {
        var workersOf = CountWorkers(gs);

        foreach (var b in gs.Buildings.Values)
        {
            // 只有工坊加工：商铺只购销不加工，资源升级全部由工坊实现
            if (b.Def.Id != "workshop" || b.Def.JobSlotsAt(b.Level) <= 0)
                continue;

            int workers = workersOf.TryGetValue(b.Id, out var n) ? n : 0;
            if (workers <= 0)
                continue;

            // 在产货品：专营品 + 升级增补副营品（ExtraGoods，见 ZoneGrowthSystem.ExtendSpecialties）
            var goods = new List<string> { b.Specialty };
            foreach (var g in b.ExtraGoods)
                if (!goods.Contains(g))
                    goods.Add(g);

            foreach (var spec in goods)
            {
                var inputs = Goods.InputsAt(spec, b.Level);
                if (inputs.Count == 0)
                    continue;
                int fuelAt = Goods.FuelAt(spec, b.Level); // 每份耗柴薪量（0 = 不耗）

                // 本次最多可产：受工人产能（含工艺科技加成与工坊效率）与最紧缺原料存量限制，
                // 燃料同样限产；不再受成品库容卡产——消耗原料份数≥产出份数，加工不会增加仓储占用
                double byWorkers = workers * CraftPerWorkerDay * gs.TechFactor("craft")
                    * b.Def.EfficiencyAt(b.Level);
                double byInputs = double.MaxValue;
                foreach (var kv in inputs)
                    byInputs = Math.Min(byInputs, b.Inv.AmountOf(kv.Key) / kv.Value); // 每份耗 kv.Value 份原料
                if (fuelAt > 0)
                    byInputs = Math.Min(byInputs, b.Inv.AmountOf(Goods.Wood) / fuelAt);

                double make = Math.Min(byWorkers, byInputs);
                if (make <= 0.0001)
                    continue;

                // 扣原料、扣燃料、入成品，副产品（废料）随产出按等级比率入坊（超限入库：总占用只减不增）
                foreach (var kv in inputs)
                    b.TakeGoods(kv.Key, make * kv.Value);
                if (fuelAt > 0)
                    b.TakeGoods(Goods.Wood, make * fuelAt);
                b.StoreGoodsForce(spec, make);
                double byp = Goods.ByproductAt(spec, b.Level);
                if (byp > 0)
                    b.StoreGoodsForce(Goods.Scrap, make * byp);
            }
        }
    }

    /// <summary>建筑 Id → 在岗雇工人数。</summary>
    private static Dictionary<int, int> CountWorkers(GameState gs)
    {
        var map = new Dictionary<int, int>();
        foreach (var c in gs.Citizens.Values)
            if (c.JobKind == JobKind.Employed && c.WorkplaceId >= 0)
                map[c.WorkplaceId] = map.GetValueOrDefault(c.WorkplaceId) + 1;
        return map;
    }
}
