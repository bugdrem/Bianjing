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
    /// <summary>每日迁入概率（有空房才成行）：夫妻户 / 单身流民。</summary>
    private const float CoupleChancePerDay = 0.1f;
    private const float SingleChancePerDay = 0.05f;

    /// <summary>每日概率：单身男婚配 / 成年人结交新友；生育概率另由算法推算（见 BirthProbability）。</summary>
    private const float MarriageChancePerDay = 0.01f;
    private const float BirthChancePerDay = 0.003f;
    private const float FriendChancePerDay = 0.01f;

    private const int EmigrateAfterHomelessMonths = 6;
    private const double CoupleStartingAssets = 60;

    /// <summary>富裕度对第五胎后生育的抑制尺度（家庭人均资产越高越不易再生，达此值降至下限）。</summary>
    private const double WealthEase = 400;
    /// <summary>满员住户每月触发拥挤事件（成年未婚男搬出 / 升级扩建）的概率。</summary>
    private const float CrowdEventChance = 0.15f;

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
        {
            // 60 岁后死亡率随年龄上升；饥荒额外增加死亡/迁出压力
            float p = 0f;
            if (c.AgeYears >= 60)
                p = 0.002f + (c.AgeYears - 60) * 0.004f;
            if (famine)
                p += 0.03f;

            if (_rng.NextDouble() < p)
                dead.Add(c.Id);
        }

        foreach (var id in dead)
            gs.RemoveCitizen(id);
    }

    /// <summary>住所被拆或从未有住所：全家找空房搬入，找不到累计计时，过久则迁出。</summary>
    private void HandleHomeless(GameState gs)
    {
        foreach (var c in gs.Citizens.Values)
            if (c.HomeId >= 0 && !gs.Buildings.ContainsKey(c.HomeId))
                c.HomeId = -1;

        var occupancy = gs.BuildHomeOccupancy();
        var homeless = gs.Citizens.Values.Where(c => c.HomeId < 0).ToList();

        foreach (var c in homeless)
        {
            var house = FindVacantHouse(gs, occupancy, 1);
            if (house != null)
            {
                MoveIn(gs, c, house.Id, occupancy);
                c.HomelessMonths = 0;
                gs.LogLifeEvent(c, "迁居新宅"); // 失宅后觅得新居
            }
            else
            {
                c.HomelessMonths++;
            }
        }

        foreach (var c in homeless.Where(c => c.HomelessMonths > EmigrateAfterHomelessMonths).ToList())
        {
            // 带上未成年子女一同迁出，不留孤幼滞留城中
            foreach (var childId in c.ChildrenIds.ToList())
                if (gs.Citizens.TryGetValue(childId, out var child) && child.IsChild)
                    gs.RemoveCitizen(childId);
            gs.RemoveCitizen(c.Id);
        }
    }

    /// <summary>迁入：优先两人小家庭（夫妻），偶有单身流民（每日判定，有空床才成行）。</summary>
    private void Immigration(GameState gs)
    {
        var occupancy = gs.BuildHomeOccupancy();

        if (_rng.NextDouble() < CoupleChancePerDay)
        {
            var house = FindVacantHouse(gs, occupancy, 2);
            if (house != null)
                SpawnCouple(gs, house, occupancy);
        }

        if (_rng.NextDouble() < SingleChancePerDay)
        {
            var house = FindVacantHouse(gs, occupancy, 1);
            if (house != null)
                SpawnSingle(gs, house, occupancy);
        }
    }

    private void SpawnCouple(GameState gs, BuildingInstance house, Dictionary<int, int> occupancy)
    {
        var family = gs.AddFamily(new Family { HomeId = house.Id, SharedAssets = CoupleStartingAssets });

        var husband = NewAdult(gs, Gender.Male);
        var wife = NewAdult(gs, Gender.Female);
        husband.SpouseId = wife.Id;
        wife.SpouseId = husband.Id;

        foreach (var c in new[] { husband, wife })
        {
            c.FamilyId = family.Id;
            family.MemberIds.Add(c.Id);
            MoveIn(gs, c, house.Id, occupancy);
            gs.LogLifeEvent(c, "携眷迁入汴京");
        }
    }

    private void SpawnSingle(GameState gs, BuildingInstance house, Dictionary<int, int> occupancy)
    {
        var single = NewAdult(gs, _rng.NextDouble() < 0.6 ? Gender.Male : Gender.Female);
        var family = gs.AddFamily(new Family { HomeId = house.Id, SharedAssets = 20 });
        single.FamilyId = family.Id;
        family.MemberIds.Add(single.Id);
        MoveIn(gs, single, house.Id, occupancy);
        gs.LogLifeEvent(single, "只身迁入汴京");
    }

    private Citizen NewAdult(GameState gs, Gender gender)
    {
        var (surname, fullName) = NameGenerator.NewName(gender);
        return gs.AddCitizen(new Citizen
        {
            Surname = surname,
            Name = fullName,
            Gender = gender,
            AgeMonths = (18 + _rng.Next(18)) * 12 + _rng.Next(12),
            Money = 10 + _rng.Next(20),
        });
    }

    /// <summary>适龄单身青年婚配：合并入丈夫家庭，同住容量允许的一方住所。</summary>
    private void Marriages(GameState gs)
    {
        var singleMen = gs.Citizens.Values
            .Where(c => c.Gender == Gender.Male && !c.IsMarried && c.AgeYears >= 18 && c.AgeYears < 50).ToList();
        var singleWomen = gs.Citizens.Values
            .Where(c => c.Gender == Gender.Female && !c.IsMarried && c.AgeYears >= 18 && c.AgeYears < 50).ToList();

        var occupancy = gs.BuildHomeOccupancy();

        foreach (var man in singleMen)
        {
            if (singleWomen.Count == 0)
                break;
            if (_rng.NextDouble() >= MarriageChancePerDay)
                continue;

            // 排除近亲：同家庭、直系、同胞均不得婚配（最多试八人，无合适对象则本日作罢）
            Citizen woman = null;
            for (int attempt = 0; attempt < 8 && singleWomen.Count > 0; attempt++)
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

    private void Marry(GameState gs, Citizen man, Citizen woman, Dictionary<int, int> occupancy)
    {
        man.SpouseId = woman.Id;
        woman.SpouseId = man.Id;

        // 妻子并入丈夫家庭（丈夫无家庭则新建）
        if (!gs.Families.TryGetValue(man.FamilyId, out var family))
        {
            family = gs.AddFamily(new Family { HomeId = man.HomeId });
            man.FamilyId = family.Id;
            family.MemberIds.Add(man.Id);
        }

        if (gs.Families.TryGetValue(woman.FamilyId, out var oldFamily))
        {
            oldFamily.MemberIds.Remove(woman.Id);
            family.SharedAssets += oldFamily.SharedAssets / Math.Max(1, oldFamily.MemberIds.Count + 1); // 嫁妆
            if (oldFamily.MemberIds.Count == 0)
            {
                family.SharedAssets += oldFamily.SharedAssets;
                gs.Families.Remove(oldFamily.Id);
            }
        }
        woman.FamilyId = family.Id;
        family.MemberIds.Add(woman.Id);

        // 夫妻各记一笔成婚履历（Name 已含姓，不再叠加 Surname）
        gs.LogLifeEvent(man, $"与 {woman.Name} 成婚");
        gs.LogLifeEvent(woman, $"与 {man.Name} 成婚");

        // 同住：丈夫家有空位则妻子搬来，否则丈夫搬去妻子家
        if (man.HomeId >= 0 && gs.Buildings.TryGetValue(man.HomeId, out var manHouse)
            && gs.HouseVacancy(manHouse, occupancy) >= 1)
        {
            MoveIn(gs, woman, man.HomeId, occupancy);
            family.HomeId = man.HomeId;
        }
        else if (woman.HomeId >= 0 && gs.Buildings.TryGetValue(woman.HomeId, out var womanHouse)
            && gs.HouseVacancy(womanHouse, occupancy) >= 1)
        {
            MoveIn(gs, man, woman.HomeId, occupancy);
            family.HomeId = woman.HomeId;
        }
    }

    private void Births(GameState gs)
    {
        var occupancy = gs.BuildHomeOccupancy();
        var mothers = gs.Citizens.Values
            .Where(c => c.Gender == Gender.Female && c.IsMarried && c.AgeYears >= 18 && c.AgeYears <= 45 && c.HomeId >= 0)
            .ToList();

        foreach (var mother in mothers)
        {
            if (_rng.NextDouble() >= BirthProbability(gs, mother))
                continue;
            if (!gs.Buildings.TryGetValue(mother.HomeId, out var house) || gs.HouseVacancy(house, occupancy) < 1)
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

            // 孩子记出生，父母各记得子/得女
            gs.LogLifeEvent(child, $"生于{HomeName(gs, mother.HomeId)}");
            string kidWord = gender == Gender.Male ? "得子" : "得女";
            gs.LogLifeEvent(father, $"{kidWord} {child.Name}");
            gs.LogLifeEvent(mother, $"{kidWord} {child.Name}");
        }
    }

    /// <summary>生育日概率：1~3 胎最大，之后随子女数递减；第五胎后再结合母亲年龄与家庭富裕程度进一步抑制，
    /// 但始终 &gt;0（仍有几率突破五个）。</summary>
    private static double BirthProbability(GameState gs, Citizen mother)
    {
        int kids = mother.ChildrenIds.Count;
        double countFactor = kids <= 2 ? 1.0
            : kids == 3 ? 0.6
            : kids == 4 ? 0.3
            : 0.12 * Math.Pow(0.5, kids - 5); // 第六胎起指数衰减，永不归零
        double modifier = 1.0;
        if (kids >= 4) // 第五胎（及以后）才结合年龄与富裕程度
        {
            int age = mother.AgeYears;
            double ageFactor = age <= 30 ? 1.0 : Math.Max(0.2, 1.0 - (age - 30) * 0.05);
            double perCapita = FamilyPerCapitaAssets(gs, mother);
            double wealthFactor = Math.Clamp(1.0 - perCapita / WealthEase, 0.3, 1.0); // 越富越不易再生
            modifier = ageFactor * wealthFactor;
        }
        return BirthChancePerDay * countFactor * modifier;
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
        var adults = gs.Citizens.Values.Where(c => !c.IsChild && c.FriendIds.Count < 5).ToList();
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

    /// <summary>住房拥挤调整（每月，概率事件）：先标记/清除废弃屋；满员住户按概率处理——
    /// 若有成年未婚男则让其独立门户搬出（候选 A），否则升级扩建以增容（候选 C，占地扩大留 TODO）。</summary>
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
            if (occupancy.GetValueOrDefault(b.Id) < b.HousingCapacity) // 未满不处理
                continue;
            if (_rng.NextDouble() >= CrowdEventChance)
                continue;

            var male = FindAdultUnmarriedMale(gs, b);
            if (male != null)
            {
                // 候选 A：成年未婚男另立门户，迁往空置/废弃住宅（无则等待坊区新建房）
                var newHome = FindVacantHouse(gs, occupancy, 1, b.Id);
                if (newHome != null)
                    LeaveForNewHome(gs, male, newHome, occupancy);
            }
            else if (b.Level < EffectiveMaxLevel(gs, b) && b.Condition >= 60f)
            {
                // 候选 C：无成年未婚男则升级扩建增容（受里程碑限级；后期改为占用邻格的大房屋）
                b.Level++;
                EventBus.RaiseMapChanged(); // 楼高变化即时重绘
            }
        }
    }

    /// <summary>当前里程碑下的住宅有效最高等级（非住宅 grown 建筑同受限）。</summary>
    private static int EffectiveMaxLevel(GameState gs, BuildingInstance b) =>
        Math.Min(b.Def.MaxLevel, Milestones.MaxHouseLevel(gs));

    /// <summary>住户中的成年未婚男（有则触发“独立门户搬出”）。</summary>
    private static Citizen FindAdultUnmarriedMale(GameState gs, BuildingInstance home)
    {
        foreach (var c in gs.Citizens.Values)
            if (c.HomeId == home.Id && c.Gender == Gender.Male && c.IsAdult && !c.IsMarried)
                return c;
        return null;
    }

    /// <summary>分家：脱离原家庭、迁入新居、成立自己的家庭（后续婚配成家）。</summary>
    private void LeaveForNewHome(GameState gs, Citizen c, BuildingInstance newHome, Dictionary<int, int> occupancy)
    {
        if (gs.Families.TryGetValue(c.FamilyId, out var old))
        {
            old.MemberIds.Remove(c.Id);
            if (old.MemberIds.Count == 0)
                gs.Families.Remove(old.Id);
        }
        if (occupancy.ContainsKey(c.HomeId))
            occupancy[c.HomeId]--;

        var fam = gs.AddFamily(new Family { HomeId = newHome.Id, SharedAssets = 15 });
        c.FamilyId = fam.Id;
        fam.MemberIds.Add(c.Id);
        newHome.Abandoned = false;
        MoveIn(gs, c, newHome.Id, occupancy);
        gs.LogLifeEvent(c, "成年分家，另立门户");
    }

    // ---- 工具 ----

    /// <summary>住宅名（出生履历用）：建筑已失则笼统称“家中”。</summary>
    private static string HomeName(GameState gs, int homeId) =>
        gs.Buildings.TryGetValue(homeId, out var b) ? $"{b.Def.Name}（{b.X},{b.Y}）" : "家中";

    /// <summary>找有空床位的住处：民居优先，其次前店后宅/工坊宿舍等一切可住建筑；excludeId 排除自身。</summary>
    private static BuildingInstance FindVacantHouse(GameState gs, Dictionary<int, int> occupancy, int needBeds, int excludeId = -1)
    {
        BuildingInstance fallback = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Id == excludeId)
                continue;
            if (b.HousingCapacity <= 0 || gs.HouseVacancy(b, occupancy) < needBeds)
                continue;
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
