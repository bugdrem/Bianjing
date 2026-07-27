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

/// <summary>植物实体：固定生长，成熟后向周围散播幼体（PlantGrowthSystem 驱动）。</summary>
public class PlantObj : Obj
{
    /// <summary>长成大树所需月数。</summary>
    public const int MatureMonths = 12;

    /// <summary>生长月龄（每月 +1，达到 MatureMonths 即成熟）。</summary>
    public int GrowthMonths;

    public bool Mature => GrowthMonths >= MatureMonths;

    /// <summary>生长进度 0-1（渲染尺寸用）。</summary>
    public float GrowthRatio => GrowthMonths >= MatureMonths ? 1f : (float)GrowthMonths / MatureMonths;
}

/// <summary>动物实体：在树林附近随机活动与繁育（WildlifeSystem 驱动），可被猎人捕获。</summary>
public class AnimalObj : Obj
{
    public int AgeMonths;
}
