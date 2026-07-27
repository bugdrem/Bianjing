using System;
using System.Collections.Generic;
using System.Linq;

namespace Bianjing;

/// <summary>
/// 居民生命周期系统（每月结算）：
/// 老化 → 死亡 → 无家处理/迁出 → 迁入（夫妻户为主，兼有单身）→ 适龄婚配 → 生育 → 交友。
/// 只操作数据层，不涉及任何表现节点。
/// </summary>
public class LifecycleSystem
{
    private const int MaxCouplesPerMonth = 1;
    private const float SingleImmigrantChance = 0.4f;
    private const float MarriageChance = 0.10f;
    private const float BirthChance = 0.05f;
    private const int EmigrateAfterHomelessMonths = 6;
    private const double CoupleStartingAssets = 60;

    private readonly Random _rng = new();

    public void Tick(GameState gs)
    {
        Age(gs);
        Deaths(gs);
        HandleHomeless(gs);
        Immigration(gs);
        Marriages(gs);
        Births(gs);
        MakeFriends(gs);
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
            }
            else
            {
                c.HomelessMonths++;
            }
        }

        foreach (var c in homeless.Where(c => c.HomelessMonths > EmigrateAfterHomelessMonths).ToList())
            gs.RemoveCitizen(c.Id);
    }

    /// <summary>迁入：优先两人小家庭（夫妻），偶有单身流民。</summary>
    private void Immigration(GameState gs)
    {
        var occupancy = gs.BuildHomeOccupancy();

        for (int i = 0; i < MaxCouplesPerMonth; i++)
        {
            var house = FindVacantHouse(gs, occupancy, 2);
            if (house == null)
                break;
            SpawnCouple(gs, house, occupancy);
        }

        if (_rng.NextDouble() < SingleImmigrantChance)
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
        }
    }

    private void SpawnSingle(GameState gs, BuildingInstance house, Dictionary<int, int> occupancy)
    {
        var single = NewAdult(gs, _rng.NextDouble() < 0.6 ? Gender.Male : Gender.Female);
        var family = gs.AddFamily(new Family { HomeId = house.Id, SharedAssets = 20 });
        single.FamilyId = family.Id;
        family.MemberIds.Add(single.Id);
        MoveIn(gs, single, house.Id, occupancy);
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
            if (_rng.NextDouble() >= MarriageChance)
                continue;

            var woman = singleWomen[_rng.Next(singleWomen.Count)];
            singleWomen.Remove(woman);
            Marry(gs, man, woman, occupancy);
        }
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
            if (_rng.NextDouble() >= BirthChance)
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
        }
    }

    /// <summary>简版社交：成年人小概率结识新朋友（为后续人物交互留接口）。</summary>
    private void MakeFriends(GameState gs)
    {
        var adults = gs.Citizens.Values.Where(c => !c.IsChild && c.FriendIds.Count < 5).ToList();
        if (adults.Count < 2)
            return;

        foreach (var c in adults)
        {
            if (_rng.NextDouble() >= 0.05)
                continue;
            var other = adults[_rng.Next(adults.Count)];
            if (other.Id == c.Id || c.FriendIds.Contains(other.Id))
                continue;
            c.FriendIds.Add(other.Id);
            other.FriendIds.Add(c.Id);
        }
    }

    // ---- 工具 ----

    /// <summary>找有空床位的住处：民居优先，其次前店后宅/工坊宿舍等一切可住建筑。</summary>
    private static BuildingInstance FindVacantHouse(GameState gs, Dictionary<int, int> occupancy, int needBeds)
    {
        BuildingInstance fallback = null;
        foreach (var b in gs.Buildings.Values)
        {
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
