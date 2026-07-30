using System;
using System.Collections.Generic;
using System.Linq;

namespace Bianjing;

/// <summary>
/// 居民生命周期系统：
/// 每日——迁入（夫妻户为主，兼有单身）→ 适龄婚配 → 生育 → 交友；
/// 每月——老化 → 死亡 → 无家处理/迁出。
/// 概率均为「日频」直接取值：1x 下一游戏日 ≈ 20 现实秒、一游戏月 ≈ 10 现实分钟，
/// 故迁入/婚育以「日」为单位小幅调参，约一游戏年（2 现实小时）内可把开局坊区填满。
/// 只操作数据层，不涉及任何表现节点。
/// </summary>
public class LifecycleSystem
{
    // 高频引用的短名转发（调参集中在 configs/PopulationConfig）
    private static float CoupleChancePerDay => PopulationConfig.CoupleChancePerDay;
    private static float SingleChancePerDay => PopulationConfig.SingleChancePerDay;
    private static float MarriageChancePerDay => PopulationConfig.MarriageChancePerDay;
    private static float BirthChancePerDay => PopulationConfig.BirthChancePerDay;
    private static float FriendChancePerDay => PopulationConfig.FriendChancePerDay;
    private static int EmigrateAfterHomelessMonths => PopulationConfig.EmigrateAfterHomelessMonths;
    private static float CrowdEventChance => PopulationConfig.CrowdEventChance;

    private readonly Random _rng = new();

    /// <summary>每日：迁入/婚配/生育/交友（日频概率）。</summary>
    public void TickDay(GameState gs)
    {
        Immigration(gs);
        Marriages(gs);
        Births(gs);
        MakeFriends(gs);
    }

    /// <summary>每月：老化/死亡/无家处理/住房拥挤调整。</summary>
    public void TickMonth(GameState gs)
    {
        Age(gs);
        Deaths(gs);
        HandleHomeless(gs);
        ResolveHousing(gs);
    }

    private static void Age(GameState gs)
    {
        foreach (var c in gs.Citizens.Values)
            c.AgeMonths++;
    }

    private void Deaths(GameState gs)
    {
        var dead = new List<int>();
        bool famine = gs.Food <= 0;

        foreach (var c in gs.Citizens.Values)
            if (_rng.NextDouble() < MonthlyDeathChance(c, famine))
                dead.Add(c.Id);

        foreach (var id in dead)
        {
            // 公告栏播报：先取名字年龄再移除（孩童报夭折，成年报享年）
            if (gs.Citizens.TryGetValue(id, out var c))
                gs.PostNews("death", c.IsChild
                    ? $"{c.Name}不幸夭折，年仅 {c.AgeYears} 岁"
                    : $"{c.Name}离世，享年 {c.AgeYears} 岁");
            gs.RemoveCitizen(id);
        }
    }

    /// <summary>月死亡概率：公式与曲线参数均在 LifeConfig（Gompertz 随龄上升，约 55-65 为主要死亡区间），
    /// 达最大寿数必亡；健康值放大死亡率（当前恒满为中性）；饥荒额外加压。</summary>
    private static float MonthlyDeathChance(Citizen c, bool famine)
    {
        int age = c.AgeYears;
        if (age >= LifeConfig.MaxAgeYears)
            return 1f; // 寿数已尽，必亡

        double annual = LifeConfig.AnnualMortalityAt(age) * LifeConfig.HealthMortalityFactor(c.Health);
        double monthly = LifeConfig.MonthlyFromAnnual(annual);
        if (famine)
            monthly += LifeConfig.FamineMonthlyDeathBonus;
        return (float)monthly;
    }

    /// <summary>住所被拆或从未有住所：按家庭分组整家迁入同一空宅（一宅一家，不再逐人塞进别家空床），
    /// 找不到累计计时，过久则迁出。</summary>
    private void HandleHomeless(GameState gs)
    {
        foreach (var c in gs.Citizens.Values)
            if (c.HomeId >= 0 && !gs.Buildings.ContainsKey(c.HomeId))
                c.HomeId = -1;

        var occupancy = gs.BuildHomeOccupancy();
        var homeless = gs.Citizens.Values.Where(c => c.HomeId < 0).ToList();

        // 按家庭分组（无家庭者以负自身 Id 单人成组）：整家同进同一宅
        foreach (var group in homeless.GroupBy(c => c.FamilyId >= 0 ? c.FamilyId : -1000000 - c.Id))
        {
            var members = group.ToList();
            // 优先找床位够整家的空宅；实在没有就挤进任意空宅（超员日后由拥挤事件扩建/疏解）
            var house = FindEmptyHouse(gs, occupancy, members.Count)
                        ?? FindEmptyHouse(gs, occupancy, 1);
            if (house != null)
            {
                house.Abandoned = false;
                foreach (var c in members)
                {
                    MoveIn(gs, c, house.Id, occupancy);
                    c.HomelessMonths = 0;
                    gs.LogLifeEvent(c, "迁居新宅"); // 失宅后觅得新居
                }
            }
            else
            {
                foreach (var c in members)
                    c.HomelessMonths++;
            }
        }

        foreach (var c in homeless.Where(c => c.HomelessMonths > EmigrateAfterHomelessMonths).ToList())
        {
            gs.PostNews("migrate-out", $"{c.Name}久觅居所不得，携家迁离汴京"); // 公告栏播报迁出
            // 带上未成年子女一同迁出，不留孤幼滞留城中
            foreach (var childId in c.ChildrenIds.ToList())
                if (gs.Citizens.TryGetValue(childId, out var child) && child.IsChild)
                    gs.RemoveCitizen(childId);
            gs.RemoveCitizen(c.Id);
        }
    }

    /// <summary>迁入：夫妻户自带随机资产入城，必自建房（无合法落位/买不起则不迁）；
    /// 单身流民资产够则自建，否则寄居有居住空位的工坊/商铺当暂住雇工（店满则不迁）。</summary>
    private void Immigration(GameState gs)
    {
        var occupancy = gs.BuildHomeOccupancy();

        if (_rng.NextDouble() < CoupleChancePerDay)
        {
            double assets = RandomImmigrantAssets();
            if (ZoneGrowthSystem.TryBuildHouse(gs, assets, out var house, out double cost))
                SpawnCouple(gs, house, occupancy, assets - cost);
            // 无合法落位 / 买不起：本轮不迁入
        }

        if (_rng.NextDouble() < SingleChancePerDay)
        {
            double assets = RandomImmigrantAssets();
            if (assets >= ImmigrationConfig.SelfBuildAssets
                && ZoneGrowthSystem.TryBuildHouse(gs, assets, out var house, out double cost))
            {
                SpawnSingle(gs, house, occupancy, assets - cost, false);
            }
            else
            {
                // 寄居有居住空位的工坊/商铺（占 1 居住位当暂住雇工，有空岗位则同时受雇）；店满则不迁
                var host = FindLodging(gs);
                if (host != null)
                    SpawnSingle(gs, host, occupancy, assets, true);
            }
        }
    }

    /// <summary>迁入者随机自带资产（家庭公产初值）。</summary>
    private double RandomImmigrantAssets()
        => ImmigrationConfig.AssetsMin
           + _rng.NextDouble() * (ImmigrationConfig.AssetsMax - ImmigrationConfig.AssetsMin);

    /// <summary>找可寄居的工坊/商铺（居住格未满，BuildingOccupancy &lt; 容量）；无则 null。</summary>
    private static BuildingInstance FindLodging(GameState gs)
    {
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "grown" || b.Def.Id == "house")
                continue;
            if (gs.BuildingOccupancy(b) < b.HousingCapacity)
                return b;
        }
        return null;
    }

    private void SpawnCouple(GameState gs, BuildingInstance house, Dictionary<int, int> occupancy, double assets)
    {
        var family = gs.AddFamily(new Family { HomeId = house.Id, SharedAssets = Math.Max(0, assets) });

        var husband = NewAdult(gs, Gender.Male);
        var wife = NewAdult(gs, Gender.Female);
        husband.SpouseId = wife.Id;
        wife.SpouseId = husband.Id;

        house.Abandoned = false;
        foreach (var c in new[] { husband, wife })
        {
            c.FamilyId = family.Id;
            family.MemberIds.Add(c.Id);
            MoveIn(gs, c, house.Id, occupancy);
            gs.LogLifeEvent(c, "携眷迁入汴京，自建新宅");
        }
        gs.PostNews("migrate-in", $"{husband.Name}携眷迁入汴京，自建新宅安家"); // 公告栏播报迁入
    }

    private void SpawnSingle(GameState gs, BuildingInstance house, Dictionary<int, int> occupancy, double assets, bool lodger)
    {
        var single = NewAdult(gs, _rng.NextDouble() < PopulationConfig.SingleMaleChance ? Gender.Male : Gender.Female);
        var family = gs.AddFamily(new Family { HomeId = house.Id, SharedAssets = Math.Max(0, assets) });
        single.FamilyId = family.Id;
        family.MemberIds.Add(single.Id);
        house.Abandoned = false;
        MoveIn(gs, single, house.Id, occupancy);
        gs.LogLifeEvent(single, lodger ? "寄居店坊，暂谋生计" : "只身迁入汴京，自建新宅");
        gs.PostNews("migrate-in", lodger
            ? $"{single.Name}迁入汴京，暂寄居{house.Def.Name}"
            : $"{single.Name}只身迁入汴京，自建新宅"); // 公告栏播报迁入

        // 寄居者：若寄居的店坊有空岗位，顺带受雇（所占居住位与工作位为同一格）
        if (lodger && house.Def.JobSlots > 0 && gs.StaffOf(house).Count < house.Def.JobSlots)
        {
            single.JobKind = JobKind.Employed;
            single.WorkplaceId = house.Id;
        }
    }

    private Citizen NewAdult(GameState gs, Gender gender)
    {
        var (surname, fullName) = NameGenerator.NewName(gender);
        return gs.AddCitizen(new Citizen
        {
            Surname = surname,
            Name = fullName,
            Gender = gender,
            // 迁入成人的年龄与随身私产：区间取自 ImmigrationConfig（钱为整数贯，同旧版分布）
            AgeMonths = (ImmigrationConfig.ArriveAgeMin + _rng.Next(ImmigrationConfig.ArriveAgeSpan)) * 12 + _rng.Next(12),
            Money = ImmigrationConfig.ArriveMoneyMin + _rng.Next((int)ImmigrationConfig.ArriveMoneySpan),
        });
    }

    /// <summary>适龄单身青年婚配：合并入丈夫家庭，同住容量允许的一方住所。</summary>
    private void Marriages(GameState gs)
    {
        var singleMen = gs.Citizens.Values
            .Where(c => c.Gender == Gender.Male && !c.IsMarried && c.AgeYears >= LifeConfig.AdultAgeYears && c.AgeYears < LifeConfig.MarriageMaxAgeYears).ToList();
        var singleWomen = gs.Citizens.Values
            .Where(c => c.Gender == Gender.Female && !c.IsMarried && c.AgeYears >= LifeConfig.AdultAgeYears && c.AgeYears < LifeConfig.MarriageMaxAgeYears).ToList();

        var occupancy = gs.BuildHomeOccupancy();

        foreach (var man in singleMen)
        {
            if (singleWomen.Count == 0)
                break;
            if (_rng.NextDouble() >= MarriageChancePerDay)
                continue;

            // 排除近亲：同家庭、直系、同胞均不得婚配（抽满候选数无合适对象则本日作罢）
            Citizen woman = null;
            for (int attempt = 0; attempt < PopulationConfig.MarriageTryCandidates && singleWomen.Count > 0; attempt++)
            {
                var candidate = singleWomen[_rng.Next(singleWomen.Count)];
                if (CloseKin(man, candidate))
                    continue;
                woman = candidate;
                break;
            }
            if (woman == null)
                continue;

            singleWomen.Remove(woman);
            Marry(gs, man, woman, occupancy);
        }
    }

    /// <summary>是否近亲：同家庭 / 父女母子 / 同父或同母。</summary>
    private static bool CloseKin(Citizen a, Citizen b)
    {
        if (a.FamilyId >= 0 && a.FamilyId == b.FamilyId)
            return true;
        if (a.ChildrenIds.Contains(b.Id) || b.ChildrenIds.Contains(a.Id))
            return true;
        if ((a.FatherId >= 0 && a.FatherId == b.FatherId) || (a.MotherId >= 0 && a.MotherId == b.MotherId))
            return true;
        return false;
    }

    /// <summary>婚配（以住宅为前置）：男方有自有住宅（居于 house）且有空位→迎妻入本宅；
    /// 否则（次子随父母、寄居店坊、本宅已满）须当场建新宅才成婚；建不起则本轮不婚（保持单身）。</summary>
    private void Marry(GameState gs, Citizen man, Citizen woman, Dictionary<int, int> occupancy)
    {
        bool manInOwnHouse = man.HomeId >= 0 && gs.Buildings.TryGetValue(man.HomeId, out var manHouse)
            && manHouse.Def.Id == "house";
        bool nextSonWithParents = !IsEldestSon(gs, man) && LivesWithParents(gs, man);
    
        // 情形一：男方已有可容妻的自有住宅（非次子随父母，且尚有空位）→ 迎妻入本宅
        if (manInOwnHouse && !nextSonWithParents
            && gs.HouseVacancy(gs.Buildings[man.HomeId], occupancy) >= 1)
        {
            BindSpouses(gs, man, woman);
            MergeWifeIntoHusband(gs, man, woman, occupancy);
            return;
        }
    
        // 情形二：需另建新宅（次子随父母、寄居店坊、或本宅已满）——建得起才成婚
        double budget = MarriageBudget(gs, man, woman);
        if (ZoneGrowthSystem.TryBuildHouse(gs, budget, out var newHome, out double cost))
        {
            BindSpouses(gs, man, woman);
            MarryIntoNewHome(gs, man, woman, newHome, occupancy, cost);
        }
        // 建不成：本轮不成婚（保持单身，下次再试）
    }
    
    /// <summary>结为夫妻：互记配偶并各记一笔成婚履历（Name 已含姓，不再叠加 Surname）。</summary>
    private static void BindSpouses(GameState gs, Citizen man, Citizen woman)
    {
        man.SpouseId = woman.Id;
        woman.SpouseId = man.Id;
        gs.LogLifeEvent(man, $"与 {woman.Name} 成婚");
        gs.LogLifeEvent(woman, $"与 {man.Name} 成婚");
    }
    
    /// <summary>妻子并入丈夫家庭（丈夫无家庭则新建），携嫁妝入住丈夫自有住宅。</summary>
    private void MergeWifeIntoHusband(GameState gs, Citizen man, Citizen woman, Dictionary<int, int> occupancy)
    {
        if (!gs.Families.TryGetValue(man.FamilyId, out var family))
        {
            family = gs.AddFamily(new Family { HomeId = man.HomeId });
            man.FamilyId = family.Id;
            family.MemberIds.Add(man.Id);
        }
        family.SharedAssets += DetachFromFamily(gs, woman); // 嫁妝：妻子从娘家分得的一份
        woman.FamilyId = family.Id;
        family.MemberIds.Add(woman.Id);
        MoveIn(gs, woman, man.HomeId, occupancy);
        family.HomeId = man.HomeId;
    }
    
    /// <summary>成婚建房预算：夫妻各自家庭公产份额估算 + 私产（非破坏性估算，供 TryBuildHouse 判断）。</summary>
    private static double MarriageBudget(GameState gs, Citizen man, Citizen woman)
        => SharedShare(gs, man) + man.Money + SharedShare(gs, woman) + woman.Money;
    
    /// <summary>家庭公产的人均份额（非破坏性估算，不改动家庭）。</summary>
    private static double SharedShare(GameState gs, Citizen c)
        => gs.Families.TryGetValue(c.FamilyId, out var fam) && fam.MemberIds.Count > 0
            ? fam.SharedAssets / fam.MemberIds.Count : 0;
    
    /// <summary>婚后另立门户：夫妻各从原家庭分得一份（分产 + 嫁妝），扣除房款后迁入自建新宅成立新家庭。</summary>
    private void MarryIntoNewHome(GameState gs, Citizen man, Citizen woman, BuildingInstance newHome, Dictionary<int, int> occupancy, double cost)
    {
        double portion = DetachFromFamily(gs, man);  // 男方分得的家产份额
        double dowry = DetachFromFamily(gs, woman);   // 妻子嫁妝
        double pooled = portion + dowry - cost;       // 公产先抵房款
        if (pooled < 0)
        {
            // 公产不够补房款：从二人私产找补
            double deficit = -pooled;
            double fromMan = Math.Min(Math.Max(0, man.Money), deficit); man.Money -= fromMan; deficit -= fromMan;
            double fromWoman = Math.Min(Math.Max(0, woman.Money), deficit); woman.Money -= fromWoman;
            pooled = 0;
        }
        var fam = gs.AddFamily(new Family { HomeId = newHome.Id, SharedAssets = pooled });
        man.FamilyId = fam.Id;
        woman.FamilyId = fam.Id;
        fam.MemberIds.Add(man.Id);
        fam.MemberIds.Add(woman.Id);
        newHome.Abandoned = false;
        MoveIn(gs, man, newHome.Id, occupancy);
        MoveIn(gs, woman, newHome.Id, occupancy);
        gs.LogLifeEvent(man, "成婚分家，自建新宅");
    }

    /// <summary>从所在家庭剥离一名成员，返回其按人均分得的家产份额（家庭空则解散）。</summary>
    private static double DetachFromFamily(GameState gs, Citizen c)
    {
        if (!gs.Families.TryGetValue(c.FamilyId, out var fam))
            return 0;
        fam.MemberIds.Remove(c.Id);
        double share = fam.SharedAssets / Math.Max(1, fam.MemberIds.Count + 1);
        fam.SharedAssets -= share;
        if (fam.MemberIds.Count == 0)
            gs.Families.Remove(fam.Id);
        return share;
    }

    /// <summary>是否为长子（继承人）：在同父或同母的男性兄弟中年龄最长者；
    /// 无父母记录者（迁入/开基）视为自立门户，返回 true。</summary>
    private static bool IsEldestSon(GameState gs, Citizen c)
    {
        if (c.Gender != Gender.Male)
            return false;
        if (c.FatherId < 0 && c.MotherId < 0)
            return true;
        foreach (var other in gs.Citizens.Values)
        {
            if (other.Id == c.Id || other.Gender != Gender.Male || !AreBrothers(c, other))
                continue;
            if (other.AgeMonths > c.AgeMonths)
                return false; // 有更年长的兄弟：本人非长子
            if (other.AgeMonths == c.AgeMonths && other.Id < c.Id)
                return false; // 同龄（如双生）以 Id 定长幼
        }
        return true;
    }

    /// <summary>同父或同母即为兄弟（同一家庭的男性子嗣，用于长幼排序）。</summary>
    private static bool AreBrothers(Citizen a, Citizen b)
        => (a.FatherId >= 0 && a.FatherId == b.FatherId)
        || (a.MotherId >= 0 && a.MotherId == b.MotherId);

    /// <summary>是否仍与父母同户（父或母在世且与本人同家庭）：判断“次子婚后应否搬离本家”。</summary>
    private static bool LivesWithParents(GameState gs, Citizen c)
    {
        if (c.FatherId >= 0 && gs.Citizens.TryGetValue(c.FatherId, out var f) && f.FamilyId == c.FamilyId)
            return true;
        if (c.MotherId >= 0 && gs.Citizens.TryGetValue(c.MotherId, out var m) && m.FamilyId == c.FamilyId)
            return true;
        return false;
    }

    private void Births(GameState gs)
    {
        var occupancy = gs.BuildHomeOccupancy();
        var mothers = gs.Citizens.Values
            .Where(c => c.Gender == Gender.Female && c.IsMarried && c.AgeYears >= LifeConfig.FertileMinAgeYears && c.AgeYears <= LifeConfig.FertileMaxAgeYears && c.HomeId >= 0)
            .ToList();

        foreach (var mother in mothers)
        {
            if (_rng.NextDouble() >= BirthProbability(gs, mother))
                continue;
            // 生育以自有住宅（house）为前置（寄居店坊/无家不生）；容量封顶倍率内略超，超员由拥挤事件扩建/分家疏解
            if (!gs.Buildings.TryGetValue(mother.HomeId, out var house) || house.Def.Id != "house"
                || occupancy.GetValueOrDefault(house.Id) >= (int)Math.Ceiling(house.HousingCapacity * PopulationConfig.BirthOverCapFactor))
                continue;
            if (!gs.Citizens.TryGetValue(mother.SpouseId, out var father))
                continue;

            var gender = _rng.NextDouble() < 0.5 ? Gender.Male : Gender.Female;
            var child = gs.AddCitizen(new Citizen
            {
                Surname = father.Surname,
                Name = father.Surname + NameGenerator.GivenName(gender),
                Gender = gender,
                AgeMonths = 0,
                FatherId = father.Id,
                MotherId = mother.Id,
                FamilyId = mother.FamilyId,
            });
            father.ChildrenIds.Add(child.Id);
            mother.ChildrenIds.Add(child.Id);
            if (gs.Families.TryGetValue(mother.FamilyId, out var family))
                family.MemberIds.Add(child.Id);
            MoveIn(gs, child, mother.HomeId, occupancy);

            // 孩子记出生，父母各记得子/得女，公告栏同步播报
            gs.LogLifeEvent(child, $"生于{HomeName(gs, mother.HomeId)}");
            string kidWord = gender == Gender.Male ? "得子" : "得女";
            gs.LogLifeEvent(father, $"{kidWord} {child.Name}");
            gs.LogLifeEvent(mother, $"{kidWord} {child.Name}");
            gs.PostNews("birth", $"{father.Name}家{kidWord}：{child.Name}");
        }
    }

    /// <summary>生育日概率：1~3 胎最大，之后随子女数递减；第五胎后再结合母亲年龄与家庭富裕程度进一步抑制，
    /// 但始终 &gt;0（仍有几率突破五个）；各胎次/年龄/富裕系数均取自 PopulationConfig。</summary>
    private static double BirthProbability(GameState gs, Citizen mother)
    {
        int kids = mother.ChildrenIds.Count;
        double modifier = 1.0;
        if (kids >= 4) // 第五胎（及以后）才结合年龄与富裕程度
            modifier = PopulationConfig.BirthAgeFactor(mother.AgeYears)
                * PopulationConfig.BirthWealthFactor(FamilyPerCapitaAssets(gs, mother));
        return BirthChancePerDay * PopulationConfig.BirthCountFactor(kids) * modifier;
    }

    /// <summary>家庭人均资产（公产+成员私产 / 人数），用于生育富裕度修正。</summary>
    private static double FamilyPerCapitaAssets(GameState gs, Citizen c)
    {
        if (gs.Families.TryGetValue(c.FamilyId, out var fam) && fam.MemberIds.Count > 0)
            return fam.TotalAssets(gs) / fam.MemberIds.Count;
        return c.Money;
    }

    /// <summary>简版社交：成年人小概率结识新朋友（为后续人物交互留接口）。</summary>
    private void MakeFriends(GameState gs)
    {
        var adults = gs.Citizens.Values.Where(c => !c.IsChild && c.FriendIds.Count < PopulationConfig.MaxFriends).ToList();
        if (adults.Count < 2)
            return;

        foreach (var c in adults)
        {
            if (_rng.NextDouble() >= FriendChancePerDay)
                continue;
            var other = adults[_rng.Next(adults.Count)];
            if (other.Id == c.Id || c.FriendIds.Contains(other.Id))
                continue;
            c.FriendIds.Add(other.Id);
            other.FriendIds.Add(c.Id);
        }
    }

    // ---- 住房拥挤 ----

    /// <summary>住房拥挤调整（每月，概率事件）：先标记/清除废弃屋；住满或超员的住户按概率处理——
    /// 先尝试扩地增容（房体变大占地格数即容量，上限 8×8 米）；扩不动则分家：
    /// 有成年未婚男先自建新宅搬出，否则（仍超员时）任挑一名成年住户自建新宅迁出（建不起则本轮不分）；
    /// 另：寄居店坊的家庭攒够钱则自建新宅携家搬入。</summary>
    private void ResolveHousing(GameState gs)
    {
        var occupancy = gs.BuildHomeOccupancy();
    
        // 废弃标志：无人居住的 grown 建筑挂牌（可被新居民重建入住，或后期邻居合并扩建）
        foreach (var b in gs.Buildings.Values)
            if (b.Def.Category == "grown")
                b.Abandoned = occupancy.GetValueOrDefault(b.Id) == 0;
    
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "grown")
                continue;
            int occ = occupancy.GetValueOrDefault(b.Id);
            if (occ < b.HousingCapacity) // 未满不处理
                continue;
            if (_rng.NextDouble() >= CrowdEventChance)
                continue;
    
            // 优先扩建：向邻接坊区空地扩一条带，房体随之变大、占地格数（容量）增加
            if (ZoneGrowthSystem.TryExpandHouse(gs, b))
                continue; // TryExpandHouse 内部已广播 MapChanged
    
            // 扩不动：成年未婚男自建新宅另立门户（建不起则本轮不分）
            var male = FindAdultUnmarriedMale(gs, b);
            if (male != null)
            {
                TryLeaveAndBuild(gs, male, occupancy);
                continue;
            }
    
            // 超员（如拆路使容量回落）：任挑一名成年住户自建新宅迁出（一家一家），一月至多一人逐步疏解
            if (occ > b.HousingCapacity)
            {
                var mover = FindAdultResident(gs, b);
                if (mover != null)
                    TryLeaveAndBuild(gs, mover, occupancy);
            }
        }
    
        // 赚钱自建宅：寄居工坊/商铺的家庭人均资产≥自建门槛且有落位 → 建房携家搬入
        BuildUpFromLodging(gs, occupancy);
    }
    
    /// <summary>尝试一名住户自建新宅另立门户：以其家庭人均公产 + 私产为预算，建得起才迁出（否则本轮不分）。</summary>
    private void TryLeaveAndBuild(GameState gs, Citizen c, Dictionary<int, int> occupancy)
    {
        double budget = SharedShare(gs, c) + Math.Max(0, c.Money);
        if (ZoneGrowthSystem.TryBuildHouse(gs, budget, out var newHome, out double cost))
            LeaveForNewHome(gs, c, newHome, occupancy, cost);
    }
    
    /// <summary>赚钱自建宅：寄居工坊/商铺的家庭（非 house 居所）人均资产≥自建门槛且有落位时，建房携家搬入。</summary>
    private void BuildUpFromLodging(GameState gs, Dictionary<int, int> occupancy)
    {
        var seen = new HashSet<int>();
        foreach (var c in gs.Citizens.Values.ToList())
        {
            if (c.IsChild || c.HomeId < 0 || !gs.Buildings.TryGetValue(c.HomeId, out var home))
                continue;
            if (home.Def.Category != "grown" || home.Def.Id == "house")
                continue; // 只处理寄居工坊/商铺者
            if (c.FamilyId >= 0 && !seen.Add(c.FamilyId))
                continue; // 同家庭只处理一次
            double budget = FamilyPerCapitaAssets(gs, c);
            if (budget < ImmigrationConfig.SelfBuildAssets)
                continue;
            if (ZoneGrowthSystem.TryBuildHouse(gs, budget, out var house, out double cost))
                MoveFamilyToNewHouse(gs, c, house, occupancy, cost);
        }
    }
    
    /// <summary>一家携入自建新宅：扣除房款后全家从旧居迁入新宅（无家庭则单人搬入）。</summary>
    private void MoveFamilyToNewHouse(GameState gs, Citizen anyMember, BuildingInstance house, Dictionary<int, int> occupancy, double cost)
    {
        house.Abandoned = false;
        if (gs.Families.TryGetValue(anyMember.FamilyId, out var fam))
        {
            fam.SharedAssets = Math.Max(0, fam.SharedAssets - cost);
            fam.HomeId = house.Id;
            foreach (var id in fam.MemberIds.ToList())
                if (gs.Citizens.TryGetValue(id, out var m))
                {
                    if (occupancy.ContainsKey(m.HomeId))
                        occupancy[m.HomeId]--;
                    MoveIn(gs, m, house.Id, occupancy);
                }
            gs.LogLifeEvent(anyMember, "积蓄自建新宅，迁离寄居");
        }
        else
        {
            if (occupancy.ContainsKey(anyMember.HomeId))
                occupancy[anyMember.HomeId]--;
            MoveIn(gs, anyMember, house.Id, occupancy);
            gs.LogLifeEvent(anyMember, "积蓄自建新宅，迁离寄居");
        }
    }

    /// <summary>住户中的成年未婚男（有则触发“独立门户搬出”）：优先迁出次子，长子（继承人）留守祖宅；
    /// 实在只剩长子时再退而求其次。</summary>
    private static Citizen FindAdultUnmarriedMale(GameState gs, BuildingInstance home)
    {
        Citizen heir = null;
        foreach (var c in gs.Citizens.Values)
        {
            if (c.HomeId != home.Id || c.Gender != Gender.Male || !c.IsAdult || c.IsMarried)
                continue;
            if (IsEldestSon(gs, c))
            {
                heir ??= c; // 长子作兑底，尽量不迁
                continue;
            }
            return c;
        }
        return heir;
    }

    /// <summary>住户中任一成年人（超员疏解搬家用，不拆未成年人离家）。</summary>
    private static Citizen FindAdultResident(GameState gs, BuildingInstance home)
    {
        foreach (var c in gs.Citizens.Values)
            if (c.HomeId == home.Id && c.IsAdult)
                return c;
        return null;
    }

    /// <summary>分家：脱离原家庭、扣除房款、迁入自建新居、成立自己的家庭（后续婚配成家）。</summary>
    private void LeaveForNewHome(GameState gs, Citizen c, BuildingInstance newHome, Dictionary<int, int> occupancy, double cost)
    {
        if (gs.Families.TryGetValue(c.FamilyId, out var old))
        {
            old.MemberIds.Remove(c.Id);
            if (old.MemberIds.Count == 0)
                gs.Families.Remove(old.Id);
        }
        if (occupancy.ContainsKey(c.HomeId))
            occupancy[c.HomeId]--;

        c.Money = Math.Max(0, c.Money - cost); // 房款从个人私产支付
        var fam = gs.AddFamily(new Family { HomeId = newHome.Id, SharedAssets = PopulationConfig.SplitFamilyAssets });
        c.FamilyId = fam.Id;
        fam.MemberIds.Add(c.Id);
        newHome.Abandoned = false;
        MoveIn(gs, c, newHome.Id, occupancy);
        gs.LogLifeEvent(c, "成年分家，自建新宅");
    }

    // ---- 工具 ----

    /// <summary>住宅名（出生履历用）：建筑已失则笼统称“家中”。</summary>
    private static string HomeName(GameState gs, int homeId) =>
        gs.Buildings.TryGetValue(homeId, out var b) ? $"{b.Def.Name}（{b.X},{b.Y}）" : "家中";

    /// <summary>找空宅（无人居住的可住建筑，床位≥needBeds）：一宅一家制下所有迁入/另立门户的唯一入口，
    /// 民居优先，其次前店后宅/工坊宿舍；excludeId 排除自身。</summary>
    private static BuildingInstance FindEmptyHouse(GameState gs, Dictionary<int, int> occupancy, int needBeds, int excludeId = -1)
    {
        BuildingInstance fallback = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Id == excludeId)
                continue;
            if (b.HousingCapacity < needBeds || occupancy.GetValueOrDefault(b.Id) > 0)
                continue; // 有人住的宅不收外人（一宅一家）
            if (b.Def.Id == "house")
                return b;
            fallback ??= b;
        }
        return fallback;
    }

    private static void MoveIn(GameState gs, Citizen c, int houseId, Dictionary<int, int> occupancy)
    {
        c.HomeId = houseId;
        c.HomelessMonths = 0;
        occupancy[houseId] = occupancy.GetValueOrDefault(houseId) + 1;
    }
}
