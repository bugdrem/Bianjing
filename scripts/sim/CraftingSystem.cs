using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 加工系统（每日结算）：工坊/商铺把库中原料加工成专营成品。
/// 一座建筑当前只加工一种成品（Specialty 指向可加工成品），后期支持多成品时可按 Recipes 扩展多条并行。
/// 产量 = 在岗工人数 × 每人日产量，受「原料齐备」与「成品库容」双重限制；
/// 消耗每种配方原料各一份产出一份成品。只动库存，不涉及雇佣/买卖/搬运（那是 JobSystem/表现层的职责）。
/// </summary>
public class CraftingSystem
{
    /// <summary>每名在岗工人每日加工产量（份）。</summary>
    private const double CraftPerWorkerDay = 0.8;

    public void TickDay(GameState gs)
    {
        var workersOf = CountWorkers(gs);

        foreach (var b in gs.Buildings.Values)
        {
            // 只有专营「可加工成品」且有岗位的 grown 建筑（工坊/商铺）才加工
            if (b.Def.Category != "grown" || b.Def.JobSlots <= 0 || !Goods.IsCraftable(b.Specialty))
                continue;

            int workers = workersOf.TryGetValue(b.Id, out var n) ? n : 0;
            if (workers <= 0)
                continue;

            var inputs = Goods.InputsOf(b.Specialty);
            if (inputs.Length == 0)
                continue;

            // 本次最多可产：受工人产能（含工艺科技加成）与最紧缺原料存量限制；
            // 不再受成品库容卡产——消耗原料份数≥产出份数，加工不会增加仓储占用，
            // 且超限存入机制下原料可能堆超上限，若按剩余库容限产会永久停工
            double byWorkers = workers * CraftPerWorkerDay * gs.TechFactor("craft");
            double byInputs = double.MaxValue;
            foreach (var raw in inputs)
                byInputs = Math.Min(byInputs, b.Inv.AmountOf(raw)); // 每份成品耗每种原料各一份

            double make = Math.Min(byWorkers, byInputs);
            if (make <= 0.0001)
                continue;

            // 扣原料、入成品（超限入库：加工前后总占用只减不增）
            foreach (var raw in inputs)
                b.TakeGoods(raw, make);
            b.StoreGoodsForce(b.Specialty, make);
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
