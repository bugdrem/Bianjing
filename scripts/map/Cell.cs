namespace Bianjing;

/// <summary>坊区类型：只保留「可建设区」——居民在区内自行决定建房/开店/设工坊（由生长系统按需配比）。</summary>
public enum ZoneType
{
    None,
    /// <summary>可建设区：AI 居民自动建造民居/商铺/工坊的划定范围。</summary>
    Buildable,

    /// <summary>耕种区：划区后农艺村民自动开垦农田（见 FarmlandSystem）；水系/道路/建筑不可划入。</summary>
    Farmland,
}

/// <summary>道路种类：无/小路/辅路/主路——小路为村民自建住宅四周自动生成（可通行、可作新房临路依据），
/// 移速最慢；小路是独立个体：归属登记在 LaneOwnerId，房屋拆除后依旧存续，新村民可直接占路重建（批次六十六）。
/// 辅路/主路由玩家绘制。移速与寻路权重见 MovementConfig。</summary>
public enum RoadKind
{
    None,
    Side,
    Main,
    /// <summary>小路：村民建房自动铺设的窄路，性质同普通道路但移速较慢，独立存续不随房拆除。</summary>
    Lane,
}

/// <summary>单个地图格子的逻辑数据。</summary>
public struct Cell
{
    /// <summary>字段初始化需显式构造（CS8983）；地图数组元素为 default 不经此构造，
    /// 但判断均以 RoadKind==Lane 为前提，LaneOwnerId 默认 0 不会被误读为有主小路。</summary>
    public Cell()
    {
        LaneOwnerId = -1;
    }

    public bool HasRoad;

    /// <summary>道路种类（仅普通道路有意义；桥面 HasRoad 为真但种类为 None）。</summary>
    public RoadKind RoadKind;

    /// <summary>是否有河水（不可建造，架桥除外）。</summary>
    public bool HasWater;

    /// <summary>水流方向（仅河/溪有意义，湖泊为静水 0）：0=静水，1-8 为八方向（1=东,顺时针到 8=东北），
    /// 见 RiverGenerator.EncodeFlow。仅世界生成期赋值、随存档保存，供未来水流表现/生态取用。</summary>
    public byte FlowDir;

    /// <summary>本格水面海拔（米，仅水格有意义）：沿程随地势逐级下降、下限 0（WaterConfig.MinWaterLevel），
    /// 湖面同湖统一。渲染水面/桥面高度/河床下压均以此为准，随存档保存。</summary>
    public float WaterH;

    /// <summary>是否有桥（架在水上，等效道路可通行）。</summary>
    public bool HasBridge;

    /// <summary>是否有树木（实体数据见 GameState.Plants，此处为快速查询缓存；铺路/建房时自动砍伐）。</summary>
    public bool HasTree;

    /// <summary>小路归属的建筑 Id（批次六十六：小路独立个体，铺路时登记；-1=无主小路/非小路格。
    /// 新村民贴有主小路建房时按半价补偿原屋主并转无主，屋主拆除后其名下小路自动转无主）。</summary>
    public int LaneOwnerId = -1;

    /// <summary>占用该格的建筑实例 Id，-1 表示无建筑。</summary>
    public int BuildingId;

    public ZoneType Zone;

    /// <summary>吸引力缓存，由 DesirabilitySystem 重算。</summary>
    public float Desirability;

    public readonly bool IsEmpty => !HasRoad && !HasWater && BuildingId < 0;
}
