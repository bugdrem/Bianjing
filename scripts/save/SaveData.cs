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

    /// <summary>v10：城市里程碑等级。</summary>
    public int MilestoneLevel;

    /// <summary>v10：已研成科技 id 列表与在研项目（id + 已投入天数）。</summary>
    public List<string> Techs = new();
    public string ResearchTechId = "";
    public double ResearchDays;
}

/// <summary>地图数据：各类地表格用一维索引（y*Size+x）紧凑存储。</summary>
public class MapSave
{
    public List<int> RoadCells = new();

    /// <summary>v9：与 RoadCells 一一对应的道路种类（(int)RoadKind）。</summary>
    public List<int> RoadKinds = new();

    public List<int> ZoneCells = new();
    public List<int> ZoneTypes = new();
    public List<int> WaterCells = new();
    public List<int> BridgeCells = new();

    /// <summary>v15：非零地形高度格——HeightCells（一维索引）与 HeightLayers（对应层数）一一对应，
    /// 平地（0 层）不存以保持稀疏。</summary>
    public List<int> HeightCells = new();
    public List<int> HeightLayers = new();
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

    /// <summary>v9：实例占地（住宅扩建；0 表示沿用定义占地）。</summary>
    public int SizeX;
    public int SizeY;
}
