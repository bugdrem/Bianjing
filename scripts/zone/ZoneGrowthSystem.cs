using System;
using Godot;

namespace Bianjing;

/// <summary>坊区生长系统：每月结算，居民在坊区内「临路 + 吸引力达标」的空格上自动建房/开店/设工坊；
/// 吸引力充足且维护完好的建筑逐级升格（容量随等级提升）。</summary>
public class ZoneGrowthSystem
{
    /// <summary>每月最多新增民居数。</summary>
    private const int MaxHousesPerMonth = 2;

    private readonly Random _rng = new();

    public void Tick(GameState gs)
    {
        // 财政破产则停止一切生长
        if (gs.Money <= 0)
            return;

        // 住房：全城总床位（含前店后宅/工坊宿舍）接近满员时继续吸引流民建房
        int capacity = 0;
        foreach (var b in gs.Buildings.Values)
            capacity += b.HousingCapacity;
        if (gs.Population >= capacity - 2)
        {
            for (int i = 0; i < MaxHousesPerMonth; i++)
                if (!TryGrow(gs, ZoneType.Residential, "house"))
                    break;
        }

        // 商铺：人口每 20 人支撑一间
        if (gs.CountByDef("shop") < gs.Population / 20 + (gs.Population > 0 ? 1 : 0))
            TryGrow(gs, ZoneType.Market, "shop");

        // 工坊：人口每 25 人支撑一间
        if (gs.CountByDef("workshop") < gs.Population / 25 + (gs.Population > 10 ? 1 : 0))
            TryGrow(gs, ZoneType.Workshop, "workshop");

        LevelUps(gs);
    }

    /// <summary>坊区建筑升级：吸引力越高级要求越高，年久失修的不升。</summary>
    private void LevelUps(GameState gs)
    {
        bool changed = false;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "grown" || b.Level >= b.Def.MaxLevel || b.Condition < 60f)
                continue;
            if (gs.Map.CellAt(b.Origin).Desirability < 1.2f * b.Level)
                continue;
            if (_rng.NextDouble() < 0.15)
            {
                b.Level++;
                changed = true;
            }
        }
        if (changed)
            EventBus.RaiseMapChanged();
    }

    /// <summary>在指定坊区内挑选吸引力最高的合法格生成建筑；无合法格返回 false。</summary>
    private static bool TryGrow(GameState gs, ZoneType zone, string defId)
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
                if (cell.Zone != zone || !cell.IsEmpty || cell.HasTree)
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
