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
    /// <summary>v6：住宅容量（一格 4~6 人）、建筑废弃标志、住宅自生长与升级转业语义变更；早期开发不兼容老档，版本不符拒读。</summary>
    public const int FormatVersion = 6;
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

    public static void Save(GameClock clock, string slot, string saveName)
    {
        var gs = GameState.I;
        string dir = SlotDir(slot);
        Directory.CreateDirectory(dir);

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
        };

        var map = new MapSave();
        for (int y = 0; y < MapGrid.Size; y++)
        {
            for (int x = 0; x < MapGrid.Size; x++)
            {
                ref var cell = ref gs.Map.CellAt(x, y);
                int index = y * MapGrid.Size + x;
                if (cell.HasRoad)
                    map.RoadCells.Add(index);
                if (cell.HasWater)
                    map.WaterCells.Add(index);
                if (cell.HasBridge)
                    map.BridgeCells.Add(index);
                if (cell.Zone != ZoneType.None)
                {
                    map.ZoneCells.Add(index);
                    map.ZoneTypes.Add((int)cell.Zone);
                }
            }
        }

        var buildings = new List<BuildingSave>(gs.Buildings.Count);
        foreach (var b in gs.Buildings.Values)
            buildings.Add(new BuildingSave
            {
                Id = b.Id, DefId = b.Def.Id, X = b.Origin.X, Y = b.Origin.Y,
                Level = b.Level, Condition = b.Condition,
                BuiltYear = b.BuiltYear, BuiltMonth = b.BuiltMonth,
                Specialty = b.Specialty, Inv = b.Inv, MonthsSinceHarvest = b.MonthsSinceHarvest,
                Abandoned = b.Abandoned,
            });

        var citizens = new List<Citizen>(gs.Citizens.Values);
        var families = new List<Family>(gs.Families.Values);
        var plants = new List<PlantObj>(gs.Plants.Values);
        var animals = new List<AnimalObj>(gs.Animals.Values);
        var piles = new List<ItemPileObj>(gs.Piles.Values);

        using var env = OpenEnv(dir);
        using var tx = env.BeginTransaction();
        using var db = tx.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });

        Put(tx, db, "meta", meta);
        Put(tx, db, "world", world);
        Put(tx, db, "map", map);
        Put(tx, db, "buildings", buildings);
        Put(tx, db, "citizens", citizens);
        Put(tx, db, "families", families);
        Put(tx, db, "plants", plants);
        Put(tx, db, "animals", animals);
        Put(tx, db, "piles", piles);

        tx.Commit(); // 单事务提交：原子落盘
    }

    // ---- 读取 ----

    public static bool Load(GameClock clock, string slot)
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
        };

        foreach (int index in map.RoadCells)
        {
            var c = new Vector2I(index % MapGrid.Size, index / MapGrid.Size);
            gs.Map.CellAt(c).HasRoad = true;
            gs.Roads.SetRoad(c, true);
        }
        for (int i = 0; i < map.ZoneCells.Count; i++)
        {
            int index = map.ZoneCells[i];
            gs.Map.CellAt(index % MapGrid.Size, index / MapGrid.Size).Zone = (ZoneType)map.ZoneTypes[i];
        }
        foreach (int index in map.WaterCells ?? new List<int>())
            gs.Map.CellAt(index % MapGrid.Size, index / MapGrid.Size).HasWater = true;
        foreach (int index in map.BridgeCells ?? new List<int>())
            gs.Map.CellAt(index % MapGrid.Size, index / MapGrid.Size).HasBridge = true; // HasRoad 已由 RoadCells 恢复

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
            };
            gs.Buildings[b.Id] = b;
            for (int x = bs.X; x < bs.X + def.SizeX; x++)
                for (int y = bs.Y; y < bs.Y + def.SizeY; y++)
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

    private static void Put<T>(LightningTransaction tx, LightningDatabase db, string key, T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOpts);
        tx.Put(db, Encoding.UTF8.GetBytes(key), bytes);
    }

    private static T Get<T>(LightningTransaction tx, LightningDatabase db, string key) where T : class
    {
        var (code, _, value) = tx.Get(db, Encoding.UTF8.GetBytes(key));
        if (code != MDBResultCode.Success)
            return null;
        return JsonSerializer.Deserialize<T>(value.CopyToNewArray(), JsonOpts);
    }
}
