using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 一堆货品：统一仓储体系的最小单元。
/// AgeDays 记录入库天数（每日 +1，本批次仅计龄无游戏效果），为后期变质/鲜度系统铺垫。
/// 纯数据类（公共字段），可直接 JSON 序列化入存档。
/// </summary>
public class GoodsStack
{
    /// <summary>货品 id（见 Goods）。</summary>
    public string GoodsId = "";

    /// <summary>份数。</summary>
    public double Amount;

    /// <summary>入库天数（并堆时取较大值，即按最早一批计龄）。</summary>
    public int AgeDays;
}

/// <summary>
/// 统一库存：建筑仓房、居民背包（后期载具货舱同接口）、地面物资堆、
/// 各类持有者共用同一套容量/存取规则，后期变质等效果只需在此扩展。
/// 同一货品并为一堆（避免多堆管理复杂度），纯数据类可直接序列化。
/// </summary>
public class Inventory
{
    /// <summary>容量上限（份）；&lt;=0 视为无容量（存不进任何东西）。</summary>
    public double Capacity;

    /// <summary>货品堆列表（同货品至多一堆）。</summary>
    public List<GoodsStack> Stacks = new();

    /// <summary>库存总量（份）。</summary>
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

    /// <summary>指定货品的存量（份）。</summary>
    public double AmountOf(string goodsId)
    {
        foreach (var s in Stacks)
            if (s.GoodsId == goodsId)
                return s.Amount;
        return 0;
    }

    /// <summary>入库（受容量限制），返回实际入库份数；新货计龄从 0 起。</summary>
    public double Store(string goodsId, double amount)
    {
        double accepted = System.Math.Min(amount, Free);
        if (accepted <= 0)
            return 0;
        return StoreInto(goodsId, accepted);
    }

    /// <summary>超限入库：无视容量全部收下（村民背回的货不浪费）——
    /// 上限只作"继续派人采集/进货"的闸门，不作硬墙。</summary>
    public double StoreForce(string goodsId, double amount)
    {
        if (amount <= 0)
            return 0;
        return StoreInto(goodsId, amount);
    }

    /// <summary>实际并堆写入（Store/StoreForce 共用）。</summary>
    private double StoreInto(string goodsId, double accepted)
    {
        foreach (var s in Stacks)
        {
            if (s.GoodsId == goodsId)
            {
                s.Amount += accepted; // 并堆：龄期保留较早一批（AgeDays 不回退）
                return accepted;
            }
        }
        Stacks.Add(new GoodsStack { GoodsId = goodsId, Amount = accepted });
        return accepted;
    }

    /// <summary>出库，返回实际取出份数；取空的堆即移除。</summary>
    public double Take(string goodsId, double amount)
    {
        for (int i = 0; i < Stacks.Count; i++)
        {
            var s = Stacks[i];
            if (s.GoodsId != goodsId)
                continue;
            double taken = System.Math.Min(s.Amount, amount);
            if (taken <= 0)
                return 0;
            s.Amount -= taken;
            if (s.Amount <= 0.0001)
                Stacks.RemoveAt(i);
            return taken;
        }
        return 0;
    }

    /// <summary>全部堆计龄 +1 天（每日结算调用；变质效果留后期在此挂接）。</summary>
    public void AgeOneDay()
    {
        foreach (var s in Stacks)
            s.AgeDays++;
    }
}
