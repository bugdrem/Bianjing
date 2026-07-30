using System;
using System.Collections.Generic;
using System.Linq;

namespace Bianjing;

/// <summary>
/// 就业系统（每日结算）：
/// 岗位失效清理 → 退休（家产充裕的老人才退）→ 适龄求职（无岗位则伐木自谋生路）→ 家庭开销；
/// 修缮匠并入受雇体系（岗位在修缮房，俸禄由官库在下工时结算）。
/// 工钱不在此处发放——由表现层 CitizenAgent 在每班下工时按动作即时结算（月俸/30 一班）。
/// 工商业「一直营业只退休」，作息疲劳由表现层 CitizenAgent 实时驱动。
/// </summary>
public class JobSystem
{
    /// <summary>每人每月生活开销：转发自 JobsConfig。</summary>
    private static double LivingCostPerCapita => JobsConfig.LivingCostPerCapita;

    private readonly Random _rng = new();

    public void TickDay(GameState gs)
    {
        CleanInvalidJobs(gs);
        StaffHomeBusinesses(gs); // 工坊/商铺岗位优先由本楼居民承担
        Retirement(gs);
        SeekJobs(gs);
        HouseholdSpending(gs);
    }

    /// <summary>居住者优先承担自家产业：工坊/商铺（grown + 有岗位）的岗位先由本楼居民填，
    /// 岗位被外来雇工占满时辞退外人给本楼人让位；余下空缺由 SeekJobs 对外招工。
    /// 本楼居民若在外就业则辞外职回自家上工；孩童不入职。</summary>
    private static void StaffHomeBusinesses(GameState gs)
    {
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "grown" || b.Def.JobSlots <= 0)
                continue;
            int filled = 0;
            foreach (var c in gs.Citizens.Values)
            {
                if (filled >= b.Def.JobSlots)
                    break;
                // 孩童不入职；家族产业内的人可干到 FamilyBusinessAge（比普通雇工晚退），过龄不再拉回
                if (c.HomeId != b.Id || c.IsChild || c.AgeYears >= RetireConfig.FamilyBusinessAge)
                    continue;
                // 已在自家上工则计数；在外就业或无业则拉回自家产业
                if (c.JobKind == JobKind.Employed && c.WorkplaceId == b.Id)
                {
                    filled++;
                    continue;
                }
                if (c.JobKind == JobKind.Employed && c.WorkplaceId != b.Id)
                    gs.LogLifeEvent(c, $"辞去外职，回自家{b.Def.Name}营生"); // 辞外职也是大事，记一笔
                c.JobKind = JobKind.Employed;
                c.WorkplaceId = b.Id;
                filled++;
            }

            // 本楼人上工后岗位超员（外来雇工占着位）：辞退多余的外人，东家人优先
            int total = 0;
            foreach (var c in gs.Citizens.Values)
                if (c.JobKind == JobKind.Employed && c.WorkplaceId == b.Id)
                    total++;
            if (total > b.Def.JobSlots)
                foreach (var c in gs.Citizens.Values)
                {
                    if (total <= b.Def.JobSlots)
                        break;
                    if (c.JobKind != JobKind.Employed || c.WorkplaceId != b.Id || c.HomeId == b.Id)
                        continue;
                    c.JobKind = JobKind.None;
                    c.WorkplaceId = -1;
                    total--;
                    gs.LogLifeEvent(c, $"东家自用家人，被{b.Def.Name}辞退");
                }
        }
    }

    /// <summary>工作单位被拆则失业。</summary>
    private static void CleanInvalidJobs(GameState gs)
    {
        foreach (var c in gs.Citizens.Values)
        {
            if (c.JobKind == JobKind.Employed && !gs.Buildings.ContainsKey(c.WorkplaceId))
            {
                c.JobKind = JobKind.None;
                c.WorkplaceId = -1;
                gs.LogLifeEvent(c, "工作地已失，失去生计");
            }
        }
    }

    /// <summary>退休致仕：到龄退出当前岗位（普通雇工 Retire.Age，店主/家族产业内的人延至 FamilyBusinessAge）；
    /// 退休后不再受雇，只参与采集等轻活（行为在表现层按家资分流）。</summary>
    private static void Retirement(GameState gs)
    {
        foreach (var c in gs.Citizens.Values)
        {
            if (c.JobKind != JobKind.Employed)
                continue;
            if (c.AgeYears < RetireAgeFor(c))
                continue;
            c.JobKind = JobKind.None;
            c.WorkplaceId = -1;
            gs.LogLifeEvent(c, "年届致仕，退居采薪"); // 退休
        }
    }

    /// <summary>本人的退休年龄：店主/家族产业内的人延迟退休。
    /// 预留：后期可按职业（重体力提前/文职延后）、健康程度、家庭资产进一步微调。</summary>
    private static int RetireAgeFor(Citizen c)
        => IsFamilyBusiness(c) ? RetireConfig.FamilyBusinessAge : RetireConfig.Age;

    /// <summary>是否在自家产业上工（店主/家族内人）：工作地即居所（grown 商铺/工坊由本楼居民承担）。</summary>
    private static bool IsFamilyBusiness(Citizen c)
        => c.JobKind == JobKind.Employed && c.WorkplaceId >= 0 && c.WorkplaceId == c.HomeId;

    /// <summary>
    /// 求职：适龄青年应聘建筑岗位（含修缮房/税所/铸币局/矿盐厂）；无空缺则上山谋生（伐木/采摘/打猎）。
    /// 已婚且丈夫有工作的妻子留家采购（不求职）；家里揭不开锅的老人也会再就业。
    /// </summary>
    private void SeekJobs(GameState gs)
    {
        var workers = CountWorkers(gs);

        foreach (var c in gs.Citizens.Values)
        {
            // 孩童、已有职、以及已过退休年龄者不再受雇（退休者只参与采集等轻活，见表现层）
            if (c.HasJob || c.IsChild || c.AgeYears >= RetireConfig.Age)
                continue;

            // 主妇：丈夫在业则持家采购
            if (c.Gender == Gender.Female && c.IsMarried
                && gs.Citizens.TryGetValue(c.SpouseId, out var husband) && husband.HasJob)
                continue;

            var workplace = FindVacancy(gs, workers);
            if (workplace != null)
            {
                c.JobKind = JobKind.Employed;
                c.WorkplaceId = workplace.Id;
                workers[workplace.Id] = workers.GetValueOrDefault(workplace.Id) + 1;
                gs.LogLifeEvent(c, $"受雇于{workplace.Def.Name}（{workplace.X},{workplace.Y}）");
            }
            else if (_rng.NextDouble() < JobsConfig.JoblessForageChance)
            {
                // 上山谋生：伐木/采摘/打猎（创业开店由坊区生长承接，后续版本个体化）
                c.JobKind = JobKind.Logger;
                c.WorkplaceId = -1;
                gs.LogLifeEvent(c, "进山伐木采猎谋生");
            }
        }
    }

    private static Dictionary<int, int> CountWorkers(GameState gs)
    {
        var workers = new Dictionary<int, int>();
        foreach (var c in gs.Citizens.Values)
            if (c.JobKind == JobKind.Employed)
                workers[c.WorkplaceId] = workers.GetValueOrDefault(c.WorkplaceId) + 1;
        return workers;
    }

    /// <summary>寻找空缺岗位：官营建筑面向全城招工（雇工从各自住处通勤，不占居住格）；
    /// 工坊/商铺本楼居民优先（见 StaffHomeBusinesses），住户填不满的余缺对外招——
    /// 外来打工者在工作地占一个居住格（居住与打工共用同一格池），满员（居民+雇工≥容量）则不再招。</summary>
    private static BuildingInstance FindVacancy(GameState gs, Dictionary<int, int> workers)
    {
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.JobSlots <= 0)
                continue;
            if (workers.GetValueOrDefault(b.Id) >= b.Def.JobSlots)
                continue;
            // grown 工坊/商铺：外来雇工占一个居住格，无空格（居民+雇工已满）则不对外招，直至扩建
            if (b.Def.Category == "grown" && gs.BuildingOccupancy(b) >= b.HousingCapacity)
                continue;
            return b;
        }
        return null;
    }

    /// <summary>家庭生活开销（月值 1/30 逐日扣）：先扣公产，不足再由成员分摊。</summary>
    private static void HouseholdSpending(GameState gs)
    {
        foreach (var family in gs.Families.Values)
        {
            double cost = family.MemberIds.Count * LivingCostPerCapita / GameClock.DaysPerMonth;
            if (family.SharedAssets >= cost)
            {
                family.SharedAssets -= cost;
                continue;
            }

            cost -= family.SharedAssets;
            family.SharedAssets = 0;

            var members = family.MemberIds
                .Select(id => gs.Citizens.GetValueOrDefault(id))
                .Where(c => c != null && c.Money > 0)
                .ToList();
            foreach (var member in members)
            {
                double share = Math.Min(member.Money, cost / members.Count);
                member.Money -= share;
            }
        }
    }
}
