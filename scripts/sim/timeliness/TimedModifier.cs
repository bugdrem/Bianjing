namespace Bianjing;

/// <summary>时效所属的实体类别——修正源可据此只对某几类生效。</summary>
public enum TimedEntityKind
{
    /// <summary>货品（可能在仓房、背包或地面堆）。</summary>
    Goods = 0,

    /// <summary>房屋建筑。</summary>
    Building = 1,

    /// <summary>道路格。</summary>
    Road = 2,

    /// <summary>植物（树）。</summary>
    Plant = 3,

    /// <summary>动物。</summary>
    Animal = 4,

    /// <summary>人。</summary>
    Person = 5,
}

/// <summary>物品所处的位置形态——决定基础防腐条件。</summary>
public enum TimedPlace
{
    /// <summary>露天堆放（地面物资堆、无建筑覆盖）：基准环境（倍率 1.0）。</summary>
    Ground = 0,

    /// <summary>有顶室内（建筑覆盖且非露棚）：遮阳挡雨，防腐主来源。</summary>
    Sheltered = 1,

    /// <summary>无顶棚屋（建筑覆盖但 NoRoof）：遮阳不挡湿。</summary>
    OpenShed = 2,

    /// <summary>随身携带（村民/访客背包）：颠簸日晒。</summary>
    Carried = 3,
}

/// <summary>
/// 时效修正量：各修正源<b>乘算累积</b>，中性值为 1.0。
/// 只放"能改变时间走向"的两个量——环境不直接改当前鲜度（那是翻新动作的事）。
/// </summary>
public struct TimedRates
{
    /// <summary>软轴衰减速率倍率（&gt;1 坏得更快）。</summary>
    public float FreshRate;

    /// <summary>硬轴寿限阈值倍率（&gt;1 总保质期更长）——"入库续命"即此值。</summary>
    public float LifespanSpan;

    /// <summary>中性倍率（露天 + 春季 + 无翻新）。</summary>
    public static TimedRates Neutral => new() { FreshRate = 1f, LifespanSpan = 1f };

    /// <summary>乘算软轴速率倍率。</summary>
    public void MulRate(float m) => FreshRate *= m;

    /// <summary>乘算硬轴寿限倍率。</summary>
    public void MulSpan(float m) => LifespanSpan *= m;
}

/// <summary>
/// 时效判定的上下文：修正源据此决定是否命中、命中后改多少。
/// 刻意保持为纯数据（不含 Godot 类型），以便在存档/预览/离线计算中复用。
/// </summary>
public readonly struct TimedContext
{
    /// <summary>实体类别。</summary>
    public readonly TimedEntityKind Kind;

    /// <summary>货品 id（仅 Kind == Goods 时有意义，否则为空串）。</summary>
    public readonly string GoodsId;

    /// <summary>所处位置形态。</summary>
    public readonly TimedPlace Place;

    /// <summary>所在格索引（y * MapGrid.Size + x）；-1 表示无位置（随身携带）。</summary>
    public readonly int CellIndex;

    /// <summary>当前游戏月 1~12（驱动季节修正）。</summary>
    public readonly int Month;

    /// <summary>是否临水（四向邻格有水面）。</summary>
    public readonly bool NearWater;

    public TimedContext(TimedEntityKind kind, string goodsId, TimedPlace place,
        int cellIndex, int month, bool nearWater)
    {
        Kind = kind;
        GoodsId = goodsId;
        Place = place;
        CellIndex = cellIndex;
        Month = month;
        NearWater = nearWater;
    }

    /// <summary>货品上下文（仓库 / 地面堆 / 背包共用）。</summary>
    public static TimedContext ForGoods(string goodsId, TimedPlace place, int cellIndex, int month, bool nearWater)
        => new(TimedEntityKind.Goods, goodsId, place, cellIndex, month, nearWater);

    /// <summary>非货品实体上下文（房屋/道路/植物/动物/人）。</summary>
    public static TimedContext ForEntity(TimedEntityKind kind, TimedPlace place, int cellIndex, int month, bool nearWater)
        => new(kind, "", place, cellIndex, month, nearWater);
}

/// <summary>
/// 时效修正源：任何环境因素、存储方式、存储设施都可实现本接口并注册，
/// 新增一种（地窖、熏制、冰鉴、地火炕……）无需改动核心衰减器。
///
/// 约定：
///   ① 只改倍率，不改状态值——改状态值是"翻新动作"的职责；
///   ② "露天 + 春季"为中性基准（1.0），修正源只描述偏离基准的程度；
///   ③ <see cref="Order"/> 小者先应用，便于后续源覆盖前者的近似值。
/// </summary>
public interface ITimedModifier
{
    /// <summary>唯一标识（用于调试与去重）。</summary>
    string Id { get; }

    /// <summary>应用顺序（小者先）。</summary>
    int Order { get; }

    /// <summary>当前上下文是否命中本修正源。</summary>
    bool Applies(in TimedContext ctx);

    /// <summary>就地修改倍率（乘算累积）。</summary>
    void Apply(in TimedContext ctx, ref TimedRates rates);
}
