using System.Collections.Generic;

namespace Bianjing;

/// <summary>存档元信息（版本号用于格式校验：早期开发不做跨版本兼容，版本不符直接拒读）。</summary>
public class SaveMeta
{
    public int Version;
    public int Year;
    public int Month;
    public int Day;
    public int Hour;

    public long SavedAtUnix;
    public string CityName = "";
    public string SaveName = "";
}

/// <summary>存档列表条目（读档界面展示用）。</summary>
public class SaveInfo
{
    public string Slot = "";
    public string CityName = "";
    public string SaveName = "";
    public int Year;
    public int Month;
    public long SavedAtUnix;
}

/// <summary>全局世界数据。</summary>
public class WorldSave
{
    public double Money;
    public double Food;
    public int NextBuildingId;
    public int NextCitizenId;
    public int NextFamilyId;
    public int NextPlantId;
    public int NextAnimalId;
    public int NextPileId;

    /// <summary>税收政策：税种 Id -&gt; 档位。</summary>
    public Dictionary<string, int> TaxLevels = new();

    /// <summary>v3 起：官库账本（本月/上月分类流水）。</summary>
    public Dictionary<string, double> LedgerCur = new();
    public Dictionary<string, double> LedgerPrev = new();
}

/// <summary>地图数据：各类地表格用一维索引（y*Size+x）紧凑存储。</summary>
public class MapSave
{
    public List<int> RoadCells = new();
    public List<int> ZoneCells = new();
    public List<int> ZoneTypes = new();
    public List<int> WaterCells = new();
    public List<int> BridgeCells = new();
}

/// <summary>建筑实例 DTO（BuildingInstance 含 Godot 类型与 Def 引用，不直接序列化）。</summary>
public class BuildingSave
{
    public int Id;
    public string DefId = "";
    public int X;
    public int Y;
    public int Level;
    public float Condition;

    /// <summary>建造日期、专营货品、统一库存（v4 起堆列表）与农时计数。</summary>
    public int BuiltYear;
    public int BuiltMonth;
    public string Specialty = "";
    public Inventory Inv = new();
    public int MonthsSinceHarvest;

    /// <summary>v6：废弃标志（无人居住的 grown 建筑）。</summary>
    public bool Abandoned;
}
