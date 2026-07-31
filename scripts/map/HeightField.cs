using Godot;

namespace Bianjing;

/// <summary>
/// 顶点级高度场（灰度地图思想）：(Size+1)² 个 float 顶点覆盖全图，每格由四角顶点构成 2 个三角面。
/// y=0 为平原基准；地形只在世界生成期成形 + 建筑落位整平垫基，后期玩家升降地形复用同一套顶点写接口。
/// 格级衍生量（中心高/极值/坡角）即时由四角顶点算出，不另存格高。
/// </summary>
public class HeightField
{
    /// <summary>每边顶点数：格数 +1（1024 格 → 1025 顶点）。</summary>
    public const int VertsPerSide = MapGrid.Size + 1;

    /// <summary>顶点高度数组（行主序 vy*VertsPerSide+vx），米。</summary>
    private readonly float[] _h = new float[VertsPerSide * VertsPerSide];

    /// <summary>顶点读（越界钳制到边缘，图外视同边缘高度）。</summary>
    public float VertexH(int vx, int vy)
    {
        vx = Mathf.Clamp(vx, 0, VertsPerSide - 1);
        vy = Mathf.Clamp(vy, 0, VertsPerSide - 1);
        return _h[vy * VertsPerSide + vx];
    }

    /// <summary>顶点写（越界忽略）：世界生成/整平垫基/后期玩家塑形共用的唯一写入口。</summary>
    public void SetVertex(int vx, int vy, float h)
    {
        if (vx < 0 || vy < 0 || vx >= VertsPerSide || vy >= VertsPerSide)
            return;
        _h[vy * VertsPerSide + vx] = h;
    }

    /// <summary>世界坐标处的地面高度：双线性插值四邻顶点（村民/物件贴地用，坡面平滑升降）。</summary>
    public float SampleWorld(float wx, float wz)
    {
        float half = MapGrid.Size * MapGrid.CellSize / 2f;
        float fx = (wx + half) / MapGrid.CellSize;
        float fz = (wz + half) / MapGrid.CellSize;
        int ix = Mathf.FloorToInt(fx), iz = Mathf.FloorToInt(fz);
        float tx = fx - ix, tz = fz - iz;
        float a = VertexH(ix, iz), b = VertexH(ix + 1, iz);
        float c = VertexH(ix, iz + 1), d = VertexH(ix + 1, iz + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
    }

    /// <summary>格中心高度：四角顶点均值（渲染物件/建筑的 Y 基准）。</summary>
    public float CellCenterH(Vector2I c) =>
        (VertexH(c.X, c.Y) + VertexH(c.X + 1, c.Y) + VertexH(c.X, c.Y + 1) + VertexH(c.X + 1, c.Y + 1)) * 0.25f;

    /// <summary>格四角最低 / 最高顶点高度（建造校验用）。</summary>
    public float CellMinH(Vector2I c) =>
        Mathf.Min(Mathf.Min(VertexH(c.X, c.Y), VertexH(c.X + 1, c.Y)),
                  Mathf.Min(VertexH(c.X, c.Y + 1), VertexH(c.X + 1, c.Y + 1)));

    public float CellMaxH(Vector2I c) =>
        Mathf.Max(Mathf.Max(VertexH(c.X, c.Y), VertexH(c.X + 1, c.Y)),
                  Mathf.Max(VertexH(c.X, c.Y + 1), VertexH(c.X + 1, c.Y + 1)));

    /// <summary>格内坡角（度）：四角最大高差按 1m 格宽换算（铺路/通行的坡度判据）。</summary>
    public float CellSlopeDeg(Vector2I c) =>
        Mathf.RadToDeg(Mathf.Atan((CellMaxH(c) - CellMinH(c)) / MapGrid.CellSize));

    /// <summary>占地（origin 起 sx×sy 格）的四角顶点平均高：整平垫基的目标台面高。</summary>
    public float FootprintAvgH(Vector2I origin, int sx, int sy)
    {
        float sum = 0;
        int n = 0;
        for (int vx = origin.X; vx <= origin.X + sx; vx++)
            for (int vy = origin.Y; vy <= origin.Y + sy; vy++)
            {
                sum += VertexH(vx, vy);
                n++;
            }
        return n > 0 ? sum / n : 0f;
    }

    /// <summary>整平垫基：把占地覆盖的 (sx+1)×(sy+1) 顶点压平到 h——
    /// 占地边缘顶点与邻格共享，四周地表自动从外缘顶点接坡到台面（人工台地观感），无需额外过渡处理。</summary>
    public void FlattenRect(Vector2I origin, int sx, int sy, float h)
    {
        for (int vx = origin.X; vx <= origin.X + sx; vx++)
            for (int vy = origin.Y; vy <= origin.Y + sy; vy++)
                SetVertex(vx, vy, h);
    }

    /// <summary>导出 uint16 量化灰度 blob（存档用）：height = min + v × step，步长 0.01m；
    /// 若高度跨度超出 uint16 表达范围（>655m，正常不会发生）自动放大步长保底。</summary>
    public byte[] ToBlob(out float min, out float step)
    {
        min = _h[0];
        float max = _h[0];
        foreach (float h in _h)
        {
            if (h < min) min = h;
            if (h > max) max = h;
        }
        step = 0.01f;
        if (max - min > step * 65535f)
            step = (max - min) / 65535f; // 跨度过大时放大步长，保证可编码
        var blob = new byte[_h.Length * 2];
        for (int i = 0; i < _h.Length; i++)
        {
            int v = Mathf.Clamp(Mathf.RoundToInt((_h[i] - min) / step), 0, 65535);
            blob[i * 2] = (byte)(v & 0xFF);       // 小端低字节
            blob[i * 2 + 1] = (byte)(v >> 8);     // 小端高字节
        }
        return blob;
    }

    /// <summary>从灰度 blob 恢复全部顶点（读档用）：长度不符视为坏档，保持全零平原不崩溃。</summary>
    public void FromBlob(byte[] blob, float min, float step)
    {
        if (blob == null || blob.Length != _h.Length * 2)
            return;
        for (int i = 0; i < _h.Length; i++)
            _h[i] = min + (blob[i * 2] | (blob[i * 2 + 1] << 8)) * step;
    }
}
