using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 单级里程碑定义：晋级门槛与该级放开的内容。
/// 当前晋级只看人口（方案 a）；PopulationRequired 之外预留 MoneyRequired/RequiredBuildingId，
/// 后续融合方案 b/c（官库钱、标志建筑）时填值即生效，Reached 判定已包含三项。
/// </summary>
public class MilestoneDef
{
    public int Level;
    public string Name = "";

    /// <summary>晋级所需人口。</summary>
    public int PopulationRequired;

    /// <summary>预留：晋级所需官库钱（0 不要求，方案 b 融合口）。</summary>
    public double MoneyRequired;

    /// <summary>预留：晋级所需标志建筑定义 id（空串不要求，方案 c 融合口）。</summary>
    public string RequiredBuildingId = "";

    /// <summary>晋级时官库一次性拨款（贯）。</summary>
    public int Reward;

    /// <summary>本级下住宅可升到的最高等级（转业工商同受限）。</summary>
    public int MaxHouseLevel;

    /// <summary>晋级时全城成人兴致小幅提升。</summary>
    public float FunBonus;

    /// <summary>是否满足全部晋级条件。MoneyRequired 为 0 时不检查官库——
    /// 否则无限钱模式下负债建造会把晋级永久卡死（人口达标也不升）。</summary>
    public bool Reached(GameState gs) =>
        gs.Population >= PopulationRequired
        && (MoneyRequired <= 0 || gs.Money >= MoneyRequired)
        && (RequiredBuildingId == "" || gs.CountByDef(RequiredBuildingId) > 0);
}

/// <summary>里程碑注册表：村落→集镇→县城→州城→京城（宋代聚落层级）。</summary>
public static class Milestones
{
    public static readonly MilestoneDef[] Levels =
    {
        new() { Level = 0, Name = "村落", PopulationRequired = 0,   Reward = 0,    MaxHouseLevel = 1, FunBonus = 0f },
        new() { Level = 1, Name = "集镇", PopulationRequired = 15,  Reward = 300,  MaxHouseLevel = 2, FunBonus = 5f },
        new() { Level = 2, Name = "县城", PopulationRequired = 40,  Reward = 800,  MaxHouseLevel = 3, FunBonus = 5f },
        new() { Level = 3, Name = "州城", PopulationRequired = 100, Reward = 1500, MaxHouseLevel = 3, FunBonus = 8f },
        new() { Level = 4, Name = "京城", PopulationRequired = 250, Reward = 3000, MaxHouseLevel = 3, FunBonus = 10f },
    };

    /// <summary>当前等级定义（越界钳制到首末级）。</summary>
    public static MilestoneDef Of(int level) =>
        Levels[System.Math.Clamp(level, 0, Levels.Length - 1)];

    /// <summary>当前等级名（顶栏展示）。</summary>
    public static string NameOf(int level) => Of(level).Name;

    /// <summary>当前里程碑下住宅最高等级。</summary>
    public static int MaxHouseLevel(GameState gs) => Of(gs.MilestoneLevel).MaxHouseLevel;

    /// <summary>居民分级需求：达到指定里程碑后新增的日常消耗（依次尝试候选货品，家中无存则上市购买）。
    /// 州城起的成品需求（酒/腌货、木器/铁器）为加工链补上消费端出口。</summary>
    public class TierNeed
    {
        public int MilestoneRequired;
        public string Label = "";

        /// <summary>候选货品（任一满足即可，按序尝试）。</summary>
        public string[] GoodsIds = System.Array.Empty<string>();

        /// <summary>每人每日消耗（份）。</summary>
        public double PerDay;

        /// <summary>断供时每日兴致扣减。</summary>
        public float FunPenalty;
    }

    public static readonly TierNeed[] TierNeeds =
    {
        new() { MilestoneRequired = 2, Label = "副食", GoodsIds = new[] { Goods.Fruit },
                PerDay = 0.03, FunPenalty = 0.5f },
        new() { MilestoneRequired = 3, Label = "酒馔", GoodsIds = new[] { Goods.Wine, Goods.Cured },
                PerDay = 0.015, FunPenalty = 0.5f },
        new() { MilestoneRequired = 4, Label = "器用", GoodsIds = new[] { Goods.Timber, Goods.Ironware },
                PerDay = 0.008, FunPenalty = 0.3f },
    };
}

/// <summary>里程碑系统（每日结算）：条件达成即晋级——官库拨款记账、全城成人兴致小涨、广播事件
/// （建造菜单刷新解锁项、HUD 弹报、被动科技由 TechSystem 顺势解锁）。一日至多晋一级，读档大城逐日补晋。</summary>
public class MilestoneSystem
{
    public void TickDay(GameState gs)
    {
        int next = gs.MilestoneLevel + 1;
        if (next >= Milestones.Levels.Length)
            return;

        var def = Milestones.Levels[next];
        if (!def.Reached(gs))
            return;

        gs.MilestoneLevel = next;
        if (def.Reward > 0)
        {
            gs.Money += def.Reward;
            gs.Ledger.Add("晋级拨款", def.Reward);
        }
        if (def.FunBonus > 0f)
            foreach (var c in gs.Citizens.Values)
                if (!c.IsChild)
                    c.Fun = System.Math.Min(100f, c.Fun + def.FunBonus);

        EventBus.RaiseMilestoneReached(next);
        EventBus.RaiseStatsChanged();
    }
}
