using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 耕种区系统（每日结算，日频概率——时间口径见 TimeConfig（一游戏日 ≈ 43 现实秒）：
/// 玩家划定耕种区（ZoneType.Farmland）后，区内空地按连通块分组；
/// 具备农艺技能的村民自动开垦——在块内按尺寸阶梯（6×6→4×4→2×2）生成田块实体
/// （田块为 buildings.json 的 farmland 定义，category=field，复用建筑占位/库存/月结收获链路）；
/// 田块一级=田主自种（岗位 1），升级后岗位递增、月结产量 = 在岗农夫数 × 每工产量（GoodsSystem.TickMonth）；
/// 田块升级条件：田主家庭公产达门槛并扣款 + 全城有闲置农艺劳动力，升级才落地。
/// 荒田继承（批次八十四）：田主亡故/迁离后田块保留不拆，由闲置农艺者 0 投入接手（先继承后开垦），
/// 农田不再只增不减；开垦按尺寸阶梯实际落块（旧版落点放大成 6×6 压盖已有田块致重叠穿模）。
/// 后续果树/桑田等新田种：新增 buildings.json 定义即可，逻辑层按 Def.ProduceGoods 天然区分。
/// </summary>
public class FarmlandSystem
{
    private readonly Random _rng = new();

    public void TickDay(GameState gs)
    {
        if (gs.FarmlandCells.Count == 0)
            return;

        var blocks = GroupBlocks(gs);
        foreach (var block in blocks)
            TickBlock(gs, block);
    }

    /// <summary>把耕种区格按 4 邻域连通分组（HashSet 快照遍历，逐格 BFS 收集），返回各组格集。</summary>
    private static List<HashSet<Vector2I>> GroupBlocks(GameState gs)
    {
        var blocks = new List<HashSet<Vector2I>>();
        var seen = new HashSet<Vector2I>();
        Vector2I[] dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        foreach (var start in gs.FarmlandCells)
        {
            if (!seen.Add(start))
                continue;
            var block = new HashSet<Vector2I> { start };
            var queue = new Queue<Vector2I>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var d in dirs)
                {
                    var n = c + d;
                    if (!MapGrid.InBounds(n) || !gs.FarmlandCells.Contains(n) || !seen.Add(n))
                        continue;
                    block.Add(n);
                    queue.Enqueue(n);
                }
            }
            blocks.Add(block);
        }
        return blocks;
    }

    /// <summary>处理一个连通块：田主指派 → 继承荒田/开垦新田 → 田块升级（开垦/升级为日概率，继承即时）。</summary>
    private void TickBlock(GameState gs, HashSet<Vector2I> block)
    {
        // 块内现有田块与占用格统计
        var fields = new List<BuildingInstance>();
        int occupied = 0;
        foreach (var c in block)
        {
            int bid = gs.Map.CellAt(c).BuildingId;
            if (bid >= 0 && gs.Buildings.TryGetValue(bid, out var b) && b.Def.Id == "farmland")
            {
                if (!fields.Contains(b))
                    fields.Add(b);
            }
            else if (bid >= 0 || gs.Map.CellAt(c).HasRoad || gs.Map.CellAt(c).HasWater)
            {
                occupied++;
            }
        }

        // 1) 田主指派/校正：无主或业主失效的田块，指派块内在岗最资深农夫（按 Id 长幼定序）
        foreach (var f in fields)
            EnsureOwner(gs, f);

        // 2) 荒田继承（批次八十四）：田主亡故/迁离且无在岗农夫可改派的田块（OwnerCitizenId 已置 -1）
        // 由闲置农艺者 0 投入接手——先继承后开垦，荒田复用不再只增不减；继承即时不掷签
        Citizen farmer = FindIdleFarmer(gs);
        if (farmer != null)
        {
            BuildingInstance vacant = null;
            foreach (var f in fields)
                if (f.OwnerCitizenId < 0)
                {
                    vacant = f;
                    break;
                }
            if (vacant != null)
            {
                vacant.OwnerCitizenId = farmer.Id; // 接手者即田主兼农夫（一人一田）
                farmer.JobKind = JobKind.Employed;
                farmer.WorkplaceId = vacant.Id;
                gs.LogLifeEvent(farmer, $"接手了他人荒废的{vacant.Def.Name}（{vacant.Origin.X},{vacant.Origin.Y}）");
            }
            // 3) 开垦：块内还有空地且存在「有住所的闲置农艺村民（尚未当田主）」→ 按尺寸阶梯落一块新田（日概率；
            // 批次七十四需求度：全城缺粮时日概率翻倍，优先把地种上；无荒田可继才开新田
            else
            {
                double reclaimChance = FarmlandConfig.FarmChancePerDay
                    * (gs.Demand.IsShort(Goods.Grain) ? FarmlandConfig.ScarcityReclaimBoost : 1);
                if (block.Count - occupied >= 4
                    && _rng.NextDouble() < reclaimChance
                    && FindFieldSpot(gs, block, out var origin, out int size))
                {
                    var def = gs.Defs["farmland"];
                    // 批次八十四：按档位实际落块（旧版丢尺寸恒放 6×6，2×2/4×4 档落点放大后压盖已有田块致重叠）
                    var field = gs.PlaceBuilding(def, origin, size, size);
                    field.OwnerCitizenId = farmer.Id; // 开垦人即田主兼首任农夫（一人一田）
                    farmer.JobKind = JobKind.Employed;
                    farmer.WorkplaceId = field.Id;
                    gs.LogLifeEvent(farmer, $"在耕种区开垦了一亩{def.Name}（{origin.X},{origin.Y}）");
                }
            }
        }

        // 4) 田块升级：田主公产达标 + 有闲置农艺劳动力 → 逐级升（每级扣款，公产不足停）
        foreach (var f in fields)
            TryUpgradeField(gs, f);
    }

    /// <summary>田主校正：田主已亡故/迁离或字段失效时，改指派块内在岗农夫中资历最老者（Id 最小）；
    /// 已是别田田主者不兼任（一人一田，批次七十一）。</summary>
    private static void EnsureOwner(GameState gs, BuildingInstance field)
    {
        if (field.OwnerCitizenId >= 0
            && gs.Citizens.TryGetValue(field.OwnerCitizenId, out var owner)
            && owner.JobKind == JobKind.Employed && owner.WorkplaceId == field.Id)
            return;

        Citizen best = null;
        foreach (var c in gs.Citizens.Values)
        {
            if (c.JobKind != JobKind.Employed || c.WorkplaceId != field.Id)
                continue;
            if (OwnsField(gs, c.Id))
                continue; // 一人一田：已是别田田主不兼任（本块田主在前面已通过检查，不会误伤）
            if (best == null || c.Id < best.Id)
                best = c;
        }
        field.OwnerCitizenId = best?.Id ?? -1;
    }

    /// <summary>块内找田块落位（批次七十一：贴路优先）：同一尺寸档内挑贴路分最高的落位——
    /// 农田与民居一样优先贴着主/辅路成片开垦（外扩 SiteScanDist 内主路 2 分/格、辅路 1 分/格，
    /// 小路不计）；全块无路则退回原遍历序。落位要求整块占地均为耕种区内「可开垦」格
    /// （无路/无水/无建筑；树可砍——开荒即砍树整平，落位时 PlaceBuilding 自动砍伐）。
    /// 批次八十四：out size 带出所选档位——开垦按档实际落块，防小块落点被放大成 6×6 压盖已有田块。</summary>
    private static bool FindFieldSpot(GameState gs, HashSet<Vector2I> block, out Vector2I origin, out int size)
    {
        foreach (int s in FarmlandConfig.FieldSizeTiers)
        {
            Vector2I best = default;
            int bestRoad = int.MinValue;
            bool any = false;
            // 落位起点取块内格（枚举顺序即遍历序，保证同块内先占先得；同分保留先者）
            foreach (var c in block)
            {
                var cand = new Vector2I(c.X - (s - 1) / 2, c.Y - (s - 1) / 2);
                if (!FootprintReclaimable(gs, cand, s, s))
                    continue;
                int road = RoadScore(gs, cand, s, s);
                if (!any || road > bestRoad)
                {
                    any = true;
                    best = cand;
                    bestRoad = road;
                }
            }
            if (any)
            {
                origin = best;
                size = s;
                return true;
            }
        }
        origin = default;
        size = 0;
        return false;
    }

    /// <summary>占地外扩 SiteScanDist 格内的贴路分：主路每格 2 分、辅路每格 1 分、小路不计
    /// （到处都有无区分度）——路边地优先开垦，成片农田沿道路展开。</summary>
    private static int RoadScore(GameState gs, Vector2I origin, int sx, int sy)
    {
        int r = GrowthConfig.SiteScanDist;
        int score = 0;
        for (int x = origin.X - r; x < origin.X + sx + r; x++)
        {
            for (int y = origin.Y - r; y < origin.Y + sy + r; y++)
            {
                if (x >= origin.X && x < origin.X + sx && y >= origin.Y && y < origin.Y + sy)
                    continue; // 占地内部不算
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c))
                    continue;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.HasRoad && cell.RoadKind == RoadKind.Main)
                    score += 2;
                else if (cell.HasRoad && cell.RoadKind == RoadKind.Side)
                    score += 1;
            }
        }
        return score;
    }

    /// <summary>sx×sy 占地是否全部为耕种区内可开垦格：区内、无路/无水/无建筑、且与块外占地不重叠。</summary>
    private static bool FootprintReclaimable(GameState gs, Vector2I origin, int sx, int sy)
    {
        for (int x = origin.X; x < origin.X + sx; x++)
        {
            for (int y = origin.Y; y < origin.Y + sy; y++)
            {
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c))
                    return false;
                ref var cell = ref gs.Map.CellAt(c);
                if (cell.Zone != ZoneType.Farmland || cell.HasRoad || cell.HasWater || cell.BuildingId >= 0)
                    return false;
            }
        }
        return true;
    }

    /// <summary>找一名开垦/就职人选（批次七十七放宽）：成年 + 农艺技能 + 尚未当田主（一人一田），当前非在职——
    /// 山民（Logger）/退休者皆可转业务农；此前仅限 JobKind.None，而 SeekJobs 会把无业者派成山民
    /// （缺粮时 100%），农艺者常年有职、开垦永远无人——八九年不开一亩田的根因之一。
    /// 另有安居门槛两级：优先有自有民居者（安居才事农）；若无安居者，寄居流民营/王爷府等官方建筑的
    /// 农艺者也可开垦（视作官田佃农/流民开荒）——民居要靠迁入者攒钱在建筑区自建，玩家若只划耕种区
    /// 不划建筑区则全城无人安居，此前农田系统整体死锁，官粮只出不进终至饥荒死亡潮。
    /// 寄居者开垦后同样在岗务农，产出与安居者一致。</summary>
    private static Citizen FindIdleFarmer(GameState gs)
    {
        foreach (var c in gs.Citizens.Values)
            if (FarmEligible(gs, c, needHome: true))
                return c; // 第一批：有自有民居者优先（安居才事农）
        foreach (var c in gs.Citizens.Values)
            if (FarmEligible(gs, c, needHome: false))
                return c; // 第二批兜底：寄居官方建筑者也准开垦（官田佃农），破除无民居死锁
        return null;
    }

    /// <summary>开垦/务农资格：成年 + 农艺技能 + 非在职 + 未当田主；needHome 时还须自有民居
    /// （批次七十七：两级门槛共用，升级闲置数统计与开垦同口径）。</summary>
    private static bool FarmEligible(GameState gs, Citizen c, bool needHome) =>
        !c.IsChild && c.JobKind != JobKind.Employed && c.Skill == SkillType.Farming
        && !OwnsField(gs, c.Id) && (!needHome || OwnsHome(gs, c));

    /// <summary>是否有私有住所（批次七十二）：住处是自有民居类建筑（house/mansion/店坊前店后宅），
    /// 寄居流民营/王爷府等官方建筑不算自有住所，不得开垦农田。</summary>
    private static bool OwnsHome(GameState gs, Citizen c) =>
        c.HomeId >= 0 && gs.Buildings.TryGetValue(c.HomeId, out var home)
        && home.Def.Category == "grown";

    /// <summary>该居民是否已是一块农田的田主（一人只开垦一块田，批次七十一）。</summary>
    private static bool OwnsField(GameState gs, int citizenId)
    {
        foreach (var b in gs.Buildings.Values)
            if (b.Def.Id == "farmland" && b.OwnerCitizenId == citizenId)
                return true;
        return false;
    }

    /// <summary>田块升级：田主公产 ≥ 门槛时扣款升级（岗位随之增加，产量随工人数提高）。</summary>
    private void TryUpgradeField(GameState gs, BuildingInstance field)
    {
        if (field.Level >= field.Def.MaxLevel)
            return;
        if (field.OwnerCitizenId < 0 || !gs.Citizens.TryGetValue(field.OwnerCitizenId, out var owner))
            return;

        int idx = field.Level - 1; // 升到 Level+1 用第 idx 档门槛
        long needAssets = FarmlandConfig.UpgradeAssets[idx];
        long cost = FarmlandConfig.UpgradeCosts[idx];
        bool scarce = gs.Demand.IsShort(Goods.Grain); // 批次七十四需求度：缺粮门槛放宽、日概率翻倍
        if (scarce)
            needAssets = (long)(needAssets * (1 - FarmlandConfig.UpgradeScarcityDiscount));

        // 硬条件：全城闲置农艺劳动力足够（升级后岗位有人可填；与开垦同标准，含寄居者——宽松版）
        int spare = 0;
        foreach (var c in gs.Citizens.Values)
            if (FarmEligible(gs, c, needHome: false))
                spare++;
        if (spare < FarmlandConfig.UpgradeSpareFarmers)
            return;

        if (!gs.Families.TryGetValue(owner.FamilyId, out var fam) || fam.SharedAssets < needAssets)
            return;
        if (_rng.NextDouble() >= FarmlandConfig.FarmChancePerDay * (scarce ? FarmlandConfig.ScarcityReclaimBoost : 1))
            return;

        fam.SharedAssets -= cost;
        gs.Money += cost; // 批次七十八：田产升级费入官库（土地相关交王爷）——旧版扣款凭空消失
        gs.Ledger.Add("田产升级", cost);
        field.Level++;
        gs.LogLifeEvent(owner, $"农田升为{field.Level}级，雇工种田");
        EventBus.RaiseBuildingsChanged();
    }
}
