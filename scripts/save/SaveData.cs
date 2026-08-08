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
    public long Money;
    public double Food;
    public int NextBuildingId;
    public int NextCitizenId;
    public int NextFamilyId;
    public int NextPlantId;
    public int NextAnimalId;
    public int NextPileId;

    /// <summary>税收政策（批次五十六：三字段替代旧版税种-档位字典）。</summary>
    public double LandTaxRate = EconomyConfig.LandTaxRateDefault;
    public double TradeTaxRate = EconomyConfig.TradeTaxRateDefault;
    public bool PollTaxEnabled;

    /// <summary>v3 起：官库账本（本月/上月分类流水）。</summary>
    public Dictionary<string, long> LedgerCur = new();
    public Dictionary<string, long> LedgerPrev = new();

    /// <summary>v10：城市里程碑等级。</summary>
    public int MilestoneLevel;

    /// <summary>v10：已研成科技 id 列表与在研项目（id + 已投入天数）。</summary>
    public List<string> Techs = new();
    public string ResearchTechId = "";
    public double ResearchDays;

    /// <summary>v16 追加：公告栏历史（最新在前，数据层封顶 NewsCap 条）——可选字段，
    /// 旧档缺失时读出空表，不破坏格式兼容。</summary>
    public List<NewsItem> News = new();
}

/// <summary>地图数据：各类地表格用一维索引（y*Size+x）紧凑存储。</summary>
public class MapSave
{
    public List<int> RoadCells = new();

    /// <summary>v9：与 RoadCells 一一对应的道路种类（(int)RoadKind）。</summary>
    public List<int> RoadKinds = new();

    /// <summary>v24：与 RoadCells 一一对应的小路归属建筑 Id（非小路格/无主为 -1；批次六十六小路独立个体）。</summary>
    public List<int> LaneOwnerIds = new();

    public List<int> ZoneCells = new();
    public List<int> ZoneTypes = new();
    public List<int> WaterCells = new();

    /// <summary>v19：与 WaterCells 一一对应的水流方向（(byte)Cell.FlowDir，0=静水/湖）。</summary>
    public List<int> WaterFlow = new();

    /// <summary>v21：与 WaterCells 一一对应的逐格水面海拔（Cell.WaterH，米）——
    /// 水位沿程随地势变化（下限 0），不再是全图统一常量，须随档保存。</summary>
    public List<float> WaterLevels = new();

    public List<int> BridgeCells = new();

    /// <summary>v20：顶点高度场灰度图——uint16 量化 blob（每顶点 2 字节小端，JSON 自动 base64），
    /// height = HeightMin + v × HeightStep；替代旧版整数层稀疏表（HeightCells/HeightLayers 已删）。</summary>
    public byte[] HeightMap;
    public float HeightMin;
    public float HeightStep;
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

    /// <summary>v23：业主居民 Id（田块等自营建筑的田主；-1 无）。</summary>
    public int OwnerCitizenId = -1;

    /// <summary>v24：升级增补的副营货品（商铺/工坊升级多品种）。</summary>
    public List<string> ExtraGoods = new();
}
