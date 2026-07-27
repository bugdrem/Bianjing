using Godot;

namespace Bianjing;

/// <summary>建造合法性校验。</summary>
public static class PlacementValidator
{
    public static bool CanPlaceRoad(GameState gs, Vector2I c)
    {
        if (!MapGrid.InBounds(c))
            return false;
        ref var cell = ref gs.Map.CellAt(c);
        return cell.IsEmpty && gs.Money >= GameState.RoadCost;
    }

    /// <summary>桥梁：只能架在没有桥的水面上。</summary>
    public static bool CanPlaceBridge(GameState gs, Vector2I c)
    {
        if (!MapGrid.InBounds(c))
            return false;
        ref var cell = ref gs.Map.CellAt(c);
        return cell.HasWater && !cell.HasBridge && gs.Money >= GameState.BridgeCost;
    }

    /// <summary>建筑：占地全部为空格、在界内、至少一边临路（连通性）、钱够。</summary>
    public static bool CanPlaceBuilding(GameState gs, BuildingDef def, Vector2I origin, bool checkCost = true)
    {
        for (int x = origin.X; x < origin.X + def.SizeX; x++)
        {
            for (int y = origin.Y; y < origin.Y + def.SizeY; y++)
            {
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c) || !gs.Map.CellAt(c).IsEmpty)
                    return false;
            }
        }

        if (gs.Map.FindAdjacentRoad(origin, def.SizeX, def.SizeY) == null)
            return false;

        if (checkCost && def.Category == "official" && gs.Money < def.Cost)
            return false;

        return true;
    }

    /// <summary>坊区只能划在空地上。</summary>
    public static bool CanZone(GameState gs, Vector2I c)
    {
        return MapGrid.InBounds(c) && gs.Map.CellAt(c).IsEmpty;
    }
}
