namespace Bianjing;

/// <summary>
/// 货币格式化工具：文 → 显示字符串。
/// 铜钱（文）为唯一内部单位；大额以白银（两 / 万两）展示（需求 §1.2）。黄金单位已废除（批次九十三）。
/// </summary>
public static class CurrencyHelper
{
   /// <summary>文 → 日常显示（三级制）："XXX文" / "X两Y文" / ≥1万两时 "X万两Y两Z文"（零值段省略）。</summary>
    public static string FormatWen(long wen)
    {
        if (wen < 0)
            return $"-{FormatWen(-wen)}";
        if (wen < CurrencyConfig.SilverDisplayThreshold)
            return $"{wen}文";
        long wanLiang = wen / CurrencyConfig.WenPerWanLiang;
        long silver = (wen % CurrencyConfig.WenPerWanLiang) / CurrencyConfig.WenPerSilver;
        long coins = wen % CurrencyConfig.WenPerSilver;
        if (wanLiang > 0)
        {
            // 万两主段必显；两/文仅在非零时缀上（"1万两"、"1万两2两"、"1万两2两3文"）
            string s = $"{wanLiang}万两";
            if (silver > 0)
                s += $"{silver}两";
            if (coins > 0)
                s += $"{coins}文";
            return s;
        }
        return silver > 0 && coins > 0 ? $"{silver}两{coins}文" : $"{silver}两";
    }

    /// <summary>文 → 国库总览显示："白银 X万两 Y两 Z文"（万两段为 0 时省略）。</summary>
    public static string FormatTreasury(long wen)
    {
        if (wen < 0)
            return $"-{FormatTreasury(-wen)}";
        long wanLiang = wen / CurrencyConfig.WenPerWanLiang;
        long silver = (wen % CurrencyConfig.WenPerWanLiang) / CurrencyConfig.WenPerSilver;
        long coins = wen % CurrencyConfig.WenPerSilver;
        if (wanLiang > 0)
            return $"白银 {wanLiang}万两 {silver}两 {coins}文";
        return $"白银 {silver}两 {coins}文";
    }

    /// <summary>文 → 两（浮点，供计算用）。</summary>
    public static double ToSilver(long wen) => (double)wen / CurrencyConfig.WenPerSilver;

    /// <summary>两 → 文。</summary>
    public static long FromSilver(double silver) => (long)(silver * CurrencyConfig.WenPerSilver);
}
