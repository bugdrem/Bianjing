using System;

namespace Bianjing;

/// <summary>
/// 科技系统（每日结算）：
/// 被动通道——条件（里程碑+前置）达成的 passive 科技自动研成并弹报；
/// 主动通道——玩家立项的 active 科技逐日从官库拨经费（钱不够当日停工不计进度），天数攒满研成。
/// 效果不在此处生效：各系统结算时经 GameState.TechFactor 取加成倍率。
/// </summary>
public class TechSystem
{
    public void TickDay(GameState gs)
    {
        PassiveUnlocks(gs);
        ActiveResearch(gs);
    }

    /// <summary>被动科技：条件达成即研成（每日至多全表扫一遍，科技数量少）。</summary>
    private static void PassiveUnlocks(GameState gs)
    {
        foreach (var def in TechDefs.All)
        {
            if (def.IsActive || gs.TechsUnlocked.Contains(def.Id))
                continue;
            if (!ConditionsMet(gs, def))
                continue;
            Unlock(gs, def);
        }
    }

    /// <summary>主动研习：逐日拨经费攒进度；研成后清空立项。</summary>
    private static void ActiveResearch(GameState gs)
    {
        if (gs.ResearchTechId == "")
            return;
        var def = TechDefs.Find(gs.ResearchTechId);
        if (def == null || gs.TechsUnlocked.Contains(def.Id))
        {
            gs.ResearchTechId = "";
            gs.ResearchDays = 0;
            return;
        }

        // 当日经费 = 总经费均摊；官库掏不出则当日停工（无限钱不受限）
        long daily = def.ResearchDays > 0 ? (long)(def.CostMoney / def.ResearchDays) : 0;
        if (!GameSettings.InfiniteMoney && gs.Money < daily)
            return;
        gs.Money -= daily;
        gs.Ledger.Add("研习经费", -daily);
        gs.ResearchDays += 1;

        if (gs.ResearchDays >= def.ResearchDays)
        {
            Unlock(gs, def);
            gs.ResearchTechId = "";
            gs.ResearchDays = 0;
        }
    }

    /// <summary>立项条件：未研成、里程碑与前置齐备（主动模式还需当前无在研项目）。</summary>
    public static bool ConditionsMet(GameState gs, TechDef def)
    {
        if (gs.MilestoneLevel < def.MilestoneRequired)
            return false;
        foreach (var pre in def.Prerequisites)
            if (!gs.TechsUnlocked.Contains(pre))
                return false;
        return true;
    }

    /// <summary>玩家在研习面板点「立项」：校验后设为在研项目，返回是否成功。</summary>
    public static bool StartResearch(GameState gs, string techId)
    {
        var def = TechDefs.Find(techId);
        if (def == null || !def.IsActive || gs.TechsUnlocked.Contains(techId))
            return false;
        if (gs.ResearchTechId != "" || !ConditionsMet(gs, def))
            return false;
        gs.ResearchTechId = techId;
        gs.ResearchDays = 0;
        return true;
    }

    private static void Unlock(GameState gs, TechDef def)
    {
        gs.TechsUnlocked.Add(def.Id);
        EventBus.RaiseTechUnlocked(def.Id);
    }
}
