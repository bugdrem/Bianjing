using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 时效衰减器（日结）：把两根时间轴推进一天。
///
/// 处理范围（4 类 <see cref="Inventory"/> 持有者中的 3 类主循环持有者）：
/// 建筑仓房、村民背包、地面物资堆；访客背包由 VisitorSystem 调 <see cref="AdvanceInventory"/> 自行推进。
///
/// 与既有系统的分工：
///   · 本系统只管货品时效（软轴打折 + 硬轴消亡），不动价格、不动库存容量；
///   · 房屋老化仍由 <see cref="MaintenanceSystem"/> 驱动（其 Condition 即房屋的软轴，批次②统一）；
///   · 消耗端按鲜度折算有效量，见 <see cref="GoodsSystem"/> 的消耗路径。
///
/// 时间口径：每个 TickDay 推进 1 游戏日。
/// </summary>
public class TimelinessSystem
{
    /// <summary>日结：推进全部持有者的时效。</summary>
    public void TickDay(GameState gs)
    {
        if (!TimelinessConfig.Enabled)
            return;

        int month = gs.CurMonth;

        // 建筑仓房：有顶为室内、NoRoof 为露棚
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Inv.IsEmpty)
                continue;
            var place = b.Def.NoRoof ? TimedPlace.OpenShed : TimedPlace.Sheltered;
            int cell = b.X + b.Y * MapGrid.Size;
            var ctx = TimedContext.ForGoods("", place, cell, month, IsNearWater(gs, b.X, b.Y));
            AdvanceInventory(b.Inv, ctx, gs, b.X, b.Y);
        }

        // 村民背包：随身携带
        foreach (var c in gs.Citizens.Values)
        {
            if (c.Pack.IsEmpty)
                continue;
            var ctx = TimedContext.ForGoods("", TimedPlace.Carried, -1, month, false);
            AdvanceInventory(c.Pack, ctx, gs, -1, -1);
        }

        // 地面物资堆：露天（含是否临水，临水的堆坏得更快）
        foreach (var p in gs.Piles.Values)
        {
            if (p.Inv.IsEmpty)
                continue;
            var ctx = TimedContext.ForGoods("", TimedPlace.Ground, p.X + p.Y * MapGrid.Size,
                month, IsNearWater(gs, p.X, p.Y));
            AdvanceInventory(p.Inv, ctx, gs, p.X, p.Y);
        }
    }

    /// <summary>
    /// 推进单个库存的一天：逐堆计龄、再按环境推进两根轴，硬轴耗尽则降级为废料或移除。
    /// 供访客背包等外部持有者复用（<paramref name="ctx"/> 由调用方按实际位置构造）。
    /// </summary>
    public static void AdvanceInventory(Inventory inv, in TimedContext ctx, GameState gs, int x, int y)
    {
        inv.AgeOneDay();

        // 倒序遍历：消失/转化的堆可安全移除，往末尾追加的新堆也不会被重复访问
        for (int i = inv.Stacks.Count - 1; i >= 0; i--)
        {
            var s = inv.Stacks[i];
            if (!TimedRules.Applies(s.GoodsId))
                continue;

            // 每堆单独构造上下文：内置修正源不看货品，但货品专属修正源（如怕潮的盐）需要它
            var gctx = new TimedContext(TimedEntityKind.Goods, s.GoodsId, ctx.Place, ctx.CellIndex,
                ctx.Month, ctx.NearWater);
            var rates = TimedRegistry.Compute(gctx);
            var st = new TimedState { Fresh = s.Fresh, UsedLifespan = s.UsedLifespan, RenewCount = s.RenewCount };
            var r = TimedRules.Resolve(st, rates, s.GoodsId);

            // 软轴：新鲜度按当前速率滑落
            if (r.FreshRate > 0f)
                st.Fresh = MathF.Max(0f, st.Fresh - r.FreshRate);

            // 硬轴：有硬轴才累积（无硬轴的货品永不消亡，如柴薪、木炭）
            if (!r.SoftOnly)
                st.UsedLifespan += 1f;

            // 硬轴耗尽：降级为废料（可当柴烧）或直接消失
            if (!r.SoftOnly && st.UsedLifespan >= r.Span)
            {
                ConvertToScrap(inv, i, s.Amount);
                continue;
            }

            s.Fresh = st.Fresh;
            s.UsedLifespan = st.UsedLifespan;
        }
    }

    /// <summary>
    /// 硬轴耗尽处理：把该堆按比例降级为废料（<see cref="Goods.Scrap"/>，已是燃料类货品，天然可作他用），
    /// 或直接移除。转化优先并入已有废料堆，避免同一仓内出现两堆废料。
    /// </summary>
    private static void ConvertToScrap(Inventory inv, int index, double amount)
    {
        inv.Stacks.RemoveAt(index);
        if (!TimelinessConfig.ExpiryToScrap)
            return;

        double keep = amount * TimelinessConfig.ExpiryScrapRatio;
        if (keep <= 0.0001)
            return;

        foreach (var t in inv.Stacks)
        {
            if (t.GoodsId == Goods.Scrap)
            {
                t.Amount += keep;
                return;
            }
        }
        inv.Stacks.Add(new GoodsStack { GoodsId = Goods.Scrap, Amount = keep, Fresh = 100f });
    }

    /// <summary>四向邻格是否有水面（潮气修正的判定）。公开供面板等外部处复用同一口径。</summary>
    public static bool IsNearWater(GameState gs, int x, int y)
    {
        if (x < 0 || y < 0)
            return false;
        return IsWaterAt(gs, x + 1, y) || IsWaterAt(gs, x - 1, y)
            || IsWaterAt(gs, x, y + 1) || IsWaterAt(gs, x, y - 1);
    }

    private static bool IsWaterAt(GameState gs, int x, int y)
    {
        if (x < 0 || y < 0 || x >= MapGrid.Size || y >= MapGrid.Size)
            return false;
        return gs.Map.CellAt(x, y).HasWater;
    }
}
