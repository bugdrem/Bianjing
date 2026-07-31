using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Godot;
using LightningDB;

namespace Bianjing;

/// <summary>
/// LMDB 存档服务：一份存档一个库（游戏根目录/saves/&lt;slot&gt; 目录一个环境），
/// 保存时在单个写事务内写入全部 key 后提交——要么全部落盘要么全部回滚，天然原子。
/// key 划分（meta/world/map/buildings/citizens/families/plants/animals）便于未来 mod 追加自己的数据段。
/// </summary>
public static class SaveService
{
    /// <summary>v21：水位改逐格变化（Cell.WaterH 随地势、下限 0），新增 WaterLevels 随档；旧档无水位数据，拒读。</summary>
    public const int FormatVersion = 21;
    /// <summary>F5/F9 快速存档槽。</summary>
    public const string QuickSlot = "quick";
    /// <summary>自动存档槽。</summary>
    public const string AutoSlot = "autosave";

    private const long MapSizeBytes = 32L * 1024 * 1024;

    /// <summary>Citizen/Family 用公共字段承载数据，序列化必须开 IncludeFields。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    private static string SavesRoot => GamePaths.SavesDir;

    private static string SlotDir(string slot) => Path.Combine(SavesRoot, slot);

    public static bool SaveExists(string slot) =>
        File.Exists(Path.Combine(SlotDir(slot), "data.mdb"));

    /// <summary>删除指定槽的存档（整目录连同 LMDB 数据一并移除），目录不存在或删除失败返回 false。</summary>
    public static bool DeleteSave(string slot)
    {
        string dir = SlotDir(slot);
        if (!Directory.Exists(dir))
            return false;
        try
        {
            Directory.Delete(dir, true);
            return true;
        }
        catch (Exception e)
        {
            GD.PushWarning($"删除存档 {slot} 失败：{e.Message}");
            return false;
        }
    }

    /// <summary>由存档名生成槽名：同名即覆盖，非法字符替换为下划线。</summary>
    public static string SlotFor(string saveName)
    {
        var sb = new StringBuilder(saveName.Length);
        foreach (char ch in saveName)
            sb.Append(char.IsLetterOrDigit(ch) || ch > 127 ? ch : '_');
        string slot = sb.ToString().Trim('_');
        return slot.Length == 0 ? "save" : slot;
    }

    /// <summary>枚举全部存档（读各槽 meta），按保存时间倒序。</summary>
    public static List<SaveInfo> ListSaves()
    {
        var result = new List<SaveInfo>();
        if (!Directory.Exists(SavesRoot))
            return result;

        foreach (string dir in Directory.GetDirectories(SavesRoot))
        {
            if (!File.Exists(Path.Combine(dir, "data.mdb")))
                continue;
            try
            {
                using var env = OpenEnv(dir);
                using var tx = env.BeginTransaction(TransactionBeginFlags.ReadOnly);
                using var db = tx.OpenDatabase();
                var meta = Get<SaveMeta>(tx, db, "meta");
                if (meta == null)
                    continue;
                result.Add(new SaveInfo
                {
                    Slot = Path.GetFileName(dir),
                    CityName = meta.CityName ?? "",
                    SaveName = meta.SaveName ?? "",
                    Year = meta.Year,
                    Month = meta.Month,
                    SavedAtUnix = meta.SavedAtUnix,
                });
            }
            catch (Exception e)
            {
                GD.PushWarning($"读取存档目录 {dir} 失败：{e.Message}");
            }
        }

        result.Sort((a, b) => b.SavedAtUnix.CompareTo(a.SavedAtUnix));
        return result;
    }

    // ---- 保存 ----

    /// <summary>上一次异步保存是否仍在写盘（后台任务未完）：避免并发写同一库与重复快照开销。</summary>
    private static volatile bool _asyncSaving;

    /// <summary>是否有异步保存正在进行（HUD 可据此提示“保存中”）。</summary>
    public static bool IsSaving => _asyncSaving;

    /// <summary>同步保存到指定槽（主线程序列化 + 当场写盘）；磁盘/序列化异常不崩游戏，返回是否成功。</summary>
    public static bool Save(GameClock clock, string slot, string saveName)
    {
        try
        {
            WriteRecords(SlotDir(slot), BuildRecords(clock, saveName));
            return true;
        }
        catch (Exception e)
        {
            GD.PushWarning($"保存存档 {slot} 失败：{e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 异步原子保存：先在主线程将全部存档段序列化为不可变字节（读游戏状态必须在主线程，
    /// 免与模拟线程竞争），再把阻塞的 LMDB 写盘+提交（磁盘 I/O）丢后台线程（免卡帧）；
    /// 单事务提交保留原子性。完成回调 <paramref name="onDone"/> 在后台线程投递，调用方需自行 marshal 回主线程再碰节点。
    /// </summary>
    public static void SaveAsync(GameClock clock, string slot, string saveName, Action<bool> onDone = null)
    {
        if (_asyncSaving)
        {
            onDone?.Invoke(false); // 上一次还没写完：本次跳过
            return;
        }

        Dictionary<string, byte[]> records;
        try
        {
            records = BuildRecords(clock, saveName); // 主线程快照+序列化（字节一旦生成即不变）
        }
        catch (Exception e)
        {
            GD.PushWarning($"保存快照 {slot} 失败：{e.Message}");
            onDone?.Invoke(false);
            return;
        }

        _asyncSaving = true;
        string dir = SlotDir(slot);
        System.Threading.Tasks.Task.Run(() =>
        {
            bool ok = false;
            try
            {
                WriteRecords(dir, records);
                ok = true;
            }
            catch (Exception e)
            {
                GD.PushWarning($"写入存档 {slot} 失败：{e.Message}");
            }
            finally
            {
                _asyncSaving = false;
                onDone?.Invoke(ok);
            }
        });
    }

    /// <summary>在主线程构建并序列化全部存档段为字节：读游戏状态必须在主线程（与模拟同线程免竞争），
    /// 序列化产出的字节即快照，交给后台线程写盘不再触碰可变对象。</summary>
    private static Dictionary<string, byte[]> BuildRecords(GameClock clock, string saveName)
    {
        var gs = GameState.I;
        var meta = new SaveMeta
        {
            Version = FormatVersion,
            Year = clock.Year,
            Month = clock.Month,
            Day = clock.Day,
            Hour = clock.Hour,
            SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CityName = gs.CityName,
            SaveName = saveName,
        };

        var world = new WorldSave
        {
            Money = gs.Money,
            Food = gs.Food,
            NextBuildingId = gs.NextBuildingId,
            NextCitizenId = gs.NextCitizenId,
            NextFamilyId = gs.NextFamilyId,
            NextPlantId = gs.NextPlantId,
            NextAnimalId = gs.NextAnimalId,
            NextPileId = gs.NextPileId,
            TaxLevels = new Dictionary<string, int>(gs.Taxes.Levels),
            LedgerCur = new Dictionary<string, double>(gs.Ledger.Current),
            LedgerPrev = new Dictionary<string, double>(gs.Ledger.Previous),
            MilestoneLevel = gs.MilestoneLevel,
            Techs = new List<string>(gs.TechsUnlocked),
            ResearchTechId = gs.ResearchTechId,
            ResearchDays = gs.ResearchDays,
            News = new List<NewsItem>(gs.News), // 公告随档保存（浅拷即可，NewsItem 写入后不变）
        };

        var map = new MapSave();

        // 道路/坊区直接取增量索引（RoadCells/BuildableCells），与架构方向一致；
        // 全图扫描仅保留给无索引的水面/桥面格
        foreach (var rc in gs.RoadCells)
        {
            map.RoadCells.Add(rc.Y * MapGrid.Size + rc.X);
            map.RoadKinds.Add((int)gs.Map.CellAt(rc).RoadKind); // 与 RoadCells 一一对应
        }
        foreach (var zc in gs.BuildableCells)
        {
            map.ZoneCells.Add(zc.Y * MapGrid.Size + zc.X);
            map.ZoneTypes.Add((int)gs.Map.CellAt(zc).Zone);
        }
        for (int y = 0; y < MapGrid.Size; y++)
        {
            for (int x = 0; x < MapGrid.Size; x++)
            {
                ref var cell = ref gs.Map.CellAt(x, y);
                int index = y * MapGrid.Size + x;
                if (cell.HasWater)
                {
                    map.WaterCells.Add(index);
                    map.WaterFlow.Add(cell.FlowDir); // 与 WaterCells 一一对应
                    map.WaterLevels.Add(cell.WaterH); // v21：逐格水位同步随档
                }
                if (cell.HasBridge)
                    map.BridgeCells.Add(index);
            }
        }

        // v20：顶点高度场整张导出为 uint16 灰度 blob（约 2.1MB，LMDB 直存）
        map.HeightMap = gs.Map.Height.ToBlob(out map.HeightMin, out map.HeightStep);

        var buildings = new List<BuildingSave>(gs.Buildings.Count);
        foreach (var b in gs.Buildings.Values)
            buildings.Add(new BuildingSave
            {
                Id = b.Id, DefId = b.Def.Id, X = b.Origin.X, Y = b.Origin.Y,
                Level = b.Level, Condition = b.Condition,
                BuiltYear = b.BuiltYear, BuiltMonth = b.BuiltMonth,
                Specialty = b.Specialty, Inv = b.Inv, MonthsSinceHarvest = b.MonthsSinceHarvest,
                Abandoned = b.Abandoned,
                SizeX = b.SizeX, SizeY = b.SizeY,
            });

        var citizens = new List<Citizen>(gs.Citizens.Values);
        var families = new List<Family>(gs.Families.Values);
        var plants = new List<PlantObj>(gs.Plants.Values);
        var animals = new List<AnimalObj>(gs.Animals.Values);
        var piles = new List<ItemPileObj>(gs.Piles.Values);

        // 当场序列化为字节（仍在主线程）：字节即不可变快照，后续写盘可安全交后台
        return new Dictionary<string, byte[]>
        {
            ["meta"] = Serialize(meta),
            ["world"] = Serialize(world),
            ["map"] = Serialize(map),
            ["buildings"] = Serialize(buildings),
            ["citizens"] = Serialize(citizens),
            ["families"] = Serialize(families),
            ["plants"] = Serialize(plants),
            ["animals"] = Serialize(animals),
            ["piles"] = Serialize(piles),
        };
    }

    /// <summary>把已序列化的字节在单个写事务内全部落盘并提交（原子：要么全落要么回滚）；
    /// 只碰不可变字节与磁盘，可在后台线程执行。</summary>
    private static void WriteRecords(string dir, Dictionary<string, byte[]> records)
    {
        Directory.CreateDirectory(dir);
        using var env = OpenEnv(dir);
        using var tx = env.BeginTransaction();
        using var db = tx.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });

        foreach (var kv in records)
            tx.Put(db, Encoding.UTF8.GetBytes(kv.Key), kv.Value);

        tx.Commit(); // 单事务提交：原子落盘
    }

    // ---- 读取 ----

    /// <summary>读档：任何异常（损坏档/磁盘错误）均不崩游戏，返回 false 由界面提示。</summary>
    public static bool Load(GameClock clock, string slot)
    {
        try
        {
            return LoadCore(clock, slot);
        }
        catch (Exception e)
        {
            GD.PushWarning($"读取存档 {slot} 失败：{e.Message}");
            return false;
        }
    }

    private static bool LoadCore(GameClock clock, string slot)
    {
        string dir = SlotDir(slot);
        if (!File.Exists(Path.Combine(dir, "data.mdb")))
            return false;

        SaveMeta meta;
        WorldSave world;
        MapSave map;
        List<BuildingSave> buildings;
        List<Citizen> citizens;
        List<Family> families;
        List<PlantObj> plants;
        List<AnimalObj> animals;
        List<ItemPileObj> piles;

        using (var env = OpenEnv(dir))
        using (var tx = env.BeginTransaction(TransactionBeginFlags.ReadOnly))
        using (var db = tx.OpenDatabase())
        {
            meta = Get<SaveMeta>(tx, db, "meta");
            world = Get<WorldSave>(tx, db, "world");
            map = Get<MapSave>(tx, db, "map");
            buildings = Get<List<BuildingSave>>(tx, db, "buildings");
            citizens = Get<List<Citizen>>(tx, db, "citizens");
            families = Get<List<Family>>(tx, db, "families");
            plants = Get<List<PlantObj>>(tx, db, "plants");
            animals = Get<List<AnimalObj>>(tx, db, "animals");
            piles = Get<List<ItemPileObj>>(tx, db, "piles");
        }

        if (meta == null || world == null || map == null)
        {
            GD.PushWarning($"存档 {slot} 数据不完整，读取取消。");
            return false;
        }

        // 早期开发不做跨版本迁移：格式不符直接拒读，避免半坏数据污染运行时
        if (meta.Version != FormatVersion)
        {
            GD.PushWarning($"存档 {slot} 版本 v{meta.Version} 与当前 v{FormatVersion} 不兼容，读取取消。");
            return false;
        }

        // 复用已加载的建筑定义，重建全新 GameState 后整体替换
        var gs = new GameState(GameState.I.Defs)
        {
            Money = world.Money,
            Food = world.Food,
            NextBuildingId = world.NextBuildingId,
            NextCitizenId = world.NextCitizenId,
            NextFamilyId = world.NextFamilyId,
            NextPlantId = world.NextPlantId,
            NextAnimalId = world.NextAnimalId,
            NextPileId = world.NextPileId,
            Taxes = new TaxPolicy { Levels = world.TaxLevels ?? new Dictionary<string, int>() },
            CityName = string.IsNullOrEmpty(meta.CityName) ? "汴京" : meta.CityName,
            Ledger = new Ledger
            {
                Current = world.LedgerCur ?? new Dictionary<string, double>(),
                Previous = world.LedgerPrev ?? new Dictionary<string, double>(),
            },
            CurYear = meta.Year,
            CurMonth = meta.Month,
            MilestoneLevel = world.MilestoneLevel,
            ResearchTechId = world.ResearchTechId ?? "",
            ResearchDays = world.ResearchDays,
        };
        foreach (var id in world.Techs ?? new List<string>())
            gs.TechsUnlocked.Add(id);
        gs.News.AddRange(world.News ?? new List<NewsItem>()); // 公告随档恢复，公告栏读档后续接旧事

        for (int i = 0; i < map.RoadCells.Count; i++)
        {
            int index = map.RoadCells[i];
            var c = new Vector2I(index % MapGrid.Size, index / MapGrid.Size);
            ref var cell = ref gs.Map.CellAt(c);
            cell.HasRoad = true;
            // v9：道路种类与 RoadCells 一一对应（桥面格为 None）
            cell.RoadKind = i < (map.RoadKinds?.Count ?? 0) ? (RoadKind)map.RoadKinds[i] : RoadKind.None;
            gs.Roads.SetRoad(c, true, cell.RoadKind); // 含寻路权重重建（主路代价低）
            gs.RegisterRoadCell(c); // 重建增量道路格索引
        }
        for (int i = 0; i < map.ZoneCells.Count; i++)
        {
            int index = map.ZoneCells[i];
            // 经 SetZone 写入，同步重建坊区候选集索引
            gs.SetZone(new Vector2I(index % MapGrid.Size, index / MapGrid.Size), (ZoneType)map.ZoneTypes[i]);
        }
        var waterCells = map.WaterCells ?? new List<int>();
        var waterFlow = map.WaterFlow ?? new List<int>();
        var waterLevels = map.WaterLevels ?? new List<float>();
        for (int i = 0; i < waterCells.Count; i++)
        {
            int index = waterCells[i];
            ref var wcell = ref gs.Map.CellAt(index % MapGrid.Size, index / MapGrid.Size);
            wcell.HasWater = true;
            wcell.FlowDir = i < waterFlow.Count ? (byte)waterFlow[i] : (byte)0; // v19：流向随档恢复
            wcell.WaterH = i < waterLevels.Count ? waterLevels[i] : 0f; // v21：逐格水位随档恢复
        }
        foreach (int index in map.BridgeCells ?? new List<int>())
            gs.Map.CellAt(index % MapGrid.Size, index / MapGrid.Size).HasBridge = true; // HasRoad 已由 RoadCells 恢复

        // v20：顶点高度场从灰度 blob 整张恢复（高度随档回来，建筑垫基台面也在其中，读档不再整平）
        gs.Map.Height.FromBlob(map.HeightMap, map.HeightMin, map.HeightStep);

        foreach (var p in plants ?? new List<PlantObj>())
        {
            gs.Plants[GameState.CellIndex(new Vector2I(p.X, p.Y))] = p;
            gs.Map.CellAt(p.X, p.Y).HasTree = true;
            gs.NextPlantId = Math.Max(gs.NextPlantId, p.Id + 1);
        }

        foreach (var a in animals ?? new List<AnimalObj>())
        {
            gs.Animals[a.Id] = a;
            gs.NextAnimalId = Math.Max(gs.NextAnimalId, a.Id + 1);
        }

        foreach (var p in piles ?? new List<ItemPileObj>())
        {
            gs.Piles[GameState.CellIndex(new Vector2I(p.X, p.Y))] = p;
            gs.NextPileId = Math.Max(gs.NextPileId, p.Id + 1);
        }

        foreach (var bs in buildings ?? new List<BuildingSave>())
        {
            if (!gs.Defs.TryGetValue(bs.DefId, out var def))
            {
                GD.PushWarning($"存档含未知建筑定义 {bs.DefId}（mod 被移除？），已跳过。");
                continue;
            }
            var b = new BuildingInstance
            {
                Id = bs.Id, Def = def, Origin = new Vector2I(bs.X, bs.Y),
                Level = Math.Max(1, bs.Level),
                Condition = bs.Condition <= 0f ? 100f : bs.Condition,
                BuiltYear = bs.BuiltYear,
                BuiltMonth = bs.BuiltMonth,
                Specialty = bs.Specialty ?? "",
                Inv = bs.Inv ?? new Inventory(),
                MonthsSinceHarvest = bs.MonthsSinceHarvest,
                Abandoned = bs.Abandoned,
                SizeX = bs.SizeX,
                SizeY = bs.SizeY,
            };
            gs.Buildings[b.Id] = b;
            // 按实例占地标格（住宅扩建后大于定义占地）
            for (int x = bs.X; x < bs.X + b.FootX; x++)
                for (int y = bs.Y; y < bs.Y + b.FootY; y++)
                    gs.Map.CellAt(x, y).BuildingId = b.Id;
        }

        foreach (var c in citizens ?? new List<Citizen>())
            gs.Citizens[c.Id] = c;
        foreach (var f in families ?? new List<Family>())
            gs.Families[f.Id] = f;

        GameState.I = gs;
        clock.SetDate(meta.Year, meta.Month, meta.Day, meta.Hour);

        EventBus.RaiseMapChanged();
        EventBus.RaiseZonesChanged();
        EventBus.RaiseStatsChanged();
        EventBus.RaiseGameLoaded();
        return true;
    }

    // ---- LMDB 工具 ----

    private static LightningEnvironment OpenEnv(string dir)
    {
        var env = new LightningEnvironment(dir)
        {
            MapSize = MapSizeBytes,
            MaxDatabases = 1,
        };
        env.Open();
        return env;
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOpts);

    private static T Get<T>(LightningTransaction tx, LightningDatabase db, string key) where T : class
    {
        var (code, _, value) = tx.Get(db, Encoding.UTF8.GetBytes(key));
        if (code != MDBResultCode.Success)
            return null;
        return JsonSerializer.Deserialize<T>(value.CopyToNewArray(), JsonOpts);
    }
}
