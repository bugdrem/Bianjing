using System;
using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 加工配方（批次六十七）：按工坊等级多对多耗料 + 燃料 + 副产品。
/// 索引 0 = 一级；数组长度为 1 时所有等级同配方（数据驱动向前兼容）。
/// 早期木头→木材一对一，等级越高所需材料越多、耗燃料，产出还带副产品（废料）。
/// </summary>
public class RecipeDef
{
    /// <summary>成品 id。</summary>
    public string Output = "";

    /// <summary>每级原料需求（多对多：原料 id → 每份成品耗量），索引 0 = 一级。</summary>
    public Dictionary<string, int>[] InputsByLevel = { new() };

    /// <summary>每级每份成品耗燃料（柴薪）量，索引 0 = 一级；0 = 不耗。</summary>
    public int[] FuelByLevel = { 0 };

    /// <summary>每级每份成品附带产出副产品（废料）量，索引 0 = 一级；0 = 无。</summary>
    public double[] ByproductRateByLevel = { 0 };

    /// <summary>指定等级的原料需求（多对多）。</summary>
    public Dictionary<string, int> InputsAt(int level) =>
        InputsByLevel[Math.Clamp(level - 1, 0, InputsByLevel.Length - 1)];

    /// <summary>指定等级的每份燃料耗量（柴薪）。</summary>
    public int FuelAt(int level) => FuelByLevel[Math.Clamp(level - 1, 0, FuelByLevel.Length - 1)];

    /// <summary>指定等级的每份副产品（废料）产量。</summary>
    public double ByproductAt(int level) =>
        ByproductRateByLevel[Math.Clamp(level - 1, 0, ByproductRateByLevel.Length - 1)];
}
