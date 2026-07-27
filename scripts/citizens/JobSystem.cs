using System;
using System.Collections.Generic;
using System.Linq;

namespace Bianjing;

/// <summary>
/// 就业系统（每日结算）：
/// 岗位失效清理 → 退休（家产充裕的老人才退）→ 适龄求职（无岗位则伐木自谋生路）→ 家庭开销；
/// 工钱不在此处发放——由表现层 CitizenAgent 在每班下工时按动作即时结算（月俸/30 一班）。
/// 工商业「一直营业只退休」，作息疲劳由表现层 CitizenAgent 实时驱动。
/// </summary>
public class JobSystem
{
    /// <summary>修缮匠月俸（官府出资，专职修缮公共设施；每班按 1/30 由下工动作结算）。</summary>
    public const double RepairerIncome = 2.0;

    /// <summary>家产超过此数的老人选择退休颐养。</summary>
    private const double ElderRetireAssets = 200;

    /// <summary>每人每月生活开销。</summary>
    private const double LivingCostPerCapita = 0.8;

    private readonly Random _rng = new();

    public void TickDay(GameState gs)
    {
        CleanInvalidJobs(gs);
        Retirement(gs);
        SeekJobs(gs);
        HouseholdSpending(gs);
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
            }
        }
    }

    /// <summary>老人：家产富足则退休，否则继续劳作补贴家用。</summary>
    private static void Retirement(GameState gs)
    {
        foreach (var c in gs.Citizens.Values)
        {
            if (!c.IsElder || !c.HasJob)
                continue;
            double assets = gs.Families.TryGetValue(c.FamilyId, out var f) ? f.TotalAssets(gs) : c.Money;
            if (assets >= ElderRetireAssets)
            {
                c.JobKind = JobKind.None;
                c.WorkplaceId = -1;
            }
        }
    }

    /// <summary>
    /// 求职：适龄青年优先应聘建筑岗位；无空缺则入行修缮匠或上山谋生（伐木/采摘/打猎）。
    /// 已婚且丈夫有工作的妻子留家采购（不求职）；家里揭不开锅的老人也会再就业。
    /// </summary>
    private void SeekJobs(GameState gs)
    {
        var workers = CountWorkers(gs);
        bool hasOfficial = gs.Buildings.Values.Any(b => b.Def.Category == "official" && !b.Def.Natural);

        foreach (var c in gs.Citizens.Values)
        {
            if (c.HasJob || c.IsChild)
                continue;

            // 主妇：丈夫在业则持家采购
            if (c.Gender == Gender.Female && c.IsMarried
                && gs.Citizens.TryGetValue(c.SpouseId, out var husband) && husband.HasJob)
                continue;

            // 老人只有家贫才出山
            if (c.IsElder)
            {
                double assets = gs.Families.TryGetValue(c.FamilyId, out var f) ? f.TotalAssets(gs) : c.Money;
                if (assets >= ElderRetireAssets / 2)
                    continue;
            }

            var workplace = FindVacancy(gs, workers);
            if (workplace != null)
            {
                c.JobKind = JobKind.Employed;
                c.WorkplaceId = workplace.Id;
                workers[workplace.Id] = workers.GetValueOrDefault(workplace.Id) + 1;
            }
            else
            {
                double roll = _rng.NextDouble();
                if (roll < 0.2 && hasOfficial)
                {
                    // 入行修缮匠：专职维护公共设施，吃官府料钱
                    c.JobKind = JobKind.Repairer;
                    c.WorkplaceId = -1;
                }
                else if (roll < 0.6)
                {
                    // 上山谋生：伐木/采摘/打猎（创业开店由坊区生长承接，后续版本个体化）
                    c.JobKind = JobKind.Logger;
                    c.WorkplaceId = -1;
                }
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

    private static BuildingInstance FindVacancy(GameState gs, Dictionary<int, int> workers)
    {
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.JobSlots <= 0)
                continue;
            if (workers.GetValueOrDefault(b.Id) < b.Def.JobSlots)
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
