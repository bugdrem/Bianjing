using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>道路连通网络：基于 AStarGrid2D，只有道路格可通行。</summary>
public class RoadNetwork
{
    private readonly AStarGrid2D _astar = new();

    public RoadNetwork()
    {
        _astar.Region = new Rect2I(0, 0, MapGrid.Size, MapGrid.Size);
        _astar.CellSize = Vector2.One;
        _astar.DiagonalMode = AStarGrid2D.DiagonalModeEnum.Never;
        _astar.Update();

        // 初始全部不可通行，铺路后逐格打开（1024² 百万点用区域填充，免逐点 interop 开销）
        _astar.FillSolidRegion(new Rect2I(0, 0, MapGrid.Size, MapGrid.Size), true);
    }

    public void SetRoad(Vector2I c, bool isRoad) => _astar.SetPointSolid(c, !isRoad);

    /// <summary>沿道路寻路，返回格子序列；不可达时返回空列表。</summary>
    public List<Vector2I> FindPath(Vector2I from, Vector2I to)
    {
        var result = new List<Vector2I>();
        if (!MapGrid.InBounds(from) || !MapGrid.InBounds(to))
            return result;
        if (_astar.IsPointSolid(from) || _astar.IsPointSolid(to))
            return result;

        foreach (Vector2I id in _astar.GetIdPath(from, to))
            result.Add(id);
        return result;
    }
}
