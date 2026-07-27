using System.Collections.Generic;

namespace Bianjing;

/// <summary>存档元信息（版本号用于未来格式迁移）。</summary>
public class SaveMeta
{
    public int Version;
    public int Year;
    public int Month;

    /// <summary>v3 起：日/小时（老档为 0，读档时回退默认）。</summary>
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

    /// <summary>v1 老档兼容：只存树格位；v2 起树木改存 plants 实体列表。</summary>
    public List<int> TreeCells = new();
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

    /// <summary>v3 起：建造日期（老档为 0 显示「不详」）、专营货品与库存。</summary>
    public int BuiltYear;
    public int BuiltMonth;
    public string Specialty = "";
    public Dictionary<string, double> Storage = new();
}
