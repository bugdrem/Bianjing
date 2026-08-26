using Godot;

namespace Bianjing;

/// <summary>
/// 道路与世界配置：铺路造价与宽度、开局资源与记录上限
/// （业务归属：GameState 铺路盖戳扣款/开局初值/履历与公告截断）。
/// </summary>
public static class WorldConfig
{
    /// <summary>主路 / 辅路每延米造价（文，不计宽度）。</summary>
    public const int MainRoadCost = 18;
    public const int SideRoadCost = 10;

    /// <summary>小路每格造价（文）：村民自建小路环的计价基准，后续贴路建房时按半价补偿原屋主（见 ZoneGrowthSystem）。</summary>
    public const long LaneCost = 10;

    /// <summary>主路 / 辅路占地宽度（米/格，方形画笔盖戳宽）。</summary>
    public const int MainRoadWidth = 4;
    public const int SideRoadWidth = 2;

    /// <summary>桥梁每延米造价（文）/ 占地宽度（米/格，独立桥工具用；画路跨水时桥随路同宽）。</summary>
    public const int BridgeCost = 30;
    public const int BridgeWidth = 4;

    /// <summary>路面高出所在地面的抬升（米）：渲染贴地路面与村民路上站面共用，
    /// 抬高避免与地形三角网格 z-fighting，也让路面在草地上有可读的台面感。</summary>
    public const float RoadSurfaceLift = 0.1f;

    /// <summary>道路地基深度（米）：路面外边缘（邻非路/非桥格）向下垂一圈基座立面，
    /// 路面读作坐在 1m 高台基上（而非贴地一层皮），斜坡上也不镂空。</summary>
    public const float RoadFoundationDepth = 1f;

    /// <summary>建筑底面高出垫基台面的抬升（米）：房体/门/屋顶整体抬起，免与地表穿插。</summary>
    public const float BuildingBaseLift = 0.1f;

    /// <summary>建筑地基深度（米）：房体下方不透明基座向下延伸，斜坡上建造时遮住悬空的底部。</summary>
    public const float FoundationDepth = 2f;

    /// <summary>拱桥拱顶高出「两岸较低者地面」的封顶抬升（米）：整段跨水为一座拱，
    /// 拱顶（河中央）= min(两岸地面高) + 此值（见 MapGrid.BridgeDeckTopAt）；渲染桥体与村民过桥站面共用。</summary>
    public const float BridgeArchApexRise = 2f;

    /// <summary>桥体厚（米）：桥面为实体板而非平面，从桥面顶向下拉出此厚度的侧壁与底面。</summary>
    public const float BridgeBodyThickness = 0.2f;

    /// <summary>引桥过渡最大延伸格数：桥旁陆地路格按离桥距从桥面高渐降到岸路面高，
    /// 形成引桥斜坡与道路无缝相接（岸坡越陡、高差越大，实际引桥越长，上限此值）。</summary>
    public const int BridgeRampCells = 3;

    /// <summary>地图向外扩展的白底边室宽（米）：地图与卷轴之间垫一层白底，
    /// 白底在地图四周外扩此距离（形成地图到卷轴画布的白边过渡）。</summary>
    public const float MapEdgeExtend = 32f;

    /// <summary>开局官库钱（文）/ 官粮（份）。安家银值匹配 EconomyConfig.SettlementGrant。</summary>
    public const long StartMoney = 100_000;
    public const double StartFood = 500;

    // ---- 昼夜光照（批次七十四）：白天/夜晚两档能量与过渡速率，夜间调暗但保持可操作 ----

    /// <summary>白天/夜晚主光能量（DirectionalLight.LightEnergy）。</summary>
    public const float DaySunEnergy = 0.95f;
    public const float NightSunEnergy = 0.15f;

    /// <summary>白天/夜晚环境光能量（Environment.AmbientLightEnergy）。</summary>
    public const float DayAmbientEnergy = 0.5f;
    public const float NightAmbientEnergy = 0.3f;

    /// <summary>晨昏（地平线附近）主光/环境光能量：用于随太阳高度角插值。
    /// 主光弱、环境光强 → 晨昏阴影“浅”（长而柔）；正午主光强、环境光弱 → 阴影“深”（短而硬）。</summary>
    public const float DawnSunEnergy = 0.5f;
    public const float DawnAmbientEnergy = 0.68f;

    /// <summary>夜晚环境光改为固定中性色（而非采样蓝天），避免地图泛蓝。
    /// 取低饱和的冷灰，能量低、不抢月光主光。</summary>
    public static readonly Color NightAmbientColor = new(0.50f, 0.52f, 0.55f);

    // ---- 天空色（白天淡灰蓝 / 夜晚蓝黑），由 Main 在昼夜间平滑插值 ----
    public static readonly Color DaySkyTop = new(0.40f, 0.50f, 0.66f);
    public static readonly Color DaySkyHorizon = new(0.68f, 0.74f, 0.82f);
    public static readonly Color DaySkyGround = new(0.64f, 0.70f, 0.76f);
    public static readonly Color NightSkyTop = new(0.04f, 0.06f, 0.12f);
    public static readonly Color NightSkyHorizon = new(0.10f, 0.14f, 0.22f);
    public static readonly Color NightSkyGround = new(0.08f, 0.10f, 0.14f);

    /// <summary>昼夜过渡速率（1/秒，指数逼近系数；取值越小过渡越平缓，约 5–6 秒完成大半过渡）。</summary>
    public const float DayNightSmoothPerSec = 0.5f;

    // ---- 天空天体（太阳/月亮）：配色与参数，集中此处便于微调 ----

    /// <summary>太阳颜色：地平（早/黄昏）红黄，正午转白。同步作用于太阳精灵与平行光，营造早晚金辉。</summary>
    public static readonly Color SunWarmColor = new(1.0f, 0.45f, 0.16f);
    public static readonly Color SunNoonColor = new(1.0f, 0.97f, 0.90f);
    /// <summary>月盘冷白 / 月照冷蓝。</summary>
    public static readonly Color MoonTintColor = new(0.82f, 0.86f, 1.0f);
    public static readonly Color MoonLightColor = new(0.55f, 0.65f, 0.95f);
    /// <summary>满月且完全可见时的月光能量（DirectionalLight.LightEnergy）；实际能量再乘可见度与相位受光比例，
    /// 故夜晚阴影深浅随月亮亮度变化（满月硬、弦月软）。</summary>
    public const float MoonBaseEnergy = 0.7f;
    /// <summary>月相周期（天）：与月份挂扣——一个游戏月走完一次朔望循环（新月→满月→新月）。</summary>
    public static readonly float MoonCycleDays = TimeConfig.DaysPerMonth;

    /// <summary>居民履历条数上限 / 全城公告条数上限（超出移除最旧）。</summary>
    public const int LifeEventCap = 40;
    public const int NewsCap = 200;
}
