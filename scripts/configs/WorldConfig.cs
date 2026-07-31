namespace Bianjing;

/// <summary>
/// 道路与世界配置：铺路造价与宽度、开局资源与记录上限
/// （业务归属：GameState 铺路盖戳扣款/开局初值/履历与公告截断）。
/// </summary>
public static class WorldConfig
{
    /// <summary>主路 / 辅路每延米造价（贯，不计宽度）。</summary>
    public const int MainRoadCost = 18;
    public const int SideRoadCost = 10;

    /// <summary>主路 / 辅路占地宽度（米/格，方形画笔盖戳宽）。</summary>
    public const int MainRoadWidth = 4;
    public const int SideRoadWidth = 2;

    /// <summary>桥梁每延米造价（贯）/ 占地宽度（米/格，独立桥工具用；画路跨水时桥随路同宽）。</summary>
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
    public const float BridgeArchApexRise = 1f;

    /// <summary>桥体厚（米）：桥面为实体板而非平面，从桥面顶向下拉出此厚度的侧壁与底面。</summary>
    public const float BridgeBodyThickness = 0.2f;

    /// <summary>引桥过渡最大延伸格数：桥旁陆地路格按离桥距从桥面高渐降到岸路面高，
    /// 形成引桥斜坡与道路无缝相接（岸坡越陡、高差越大，实际引桥越长，上限此值）。</summary>
    public const int BridgeRampCells = 3;

    /// <summary>地图向外扩展的白底边室宽（米）：地图与卷轴之间垫一层白底，
    /// 白底在地图四周外扩此距离（形成地图到卷轴画布的白边过渡）。</summary>
    public const float MapEdgeExtend = 64f;

    /// <summary>开局官库钱（贯）/ 官粮（份）。</summary>
    public const double StartMoney = 5000;
    public const double StartFood = 500;

    /// <summary>居民履历条数上限 / 全城公告条数上限（超出移除最旧）。</summary>
    public const int LifeEventCap = 40;
    public const int NewsCap = 200;
}
