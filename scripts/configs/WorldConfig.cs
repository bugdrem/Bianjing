namespace Bianjing;

/// <summary>
/// 道路与世界配置：铺路造价、开局资源与记录上限
/// （业务归属：GameState 铺路盖戳扣款/开局初值/履历与公告截断；道路宽度是几何结构，仍留 GameState）。
/// </summary>
public static class WorldConfig
{
    /// <summary>主路 / 辅路每延米造价（贯，不计宽度）。</summary>
    public const int MainRoadCost = 18;
    public const int SideRoadCost = 10;

    /// <summary>桥梁每延米造价（贯）。</summary>
    public const int BridgeCost = 30;

    /// <summary>开局官库钱（贯）/ 官粮（份）。</summary>
    public const double StartMoney = 5000;
    public const double StartFood = 500;

    /// <summary>居民履历条数上限 / 全城公告条数上限（超出移除最旧）。</summary>
    public const int LifeEventCap = 40;
    public const int NewsCap = 200;
}
