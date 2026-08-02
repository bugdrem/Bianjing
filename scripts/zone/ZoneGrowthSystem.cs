using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>坊区生长系统（每日结算，日频概率——1x 下一游戏日 ≈ 20 现实秒、一游戏月 ≈ 10 现实分钟）：
/// 住宅不再缺房自动生成（人口靠迁入 + 分家建房驱动，见 LifecycleSystem）；
/// 住宅容量=占地格数，住满后由拥挤事件驱动扩地；升级只影响楼高观感，
/// 路边（主/辅路旁）够格占地的住宅按日概率转业为商铺/工坊（前店后宅，带来就业与交易）。</summary>
public class ZoneGrowthSystem
{
    /// <summary>建筑每日升级概率（调参见 configs/GrowthConfig）。</summary>
    private const float LevelUpChancePerDay = GrowthConfig.LevelUpChancePerDay;

    private readonly Random _rng = new();

    public void TickDay(GameState gs)
    {
        // 财政破产则停止一切生长（无限钱下不受此限）
        if (gs.Money <= 0 && !GameSettings.InfiniteMoney)
            return;

        // 住宅不再缺房自动生成：人口靠迁入 + 分家建房驱动（见 LifecycleSystem）；仅保留升级/转业
        LevelUps(gs);
        Conversions(gs); // 路边住宅转商铺/工坊：独立于升级链（升级依赖吸引力，而村民多沿零吸引力小路建房）
    }

    /// <summary>路边转业（每日）：够格占地的路边民居按概率转商铺/工坊——独立于升级/吸引力，
    /// 使沿主/辅路的住宅能如实长出工商户（吸引力驱动的升级另见 LevelUps）。</summary>
    private void Conversions(GameState gs)
    {
        // 村落阶段不开店：集镇（里程碑 1）起才允许（与 TryConvertHouse 内闸门一致，此处先挡免无谓遍历）
        if (gs.MilestoneLevel < 1)
            return;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Id != "house" || b.Condition < GrowthConfig.LevelUpMinCondition)
                continue; // 只转民居，失修的不转
            if (b.FootX * b.FootY < GrowthConfig.ConvertMinArea)
                continue; // 占地不够（须扩建过）
            if (_rng.NextDouble() < GrowthConfig.ConvertChancePerDay)
                TryConvertHouse(gs, b);
        }
    }

    /// <summary>坊区建筑升级：吸引力越高级要求越高，年久失修的不升，里程碑限制最高等级（只影响楼高观感）。</summary>
    private void LevelUps(GameState gs)
    {
        int maxLevel = Milestones.MaxHouseLevel(gs); // 住宅限级随里程碑放开
        bool changed = false;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "grown" || b.Level >= Math.Min(b.Def.MaxLevel, maxLevel)
                || b.Condition < GrowthConfig.LevelUpMinCondition) // 失修不升
                continue;
            if (gs.Map.CellAt(b.Origin).Desirability < GrowthConfig.LevelUpDesirPerLevel * b.Level)
                continue;
            if (_rng.NextDouble() < LevelUpChancePerDay)
            {
                b.Level++;
                changed = true;
                // 扩建不再随升级触发（改由住满拥挤事件驱动，见 LifecycleSystem.ResolveHousing）；
                // 转业已从升级链解耦（见 Conversions）：升级依赖吸引力，而村民多沿零吸引力小路建房，两者矛盾
            }
        }
        if (changed)
            EventBus.RaiseBuildingsChanged(); // 升级只变楼高：仅重建建筑层，不重建地表分块
    }

    /// <summary>住宅升级时的去向掷签（按临路档位取分布，余量 = 维持住宅照常升级）：
    /// 贴近主路→商铺大概率/工坊中概率/更高级住宅小概率；
    /// 贴近辅路（不贴主路）→工坊与住宅都高、商铺小概率；
    /// 只靠自带小路→高概率维持住宅、小概率转工坊。
    /// 保留闸门：集镇（里程碑 1）起 + 全城工商占比 30% 封顶（占地门槛在调用处）。</summary>
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
        if (grown > 0 && biz >= grown * GrowthConfig.BizRatioCap)
            return;
    
        // 临路档位：优先认主路，其次辅路，都不贴则落入“只靠自带小路”档（小路/桥面不计入前两档）
        var (dMain, dSide) = NearestRoadDistance(gs, b, GrowthConfig.ConvertRoadDist);
        double pShop, pWorkshop;
        if (dMain > 0)
        {
            pShop = GrowthConfig.MainShopChance;
            pWorkshop = GrowthConfig.MainWorkshopChance;
        }
        else if (dSide > 0)
        {
            pShop = GrowthConfig.SideShopChance;
            pWorkshop = GrowthConfig.SideWorkshopChance;
        }
        else
        {
            pShop = GrowthConfig.LaneShopChance;
            pWorkshop = GrowthConfig.LaneWorkshopChance;
        }
    
        double r = _rng.NextDouble();
        if (r < pShop)
            gs.ConvertGrown(b, "shop");
        else if (r < pShop + pWorkshop)
            gs.ConvertGrown(b, "workshop");
        // 其余：维持住宅，本次即“升级成更高级的住宅”（Level 已在调用处 +1）
    }
    
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
        const int MaxSide = GrowthConfig.ExpandMaxSide; // 扩建边长上限（米）
        int fx = b.FootX, fy = b.FootY;
        if (fx * fy >= MaxSide * MaxSide)
            return false;

        float padH = gs.Map.GroundY(b.Origin); // 原垫基台面高：扩建条带压到同高，房体不悬空
    
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
            // 新占地整体重新整平到原台面高（含新并入条带），与 PlaceBuilding 垫基规则一致
            gs.Map.Height.FlattenRect(b.Origin, b.FootX, b.FootY, padH);
            gs.LayBuildingLaneRing(b); // 小路环随占地前移：在新边界外重新环一圈
            // 局部重建：扩建只动新占地矩形顶点，标脏覆盖分块即可（小路环已逐格 CellChanged）
            EventBus.RaiseRectChanged(b.Origin, new Vector2I(b.FootX, b.FootY));
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
    
    /// <summary>在可建设区内按选址偏好挑一处合法落位自建住宅（house）：
    /// 偏好叠加打分（SiteScore：主路 &gt; 辅路 &gt; 河道 &gt; 水井/已有建筑，河边十字路口分最高），
    /// 地价按需求 §4.1 四级（近资源点 8,000 / 普通 10,000 / 临街 15,000 / 城中心 25,000 文，见 GrowthConfig）；
    /// 达到 SiteThreshold 的可负担候选按分数加权抽签
    /// （高分地段大概率中签、达标冷清处保留小概率，既聚居又不死板），无达标者退选最高分；
    /// 全买不起或无合法落位返回 false，成功输出新宅与实际地价（调用方扣款）。</summary>
    public static bool TryBuildHouse(GameState gs, long budget, out BuildingInstance built, out long cost)
    {
        built = null;
        cost = 0;
        var def = gs.Defs["house"];

        // 近王爷府加成（用户需求：村民建房候选地叠加王爷府数值）：预取府邸中心，供逐候选格按距加分
        Vector2I? mansion = PrinceMansionCenter(gs);
    
        // 可负担候选分两组：达标集（按分加权抽签）与全集最高分（兜底）
        var qualified = new List<(Vector2I Cell, long Price, double Weight)>();
        Vector2I bestFallback = default;
        double bestScore = double.MinValue;
        long bestPrice = 0;
        bool afford = false;
    
        foreach (var c in gs.BuildableCells)
        {
            // 整块占地均为坊区内无树空地，且四周小路环可铺并接入既有路网（小路也算接入）
            if (!FootprintBuildable(gs, c, def.SizeX, def.SizeY) || !RingLayable(gs, c, def.SizeX, def.SizeY))
                continue;
            if (NearBridge(gs, c, def.SizeX, def.SizeY))
                continue; // 不沿桥/引桥建房
            double score = SiteScore(gs, c, def.SizeX, def.SizeY) + PrinceMansionBonus(mansion, c, def.SizeX, def.SizeY);
            long price = GrowthConfig.LandPriceOf(score, NearResource(gs, c, def.SizeX, def.SizeY)); // 地价公式见 GrowthConfig
            if (price > budget)
                continue; // 该地段负担不起
            afford = true;
            if (score >= GrowthConfig.SiteThreshold)
                qualified.Add((c, price, GrowthConfig.SiteWeightOf(score)));
            if (score > bestScore)
            {
                bestScore = score;
                bestFallback = c;
                bestPrice = price;
            }
        }
    
        if (!afford)
            return false; // 全买不起 / 无合法落位
    
        // 达标候选按分数加权抽签：邻居多/地段好处大概率中签，达标冷清处仍有小概率；无达标者退取全集最高分
        Vector2I chosen;
        if (qualified.Count > 0)
        {
            var pick = qualified[WeightedPick(qualified)];
            chosen = pick.Cell;
            cost = pick.Price;
        }
        else
        {
            chosen = bestFallback;
            cost = bestPrice;
        }
    
        built = gs.PlaceBuilding(def, chosen);
        return true;
    }

    /// <summary>占地外扩 2 格内是否有资源点（树/水）：近资源的宅基地按最贱地价（需求 §4.1），鼓励定居者近资源谋生。</summary>
    private static bool NearResource(GameState gs, Vector2I origin, int sizeX, int sizeY)
    {
        for (int dx = -2; dx <= sizeX + 1; dx++)
            for (int dy = -2; dy <= sizeY + 1; dy++)
            {
                var c = new Vector2I(origin.X + dx, origin.Y + dy);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.HasTree || cell.HasWater)
                    return true;
            }
        return false;
    }

    /// <summary>占地外扩 2 格内是否有桥（含引桥）：村民不沿桥/引桥建房，候选地近桥即作废。</summary>
    private static bool NearBridge(GameState gs, Vector2I origin, int sizeX, int sizeY)
    {
        for (int dx = -2; dx <= sizeX + 1; dx++)
            for (int dy = -2; dy <= sizeY + 1; dy++)
            {
                var c = new Vector2I(origin.X + dx, origin.Y + dy);
                if (!MapGrid.InBounds(c))
                    continue;
                if (gs.Map.CellAt(c).HasBridge)
                    return true;
            }
        return false;
    }

    /// <summary>选址随机源（静态方法内使用，与实例 _rng 分开）。</summary>
    private static readonly Random _siteRng = new();

    /// <summary>按权重轮盘抽签：返回中签候选的下标（总权重内掏一点，逐个扣减定位）。</summary>
    private static int WeightedPick(List<(Vector2I Cell, long Price, double Weight)> cands)
    {
        double total = 0;
        foreach (var q in cands)
            total += q.Weight;
        double roll = _siteRng.NextDouble() * total;
        for (int i = 0; i < cands.Count; i++)
        {
            roll -= cands[i].Weight;
            if (roll <= 0)
                return i;
        }
        return cands.Count - 1; // 浮点尾差兜底
    }

    /// <summary>全局唯一王爷府的占地中心（无则 null）：供“近府邸”选址加成。</summary>
    private static Vector2I? PrinceMansionCenter(GameState gs)
    {
        foreach (var b in gs.Buildings.Values)
            if (b.Def.Id == PrinceMansionConfig.DefId)
                return new Vector2I(b.Origin.X + b.FootX / 2, b.Origin.Y + b.FootY / 2);
        return null;
    }

    /// <summary>近王爷府选址加成：占地中心到府邸中心切比雪夫距≤半径时按距线性衰减加分（距 0 满分），
    /// 使村民建房优先聚于府邸周边（居选址首档）。</summary>
    private static double PrinceMansionBonus(Vector2I? center, Vector2I origin, int sx, int sy)
    {
        if (!center.HasValue)
            return 0;
        int cx = origin.X + sx / 2, cy = origin.Y + sy / 2;
        int d = Math.Max(Math.Abs(cx - center.Value.X), Math.Abs(cy - center.Value.Y));
        if (d > PrinceMansionConfig.SiteRadius)
            return 0;
        return PrinceMansionConfig.SiteScore * (1.0 - (double)d / PrinceMansionConfig.SiteRadius);
    }

    /// <summary>选址叠加打分：占地外扩 SiteScanDist 米内——主路/辅路/河道各计一次分，
    /// 邻居按密度计分（每栋建筑去重计分，栋数封顶）：邻居越多越想挨着建，使民居成片聚居；
    /// 可叠加——河边十字路口且邻居多处分最高；自带小路不计分（到处都有，无区分度）。</summary>
    private static double SiteScore(GameState gs, Vector2I origin, int sx, int sy)
    {
        int r = GrowthConfig.SiteScanDist;
        bool hasMain = false, hasSide = false, hasRiver = false;
        var neighborIds = new HashSet<int>(); // 邻近建筑按实例去重，防大占地（如王爷府）按格数灌分
        for (int x = origin.X - r; x < origin.X + sx + r; x++)
        {
            for (int y = origin.Y - r; y < origin.Y + sy + r; y++)
            {
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c))
                    continue;
                if (x >= origin.X && x < origin.X + sx && y >= origin.Y && y < origin.Y + sy)
                    continue; // 占地内部不算
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.HasRoad && cell.RoadKind == RoadKind.Main)
                    hasMain = true;
                else if (cell.HasRoad && cell.RoadKind == RoadKind.Side)
                    hasSide = true;
                if (cell.HasWater)
                    hasRiver = true;
                if (cell.BuildingId >= 0)
                    neighborIds.Add(cell.BuildingId); // 水井或任意已有建筑（有人烟/设施的地方）
            }
        }
        double score = 0;
        if (hasMain) score += GrowthConfig.SiteMainRoadScore;
        if (hasSide) score += GrowthConfig.SiteSideRoadScore;
        if (hasRiver) score += GrowthConfig.SiteRiverScore;
        // 邻居密度：每栋加分、栋数封顶（3 栋即与主路同档，聚落可脱离主辅路向外扩片）
        score += GrowthConfig.SiteNeighborScorePerBuilding
            * Math.Min(neighborIds.Count, GrowthConfig.SiteNeighborCountCap);
        return score;
    }

    /// <summary>以 origin 为原点的 sx×sy 占地是否全部为可建设区内的无树空地，且高差在垫基限内（落位时自动整平）。</summary>
    private static bool FootprintBuildable(GameState gs, Vector2I origin, int sx, int sy)
    {
        float minH = float.MaxValue, maxH = float.MinValue; // 住宅也走垫基规则（与 PlacementValidator 同限）
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
                minH = Math.Min(minH, gs.Map.Height.CellMinH(c));
                maxH = Math.Max(maxH, gs.Map.Height.CellMaxH(c));
            }
        }
        return maxH - minH <= TerrainConfig.MaxBuildFlattenDiff;
    }

    /// <summary>四周一圈小路环是否可铺：每个环格须
    /// 「已是道路（共享/连接，保留）」或「可建设区内的空地（将铺成小路）」；
    /// 且至少一格已是道路，确保新住宅经小路接入既有路网（村民靠小路继续外扩）。</summary>
    private static bool RingLayable(GameState gs, Vector2I origin, int sx, int sy)
    {
        int w = GrowthConfig.LaneRing;
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
