namespace Bianjing;

/// <summary>
/// 货币换算与显示规则：
/// 铜钱（文）为基础单位，1两白银 = 1000文，1两黄金 = 10两白银 = 10000文。
/// 日常交易全部以文结算；白银/黄金仅用于大额显示与国库总览。
/// （业务归属：CurrencyHelper 格式化、TopBar/FinancePanel 显示、GameState.Money 存储）
/// </summary>
public static class CurrencyConfig
{
    /// <summary>1两白银兑铜钱数（文）。</summary>
    public const long WenPerSilver = 1_000;

    /// <summary>1两黄金兑白银数（两）。</summary>
    public const long SilverPerGold = 10;

    /// <summary>1两黄金兑铜钱数（文）= 10000。</summary>
    public const long WenPerGold = WenPerSilver * SilverPerGold;

    /// <summary>显示切换阈值（文）：&lt; 此值只显示"XXX文"，&gt;= 此值显示"X两Y文"。</summary>
    public const long SilverDisplayThreshold = WenPerSilver;
}
