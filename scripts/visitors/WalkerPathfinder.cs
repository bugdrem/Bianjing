using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 外来访客专用轻量寻路：复用现有 FindNearestRoad + RoadNetwork.FindPath，
/// 不复制 CitizenAgent 的 1800 行决策逻辑。混合寻路：就近上路 → 路网 AStar → 末段直线接近目标。
/// </summary>
public static class WalkerPathfinder
{
    /// <summary>从 fromWorld 走到 toWorld 的世界坐标路径点序列（水平面，y=0.2 贴地基准，由调用方做地形贴合）。</summary>
    public static List<Vector3> BuildPath(GameState gs, Vector3 fromWorld, Vector3 toWorld)
    {
        var points = new List<Vector3>();
        var startCell = MapGrid.WorldToCell(fromWorld);
        var targetCell = MapGrid.WorldToCell(toWorld);

        Vector2I? entry = gs.RoadCells.Count > 0 ? gs.Map.FindNearestRoad(startCell, 64) : null;
        Vector2I? exit = entry != null ? gs.Map.FindNearestRoad(targetCell, 96) : null;
        if (entry != null && exit != null && entry.Value != exit.Value)
        {
            var cells = gs.Roads.FindPath(entry.Value, exit.Value);
            if (cells.Count > 0)
            {
                // 上路前脱路段（极短，直接连到第一格路心）
                points.Add(MapGrid.CellToWorld(cells[0]) + Vector3.Up * 0.2f);
                foreach (var c in cells)
                    points.Add(MapGrid.CellToWorld(c) + Vector3.Up * 0.2f);
            }
        }

        // 末段：脱路走向目标（直线；路面已覆盖主要通行，短距贴边一般不蹚水）
        points.Add(new Vector3(toWorld.X, 0.2f, toWorld.Z));
        return points;
    }
}
