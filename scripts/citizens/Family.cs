using System.Collections.Generic;

namespace Bianjing;

/// <summary>家庭：共享住所与公产。典型形态为夫妻 + 子女 + 父母，也可为单身户。</summary>
public class Family
{
    public int Id;
    public List<int> MemberIds = new();
    public int HomeId = -1;

    /// <summary>家庭公产（婚嫁/迁入时注入，日常开销优先扣此）。</summary>
    public double SharedAssets;

    /// <summary>家庭总资产 = 公产 + 各成员私产。</summary>
    public double TotalAssets(GameState gs)
    {
        double total = SharedAssets;
        foreach (var id in MemberIds)
            if (gs.Citizens.TryGetValue(id, out var c))
                total += c.Money;
        return total;
    }
}
