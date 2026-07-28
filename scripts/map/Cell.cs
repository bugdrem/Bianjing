namespace Bianjing;

/// <summary>坊区类型：只保留「可建设区」——居民在区内自行决定建房/开店/设工坊（由生长系统按需配比）。</summary>
public enum ZoneType
{
    None,
    /// <summary>可建设区：AI 居民自动建造民居/商铺/工坊的划定范围。</summary>
    Buildable,
}

/// <summary>单个地图格子的逻辑数据。</summary>
public struct Cell
{
    public bool HasRoad;

    /// <summary>是否有河水（不可建造，架桥除外）。</summary>
    public bool HasWater;

    /// <summary>是否有桥（架在水上，等效道路可通行）。</summary>
    public bool HasBridge;

    /// <summary>是否有树木（实体数据见 GameState.Plants，此处为快速查询缓存；铺路/建房时自动砍伐）。</summary>
    public bool HasTree;

    /// <summary>占用该格的建筑实例 Id，-1 表示无建筑。</summary>
    public int BuildingId;

    public ZoneType Zone;

    /// <summary>吸引力缓存，由 DesirabilitySystem 重算。</summary>
    public float Desirability;

    public readonly bool IsEmpty => !HasRoad && !HasWater && BuildingId < 0;
}
