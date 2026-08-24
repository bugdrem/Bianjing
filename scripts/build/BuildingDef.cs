using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bianjing;

/// <summary>建筑静态定义，从 res://data/buildings.json 加载。</summary>
public class BuildingDef
{
    public string Id { get; set; }
    public string Name { get; set; }

    /// <summary>official=玩家建造, grown=居民自动生长, court=朝廷直属机构（批次七十七：
    /// 朝廷拨款营造/维护不占官库，收购款凭空生成），field=耕种区农田。</summary>
    public string Category { get; set; } = "official";

    public int SizeX { get; set; } = 1;
    public int SizeY { get; set; } = 1;
    public int Cost { get; set; }
    public int Upkeep { get; set; }
    public string Color { get; set; } = "#ffffff";
    public float Height { get; set; } = 2f;

    /// <summary>可选 3D 模型资产路径（如 "res://assets/buildings/palace.glb"）。非空则优先用该 glb 渲染，
    /// 空字符串回退到 BuildingModelFactory 的原始体宋代造型；多实例共享同一 PackedScene 仅 Instantiate。</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>是否挂了外部模型资产（走 glb 路径）。</summary>
    [JsonIgnore]
    public bool HasModel => !string.IsNullOrEmpty(ModelPath);

    /// <summary>不绘制斜屋顶（如农田等开阔地块，只有地面无房顶）；默认 false 照常盖顶。</summary>
    public bool NoRoof { get; set; }

    /// <summary>全局唯一：全城至多存在一座（如王爷府）；已建成时放置校验/菜单据此拦截第二座。</summary>
    public bool Unique { get; set; }

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

    /// <summary>提供的岗位数量（0 表示不雇工）。每级岗位数见 JobSlotsByLevel，为 null 时本字段当固定数。</summary>
    public int JobSlots { get; set; }

    /// <summary>雇工每月工资（文）。</summary>
    public long Salary { get; set; }

    /// <summary>每级岗位数（长度 3，索引 0=一级；null 时沿用 JobSlots 固定值）。</summary>
    public int[] JobSlotsByLevel { get; set; }

    /// <summary>每级最低技能经验值要求（长度 3，索引 0=一级；0=无要求；null 时无技能要求）。</summary>
    public float[] MinSkillExpByLevel { get; set; }

    /// <summary>每级加工效率倍率（长度 3，索引 0=一级，默认全 1.0；null 时恒 1.0）。</summary>
    public double[] EfficiencyByLevel { get; set; }

    /// <summary>商铺每级服务范围（格，长度 3，索引 0=一级；-1=全城；null 时不适用）。</summary>
    public int[] ServiceRangeByLevel { get; set; }

    public int JobSlotsAt(int level) => JobSlotsByLevel?.Length >= level ? JobSlotsByLevel[level - 1] : JobSlots;
    public double EfficiencyAt(int level) => EfficiencyByLevel?.Length >= level ? EfficiencyByLevel[level - 1] : 1.0;
    public int ServiceRangeAt(int level) => ServiceRangeByLevel?.Length >= level ? ServiceRangeByLevel[level - 1] : 0;
    public float MinSkillExpAt(int level) => MinSkillExpByLevel?.Length >= level ? MinSkillExpByLevel[level - 1] : 0;

    /// <summary>储存上限（份）：住宅存家用物资，商铺/工坊存专营货品，农田存待运粮食。</summary>
    public int StorageCapacity { get; set; }

    /// <summary>收获周期（月）：>0 表示按时间结算的农田类建筑（默认 1 月一收，数据驱动可配）。</summary>
    public int HarvestMonths { get; set; }

    /// <summary>每名在岗工人每次收获的产量（份），收成散落在占地格上待拾运。</summary>
    public double YieldPerWorker { get; set; }

    /// <summary>按时间结算建筑的产出货品 id（空串默认产粮；采矿场产矿石、制盐厂产盐）。</summary>
    public string ProduceGoods { get; set; } = "";

    /// <summary>税所：每名在岗吏员对全城税收的加成比例（如 0.1 = +10%）。</summary>
    public double TaxBoostPerWorker { get; set; }

    /// <summary>铸币局：每名在岗工匠每日铸钱入官库（文）。</summary>
    public double MintPerWorkerDay { get; set; }

    /// <summary>朝廷采购衙门（批次七十六：柴炭司/市易务等朝廷直属机构）：非空时表示该衙门收购的货品清单，
    /// NPC 富余资源按朝廷牌价（基价×CourtProcurementPriceFactor，批次七十七全场最低价 0.8）直售衙门，
    /// 货款由朝廷凭空生成（不经过官库）；城内交易优先、朝廷兑底，不设配额，衙门库容每月清空（朝廷漕运拉走）。
    /// null/空数组 = 非衙门，无此能力。</summary>
    public string[] CourtGoods { get; set; }

    /// <summary>是否为朝廷直属采购衙门（收购清单非空）。</summary>
    public bool IsCourtBuyer => CourtGoods is { Length: > 0 };

    /// <summary>建造菜单排序权重：&gt;0 才在菜单中显示（组内升序排列）；0 表不上架（grown 建筑由居民自建）。mod 可自定序号插入新建筑。</summary>
    public int MenuOrder { get; set; }

    /// <summary>建造菜单所属分组：infrastructure(基础设施)/public(公共设施)/official(官府设施)；空串默认归官府设施。mod 可填自定义组名（未知组自动追加在末尾）。</summary>
    public string MenuGroup { get; set; } = "";

    /// <summary>解锁所需城市里程碑等级（0 开局即可建）：菜单置灰展示、放置校验同受此限。</summary>
    public int MilestoneRequired { get; set; }

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
        var dict = new Dictionary<string, BuildingDef>();

        // 1) 基础定义：随游戏发行，位于 res://data/buildings.json（缺失时报错而非直接崩溃）
        using (var f = Godot.FileAccess.Open("res://data/buildings.json", Godot.FileAccess.ModeFlags.Read))
        {
            if (f != null)
                MergeInto(dict, f.GetAsText());
            else
                Godot.GD.PushError("缺少 res://data/buildings.json，建筑定义为空。");
        }

        // 2) 建筑 mod：游戏根目录 mods/<模组名>/buildings.json，按目录名升序加载，
        //    同 id 覆盖基础定义、新 id 直接追加——玩家放入文件夹即生效，无需改代码。
        LoadMods(dict);

        return dict;
    }

    /// <summary>扫描 mods 目录，把各模组的建筑定义并入字典（后加载者覆盖同 id）。</summary>
    private static void LoadMods(Dictionary<string, BuildingDef> dict)
    {
        string modsDir = GamePaths.ModsDir;
        if (!Directory.Exists(modsDir))
            return;

        foreach (string dir in Directory.GetDirectories(modsDir).OrderBy(d => d))
        {
            string file = Path.Combine(dir, "buildings.json");
            if (!File.Exists(file))
                continue;
            try
            {
                int before = dict.Count;
                MergeInto(dict, File.ReadAllText(file));
                Godot.GD.Print($"[mod] 载入建筑定义 {Path.GetFileName(dir)}（现共 {dict.Count} 种，原 {before} 种）");
            }
            catch (System.Exception e)
            {
                Godot.GD.PushWarning($"[mod] 解析 {file} 失败：{e.Message}");
            }
        }
    }

    /// <summary>把一段 JSON 数组文本解析为建筑定义并按 id 并入字典（覆盖同 id）。</summary>
    private static void MergeInto(Dictionary<string, BuildingDef> dict, string json)
    {
        var list = JsonSerializer.Deserialize<List<BuildingDef>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (list == null)
            return;
        foreach (var def in list)
            dict[def.Id] = def;
    }
}

/// <summary>已放置的建筑实例（人造建筑随时间老化，天然建筑固定不变）。</summary>
public class BuildingInstance : Obj
{
    public BuildingDef Def;

    /// <summary>建筑等级 1..MaxLevel，等级越高可居住人数越多。</summary>
    public int Level = 1;

    /// <summary>实例占地（grown 住宅升级可扩大占地）：0 表示沿用 Def.SizeX/SizeY（官营建筑与旧档默认）。</summary>
    public int SizeX;
    public int SizeY;

    /// <summary>当前实际占地（优先实例值，否则用定义值）。</summary>
    public int FootX => SizeX > 0 ? SizeX : Def.SizeX;
    public int FootY => SizeY > 0 ? SizeY : Def.SizeY;

    /// <summary>建筑的门（大门+若干后门）：懒算缓存，不入存档，读档后按当前道路重算；
    /// 占地/临路变化（扩建/转业/拆邻）时置 null 令其失效，下次访问经 GameState.EnsureDoors 重算。</summary>
    [JsonIgnore]
    public List<Door> Doors;

    /// <summary>完好度 0-100：人造建筑逐月老化，修缮恢复，归零坍塌。</summary>
    public float Condition = 100f;
    
    /// <summary>废弃标志：grown 建筑无人居住时置位，可供新居民重建入住，并为后期邻居合并扩建预留钩子。</summary>
    public bool Abandoned;

    /// <summary>业主居民 Id（-1 无）：田块等自营建筑登记田主，业主亡故/离城后由系统重新指派。</summary>
    public int OwnerCitizenId = -1;

    /// <summary>建造日期（游戏年/月；老存档为 0 表示不详）。</summary>
    public int BuiltYear;
    public int BuiltMonth;

    /// <summary>专营货品（商铺/工坊倾向单一货品交易；空串表示不经营）。</summary>
    public string Specialty = "";

    /// <summary>升级增补的副营货品（批次六十七：仅限同大类、数量封顶，见 ZoneGrowthSystem.ExtendSpecialties）。</summary>
    public List<string> ExtraGoods = new();

    /// <summary>距上次收获的月数（仅农田类建筑使用，到期清零）。</summary>
    public int MonthsSinceHarvest;

    /// <summary>建筑内库存（统一仓储接口，典型案例一）；容量随实例占地等比伸缩（见 StorageCap）。</summary>
    public Inventory Inv = new();

    /// <summary>实例仓储容量：定义值按基准占地计，实例扩地后容量与占地面积成正比
    /// （如民居 4×4 基准 32 份，扩到 8×8 即 128 份）；mod 改定义即时生效。</summary>
    public double StorageCap =>
        Def.StorageCapacity * (double)(FootX * FootY) / System.Math.Max(1, Def.SizeX * Def.SizeY);

    /// <summary>库存总量（份）。</summary>
    public double StorageTotal => Inv.Total;

    /// <summary>入库（受容量限制），返回实际入库份数。</summary>
    public double StoreGoods(string goodsId, double amount)
    {
        Inv.Capacity = StorageCap;
        return Inv.Store(goodsId, amount);
    }

    /// <summary>超限入库：村民背来的货全收（超过上限也不浪费）；
    /// 上限只作 StorageAtCap 闸门，阻断后续派人采集/进货。</summary>
    public double StoreGoodsForce(string goodsId, double amount)
    {
        Inv.Capacity = StorageCap;
        return Inv.StoreForce(goodsId, amount);
    }

    /// <summary>仓储已达/超上限（超限入库后可为真）：作为"继续进货/派人采集"的闸门。</summary>
    public bool StorageAtCap => Inv.Total >= StorageCap;

    /// <summary>剩余仓储余量（可为负，超限时负得越多越满）：挑选"最空收货方"的比较基准。</summary>
    public double SpareCap => StorageCap - Inv.Total;

    /// <summary>出库，返回实际取出份数。</summary>
    public double TakeGoods(string goodsId, double amount) => Inv.Take(goodsId, amount);

    public Godot.Vector2I Origin
    {
        get => new(X, Y);
        set { X = value.X; Y = value.Y; }
    }

    /// <summary>可居住人数：grown 建筑 = 占地格数（房体=占地，居住与打工共用同一格池，不预留工位）；
    /// 生育可略超，超员由拥挤事件扩建/分家疏解；官营建筑沿用定义容量（通常为 0 不住人）。</summary>
    public int HousingCapacity
    {
        get
        {
            if (Def.Category != "grown")
                return Def.CapacityAt(Level);
            return FootX * FootY; // 房体=占地：2×2 宅=4，2×3=6，3×3=9
        }
    }
}

/// <summary>建筑的门：村民经门出入建筑（不走屋墙）。
/// Inside 为门所属建筑占地内、贴着边界的一格；Outside 为门外相邻的道路/空地（村民停靠点）。</summary>
public struct Door
{
    /// <summary>门内侧格（建筑占地内、贴边界的一格）。</summary>
    public Godot.Vector2I Inside;

    /// <summary>门外侧格（建筑外相邻格，村民进出停靠点）。</summary>
    public Godot.Vector2I Outside;

    /// <summary>是否为大门（true=大门朝最高等级路，false=后门）。</summary>
    public bool IsMain;

    public Door(Godot.Vector2I inside, Godot.Vector2I outside, bool isMain)
    {
        Inside = inside;
        Outside = outside;
        IsMain = isMain;
    }
}
