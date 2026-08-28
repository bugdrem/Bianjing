using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Bianjing;

/// <summary>
/// 外来访客系统（道路通边 → 四向邻城来人）：日结调度随机 NPC、选出生边格、派发行程
/// （商人赴商铺/驿站、货郎路边摆摊、游客进城住宿），推进入城/离城、结算买卖与摆摊售货。
/// 访客与摊位均为瞬态 Node，不写 GameState.Citizens，重载即清空，无需改存档结构。
/// </summary>
public partial class VisitorSystem : Node
{
    private GameClock _clock;
    private GameState _gs;
    private float _spawnAccum; // 实时生成节流累加器（秒）
    private readonly List<ForeignVisitor> _visitors = new();
    private readonly List<Stall> _stalls = new();
    private readonly Random _rng = new();

    public void Setup(GameClock clock, GameState gs)
    {
        _clock = clock;
        _gs = gs;
    }

    /// <summary>当前在场访客（点选拾取用）。</summary>
    public IEnumerable<ForeignVisitor> ActiveVisitors => _visitors;

    public void TickDay(GameState gs)
    {
        _gs = gs;

        // 摆摊：日结售货 + 寿命递减（到期收摊并放人离城）。生成与回收已移至实时 _Process。
        foreach (var st in new List<Stall>(_stalls))
        {
            st.TickTrade(gs);
            st.DaysLeft--;
            if (st.DaysLeft <= 0)
            {
                _stalls.Remove(st);
                st.QueueFree();
                st.OwnerVisitor?.ForceLeave();
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_gs == null)
            return;

        // 仅当至少有一座邻城道路通边时，才有外来访客过来消费
        bool anyConnected = false;
        for (int i = 0; i < 4; i++)
        {
            if (_gs.Neighbors[i].Connected) { anyConnected = true; break; }
        }
        if (!anyConnected)
            return;

        int target = TargetCount(_gs);

        // 回收已离场访客（到达边缘出口即标记 IsDone，立即可见地消失）
        for (int i = _visitors.Count - 1; i >= 0; i--)
        {
            if (_visitors[i].IsDone)
            {
                _visitors[i].QueueFree();
                _visitors.RemoveAt(i);
            }
        }

        // 实时随机生成：目标数与城市总人口成正比，完全不挂天数
        _spawnAccum += (float)delta;
        while (_spawnAccum >= VisitorConfig.SpawnIntervalSec)
        {
            _spawnAccum -= VisitorConfig.SpawnIntervalSec;
            if (_visitors.Count >= target || _visitors.Count >= VisitorConfig.MaxConcurrentVisitors)
                break; // 已达人口比例目标/上限，停止本周期生成
            if (_rng.NextDouble() < VisitorConfig.SpawnChancePerInterval)
                SpawnFromRandomConnected(_gs);
        }
    }

    /// <summary>目标在场访客数：城市总人口 × 比例，向下取整（与人口成正比）。</summary>
    private int TargetCount(GameState gs)
        => Math.Min((int)(gs.Population * VisitorConfig.PopulationRatio), VisitorConfig.MaxConcurrentVisitors);

    /// <summary>从全部已连通邻城中随机挑一座，在其边缘路格生成一名随机访客。</summary>
    private void SpawnFromRandomConnected(GameState gs)
    {
        var idxs = new List<int>(4);
        for (int i = 0; i < 4; i++)
            if (gs.Neighbors[i].Connected)
                idxs.Add(i);
        if (idxs.Count == 0)
            return;
        int k = idxs[_rng.Next(idxs.Count)];
        SpawnVisitor(gs, (MapDir)k, gs.Neighbors[k]);
    }

    private void SpawnVisitor(GameState gs, MapDir dir, NeighborCity nb)
    {
        if (nb.EdgeCells.Count == 0)
            return;
        var exitCell = nb.EdgeCells[_rng.Next(nb.EdgeCells.Count)];
        var kind = PickKind();
        var cargo = MakeCargo(gs, nb);

        var v = new ForeignVisitor();
        v.Position = MapGrid.CellToWorld(exitCell) + Vector3.Up * (gs.Map.GroundY(exitCell) + 0.2f);

        Vector3 target;
        if (kind == ForeignVisitor.VisitorKind.Peddler)
        {
            var inner = PickInnerRoadCell(gs, exitCell);
            target = MapGrid.CellToWorld(inner) + Vector3.Up * 0.2f;
        }
        else
        {
            var venue = PickVenue(gs);
            target = venue != null
                ? BuildingCenter(venue) + Vector3.Up * 0.2f
                : MapGrid.CellToWorld(PickInnerRoadCell(gs, exitCell)) + Vector3.Up * 0.2f;
        }

        v.Init(gs, _clock, dir, nb, kind, cargo, exitCell, target);
        v.Arrived += (vv) => OnVisitorArrived(vv);
        v.Departed += (vv) => { vv.IsDone = true; };
        AddChild(v);
        _visitors.Add(v);
    }

    private void OnVisitorArrived(ForeignVisitor vv)
    {
        if (vv.Kind == ForeignVisitor.VisitorKind.Peddler)
        {
            var cell = MapGrid.WorldToCell(vv.Position);
            var free = FindRoadsideFreeCell(_gs, cell);
            if (free != null)
            {
                var stall = new Stall();
                stall.Init(_gs, vv, free.Value, vv.Inv,
                    _rng.Next(VisitorConfig.StallMinDays, VisitorConfig.StallMaxDays + 1));
                AddChild(stall);
                _stalls.Add(stall);
                vv.Stall = stall;
                vv.HasStall = true;
                vv.SetDwellPermanent(); // 有摊位的小贩永久驻留，摊位到期再离场
            }
            // 找不到摊位点 → HasStall 仍 false → 有限驻留后自动离场
        }
        else if (vv.Kind == ForeignVisitor.VisitorKind.Merchant)
        {
            SettleVenueTrade(_gs, vv);
        }
        else
        {
            SettleTourist(_gs, vv);
        }
    }

    /// <summary>商人：双向贸易——先买走城市最过剩的货（城市赚钱），再把所带货（城市短缺货）售入库存（城市付钱给外城）。</summary>
    private void SettleVenueTrade(GameState gs, ForeignVisitor vv)
    {
        var venue = PickVenue(gs);
        if (venue == null)
            return;

        // 出口：外城收购城市最过剩的货（可支撑天数最高且高于阈值），从持有该货最多的贸易节点扣除，城市收入
        var surplus = gs.Demand.Entries.Values
            .Where(e => e.Stock > 0 && e.DaysOfStock > VisitorConfig.SurplusDaysThreshold)
            .OrderByDescending(e => e.DaysOfStock)
            .FirstOrDefault();
        if (surplus != null)
        {
            BuildingInstance holder = null;
            double best = 0;
            foreach (var b in gs.Buildings.Values)
            {
                if (!IsTradeNode(b))
                    continue; // 只从贸易/生产节点清库存，不扣居民家用
                double a = b.Inv.AmountOf(surplus.GoodsId);
                if (a > best) { best = a; holder = b; }
            }
            if (holder != null)
            {
                double qty = Math.Min(best * VisitorConfig.ExportStockShare, VisitorConfig.ExportMaxQty);
                double taken = holder.TakeGoods(surplus.GoodsId, qty);
                long revenue = (long)(taken * Goods.PriceOf(surplus.GoodsId));
                if (revenue > 0)
                {
                    gs.Money += revenue;
                    gs.Ledger?.Add("外来商队", revenue);
                }
            }
        }

        // 进口：把所带货（城市短缺货）售入 venue 库存，仅当城市仍短缺才买（已补够则不强行塞，防积压）
        long cost = 0;
        foreach (var s in new List<GoodsStack>(vv.Inv.Stacks))
        {
            if (!gs.Demand.IsShort(s.GoodsId))
                continue; // 城市已不缺此货 → 跳过，不买
            double amt = venue.StoreGoodsForce(s.GoodsId, s.Amount);
            cost += (long)(amt * Goods.PriceOf(s.GoodsId));
        }
        if (cost > 0)
        {
            gs.Money -= cost;
            gs.Ledger?.Add("外来商队", -cost);
        }
        vv.Inv = new Inventory(); // 售罄
    }

    /// <summary>是否贸易/生产节点（商铺、驿站、工坊、官营）：可参与进出口库存调度；民居不计入。</summary>
    private static bool IsTradeNode(BuildingInstance b)
    {
        var c = b.Def.Category;
        return c == "shop" || c == "inn" || c == "workshop" || c == "official";
    }

    /// <summary>游客：在驿站下榻，城市收住宿费。</summary>
    private void SettleTourist(GameState gs, ForeignVisitor vv)
    {
        var inn = gs.BuildingsOfType("inn").FirstOrDefault();
        if (inn != null)
            gs.PayToBuilding(inn, VisitorConfig.LodgeFee);
    }

    private ForeignVisitor.VisitorKind PickKind()
    {
        double r = _rng.NextDouble();
        if (r < VisitorConfig.MerchantRatio)
            return ForeignVisitor.VisitorKind.Merchant;
        if (r < VisitorConfig.MerchantRatio + VisitorConfig.PeddlerRatio)
            return ForeignVisitor.VisitorKind.Peddler;
        return ForeignVisitor.VisitorKind.Tourist;
    }

    /// <summary>取 1–2 种货作为带货：城市有缺口时大概率带「城市短缺货」补货，否则带邻城特产地货（维持多样性）。</summary>
    private Inventory MakeCargo(GameState gs, NeighborCity nb)
    {
        // 互市闭环（待办B）：城市有缺口 → 大部分访客带短缺货补货（进口响应需求）
        var shorts = gs.Demand.Entries.Values
            .Where(e => e.IsShort)
            .Select(e => e.GoodsId)
            .ToList();
        if (shorts.Count > 0 && _rng.NextDouble() < VisitorConfig.ImportBias)
        {
            Shuffle(shorts, _rng);
            int n = Math.Min(shorts.Count, 1 + _rng.Next(0, 2)); // 1–2 种短缺货
            var cargo = new Inventory();
            for (int i = 0; i < n; i++)
            {
                double qty = VisitorConfig.ShortageCargoMin
                           + _rng.NextDouble() * (VisitorConfig.ShortageCargoMax - VisitorConfig.ShortageCargoMin);
                cargo.StoreForce(shorts[i], Math.Round(qty));
            }
            return cargo;
        }

        // 无短缺（或本次不响应缺口）→ 邻城特产地逻辑（维持多样性）
        int cat = VisitorConfig.PrimarySpecialty(nb);
        if (cat < 0 || !VisitorConfig.GoodsOfCategory(cat).Any())
        {
            var cats = VisitorConfig.AllCategories().ToList();
            if (cats.Count == 0)
                return new Inventory();
            cat = cats[_rng.Next(cats.Count)];
        }
        var goods = VisitorConfig.GoodsOfCategory(cat).ToList();
        if (goods.Count == 0)
            return new Inventory();
        int m = Math.Min(goods.Count, _rng.Next(1, 3));
        var inv = new Inventory();
        for (int i = 0; i < m; i++)
        {
            string g = goods[_rng.Next(goods.Count)];
            double qty = 10 + _rng.Next(0, 31); // 10–40 份
            inv.StoreForce(g, qty);
        }
        return inv;
    }

    private static void Shuffle<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private BuildingInstance PickVenue(GameState gs)
    {
        var pool = new List<BuildingInstance>();
        pool.AddRange(gs.BuildingsOfType("shop"));
        pool.AddRange(gs.BuildingsOfType("inn"));
        return pool.Count == 0 ? null : pool[_rng.Next(pool.Count)];
    }

    /// <summary>挑一处城内有纵深的路格作为货郎/游客目标（随机索引取一格，边格重试，O(1) 不扫全图）。</summary>
    private Vector2I PickInnerRoadCell(GameState gs, Vector2I exitCell)
    {
        if (gs.RoadCells.Count == 0)
            return exitCell;
        int s = MapGrid.Size - 1;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var c = gs.RoadCells[_rng.Next(gs.RoadCells.Count)];
            if (c.X == 0 || c.X == s || c.Y == 0 || c.Y == s)
                continue; // 边格重试
            return c;
        }
        return exitCell;
    }

    /// <summary>在 near 周围找一处「真正临路的空地」支摊（不压路/水/建筑）。</summary>
    private Vector2I? FindRoadsideFreeCell(GameState gs, Vector2I near)
    {
        for (int radius = 1; radius <= 6; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue; // 只扫环
                    var c = new Vector2I(near.X + dx, near.Y + dy);
                    if (!MapGrid.InBounds(c))
                        continue;
                    var cell = gs.Map.CellAt(c);
                    if (cell.HasRoad || cell.HasWater || cell.BuildingId >= 0)
                        continue;
                    if (!IsAdjacentToRoad(gs, c))
                        continue;
                    return c;
                }
            }
        }
        return null;
    }

    private bool IsAdjacentToRoad(GameState gs, Vector2I c)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                var n = new Vector2I(c.X + dx, c.Y + dy);
                if (MapGrid.InBounds(n) && gs.Map.CellAt(n).HasRoad)
                    return true;
            }
        return false;
    }

    private static Vector3 BuildingCenter(BuildingInstance b)
    {
        var a = MapGrid.CellToWorld(b.Origin);
        var c = MapGrid.CellToWorld(new Vector2I(b.Origin.X + b.FootX - 1, b.Origin.Y + b.FootY - 1));
        return (a + c) * 0.5f;
    }
}
