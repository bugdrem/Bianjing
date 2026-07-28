using System;
using Godot;

namespace Bianjing;

/// <summary>坊区生长系统（每日结算，日频概率——1x 下一游戏日 ≈ 20 现实秒、一游戏月 ≈ 10 现实分钟）：
/// 居民在「可建设区」内临路+吸引力达标的空格上自动建房——初生均为住宅；
/// 住宅逐级升格（容量随等级提升），升级时有概率转业为商铺/工坊（前店后宅，带来就业与交易）。</summary>
public class ZoneGrowthSystem
{
    /// <summary>缺房时每日建一座住宅的概率。</summary>
    private const float HouseChancePerDay = 0.6f;

    /// <summary>建筑每日升级概率。</summary>
    private const float LevelUpChancePerDay = 0.02f;

    /// <summary>住宅升级时转为商铺 / 工坊的概率（其余仍为住宅）。</summary>
    private const float ShopConvertChance = 0.3f;
    private const float WorkshopConvertChance = 0.12f;

    private readonly Random _rng = new();

    public void TickDay(GameState gs)
    {
        // 财政破产则停止一切生长（无限钱下不受此限）
        if (gs.Money <= 0 && !GameSettings.InfiniteMoney)
            return;

        // 住房：全城总床位（含前店后宅）接近满员时继续吸引流民建房。商铺/工坊不再直接生成，而是由住宅升级转业而来。
        int capacity = 0;
        foreach (var b in gs.Buildings.Values)
            capacity += b.HousingCapacity;
        if (gs.Population >= capacity - 2 && _rng.NextDouble() < HouseChancePerDay)
            TryGrow(gs, "house");

        LevelUps(gs);
    }

    /// <summary>坊区建筑升级：吸引力越高级要求越高，年久失修的不升；住宅升级后有概率转业。</summary>
    private void LevelUps(GameState gs)
    {
        bool changed = false;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "grown" || b.Level >= b.Def.MaxLevel || b.Condition < 60f)
                continue;
            if (gs.Map.CellAt(b.Origin).Desirability < 1.2f * b.Level)
                continue;
            if (_rng.NextDouble() < LevelUpChancePerDay)
            {
                b.Level++;
                changed = true;
                // 住宅升级后概率转为商铺/工坊（占地不变，居民保留）
                if (b.Def.Id == "house")
                    TryConvertHouse(gs, b);
            }
        }
        if (changed)
            EventBus.RaiseMapChanged();
    }

    /// <summary>住宅升级时掷一下是否转业：多数变商铺，少数变工坊，余下仍为住宅。</summary>
    private void TryConvertHouse(GameState gs, BuildingInstance b)
    {
        double r = _rng.NextDouble();
        if (r < ShopConvertChance)
            gs.ConvertGrown(b, "shop");
        else if (r < ShopConvertChance + WorkshopConvertChance)
            gs.ConvertGrown(b, "workshop");
    }

    /// <summary>在可建设区内挑选吸引力最高的合法格生成建筑；无合法格返回 false。</summary>
    private static bool TryGrow(GameState gs, string defId)
    {
        var def = gs.Defs[defId];
        float bestScore = float.MinValue;
        Vector2I best = default;
        bool found = false;

        for (int x = 0; x < MapGrid.Size; x++)
        {
            for (int y = 0; y < MapGrid.Size; y++)
            {
                ref var cell = ref gs.Map.CellAt(x, y);
                if (cell.Zone != ZoneType.Buildable || !cell.IsEmpty || cell.HasTree)
                    continue;

                var c = new Vector2I(x, y);
                if (gs.Map.FindAdjacentRoad(c, 1, 1) == null)
                    continue;

                // 临路本身 +1，加上环境吸引力；达标线为 >= 1（即临路即可起步，衙门/宫殿加速）
                float score = cell.Desirability + 1f;
                if (score < 1f)
                    continue;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                    found = true;
                }
            }
        }

        if (!found)
            return false;

        gs.PlaceBuilding(def, best);
        return true;
    }
}
