using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 一堆货品：统一仓储体系的最小单元。
///
/// <b>同货不同批</b>——同一货品可以同时存在多堆，只有"属性相同且位于同一储存点"的才会合并
/// （判定见 <see cref="TimedRules.SameBand"/>）。区分批次的是三组时效字段：
/// Fresh（新鲜度）/ UsedLifespan（已耗寿限）/ RenewCount（翻新次数）。
///
/// AgeDays 保留为"入库天数"的展示口径（累计存放时长，不受环境影响），
/// 与 Fresh 的区别：AgeDays 只说"放了多久"，Fresh 才说"现在有多好"。
///
/// 纯数据类（公共字段），可直接 JSON 序列化入存档。
/// </summary>
public class GoodsStack
{
    /// <summary>货品 id（见 Goods）。</summary>
    public string GoodsId = "";

    /// <summary>份数。</summary>
    public double Amount;

    /// <summary>入库天数（并堆时取较大值，即按最早一批计龄；仅展示用）。</summary>
    public int AgeDays;

    /// <summary>
    /// 软轴：当前新鲜度 0~100。<b>字段初始化器不可省</b>——
    /// 旧存档缺失此字段时反序列化会保留 100 而非默认 0，否则全部库存会瞬间被判成"已失效"。
    /// </summary>
    public float Fresh = 100f;

    /// <summary>硬轴：已耗用的寿限（游戏日，单调累积，与环境无关）。</summary>
    public float UsedLifespan;

    /// <summary>翻新次数（回锅/烘干/腌制）：每次翻新都透支未来（寿限打折 + 衰减加速）。</summary>
    public int RenewCount;

    /// <summary>取本批的时效状态副本。</summary>
    public TimedState State => new() { Fresh = Fresh, UsedLifespan = UsedLifespan, RenewCount = RenewCount };

    /// <summary>写入时效状态。</summary>
    public void SetState(in TimedState st)
    {
        Fresh = st.Fresh;
        UsedLifespan = st.UsedLifespan;
        RenewCount = st.RenewCount;
    }

    /// <summary>本批软轴当前阶段（全效果 / 渐变衰减 / 已失效）。</summary>
    public FreshStage Stage => TimedRules.StageOf(Fresh);
}

/// <summary>
/// 统一库存：建筑仓房、居民背包（后期载具货舱同接口）、地面物资堆、外来访客行囊，
/// 各类持有者共用同一套容量/存取规则。
///
/// 批次九十五改造：同一货品不再无条件并为一堆，而是<b>按属性分批</b>——
/// 同货但新鲜度/已耗寿限/翻新次数不同档的，各自独立成堆，各批拥有独立时效属性，
/// 取出时默认<b>最差优先</b>（先吃快坏的），消耗时按鲜度<b>折算有效量</b>（鲜度 50% 的粮要吃双倍才饱）。
/// 纯数据类可直接序列化。
/// </summary>
public class Inventory
{
    /// <summary>容量上限（份）；&lt;=0 视为无容量（存不进任何东西）。</summary>
    public double Capacity;

    /// <summary>货品堆列表（同货品的<b>同一属性档</b>至多一堆）。</summary>
    public List<GoodsStack> Stacks = new();

    /// <summary>库存总量（份，不折算鲜度）。</summary>
    public double Total
    {
        get
        {
            double sum = 0;
            foreach (var s in Stacks)
                sum += s.Amount;
            return sum;
        }
    }

    /// <summary>剩余库容（份）。</summary>
    public double Free => System.Math.Max(0, Capacity - Total);

    /// <summary>是否空仓。</summary>
    public bool IsEmpty => Stacks.Count == 0;

    /// <summary>指定货品的存量（份，汇总全部批次）。</summary>
    public double AmountOf(string goodsId)
    {
        double sum = 0;
        foreach (var s in Stacks)
            if (s.GoodsId == goodsId)
                sum += s.Amount;
        return sum;
    }

    /// <summary>
    /// 指定货品的<b>有效量</b>（份，按各批鲜度折算后汇总）——
    /// 面板"能吃多久"、需求账本"够不够吃"都应以此为准，而非 <see cref="AmountOf"/>。
    /// </summary>
    public double EffectiveAmountOf(string goodsId)
    {
        double sum = 0;
        foreach (var s in Stacks)
            if (s.GoodsId == goodsId)
                sum += s.Amount * TimedRules.EffectOf(s.Fresh);
        return sum;
    }

    /// <summary>指定货品按份数加权的平均鲜度 0~100（无存则返回 100，便于面板显示）。</summary>
    public float FreshnessOf(string goodsId)
    {
        double wsum = 0, acc = 0;
        foreach (var s in Stacks)
        {
            if (s.GoodsId != goodsId)
                continue;
            acc += s.Fresh * s.Amount;
            wsum += s.Amount;
        }
        return wsum > 0.0001 ? (float)(acc / wsum) : 100f;
    }

    /// <summary>指定货品中<b>最差</b>的那一批（鲜度最低，即最先该吃掉/最该处理的）；无存返回 null。</summary>
    public GoodsStack WorstStackOf(string goodsId)
    {
        GoodsStack worst = null;
        foreach (var s in Stacks)
        {
            if (s.GoodsId != goodsId || s.Amount <= 0.0001)
                continue;
            if (worst == null || s.Fresh < worst.Fresh)
                worst = s;
        }
        return worst;
    }

    /// <summary>本仓是否含有已失效（不可作原用途）的批次——面板提示与 NPC 自救决策用。</summary>
    public bool HasSpent(string goodsId)
    {
        foreach (var s in Stacks)
            if (s.GoodsId == goodsId && s.Amount > 0.0001 && s.Stage == FreshStage.Spent)
                return true;
        return false;
    }

    // ===== 入库 =====

    /// <summary>入库（受容量限制），新货按全新状态计；返回实际入库份数。</summary>
    public double Store(string goodsId, double amount) => StoreBatch(goodsId, amount, TimedState.New);

    /// <summary>超限入库：无视容量全部收下（村民背回的货不浪费）——
    /// 上限只作"继续派人采集/进货"的闸门，不作硬墙。</summary>
    public double StoreForce(string goodsId, double amount) => StoreForceBatch(goodsId, amount, TimedState.New);

    /// <summary>带状态入库（受容量限制）：搬运时把原批的时效状态一并带过来。</summary>
    public double StoreBatch(string goodsId, double amount, in TimedState state)
    {
        double accepted = System.Math.Min(amount, Free);
        if (accepted <= 0)
            return 0;
        return StoreInto(goodsId, accepted, state);
    }

    /// <summary>带状态的超限入库（无视容量）。</summary>
    public double StoreForceBatch(string goodsId, double amount, in TimedState state)
    {
        if (amount <= 0)
            return 0;
        return StoreInto(goodsId, amount, state);
    }

    /// <summary>
    /// 实际并堆写入：仅并入<b>同档</b>批次（鲜度档 + 已耗寿限档 + 翻新次数全同），
    /// 否则新建一批。并入时按份数加权平均状态，龄期取较早者（AgeDays 不回退）。
    /// </summary>
    private double StoreInto(string goodsId, double accepted, in TimedState state)
    {
        // 不参与时效的货品（矿石、书籍、废料等）恒为全新状态，天然并为一堆
        var incoming = TimedRules.Applies(goodsId)
            ? state
            : TimedState.New;

        foreach (var s in Stacks)
        {
            if (s.GoodsId != goodsId)
                continue;
            if (!TimedRules.SameBand(s.State, incoming))
                continue;
            var blended = TimedRules.Blend(s.State, s.Amount, incoming, accepted);
            s.SetState(blended);
            s.Amount += accepted;
            return accepted;
        }

        Stacks.Add(new GoodsStack { GoodsId = goodsId, Amount = accepted }.WithState(incoming));
        return accepted;
    }

    // ===== 出库 =====

    /// <summary>出库，返回实际取出份数；<b>最差优先</b>（先取快坏的批次）；取空的堆即移除。</summary>
    public double Take(string goodsId, double amount) => TakeBatch(goodsId, amount, out _);

    /// <summary>
    /// 带状态出库：除份数外给出所取批次（多批混合时按量加权平均）的时效状态，
    /// 供搬运场景"原样搬过去"（<see cref="StoreBatch"/> 接收）。
    /// </summary>
    public double TakeBatch(string goodsId, double amount, out TimedState state)
    {
        state = TimedState.New;
        if (amount <= 0.0001)
            return 0;

        double taken = 0;
        bool got = false;
        while (taken < amount - 0.0001)
        {
            int best = WorstIndex(goodsId);
            if (best < 0)
                break;
            var s = Stacks[best];
            double get = System.Math.Min(s.Amount, amount - taken);
            if (get <= 0.0001)
                break;

            state = got ? TimedRules.Blend(state, taken, s.State, get) : s.State;
            got = true;
            taken += get;
            s.Amount -= get;
            if (s.Amount <= 0.0001)
                Stacks.RemoveAt(best);
        }
        return taken;
    }

    /// <summary>
    /// 消耗型取出：需要 <paramref name="need"/> 份的<b>有效量</b>，按各批鲜度折算实际取用量。
    /// 鲜度 50% 的粮要吃双倍才饱（这就是软轴"效果打折"的落点）。
    /// 返回实际满足的有效量（≤ need）；已失效（效果≈0）的批次不参与正常消耗。
    /// </summary>
    public double ConsumeEffective(string goodsId, double need)
    {
        if (need <= 0.0001)
            return 0;

        double effective = 0;
        while (effective < need - 0.0001)
        {
            int best = WorstUsableIndex(goodsId);
            if (best < 0)
                break;
            var s = Stacks[best];
            float eff = TimedRules.EffectOf(s.Fresh);
            if (eff <= MinConsumeEffect)
                break;
            // 需要多少实物 = 缺口有效量 ÷ 本批折算系数
            double want = (need - effective) / eff;
            double take = System.Math.Min(s.Amount, want);
            if (take <= 0.0001)
                break;
            s.Amount -= take;
            effective += take * eff;
            if (s.Amount <= 0.0001)
                Stacks.RemoveAt(best);
        }
        return effective;
    }

    /// <summary>折算系数低于此值的批次视为已失效（不能作原用途消耗，避免除零放大取用量）。</summary>
    private const float MinConsumeEffect = 0.02f;

    /// <summary>找同货品中鲜度最低的一批（最差优先）。</summary>
    private int WorstIndex(string goodsId)
    {
        int best = -1;
        float bestFresh = float.MaxValue;
        for (int i = 0; i < Stacks.Count; i++)
        {
            var s = Stacks[i];
            if (s.GoodsId != goodsId || s.Amount <= 0.0001)
                continue;
            if (s.Fresh < bestFresh)
            {
                bestFresh = s.Fresh;
                best = i;
            }
        }
        return best;
    }

    /// <summary>找同货品中"仍可用于正常消耗"的最差批次（排除已失效批次）。</summary>
    private int WorstUsableIndex(string goodsId)
    {
        int best = -1;
        float bestFresh = float.MaxValue;
        for (int i = 0; i < Stacks.Count; i++)
        {
            var s = Stacks[i];
            if (s.GoodsId != goodsId || s.Amount <= 0.0001)
                continue;
            if (TimedRules.EffectOf(s.Fresh) <= MinConsumeEffect)
                continue;
            if (s.Fresh < bestFresh)
            {
                bestFresh = s.Fresh;
                best = i;
            }
        }
        return best;
    }

    /// <summary>全部堆计龄 +1 天（展示用存放时长；时效推进见 TimelinessSystem）。</summary>
    public void AgeOneDay()
    {
        foreach (var s in Stacks)
            s.AgeDays++;
    }
}

/// <summary>GoodsStack 的构造辅助（对象初始化器不便写 in 参数时用）。</summary>
internal static class GoodsStackExtensions
{
    /// <summary>就地写入时效状态并返回自身（便于集合初始化器链式写法）。</summary>
    internal static GoodsStack WithState(this GoodsStack stack, in TimedState st)
    {
        stack.SetState(st);
        return stack;
    }
}
