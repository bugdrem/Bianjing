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

    /// <summary>桥跨探岸的两个候选轴向（东西/南北）：静态复用免逐次分配。</summary>
    private static readonly Vector2I[] DeckAxes = { new(1, 0), new(0, 1) };

    /// <summary>桥跨参数：沿两轴各探两岸最近陆格，取水面跨度更短的一轴为桥跨向；
    /// 输出跨向 axis、到两岸格距 distA/distB、两岸地面高 bankA/bankB。探不到两岸返回 false。</summary>
    private bool BridgeSpan(Vector2I c, out Vector2I axis, out int distA, out int distB, out float bankA, out float bankB)
    {
        const int maxReach = 64; // 单侧最大探岸距（格）：覆盖最宽河面，超出视为无岸
        axis = new Vector2I(1, 0);
        distA = distB = 0; bankA = bankB = 0f;
        int bestSpan = int.MaxValue;
        bool found = false;
        foreach (var dir in DeckAxes)
        {
            int dA = 0, dB = 0; float bA = 0f, bB = 0f;
            bool foundA = false, foundB = false;
            for (int i = 1; i <= maxReach && !foundA; i++)
            {
                var p = c - dir * i;
                if (!InBounds(p)) break;
                if (!CellAt(p).HasWater) { dA = i; bA = GroundY(p); foundA = true; }
            }
            for (int i = 1; i <= maxReach && !foundB; i++)
            {
                var p = c + dir * i;
                if (!InBounds(p)) break;
                if (!CellAt(p).HasWater) { dB = i; bB = GroundY(p); foundB = true; }
            }
            if (!foundA || !foundB) continue;
            int span = dA + dB;
            if (span >= bestSpan) continue;
            bestSpan = span;
            axis = dir; distA = dA; distB = dB; bankA = bA; bankB = bB;
            found = true;
        }
        return found;
    }

    /// <summary>桥面顶海拔（米）——扁平拱桥：整段跨水为一座拱，两端落在两岸地面高、
    /// 中部拱起；拱顶（河中央）= 两岸较低者 + BridgeArchApexRise（封顶 1m）。
    /// 拱形 = 两岸连线（弦）+ 抛物面鼓包 4h·t(1-t)；探不到岸退化为水面 + 抬升。
    /// 渲染桥体与村民过桥站面共用。</summary>
    public float BridgeDeckTopAt(Vector2I c)
    {
        if (BridgeSpan(c, out _, out int distA, out int distB, out float bankA, out float bankB))
        {
            int span = distA + distB;
            float t = (float)distA / span;                       // 本格在跨上的位置（0=A岸 1=B岸）
            float chord = Mathf.Lerp(bankA, bankB, t);            // 两岸连线（弦）
            float lower = Mathf.Min(bankA, bankB);
            float archH = Mathf.Max(0f, lower + WorldConfig.BridgeArchApexRise - (bankA + bankB) / 2f); // 拱高（相对弦中点）
            float bump = 4f * archH * t * (1f - t);               // 抛物鼓包：两端 0、中央最高
            float deck = chord + bump;
            // 拱面不至于没入水下（深切河谷兵底）
            return Mathf.Max(deck, (InBounds(c) ? CellAt(c).WaterH : 0f) + WorldConfig.BridgeArchApexRise);
        }
        return (InBounds(c) ? CellAt(c).WaterH : 0f) + WorldConfig.BridgeArchApexRise;
    }

    /// <summary>本格是否属于「桥面连续体」（桥格，或桥旁 ≤BridgeRampCells 的引桥陆地路格）——
    /// 用于桥面/引桥的实体板渲染与村民站面：周围 ±BridgeRampCells 窗内含桥格则是。</summary>
    public bool NearBridge(int x, int y)
    {
        int r = WorldConfig.BridgeRampCells;
        for (int oy = -r; oy <= r; oy++)
            for (int ox = -r; ox <= r; ox++)
            {
                var c = new Vector2I(x + ox, y + oy);
                if (InBounds(c) && CellAt(c).HasBridge)
                    return true;
            }
        return false;
    }

    /// <summary>桥面/引桥顶面某顶点海拔：向外扫描找最近桥格（格距 d），桥面高=最近桥格 BridgeDeckTopAt、
    /// 岸路高=顶点地高+RoadSurfaceLift，按 t=d/BridgeRampCells 插值——桥心坐桥面高、向岸逐格降到岸路高；
    /// 渲染桥体与村民过桥站面共用同一顶点高，二者严丝合缝。</summary>
    public float DeckVertexTop(int vx, int vy)
    {
        int r = WorldConfig.BridgeRampCells;
        float roadH = Height.VertexH(vx, vy) + WorldConfig.RoadSurfaceLift;
        float bestDist = float.MaxValue, deckH = roadH;
        for (int cy = vy - r - 1; cy <= vy + r; cy++)
            for (int cx = vx - r - 1; cx <= vx + r; cx++)
            {
                var c = new Vector2I(cx, cy);
                if (!InBounds(c) || !CellAt(c).HasBridge)
                    continue;
                float d = Mathf.Max(Mathf.Abs(vx - (cx + 0.5f)), Mathf.Abs(vy - (cy + 0.5f))) - 0.5f;
                if (d < bestDist)
                {
                    bestDist = d;
                    deckH = BridgeDeckTopAt(c);
                }
            }
        if (bestDist >= float.MaxValue)
            return roadH;
        float t = Mathf.Clamp(bestDist / r, 0f, 1f);
        return Mathf.Lerp(deckH, roadH, t);
    }

    /// <summary>世界坐标处的桥面/引桥顶面高：双线性插值四邻顶点 DeckVertexTop（与实体板渲染同源，
    /// 村民过桥/上下引桥坡站面贴合桥面而不下沉）。</summary>
    public float DeckSurfaceY(float wx, float wz)
    {
        float half = Size * CellSize / 2f;
        float fx = (wx + half) / CellSize;
        float fz = (wz + half) / CellSize;
        int ix = Mathf.FloorToInt(fx), iz = Mathf.FloorToInt(fz);
        float tx = fx - ix, tz = fz - iz;
        float a = DeckVertexTop(ix, iz), b = DeckVertexTop(ix + 1, iz);
        float c = DeckVertexTop(ix, iz + 1), d = DeckVertexTop(ix + 1, iz + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
    }

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
