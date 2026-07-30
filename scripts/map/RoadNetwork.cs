using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>道路连通网络：基于 AStarGrid2D，只有道路格可通行；
/// 寻路权重按道路种类区分（主路代价低、小路代价高），使居民在时间相近时偏好走快路。权重取自 MovementConfig。</summary>
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

    /// <summary>开关单格通行并按道路种类设寻路权重（主路 1.0基准，辅路/桥面/小路按移速逐档加价）。</summary>
    public void SetRoad(Vector2I c, bool isRoad, RoadKind kind = RoadKind.None)
    {
        _astar.SetPointSolid(c, !isRoad);
        if (isRoad)
            _astar.SetPointWeightScale(c, MovementConfig.RoadWeight(kind));
    }

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
