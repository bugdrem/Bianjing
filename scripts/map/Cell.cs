namespace Bianjing;

/// <summary>坊区类型：只保留「可建设区」——居民在区内自行决定建房/开店/设工坊（由生长系统按需配比）。</summary>
public enum ZoneType
{
    None,
    /// <summary>可建设区：AI 居民自动建造民居/商铺/工坊的划定范围。</summary>
    Buildable,
}

/// <summary>道路种类：无/小路/辅路/主路——小路为村民自建住宅四周自动生成（可通行、可作新房临路依据），
/// 移速最慢且随房屋拆除；辅路/主路由玩家绘制。移速与寻路权重见 MovementConfig。</summary>
public enum RoadKind
{
    None,
    Side,
    Main,
    /// <summary>小路：村民建房自动铺设的窄路，性质同普通道路但移速较慢，随房拆除（共享则保留）。</summary>
    Lane,
}

/// <summary>单个地图格子的逻辑数据。</summary>
public struct Cell
{
    public bool HasRoad;

    /// <summary>道路种类（仅普通道路有意义；桥面 HasRoad 为真但种类为 None）。</summary>
    public RoadKind RoadKind;

    /// <summary>是否有河水（不可建造，架桥除外）。</summary>
    public bool HasWater;

    /// <summary>水流方向（仅河/溪有意义，湖泊为静水 0）：0=静水，1-8 为八方向（1=东,顺时针到 8=东北），
    /// 见 RiverGenerator.EncodeFlow。仅世界生成期赋值、随存档保存，供未来水流表现/生态取用。</summary>
    public byte FlowDir;

    /// <summary>是否有桥（架在水上，等效道路可通行）。</summary>
    public bool HasBridge;

    /// <summary>是否有树木（实体数据见 GameState.Plants，此处为快速查询缓存；铺路/建房时自动砍伐）。</summary>
    public bool HasTree;

    /// <summary>占用该格的建筑实例 Id，-1 表示无建筑。</summary>
    public int BuildingId;

    public ZoneType Zone;

    /// <summary>地形高度层（整数台地，0=平地基准，越大越高）：世界海拔 = Height×TerrainConfig.LayerHeight。
    /// 相邻格层差即台阶，通行/铺路是否可行见 TerrainConfig.Traversable。仅世界生成期由山体隆起赋值。</summary>
    public int Height;

    /// <summary>吸引力缓存，由 DesirabilitySystem 重算。</summary>
    public float Desirability;

    public readonly bool IsEmpty => !HasRoad && !HasWater && BuildingId < 0;
}
