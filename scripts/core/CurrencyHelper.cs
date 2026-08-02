namespace Bianjing;

/// <summary>
/// 货币格式化工具：文 → 显示字符串。
/// 铜钱（文）为唯一内部单位；白银/黄金仅用于 UI 展示（需求 §1.2）。
/// </summary>
public static class CurrencyHelper
{
    /// <summary>文 → 日常显示："XXX文" 或 "X两Y文"。</summary>
    public static string FormatWen(long wen)
    {
        if (wen < 0)
            return $"-{FormatWen(-wen)}";
        if (wen < CurrencyConfig.SilverDisplayThreshold)
            return $"{wen}文";
        long silver = wen / CurrencyConfig.WenPerSilver;
        long remainder = wen % CurrencyConfig.WenPerSilver;
        return remainder > 0 ? $"{silver}两{remainder}文" : $"{silver}两";
    }

    /// <summary>文 → 国库总览显示："金X两，银X两，钱X文"。</summary>
    public static string FormatTreasury(long wen)
    {
        if (wen < 0)
            return $"-{FormatTreasury(-wen)}";
        long gold = wen / CurrencyConfig.WenPerGold;
        long silver = (wen % CurrencyConfig.WenPerGold) / CurrencyConfig.WenPerSilver;
        long coins = wen % CurrencyConfig.WenPerSilver;
        return $"金{gold}两，银{silver}两，钱{coins}文";
    }

    /// <summary>文 → 两（浮点，供计算用）。</summary>
    public static double ToSilver(long wen) => (double)wen / CurrencyConfig.WenPerSilver;

    /// <summary>两 → 文。</summary>
    public static long FromSilver(double silver) => (long)(silver * CurrencyConfig.WenPerSilver);
}
