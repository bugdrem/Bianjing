namespace Bianjing;

/// <summary>
/// 货币换算与显示规则：
/// 铜钱（文）为唯一基础单位，1 两白银 = 1000 文；1 万两白银 = 10000 两 = 10,000,000 文。
/// 日常交易全部以文结算；白银（两 / 万两）仅用于大额显示与国库总览。黄金单位已废除（批次九十三）。
/// （业务归属：CurrencyHelper 格式化、TopBar/FinancePanel 显示、GameState.Money 存储）
/// </summary>
public static class CurrencyConfig
{
    /// <summary>1两白银兑铜钱数（文）。</summary>
    public const long WenPerSilver = 1_000;

    /// <summary>1万两白银兑白银数（两）：万两为大额计数单位（10,000 两白银）。</summary>
    public const long SilverPerWanLiang = 10_000;

    /// <summary>1万两白银兑铜钱数（文）= 10,000,000。</summary>
    public const long WenPerWanLiang = WenPerSilver * SilverPerWanLiang;

    /// <summary>显示切换阈值（文）：&lt; 此值只显示"XXX文"，&gt;= 此值显示"X两Y文"；&gt;= WenPerWanLiang 显示"X万两Y两Z文"。</summary>
    public const long SilverDisplayThreshold = WenPerSilver;
}
