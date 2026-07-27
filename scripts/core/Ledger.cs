using System.Collections.Generic;

namespace Bianjing;

/// <summary>官库收支账本：按类目累计本月流水，月界轮转保留上月，供财政面板查阅。</summary>
public class Ledger
{
    /// <summary>本月各类目金额（收入为正、支出为负）。</summary>
    public Dictionary<string, double> Current = new();

    /// <summary>上月各类目金额。</summary>
    public Dictionary<string, double> Previous = new();

    /// <summary>记一笔流水（amount 收入为正、支出为负）。</summary>
    public void Add(string category, double amount)
    {
        if (amount == 0)
            return;
        Current[category] = Current.GetValueOrDefault(category) + amount;
    }

    /// <summary>月界轮转：本月转入上月，本月清零。</summary>
    public void Rotate()
    {
        Previous = Current;
        Current = new Dictionary<string, double>();
    }
}
