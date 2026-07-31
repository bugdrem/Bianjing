using Godot;

namespace Bianjing;

/// <summary>纯 C# 逻辑网格：1024x1024，格边长 1m（一格一米），地图中心位于世界原点。</summary>
public class MapGrid
{
    public const int Size = 1024;
    public const float CellSize = 1f;

    private readonly Cell[] _cells = new Cell[Size * Size];

    /// <summary>顶点级地形高度场（灰度地图）：渲染/通行/建造的高度真相均在此，随存档保存。</summary>
    public HeightField Height { get; } = new();

    public MapGrid()
    {
        for (int i = 0; i < _cells.Length; i++)
            _cells[i].BuildingId = -1;
    }

    public ref Cell CellAt(int x, int y) => ref _cells[y * Size + x];

    public ref Cell CellAt(Vector2I c) => ref _cells[c.Y * Size + c.X];

    public static bool InBounds(Vector2I c) => c.X >= 0 && c.X < Size && c.Y >= 0 && c.Y < Size;

    /// <summary>格子中心的世界坐标（y=0，仅取平面位置；地面海拔另查 GroundY / Height 高度场）。</summary>
    public static Vector3 CellToWorld(Vector2I c)
    {
        float half = Size * CellSize / 2f;
        return new Vector3(c.X * CellSize - half + CellSize / 2f, 0f, c.Y * CellSize - half + CellSize / 2f);
    }

    /// <summary>某格的地面海拔（米，四角顶点均值）：越界按平地 0。渲染/通行的 Y 基准都从这里取。</summary>
    public float GroundY(Vector2I c) =>
        InBounds(c) ? Height.CellCenterH(c) : 0f;

    public static Vector2I WorldToCell(Vector3 p)
    {
        float half = Size * CellSize / 2f;
        return new Vector2I(
            Mathf.FloorToInt((p.X + half) / CellSize),
            Mathf.FloorToInt((p.Z + half) / CellSize));
    }

    /// <summary>footprint（origin 起 sx*sy 格）四周一圈里找一个道路格，没有返回 null。</summary>
    public Vector2I? FindAdjacentRoad(Vector2I origin, int sx, int sy)
    {
        for (int x = origin.X - 1; x <= origin.X + sx; x++)
        {
            for (int y = origin.Y - 1; y <= origin.Y + sy; y++)
            {
                bool inside = x >= origin.X && x < origin.X + sx && y >= origin.Y && y < origin.Y + sy;
                if (inside)
                    continue;
                var c = new Vector2I(x, y);
                if (InBounds(c) && CellAt(c).HasRoad)
                    return c;
            }
        }
        return null;
    }

    /// <summary>从 from 向外逐圈搜索最近的道路格，超出 maxRadius 返回 null。</summary>
    public Vector2I? FindNearestRoad(Vector2I from, int maxRadius)
    {
        if (InBounds(from) && CellAt(from).HasRoad)
            return from;

        for (int r = 1; r <= maxRadius; r++)
        {
            for (int x = from.X - r; x <= from.X + r; x++)
            {
                for (int y = from.Y - r; y <= from.Y + r; y++)
                {
                    // 只扫环上的格子
                    if (Mathf.Abs(x - from.X) != r && Mathf.Abs(y - from.Y) != r)
                        continue;
                    var c = new Vector2I(x, y);
                    if (InBounds(c) && CellAt(c).HasRoad)
                        return c;
                }
            }
        }
        return null;
    }

    /// <summary>从 from 向外逐圈搜索最近的树木格，超出 maxRadius 返回 null。</summary>
    public Vector2I? FindNearestTree(Vector2I from, int maxRadius)
    {
        if (InBounds(from) && CellAt(from).HasTree)
            return from;

        for (int r = 1; r <= maxRadius; r++)
        {
            for (int x = from.X - r; x <= from.X + r; x++)
            {
                for (int y = from.Y - r; y <= from.Y + r; y++)
                {
                    // 只扫环上的格子
                    if (Mathf.Abs(x - from.X) != r && Mathf.Abs(y - from.Y) != r)
                        continue;
                    var c = new Vector2I(x, y);
                    if (InBounds(c) && CellAt(c).HasTree)
                        return c;
                }
            }
        }
        return null;
    }
}
