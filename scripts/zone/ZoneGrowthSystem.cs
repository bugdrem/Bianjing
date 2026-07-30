using System;
using Godot;

namespace Bianjing;

/// <summary>坊区生长系统（每日结算，日频概率——1x 下一游戏日 ≈ 20 现实秒、一游戏月 ≈ 10 现实分钟）：
/// 住宅不再缺房自动生成（人口靠迁入 + 分家建房驱动，见 LifecycleSystem）；
/// 住宅容量=占地格数，住满后由拥挤事件驱动扩地；升级只影响楼高观感，
/// 升级时占地够大的住宅有概率转业为商铺/工坊（前店后宅，带来就业与交易）。</summary>
public class ZoneGrowthSystem
{
    /// <summary>建筑每日升级概率。</summary>
    private const float LevelUpChancePerDay = 0.02f;

    /// <summary>住宅升级时转为商铺 / 工坊的概率（其余仍为住宅）。</summary>
    private const float ShopConvertChance = 0.3f;
    private const float WorkshopConvertChance = 0.12f;

    private readonly Random _rng = new();

    public void TickDay(GameState gs)
    {
        // 财政破产则停止一切生长（无限钱下不受此限）
        if (gs.Money <= 0 && !GameSettings.InfiniteMoney)
            return;

        // 住宅不再缺房自动生成：人口靠迁入 + 分家建房驱动（见 LifecycleSystem）；仅保留升级/转业
        LevelUps(gs);
    }

    /// <summary>坊区建筑升级：吸引力越高级要求越高，年久失修的不升，里程碑限制最高等级；住宅升级后有概率转业。</summary>
    private void LevelUps(GameState gs)
    {
        int maxLevel = Milestones.MaxHouseLevel(gs); // 住宅限级随里程碑放开
        bool changed = false;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "grown" || b.Level >= Math.Min(b.Def.MaxLevel, maxLevel) || b.Condition < 60f)
                continue;
            if (gs.Map.CellAt(b.Origin).Desirability < 1.2f * b.Level)
                continue;
            if (_rng.NextDouble() < LevelUpChancePerDay)
            {
                b.Level++;
                changed = true;
                // 扩建不再随升级触发（改由住满拥挤事件驱动，见 LifecycleSystem.ResolveHousing）；
                // 只有扩过地（占地 ≥ ConvertMinArea 平米，2×2 起步制下约扩建两次）的住宅升级时才有资格转为商铺/工坊
                if (b.Def.Id == "house" && b.FootX * b.FootY >= GameBalance.Growth.ConvertMinArea)
                    TryConvertHouse(gs, b);
            }
        }
        if (changed)
            EventBus.RaiseMapChanged();
    }

    /// <summary>住宅升级时掷是否转业：受全城工商占比封顶（约十间住宅出两三家）；
    /// 商铺只在贴近主路（ConvertRoadDist 米内）时可转、越贴近越容易；
    /// 工坊贴近主路或辅路即可转、同样越贴近越容易；两样都够不着则不转业。</summary>
    private void TryConvertHouse(GameState gs, BuildingInstance b)
    {
        // 村落阶段不开店：集镇（里程碑 1）起才允许住宅转工商
        if (gs.MilestoneLevel < 1)
            return;
    
        // 全城工商户数封顶 30%：大致对应“10 间住宅中两三个升级成工坊或商铺”
        int grown = 0, biz = 0;
        foreach (var g in gs.Buildings.Values)
        {
            if (g.Def.Category != "grown")
                continue;
            grown++;
            if (g.Def.Id != "house")
                biz++;
        }
        if (grown > 0 && biz >= grown * 0.3f)
            return;
    
        // 临路远近：占地边缘到最近主路/辅路的距离（米，范围内没有则 -1 无资格；小路/桥面不算）
        var (dMain, dSide) = NearestRoadDistance(gs, b, GameBalance.Growth.ConvertRoadDist);
    
        // 商铺：门面要临主街——只认主路，越贴近越容易（贴边满额，判定边界处约打二折）
        if (dMain > 0 && _rng.NextDouble() < ShopConvertChance * RoadProximity(dMain))
        {
            gs.ConvertGrown(b, "shop");
            return;
        }
    
        // 工坊：进出料方便即可——主路或辅路皆可，取两者中更近的距离，越贴近越容易
        int dAny = dMain > 0 && (dSide < 0 || dMain < dSide) ? dMain : dSide;
        if (dAny > 0 && _rng.NextDouble() < WorkshopConvertChance * RoadProximity(dAny))
            gs.ConvertGrown(b, "workshop");
    }
    
    /// <summary>临路远近的概率倍率：贴边（d=1）为 1.0，随距离线性衰减到判定边界处的 1/ConvertRoadDist。</summary>
    private static double RoadProximity(int d)
        => (GameBalance.Growth.ConvertRoadDist + 1 - d) / (double)GameBalance.Growth.ConvertRoadDist;
    
    /// <summary>占地边缘到最近主路格与辅路格的切比雪夫距离（米）：范围内找不到返回 -1；
    /// 小路与桥面不计（转业只认玩家画的主/辅路）。扫描面积 (占地+2r)² 量级，仅升级掷中时偶发调用。</summary>
    private static (int Main, int Side) NearestRoadDistance(GameState gs, BuildingInstance b, int maxDist)
    {
        int dMain = -1, dSide = -1;
        var o = b.Origin;
        int fx = b.FootX, fy = b.FootY;
        for (int x = o.X - maxDist; x < o.X + fx + maxDist; x++)
        {
            for (int y = o.Y - maxDist; y < o.Y + fy + maxDist; y++)
            {
                if (x >= o.X && x < o.X + fx && y >= o.Y && y < o.Y + fy)
                    continue; // 占地内部不算
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref gs.Map.CellAt(c);
                if (!cell.HasRoad)
                    continue;
                // 该格到占地边缘的切比雪夫距离（贴边为 1）
                int dx = x < o.X ? o.X - x : x >= o.X + fx ? x - (o.X + fx - 1) : 0;
                int dy = y < o.Y ? o.Y - y : y >= o.Y + fy ? y - (o.Y + fy - 1) : 0;
                int d = Math.Max(dx, dy);
                if (d > maxDist)
                    continue; // 矩形四角可能超出判定半径
                if (cell.RoadKind == RoadKind.Main)
                {
                    if (dMain < 0 || d < dMain)
                        dMain = d;
                }
                else if (cell.RoadKind == RoadKind.Side)
                {
                    if (dSide < 0 || d < dSide)
                        dSide = d;
                }
            }
        }
        return (dMain, dSide);
    }

    /// <summary>住宅向紧邻空地（或自家小路环格）扩大占地（最大 8×8 米）：依次试右列/左列/下行/上行，
    /// 整条带均为可建设区空地或本建筑小路环才并入；扩成后对新 footprint 重新环一圈小路。
    /// 由住满拥挤事件调用（公开供 LifecycleSystem）；兼并邻居留 TODO。</summary>
    public static bool TryExpandHouse(GameState gs, BuildingInstance b)
    {
        const int MaxSide = 8; // 扩建边长上限（米）
        int fx = b.FootX, fy = b.FootY;
        if (fx * fy >= MaxSide * MaxSide)
            return false;
    
        bool expanded = false;
        if (fx < MaxSide)
        {
            if (ClaimStrip(gs, b, new Vector2I(b.Origin.X + fx, b.Origin.Y), 0, 1, fy))
            {
                b.SizeX = fx + 1; b.SizeY = fy; expanded = true;
            }
            else if (ClaimStrip(gs, b, new Vector2I(b.Origin.X - 1, b.Origin.Y), 0, 1, fy))
            {
                b.Origin = new Vector2I(b.Origin.X - 1, b.Origin.Y);
                b.SizeX = fx + 1; b.SizeY = fy; expanded = true;
            }
        }
        if (!expanded && fy < MaxSide)
        {
            if (ClaimStrip(gs, b, new Vector2I(b.Origin.X, b.Origin.Y + fy), 1, 0, fx))
            {
                b.SizeX = fx; b.SizeY = fy + 1; expanded = true;
            }
            else if (ClaimStrip(gs, b, new Vector2I(b.Origin.X, b.Origin.Y - 1), 1, 0, fx))
            {
                b.Origin = new Vector2I(b.Origin.X, b.Origin.Y - 1);
                b.SizeX = fx; b.SizeY = fy + 1; expanded = true;
            }
        }
        if (expanded)
        {
            gs.LayBuildingLaneRing(b); // 小路环随占地前移：在新边界外重新环一圈
            EventBus.RaiseMapChanged();
        }
        return expanded;
    }
    
    /// <summary>检查并占用一条带（起点 start，步进 (dx,dy)，共 count 格）：
    /// 每格须为「可建设区内空地」或「本建筑小路环格（HasRoad 且 RoadKind.Lane）」才成立，
    /// 成立即登记归属（ClaimCellForBuilding 内部先清小路再并入）。</summary>
    private static bool ClaimStrip(GameState gs, BuildingInstance b, Vector2I start, int dx, int dy, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var c = new Vector2I(start.X + dx * i, start.Y + dy * i);
            if (!MapGrid.InBounds(c))
                return false;
            ref var cell = ref gs.Map.CellAt(c);
            bool buildableEmpty = cell.IsEmpty && cell.Zone == ZoneType.Buildable;
            bool ownLane = cell.HasRoad && cell.RoadKind == RoadKind.Lane && cell.BuildingId < 0;
            if (!buildableEmpty && !ownLane)
                return false;
        }
        for (int i = 0; i < count; i++)
            gs.ClaimCellForBuilding(new Vector2I(start.X + dx * i, start.Y + dy * i), b.Id);
        return true;
    }
    
    /// <summary>在可建设区内挑一处可负担且吸引力最高的合法落位自建住宅（house）：
    /// 地价 = HouseBaseCost + LandPricePerDesir×该格吸引力；budget 内选吸引力最高者（最靠主路/设施、最贵的可负担点），
    /// 预算不足自然退而选便宜（低吸引力）的可负担处；全买不起或无合法落位返回 false，
    /// 成功输出新建住宅与实际地价（由调用方从买房方资产中扣除）。只遍历 BuildableCells 增量索引作候选原点。</summary>
    public static bool TryBuildHouse(GameState gs, double budget, out BuildingInstance built, out double cost)
    {
        built = null;
        cost = 0;
        var def = gs.Defs["house"];
    
        Vector2I best = default;
        float bestDesir = float.MinValue;
        double bestCost = 0;
        bool afford = false;
    
        foreach (var c in gs.BuildableCells)
        {
            // 整块占地均为坊区内无树空地，且四周小路环可铺并接入既有路网（小路也算接入）
            if (!FootprintBuildable(gs, c, def.SizeX, def.SizeY) || !RingLayable(gs, c, def.SizeX, def.SizeY))
                continue;
            float desir = gs.Map.CellAt(c).Desirability;
            double price = GameBalance.Growth.HouseBaseCost + GameBalance.Growth.LandPricePerDesir * Math.Max(0f, desir);
            if (price > budget)
                continue; // 该地段负担不起
            if (desir > bestDesir)
            {
                bestDesir = desir;
                best = c;
                bestCost = price;
                afford = true;
            }
        }
    
        if (!afford)
            return false; // 全买不起 / 无合法落位
    
        cost = bestCost;
        built = gs.PlaceBuilding(def, best);
        return true;
    }

    /// <summary>以 origin 为原点的 sx×sy 占地是否全部为可建设区内的无树空地。</summary>
    private static bool FootprintBuildable(GameState gs, Vector2I origin, int sx, int sy)
    {
        for (int x = origin.X; x < origin.X + sx; x++)
        {
            for (int y = origin.Y; y < origin.Y + sy; y++)
            {
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c))
                    return false;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.Zone != ZoneType.Buildable || !cell.IsEmpty || cell.HasTree)
                    return false;
            }
        }
        return true;
    }

    /// <summary>四周一圈小路环是否可铺：每个环格须
    /// 「已是道路（共享/连接，保留）」或「可建设区内的空地（将铺成小路）」；
    /// 且至少一格已是道路，确保新住宅经小路接入既有路网（村民靠小路继续外扩）。</summary>
    private static bool RingLayable(GameState gs, Vector2I origin, int sx, int sy)
    {
        int w = GameBalance.Growth.LaneRing;
        bool touchesRoad = false;
        for (int x = origin.X - w; x < origin.X + sx + w; x++)
        {
            for (int y = origin.Y - w; y < origin.Y + sy + w; y++)
            {
                if (x >= origin.X && x < origin.X + sx && y >= origin.Y && y < origin.Y + sy)
                    continue; // 跳过 footprint 内部
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c))
                    return false;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.HasRoad)
                {
                    touchesRoad = true; // 既有道路：作连接点，保留不动
                    continue;
                }
                // 非道路环格须是可建设区内空地（含可砍伐的树），否则整块作废
                if (cell.Zone != ZoneType.Buildable || !cell.IsEmpty)
                    return false;
            }
        }
        return touchesRoad;
    }
}
