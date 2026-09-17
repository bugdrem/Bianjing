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

        TickBuildings(gs);
        TickRoads(gs, month);
        TickPlants(gs);
        TickPeople(gs);
        AutoRenew(gs);

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
    /// 房屋硬轴推进（结构寿限，游戏日累积）。
    ///
    /// 房屋的两根轴分属不同系统但<b>共用同一套规则</b>：软轴（<see cref="BuildingInstance.Condition"/>）
    /// 仍由 <see cref="MaintenanceSystem"/> 驱动（它有修缮匠与住户集资两条既有的回滚通路），
    /// 本方法只推进硬轴。归零也<b>不在此处拆房</b>——统一由 MaintenanceSystem.Collapse 判定，
    /// 免得住户失所、业权清理等收尾逻辑散成两处。
    /// </summary>
    private static void TickBuildings(GameState gs)
    {
        foreach (var b in gs.Buildings.Values)
        {
            // 与既有"不老化"口径一致：天然建筑、朝廷机构（自理）、王爷府（开局地标）豁免
            if (b.Def.Natural || b.Def.Category == "court" || b.Def.Id == PrinceMansionConfig.DefId)
                continue;
            if (TimelinessConfig.BuildingSpanDays(b.Def.Category) <= 0f)
                continue;
            b.UsedLifespan += 1f;
        }
    }

    /// <summary>
    /// 道路双轴推进：软轴（路况，影响移速）与硬轴（寿限，耗尽降级）。
    /// 状态存于 <see cref="GameState.RoadStates"/>（稀疏字典，仅几千格），
    /// 环境按"露天 + 是否临水 + 季节"计——临水路段更快被泡坏。
    /// </summary>
    private static void TickRoads(GameState gs, int month)
    {
        var roads = gs.RoadCells;
        // 倒序遍历：降级会写 RoadStates 但不增删 RoadCells，倒序仍安全
        for (int i = roads.Count - 1; i >= 0; i--)
        {
            var c = roads[i];
            var cell = gs.Map.CellAt(c);
            if (!cell.HasRoad)
                continue;

            var kind = cell.RoadKind;
            var st = gs.RoadStateOf(c);
            var ctx = TimedContext.ForEntity(TimedEntityKind.Road, TimedPlace.Ground,
                GameState.CellIndex(c), month, IsNearWater(gs, c.X, c.Y));
            var rates = TimedRegistry.Compute(ctx);

            // 软轴：路况按当前路种的基线速率衰减（土路小道衰减最快）
            float rate = 100f / TimelinessConfig.RoadSoftDays(kind) * rates.FreshRate;
            float before = st.Fresh;
            st.Fresh = MathF.Max(0f, st.Fresh - rate);
            // 跨过阶段门槛时才广播重建：路面颜色按路况插值渲染，但逐日重建全图代价太高
            // （路格数千，每天全标脏就等于每天重建整张地图）。退化为"阶段式视觉变化"——
            // 恰好与三阶段语义一致，也把重建压到低频（每格一生只跨几次门槛）。
            if (TimedRules.StageOf(before) != TimedRules.StageOf(st.Fresh))
                EventBus.RaiseCellChanged(c);

            // 硬轴：寿限累积；耗尽则降一级（降级内部已把路况重置为全新）
            float span = TimelinessConfig.RoadSpanDays(kind) * rates.LifespanSpan;
            st.UsedLifespan += 1f;
            if (st.UsedLifespan >= span)
            {
                gs.DegradeRoad(c);
                continue;
            }
            gs.RoadStates[GameState.CellIndex(c)] = st;
        }
    }

    /// <summary>
    /// 植物（树木）硬轴推进：到寿枯死倒伏，掉落部分木材（死木质次，出材减半）。
    ///
    /// 软轴（生机）仍是既有的"砍伐扣血 + 闲置到点后逐日自愈"（<see cref="PlantGrowthSystem"/>），
    /// 它本就天然是"延迟后回滚"的形态，无需改写；改造点在别处——
    /// 归一化生机（<see cref="PlantObj.VigorPercent"/>）让树木接入统一的阶段判定与效果折算，
    /// 产果与出材改按 <see cref="PlantObj.VigorFactor"/> 缩放：虚弱的树产得少。
    /// </summary>
    private static void TickPlants(GameState gs)
    {
        if (gs.Plants.Count == 0)
            return;

        List<Godot.Vector2I> dead = null;
        foreach (var p in gs.Plants.Values)
        {
            p.UsedLifespan += 1f;
            if (p.UsedLifespan >= TimelinessConfig.PlantLifespanDays)
                (dead ??= new List<Godot.Vector2I>()).Add(new Godot.Vector2I(p.X, p.Y));
        }
        if (dead == null)
            return;

        foreach (var c in dead)
        {
            if (!gs.Plants.TryGetValue(GameState.CellIndex(c), out var p))
                continue;
            double wood = p.MaxHp * VillagerConfig.WoodPerHp * TimelinessConfig.PlantDeathWoodRatio;
            gs.ChopTree(c); // 倒伏（内部广播分块重建）
            if (wood > 0)
                gs.DropOnGround(c, Goods.Wood, wood); // 枯木化为柴薪，谁都能拾
        }
    }

    /// <summary>
    /// 人（村民）软轴推进：健康值。
    ///
    /// 此前 <c>Citizen.Health</c> 恒为 100——<see cref="LifeConfig"/> 的注释自陈"健康系统接入后自动生效"，
    /// 但全项目没有任何地方降低过它，于是死亡率放大系数永远是 1.0、劳动效率折算也吃不到。
    /// 本方法让它真正随生计波动：<b>断炊与受冻逐日损耗，温饱时缓慢恢复</b>。
    ///
    /// 硬轴仍是既有的年龄（Gompertz 死亡率曲线 + 达最大寿数必亡），无需新建。
    /// 注意此处读的是<b>昨日</b>记录的断炊/受冻天数（GoodsSystem 在时效之后结算），差一天不影响体感。
    /// </summary>
    private static void TickPeople(GameState gs)
    {
        foreach (var c in gs.Citizens.Values)
        {
            float loss = 0f;
            if (c.FoodShortDays > 0)
                loss += TimelinessConfig.PersonHealthHungerLoss;
            if (c.FuelShortDays > 0)
                loss += TimelinessConfig.PersonHealthColdLoss;
            // 有损耗就只扣不回（饥寒交迫时不该还慢慢变好）；否则缓慢回血
            float delta = loss > 0f ? -loss : TimelinessConfig.PersonHealthRegen;
            // MathF 没有 Clamp 重载，此处用 Math.Clamp（与 Inventory 等处的写法一致）
            c.Health = Math.Clamp(c.Health + delta, 0f, 100f);
        }
    }

    /// <summary>
    /// 居民自动打理家务（NPC 侧的自救）：每日检查各住所库存中"已坏到值得动手"的货品，
    /// 有恢复手段、辅料够、翻新次数未用尽就出手——把要坏的饭回锅、把受潮的柴烘干。
    ///
    /// <b>只对有人住的民居生效</b>（<c>HousingCapacity &gt; 0</c>）：商铺货架与官仓廪留给店主与玩家操心，
    /// 全都自动打理就没玩家的事了。这也是"玩家与 NPC 都要能做恢复动作"里的 NPC 那一半。
    /// </summary>
    private static void AutoRenew(GameState gs)
    {
        foreach (var b in gs.Buildings.Values)
        {
            if (b.HousingCapacity <= 0 || b.Inv.IsEmpty)
                continue;
            // 遍历快照：翻新会摘出旧批、按新档位并入或新建，直接遍历原列表会踩到修改
            foreach (var s in new List<GoodsStack>(b.Inv.Stacks))
            {
                if (RenewAction.WorthRenewing(b.Inv, s.GoodsId, out _))
                    RenewAction.Apply(b.Inv, s.GoodsId, out _);
            }
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
            var st = s.State;
            var r = TimedRules.Resolve(st, rates, s.GoodsId);

            // 软轴：累积老化龄期（稳定期内照常累积，但鲜度不下降——鲜度由龄期派生，
            // 见 TimedRules.FreshOf，这就是"稻谷三年不变质"的实现）
            if (r.FreshRate > 0f)
                st.FreshAgeDays += r.FreshRate;
            st.Fresh = TimedRules.FreshOf(st.FreshAgeDays, TimedRules.BaselineOf(s.GoodsId));

            // 硬轴：有硬轴才累积（无硬轴的货品永不消亡，如柴薪、木炭）
            if (!r.SoftOnly)
                st.UsedLifespan += 1f;

            // 硬轴耗尽：降级为废料（可当柴烧）或直接消失
            if (!r.SoftOnly && st.UsedLifespan >= r.Span)
            {
                ConvertToScrap(inv, i, s.Amount);
                continue;
            }

            s.SetState(st);
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
