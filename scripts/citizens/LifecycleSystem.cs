using System;
using System.Collections.Generic;
using System.Linq;

namespace Bianjing;

/// <summary>
/// 居民生命周期系统：
/// 每旬——迁入（需求 §2.2 四类流民模型）→ 适龄婚配 → 生育 → 交友；
/// 每月——老化 → 死亡 → 无家处理/迁出。
/// 概率均为「旬频」直接取值：时间口径见 TimeConfig（一游戏旬 = 1 现实分钟、一游戏月 ≈ 3 现实分钟），
/// 故迁入/婚育以「旬」为单位小幅调参，约一游戏年（36 现实分钟）内可把开局坊区填满。
/// 只操作数据层，不涉及任何表现节点。
/// </summary>
public class LifecycleSystem
{
    // 高频引用的短名转发（调参集中在 configs/PopulationConfig）
    private static float ImmigrationChancePerDay => PopulationConfig.ImmigrationChancePerDay;
    private static float MarriageChancePerDay => PopulationConfig.MarriageChancePerDay;
    private static float BirthChancePerDay => PopulationConfig.BirthChancePerDay;
    private static float FriendChancePerDay => PopulationConfig.FriendChancePerDay;
    private static int EmigrateAfterHomelessMonths => PopulationConfig.EmigrateAfterHomelessMonths;
    private static float CrowdEventChance => PopulationConfig.CrowdEventChance;

    private readonly Random _rng = new();

    /// <summary>每旬：迁入/婚配/生育/交友（旬频概率）。</summary>
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
        {
            c.AgeMonths++;
            // 批次九十一：一年两岁——1 月与 7 月各增一岁（岁数不再由月龄整除派生）
            if (gs.CurMonth == 1 || gs.CurMonth == 7)
                c.AgeYears++;
        }
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
            // 公告栏播报：先取名字年龄再移除（批次七十七：按死因区分——寿终/饥馑/病故/夭折）
            if (gs.Citizens.TryGetValue(id, out var c))
            {
                string cause;
                if (c.AgeYears >= LifeConfig.MaxAgeYears)
                    cause = "寿终正寝"; // 达最大寿数，必亡
                else if (famine)
                    cause = "饥馑饿毙"; // 官粮见底：饥荒是首要死因，先于其他分类
                else if (c.IsChild)
                    cause = "不幸夭折";
                else if (c.AgeYears >= LifeConfig.ElderAgeYears)
                    cause = "寿终正寝";
                else
                    cause = "病故";
                gs.PostNews("death", c.IsChild
                    ? $"{c.Name}{cause}，年仅 {c.AgeYears} 岁"
                    : $"{c.Name}{cause}，享年 {c.AgeYears} 岁");
            }
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

        // 迁出公告按户去重：整户（夫妻/多名成年成员）同月迁出只报一条，免公告栏刷屏
        var reported = new HashSet<int>();
        foreach (var c in homeless.Where(c => c.HomelessMonths > EmigrateAfterHomelessMonths).ToList())
        {
            int famKey = c.FamilyId >= 0 ? c.FamilyId : -1000000 - c.Id;
            if (reported.Add(famKey))
                gs.PostNews("migrate-out", $"{c.Name}久觅居所不得，携家迁离汴京"); // 公告栏播报迁出
            // 带上未成年子女一同迁出，不留孤幼滞留城中
            foreach (var childId in c.ChildrenIds.ToList())
                if (gs.Citizens.TryGetValue(childId, out var child) && child.IsChild)
                    gs.RemoveCitizen(childId);
            gs.RemoveCitizen(c.Id);
        }
    }

    /// <summary>迁入（需求 §2.2 四类流民 + §8.1 流民营启动链）：每旬一次事件，按权重抽一类流民寄居；
    /// 人口税开启时流入停滞（§4.4）；须有可寄居处（流民营优先，其次有居住空位的店坊）才成行——
    /// 流民现金买不起地（§8.2），先落脚就业攒钱，再由 BuildUpFromLodging 攒够自建迁出。</summary>
    private void Immigration(GameState gs)
    {
        if (gs.Taxes.PollTaxEnabled)
            return; // 人口税开启：流入停滞（需求 §4.4）
        if (_rng.NextDouble() >= ImmigrationChancePerDay)
            return;
        var occupancy = gs.BuildHomeOccupancy();
        var host = FindLodging(gs);
        if (host == null)
        {
            gs.PostNews("migrate-in", "流民欲入城安置，但城中尚无流民营可接纳（建设「流民营」后迁入者抵达）");
            return;
        }
        SpawnImmigrant(gs, host, occupancy);
    }

    /// <summary>按权重抽一类流民（归一化：归民最多，客士极少）。</summary>
    private PopulationConfig.ImmigrantType RollImmigrantType()
    {
        double roll = _rng.NextDouble();
        if (roll < PopulationConfig.ImmigrantWeightSettler)
            return PopulationConfig.ImmigrantType.Settler;
        roll -= PopulationConfig.ImmigrantWeightSettler;
        if (roll < PopulationConfig.ImmigrantWeightMerchant)
            return PopulationConfig.ImmigrantType.Merchant;
        roll -= PopulationConfig.ImmigrantWeightMerchant;
        if (roll < PopulationConfig.ImmigrantWeightSoldier)
            return PopulationConfig.ImmigrantType.Soldier;
        return PopulationConfig.ImmigrantType.Scholar;
    }

    /// <summary>流民随身现金（按类型区间随机，文）。</summary>
    private long RandomImmigrantAssets(PopulationConfig.ImmigrantType type)
        => PopulationConfig.AssetsMinOf(type)
           + (long)(_rng.NextDouble() * (PopulationConfig.AssetsMaxOf(type) - PopulationConfig.AssetsMinOf(type)));

    /// <summary>找可寄居处：流民营优先（需求 §8.1 安置主通道，纯住宿无岗位），
    /// 其次有居住空位的店坊（前店后宅当暂住雇工）；均无则 null。</summary>
    private static BuildingInstance FindLodging(GameState gs)
    {
        BuildingInstance fallback = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.HousingCapacity <= 0 || gs.BuildingOccupancy(b) >= b.HousingCapacity)
                continue;
            if (b.Def.Id == "refugee_camp")
                return b;
            if (b.Def.Category == "grown" && b.Def.Id != "house")
                fallback ??= b;
        }
        return fallback;
    }

    /// <summary>落一名流民寄居（需求 §2.2）：按类型定随身现金/技能/携带物，携带物可变卖折入资产；
    /// 寄居处有空岗位则顺带受雇（流民营无岗位，次日由 JobSystem 寻城职）。</summary>
    private void SpawnImmigrant(GameState gs, BuildingInstance host, Dictionary<int, int> occupancy)
    {
        var type = RollImmigrantType();
        long assets = RandomImmigrantAssets(type);
        string carried = PopulationConfig.CarriedOf(type);
        if (carried != null)
            assets += Goods.PriceOf(carried); // 携带物价值折入资产（可变卖）
        var immigrant = NewAdult(gs, _rng.NextDouble() < PopulationConfig.SingleMaleChance ? Gender.Male : Gender.Female);
        var skill = PopulationConfig.SkillOf(type);
        // 归民本无类型技能，但务农流民带农艺、工匠流民带手艺（耕种/工坊创业主力，概率见 SettlerFarmChance/SettlerCraftChance）
        if (skill == SkillType.None)
        {
            if (_rng.NextDouble() < PopulationConfig.SettlerFarmChance)
                skill = SkillType.Farming;
            else if (_rng.NextDouble() < PopulationConfig.SettlerCraftChance)
                skill = SkillType.Craft;
        }
        immigrant.Skill = skill;
        // 迁入技能点随机：按类型区间抽经验初值（批次六十五，熟练=200/高级=600 见 EconomyConfig）
        immigrant.SkillExp = PopulationConfig.SkillExpMinOf(skill)
            + (float)_rng.NextDouble() * PopulationConfig.SkillExpSpanOf(skill);
        if (carried != null)
            immigrant.CarriedItems.Add(carried);
        var family = gs.AddFamily(new Family { HomeId = host.Id, SharedAssets = Math.Max(0, assets) });
        immigrant.FamilyId = family.Id;
        family.MemberIds.Add(immigrant.Id);
        host.Abandoned = false;
        MoveIn(gs, immigrant, host.Id, occupancy);
        gs.LogLifeEvent(immigrant, $"以{ImmigrantTypeName(type)}之身来投，寄居{host.Def.Name}");
        gs.PostNews("migrate-in", $"{immigrant.Name}以{ImmigrantTypeName(type)}之身迁入，暂居{host.Def.Name}"); // 公告栏播报迁入
    }

    /// <summary>流民类型中文名（公告/履历用）。</summary>
    private static string ImmigrantTypeName(PopulationConfig.ImmigrantType t) => t switch
    {
        PopulationConfig.ImmigrantType.Merchant => "寓商",
        PopulationConfig.ImmigrantType.Soldier => "散勇",
        PopulationConfig.ImmigrantType.Scholar => "客士",
        _ => "归民",
    };

    /// <summary>王爷府建成时随迁安置：CoupleCount 对富裕年轻夫妻暂居府中（王爷府 capacity 提供居住位），
    /// 各自成家、家庭公产丰厚；待玩家划好可建设坊区后，由 BuildUpFromLodging（寄居→攒够自建）自然迁出、
    /// 在府邸周边自建新宅。由 Main 的 BuildingPlaced 钩子调用。</summary>
    public void SettleNobleFamilies(GameState gs, BuildingInstance mansion)
    {
        var occupancy = gs.BuildHomeOccupancy();
        for (int i = 0; i < PrinceMansionConfig.CoupleCount; i++)
        {
            if (gs.BuildingOccupancy(mansion) >= mansion.HousingCapacity)
                break; // 居住位已满：兜底不再塞人
            var family = gs.AddFamily(new Family { HomeId = mansion.Id, SharedAssets = PrinceMansionConfig.CoupleAssets });
            var husband = NewNobleAdult(gs, Gender.Male);
            var wife = NewNobleAdult(gs, Gender.Female);
            husband.SpouseId = wife.Id;
            wife.SpouseId = husband.Id;
            foreach (var c in new[] { husband, wife })
            {
                c.FamilyId = family.Id;
                family.MemberIds.Add(c.Id);
                MoveIn(gs, c, mansion.Id, occupancy);
                gs.LogLifeEvent(c, "随王爷入府，暂居府中");
            }
            gs.PostNews("migrate-in", $"{husband.Name}偕眷随王爷入府，暂居王爷府");
        }
    }

    /// <summary>随迁的富裕年轻成人：年龄取自 PrinceMansionConfig（当婚育之年），随身家产由调用方注入家庭公产。</summary>
    private Citizen NewNobleAdult(GameState gs, Gender gender)
    {
        var (surname, fullName) = NameGenerator.NewName(gender);
        int years = PrinceMansionConfig.AdultAgeMin + _rng.Next(PrinceMansionConfig.AdultAgeSpan);
        return gs.AddCitizen(new Citizen
        {
            Surname = surname,
            Name = fullName,
            Gender = gender,
            AgeYears = years,
            AgeMonths = years * 12 + _rng.Next(12),
        });
    }

    private Citizen NewAdult(GameState gs, Gender gender)
    {
        var (surname, fullName) = NameGenerator.NewName(gender);
        // 迁入成人年龄与随身家产：年龄取自 PopulationConfig；
        // 家产由调用方按流民类型估资后注入家庭公产（批次六十八：资金家庭化）
        int years = PopulationConfig.ArriveAgeMin + _rng.Next(PopulationConfig.ArriveAgeSpan);
        return gs.AddCitizen(new Citizen
        {
            Surname = surname,
            Name = fullName,
            Gender = gender,
            AgeYears = years,
            AgeMonths = years * 12 + _rng.Next(12),
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
        long budget = MarriageBudget(gs, man, woman);
        if (ZoneGrowthSystem.TryBuildHouse(gs, budget, out var newHome, out long cost, familyMembers: 2))
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
    
    /// <summary>成婚建房预算：夫妻各自家庭公产份额估算（非破坏性估算，供 TryBuildHouse 判断；个人私产已停流通）。</summary>
    private static long MarriageBudget(GameState gs, Citizen man, Citizen woman)
        => SharedShare(gs, man) + SharedShare(gs, woman);
    
    /// <summary>家庭公产的人均份额（文，非破坏性估算，不改动家庭）。</summary>
    private static long SharedShare(GameState gs, Citizen c)
        => gs.Families.TryGetValue(c.FamilyId, out var fam) && fam.MemberIds.Count > 0
            ? fam.SharedAssets / fam.MemberIds.Count : 0;
    
    /// <summary>婚后另立门户：夫妻各从原家庭分得一份（分产 + 嫁妝），扣除房款后迁入自建新宅成立新家庭。</summary>
    private void MarryIntoNewHome(GameState gs, Citizen man, Citizen woman, BuildingInstance newHome, Dictionary<int, int> occupancy, long cost)
    {
        long portion = DetachFromFamily(gs, man);  // 男方分得的家产份额
        long dowry = DetachFromFamily(gs, woman);   // 妻子嫁妝
        long pooled = Math.Max(0, portion + dowry - cost); // 公产抵房款，余作新家底（预算已按份额估，正常够付）
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
    private static long DetachFromFamily(GameState gs, Citizen c)
    {
        if (!gs.Families.TryGetValue(c.FamilyId, out var fam))
            return 0;
        fam.MemberIds.Remove(c.Id);
        long share = fam.SharedAssets / Math.Max(1, fam.MemberIds.Count + 1);
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
                AgeYears = 0, // 新生儿 0 岁（1 月/7 月加龄）
                FatherId = father.Id,
                MotherId = mother.Id,
                FamilyId = mother.FamilyId,
            });
            RollInheritedSkill(child, father, mother); // 技能遗传：继承父母之一 + 小概率变异（批次六十五）
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

    /// <summary>新生儿技能遗传（遗传算法）：随机继承父或母的主技能，经验按比例衰减继承；
    /// 小概率变异换型（技能类型与经验全部重随机）；父母皆无技能时小概率随机开蒙。
    /// 概率与系数见 GeneticsConfig。</summary>
    private void RollInheritedSkill(Citizen child, Citizen father, Citizen mother)
    {
        // 变异：技能类型与经验全部重新随机（不继承）
        if (_rng.NextDouble() < GeneticsConfig.MutationChancePerBirth)
        {
            child.Skill = RollRandomSkill();
            child.SkillExp = GeneticsConfig.MutationExpMin
                + (float)_rng.NextDouble() * GeneticsConfig.MutationExpSpan;
            return;
        }
        var donor = _rng.NextDouble() < GeneticsConfig.InheritFatherChance ? father : mother;
        if (donor.Skill != SkillType.None)
        {
            child.Skill = donor.Skill;
            // 天赋承袭：经验按比例衰减继承（开蒙早者青出于蓝，也有均值回归）
            child.SkillExp = donor.SkillExp
                * (GeneticsConfig.ExpInheritMin + (float)_rng.NextDouble() * GeneticsConfig.ExpInheritSpan);
            return;
        }
        // 父母皆无技能：大部分随父母（None），小概率开蒙
        if (_rng.NextDouble() < GeneticsConfig.SkilllessRandomChance)
        {
            child.Skill = RollRandomSkill();
            child.SkillExp = GeneticsConfig.MutationExpMin
                + (float)_rng.NextDouble() * GeneticsConfig.MutationExpSpan;
        }
    }

    /// <summary>随机抽一种技能（变异/开蒙用）：枚举 1~6 对应体力/手艺/商业/文化/战斗/农艺，等概率。</summary>
    private SkillType RollRandomSkill() => (SkillType)(1 + _rng.Next(6));

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

    /// <summary>家庭人均资产（公产 / 人数），用于生育富裕度修正（个人私产已停流通）。</summary>
    private static long FamilyPerCapitaAssets(GameState gs, Citizen c)
    {
        if (gs.Families.TryGetValue(c.FamilyId, out var fam) && fam.MemberIds.Count > 0)
            return fam.TotalAssets(gs) / fam.MemberIds.Count;
        return 0;
    }

    /// <summary>家庭总资产（公产，文）：寄居攒钱自建判定用。</summary>
    private static long FamilyTotalAssets(GameState gs, Citizen c)
    {
        if (gs.Families.TryGetValue(c.FamilyId, out var fam) && fam.MemberIds.Count > 0)
            return fam.TotalAssets(gs);
        return 0;
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

        // 空房继承（批次八十六）：空置民居由无住所寄居家庭低价过户入住（先继承后自建，空房不再只增不减）
        InheritVacantHomes(gs, occupancy);
    
        // 批次八十七：遍历快照——循环内 TryLeaveAndBuild 会经 PlaceBuilding 向 Buildings 插入新房，
        // 旧版直接枚举字典：分家建房成功时字典版本号变化，下次 MoveNext 抛 InvalidOperationException 中断月结
        foreach (var b in gs.Buildings.Values.ToList())
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
    
    /// <summary>尝试一名住户自建新宅另立门户：以其家庭人均公产为预算，建得起才迁出（否则本轮不分）。</summary>
    private void TryLeaveAndBuild(GameState gs, Citizen c, Dictionary<int, int> occupancy)
    {
        long budget = SharedShare(gs, c);
        if (ZoneGrowthSystem.TryBuildHouse(gs, budget, out var newHome, out long cost, familyMembers: 1))
            LeaveForNewHome(gs, c, newHome, occupancy, cost);
    }
    
    /// <summary>空房继承（批次八十六）：绝户/分家遗留的空置民居（occupancy==0 的 house/mansion）由无自有住所的
    /// 寄居家庭低价过户入住——过户费（house 600 / mansion 1500）远低于自建门槛（5000），先继承后自建；
    /// 旧版空宅只有无家者（HandleHomeless）能免费入住，寄居者/迁入者一律自建新房，Abandoned 空房只增不减。
    /// 一栋空房一次入住一家；无候选则留待下月（后续可由玩家拆除回收地皮）。</summary>
    private void InheritVacantHomes(GameState gs, Dictionary<int, int> occupancy)
    {
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Id != "house" && b.Def.Id != "mansion")
                continue; // 店坊空置由迁入者寄居自然填充，不走继承
            if (occupancy.GetValueOrDefault(b.Id) != 0)
                continue;
            long assetsReq = b.Def.Id == "mansion" ? PopulationConfig.InheritMansionAssets : PopulationConfig.InheritHouseAssets;
            long fee = b.Def.Id == "mansion" ? PopulationConfig.InheritMansionCost : PopulationConfig.InheritHouseCost;
            var seen = new HashSet<int>();
            foreach (var c in gs.Citizens.Values.ToList())
            {
                if (c.IsChild || c.FamilyId < 0 || !seen.Add(c.FamilyId))
                    continue;
                if (OwnsResidence(gs, c))
                    continue; // 已有自有民居者不继承
                if (FamilyTotalAssets(gs, c) < assetsReq)
                    continue;
                if (!gs.Families.TryGetValue(c.FamilyId, out var fam))
                    continue;
                long paid = Math.Min(fee, fam.SharedAssets);
                fam.SharedAssets -= paid;
                gs.Money += paid;
                gs.Ledger.Add("空房过户", paid);
                fam.HomeId = b.Id;
                b.Abandoned = false;
                foreach (var id in fam.MemberIds.ToList())
                    if (gs.Citizens.TryGetValue(id, out var m))
                    {
                        if (occupancy.ContainsKey(m.HomeId))
                            occupancy[m.HomeId]--;
                        MoveIn(gs, m, b.Id, occupancy);
                    }
                gs.LogLifeEvent(c, $"低价继承空置{b.Def.Name}（{b.Origin.X},{b.Origin.Y}），迁离寄居");
                EventBus.RaiseBuildingsChanged();
                break; // 一栋空房一次入住一家
            }
        }
    }

    /// <summary>是否已有自有住所（house/mansion）：继承空房候选的排除口径（寄居店坊/流民营/无家者不算）。</summary>
    private static bool OwnsResidence(GameState gs, Citizen c) =>
        c.HomeId >= 0 && gs.Buildings.TryGetValue(c.HomeId, out var home)
        && (home.Def.Id == "house" || home.Def.Id == "mansion");

    /// <summary>攒钱自建宅：寄居流民营/店坊的家庭（非 house 居所）全家总资产≥自建门槛且有落位时，建房携家搬入。</summary>
    private void BuildUpFromLodging(GameState gs, Dictionary<int, int> occupancy)
    {
        var seen = new HashSet<int>();
        foreach (var c in gs.Citizens.Values.ToList())
        {
            if (c.IsChild || c.HomeId < 0 || !gs.Buildings.TryGetValue(c.HomeId, out var home))
                continue;
            if (home.Def.Id == "house" || home.HousingCapacity <= 0)
                continue; // 只处理寄居于流民营/店坊/王爷府（非自宅、有居住位）者，攒够即自建迁出
            if (c.FamilyId >= 0 && !seen.Add(c.FamilyId))
                continue; // 同家庭只处理一次
            // 预算 = 全家总资产（公产 + 成员私产，含寄居期间积蓄）：攒够门槛且有落位即建房携家搬入
            long budget = FamilyTotalAssets(gs, c);
            if (budget < PopulationConfig.SelfBuildAssets)
                continue;
            // 家庭人口影响初始宅子尺寸（批次六十六：人多直接起大宅）
            int members = gs.Families.TryGetValue(c.FamilyId, out var fam) ? fam.MemberIds.Count : 1;
            if (ZoneGrowthSystem.TryBuildHouse(gs, budget, out var house, out long cost, familyMembers: members))
                MoveFamilyToNewHouse(gs, c, house, occupancy, cost);
        }
    }
    
    /// <summary>一家携入自建新宅：房款由家庭公产支付（个人私产已停流通），然后全家从旧居迁入新宅（无家庭则单人搬入）。
    /// 批次七十六：地价全额交给玩家（王爷，售地收入），另提 3 成作为建房工钱发给当日无业者（雇人盖房）。</summary>
    private void MoveFamilyToNewHouse(GameState gs, Citizen anyMember, BuildingInstance house, Dictionary<int, int> occupancy, long cost)
    {
        house.Abandoned = false;
        if (gs.Families.TryGetValue(anyMember.FamilyId, out var fam))
        {
            fam.SharedAssets = Math.Max(0, fam.SharedAssets - cost); // 房款由公产支付（寄居积蓄已随工资入公产）
            LandSaleToPlayer(gs, cost); // 地价交玩家 + 3 成工钱发无业者
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
            gs.TakeFromFamily(anyMember, cost); // 单人：房款由家庭公产支付
            LandSaleToPlayer(gs, cost);
            if (occupancy.ContainsKey(anyMember.HomeId))
                occupancy[anyMember.HomeId]--;
            MoveIn(gs, anyMember, house.Id, occupancy);
            gs.LogLifeEvent(anyMember, "积蓄自建新宅，迁离寄居");
        }
    }

    /// <summary>NPC 建房土地交割（批次七十六）：地价全额入官库（土地归王爷，售地收入），
    /// 另提 3 成作为建房工钱发给当日无业者（村民雇人盖房，钱仍在玩家↔村民循环内）。
    /// 批次八十七：工钱从地价中出——先发放、按实扣款（无人领则钱留官库），
    /// 旧版 +cost 后再凭空发 30% 工钱且不扣官库，每建一宅货币净增 30% 房款（持续通胀源）。</summary>
    private static void LandSaleToPlayer(GameState gs, long cost)
    {
        if (cost <= 0)
            return;
        long paid = gs.PayBuildWages(cost * 3 / 10);
        gs.Money += cost - paid;
        gs.Ledger.Add("售地", cost - paid);
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

    /// <summary>分家：从原家庭按人均份额分产并扣除房款、迁入自建新居、成立自己的家庭（后续婚配成家）。</summary>
    private void LeaveForNewHome(GameState gs, Citizen c, BuildingInstance newHome, Dictionary<int, int> occupancy, long cost)
    {
        long portion = 0;
        if (gs.Families.TryGetValue(c.FamilyId, out var old))
        {
            // 分产：按人均份额带出家底（与 SharedShare 估算口径一致，预算够则分产够付房款）
            portion = old.MemberIds.Count > 0 ? old.SharedAssets / old.MemberIds.Count : 0;
            old.SharedAssets -= portion;
            old.MemberIds.Remove(c.Id);
            if (old.MemberIds.Count == 0)
                gs.Families.Remove(old.Id);
        }
        if (occupancy.ContainsKey(c.HomeId))
            occupancy[c.HomeId]--;

        long pooled = Math.Max(0, portion - cost); // 分产抵房款，余作新家底
        var fam = gs.AddFamily(new Family { HomeId = newHome.Id, SharedAssets = PopulationConfig.SplitFamilyAssets + pooled });
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
