using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 所有游戏实体的抽象基类：建筑 / 动物 / 植物统一继承，
/// 提供网格位置与 mod 扩展位，便于后期新增实体类型（矿脉、船只、车马等）。
/// 纯数据类（不含 Godot 类型），可直接 JSON 序列化入存档。
/// </summary>
public abstract class Obj
{
    public int Id;

    /// <summary>所在格坐标。</summary>
    public int X;
    public int Y;

    /// <summary>mod / 未来系统扩展字段。</summary>
    public Dictionary<string, string> Extra = new();
}

/// <summary>植物实体：固定生长，成熟后向周围散播幼体；果树成树逐日挂果，过熟掉落成地面果堆（PlantGrowthSystem 驱动）。
/// 砍伐为血量制：每斧扣血而非一击砍倒，无人砍伐一段时间后逐日回血。</summary>
public class PlantObj : Obj
{
    /// <summary>长成大树所需月数（调参见 configs/PlantConfig）。</summary>
    public const int MatureMonths = PlantConfig.MatureMonths;

    /// <summary>挂果上限（份）：树上未掉落的果实也是一类仓储（典型案例四）。</summary>
    public const double FruitCap = PlantConfig.FruitCap;

    /// <summary>新芽基础血量与随龄增量上限（渐进上界 BaseHp+HpGainCap）。</summary>
    public const float BaseHp = PlantConfig.BaseHp;
    public const float HpGainCap = PlantConfig.HpGainCap;

    /// <summary>是否果树：果树是树的一种（同样可砍柴），但只有果树才挂果产水果；
    /// 自然生成比例约 1:10，散播幼体继承母树类型。</summary>
    public bool IsFruitTree;

    /// <summary>生长月龄（每月 +1，达到 MatureMonths 即成熟；成熟后继续累积作树龄，驱动血量缓涨）。</summary>
    public int GrowthMonths;

    /// <summary>树上挂果存量（份）：成树逐日增长，采摘消耗，挂满过熟概率掉落地面。</summary>
    public double FruitStock;

    /// <summary>当前砍伐血量（新植时由 AddPlant 补满至 MaxHp）。</summary>
    public float Hp = BaseHp;

    /// <summary>距上次被砍的天数：达到恢复延迟后逐日回血（被砍即清零）。</summary>
    public int IdleDays;

    public bool Mature => GrowthMonths >= MatureMonths;

    /// <summary>生长进度 0-1（渲染尺寸用）。</summary>
    public float GrowthRatio => GrowthMonths >= MatureMonths ? 1f : (float)GrowthMonths / MatureMonths;

    /// <summary>满血上限：随树龄增长但增速递减（公式见 PlantConfig.MaxHpAt：
    /// 0月=20，1年≈47，2年=60，5年≈77，20年≈93）。</summary>
    public float MaxHp => PlantConfig.MaxHpAt(GrowthMonths);
}

/// <summary>动物实体：在树林附近随机活动与繁育（WildlifeSystem 驱动），可被猎人捕获。</summary>
public class AnimalObj : Obj
{
    public int AgeMonths;

    /// <summary>物种索引（对应 AnimalModelConfig 的模型序号）：决定渲染外形。
    /// 生成时按 Id 轮转分配（GameState.AddAnimal），七种外形均布出现；
    /// 旧存档无此字段时反序列化为默认 0，不影响读档。</summary>
    public int Kind;
}

/// <summary>
/// 地面物资堆：散落在地图上的货品（典型案例三）——
/// 农作物收获、猎物倒地、过熟落果等均以此形式落地，等待居民（后期载具）拾取搬运。
/// 一格至多一堆（GameState.Piles 以格索引为键），拾空即消。
/// </summary>
public class ItemPileObj : Obj
{
    /// <summary>单堆容量（份，调参见 configs/EconomyConfig）：满堆后多余收成烂在地里。</summary>
    public const double PileCapacity = EconomyConfig.PileCapacity;

    /// <summary>堆内货品（统一仓储接口，随存档序列化）。</summary>
    public Inventory Inv = new() { Capacity = PileCapacity };
}
