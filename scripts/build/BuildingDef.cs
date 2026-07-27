using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bianjing;

/// <summary>建筑静态定义，从 res://data/buildings.json 加载。</summary>
public class BuildingDef
{
    public string Id { get; set; }
    public string Name { get; set; }

    /// <summary>official=玩家建造, grown=居民自动生长。</summary>
    public string Category { get; set; } = "official";

    public int SizeX { get; set; } = 1;
    public int SizeY { get; set; } = 1;
    public int Cost { get; set; }
    public int Upkeep { get; set; }
    public string Color { get; set; } = "#ffffff";
    public float Height { get; set; } = 2f;

    /// <summary>正向吸引力（水井/衙门等）及作用半径（格）。</summary>
    public float DesirabilityBonus { get; set; }
    public float DesirabilityRadius { get; set; }

    /// <summary>污染（工坊）及作用半径（格）。</summary>
    public float Pollution { get; set; }
    public float PollutionRadius { get; set; }

    /// <summary>粮田每月产粮。</summary>
    public int FoodOutput { get; set; }

    /// <summary>民居人口容量（一级基础值；前店后宅/工坊宿舍同样可住人）。</summary>
    public int Capacity { get; set; }

    /// <summary>满级人口容量（随等级线性插值）。</summary>
    public int CapacityMax { get; set; }

    /// <summary>最高建筑等级（坊区生长建筑逐级升格）。</summary>
    public int MaxLevel { get; set; } = 1;

    /// <summary>天然建筑：固定不变，不参与老化与修缮。</summary>
    public bool Natural { get; set; }

    /// <summary>商铺/工坊每月额外税收。</summary>
    public int TaxBonus { get; set; }

    /// <summary>提供的岗位数量（0 表示不雇工）。</summary>
    public int JobSlots { get; set; }

    /// <summary>雇工每月工资。</summary>
    public double Salary { get; set; }

    /// <summary>储存上限（份）：住宅存家用物资，商铺/工坊存专营货品，农田存待运粮食。</summary>
    public int StorageCapacity { get; set; }

    [JsonIgnore]
    public Godot.Color GodotColor => new(Color);

    /// <summary>指定等级下的可居住人数（一级到满级线性插值）。</summary>
    public int CapacityAt(int level)
    {
        if (MaxLevel <= 1 || CapacityMax <= Capacity || level <= 1)
            return Capacity;
        float t = (level - 1f) / (MaxLevel - 1f);
        return (int)System.Math.Round(Capacity + (CapacityMax - Capacity) * t);
    }

    public static Dictionary<string, BuildingDef> LoadAll()
    {
        using var f = Godot.FileAccess.Open("res://data/buildings.json", Godot.FileAccess.ModeFlags.Read);
        string text = f.GetAsText();
        var list = JsonSerializer.Deserialize<List<BuildingDef>>(text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var dict = new Dictionary<string, BuildingDef>();
        foreach (var def in list)
            dict[def.Id] = def;
        return dict;
    }
}

/// <summary>已放置的建筑实例（人造建筑随时间老化，天然建筑固定不变）。</summary>
public class BuildingInstance : Obj
{
    public BuildingDef Def;

    /// <summary>建筑等级 1..MaxLevel，等级越高可居住人数越多。</summary>
    public int Level = 1;

    /// <summary>完好度 0-100：人造建筑逐月老化，修缮恢复，归零坍塌。</summary>
    public float Condition = 100f;

    /// <summary>建造日期（游戏年/月；老存档为 0 表示不详）。</summary>
    public int BuiltYear;
    public int BuiltMonth;

    /// <summary>专营货品（商铺/工坊倾向单一货品交易；空串表示不经营）。</summary>
    public string Specialty = "";

    /// <summary>库存：货品 id → 份数。</summary>
    public Dictionary<string, double> Storage = new();

    /// <summary>库存总量（份）。</summary>
    public double StorageTotal
    {
        get
        {
            double sum = 0;
            foreach (var v in Storage.Values)
                sum += v;
            return sum;
        }
    }

    /// <summary>剩余库容（份）。</summary>
    public double StorageFree => System.Math.Max(0, Def.StorageCapacity - StorageTotal);

    /// <summary>入库（受容量限制），返回实际入库份数。</summary>
    public double StoreGoods(string goodsId, double amount)
    {
        double accepted = System.Math.Min(amount, StorageFree);
        if (accepted <= 0)
            return 0;
        Storage[goodsId] = Storage.GetValueOrDefault(goodsId) + accepted;
        return accepted;
    }

    /// <summary>出库，返回实际取出份数。</summary>
    public double TakeGoods(string goodsId, double amount)
    {
        double have = Storage.GetValueOrDefault(goodsId);
        double taken = System.Math.Min(have, amount);
        if (taken <= 0)
            return 0;
        if (have - taken <= 0.0001)
            Storage.Remove(goodsId);
        else
            Storage[goodsId] = have - taken;
        return taken;
    }

    public Godot.Vector2I Origin
    {
        get => new(X, Y);
        set { X = value.X; Y = value.Y; }
    }

    /// <summary>当前等级的可居住人数。</summary>
    public int HousingCapacity => Def.CapacityAt(Level);
}
