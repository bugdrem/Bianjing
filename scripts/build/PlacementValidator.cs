using Godot;

namespace Bianjing;

/// <summary>建造合法性校验。</summary>
public static class PlacementValidator
{
    public static bool CanPlaceRoad(GameState gs, Vector2I c, RoadKind kind = RoadKind.Side)
    {
        if (!MapGrid.InBounds(c))
            return false;
        ref var cell = ref gs.Map.CellAt(c);
        if (!cell.IsEmpty)
            return false;
        if (!SlopeWalkable(gs, c)) // 陡壁不可铺路（坡度≤上限才能修路供村民翻山）
            return false;
        return GameSettings.InfiniteMoney || gs.Money >= GameState.RoadCostOf(kind);
    }

    /// <summary>本格坡度是否可供人行/铺路：与四邻的层差均在可翻越范围内（不处在陡壁边缘）。
    /// 山体生成已削平陡壁，自然山坡处处可过；此检查主为后续玩家塑形（b 方案）预留。</summary>
    public static bool SlopeWalkable(GameState gs, Vector2I c)
    {
        int h = gs.Map.CellAt(c).Height;
        Vector2I[] dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        foreach (var d in dirs)
        {
            var n = c + d;
            if (MapGrid.InBounds(n) && !TerrainConfig.Traversable(h, gs.Map.CellAt(n).Height))
                return false;
        }
        return true;
    }

    /// <summary>桥梁：只能架在没有桥的水面上。</summary>
    public static bool CanPlaceBridge(GameState gs, Vector2I c)
    {
        if (!MapGrid.InBounds(c))
            return false;
        ref var cell = ref gs.Map.CellAt(c);
        return cell.HasWater && !cell.HasBridge && (GameSettings.InfiniteMoney || gs.Money >= GameState.BridgeCost);
    }

    /// <summary>建筑：里程碑已解锁、占地全部为空格、在界内、占地整块同高（平地）、至少一边临路、钱够。</summary>
    public static bool CanPlaceBuilding(GameState gs, BuildingDef def, Vector2I origin, bool checkCost = true)
    {
        // 里程碑未到不可建（菜单置灰外的双重保险，防热键/mod 绕过）
        if (def.MilestoneRequired > gs.MilestoneLevel)
            return false;

        int baseH = gs.Map.CellAt(origin).Height; // 建筑要求平地：占地内每格高度须一致
        for (int x = origin.X; x < origin.X + def.SizeX; x++)
        {
            for (int y = origin.Y; y < origin.Y + def.SizeY; y++)
            {
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c) || !gs.Map.CellAt(c).IsEmpty)
                    return false;
                if (gs.Map.CellAt(c).Height != baseH)
                    return false; // 坡地不可建（后续 b 方案再支持依坡垫基）
            }
        }

        if (gs.Map.FindAdjacentRoad(origin, def.SizeX, def.SizeY) == null)
            return false;

        if (checkCost && !GameSettings.InfiniteMoney && def.Category == "official" && gs.Money < def.Cost)
            return false;

        return true;
    }

    /// <summary>坊区只能划在空地上。</summary>
    public static bool CanZone(GameState gs, Vector2I c)
    {
        return MapGrid.InBounds(c) && gs.Map.CellAt(c).IsEmpty;
    }
}
