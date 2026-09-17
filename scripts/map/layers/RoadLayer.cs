using Godot;

namespace Bianjing;

/// <summary>
/// 道路层：三类砖纹路面各一张网格（主路白石 / 辅路青砖 / 小路深青砖）+ 一张桥面/引桥实体板网格。
/// 路面贴地（四角采地形顶点高，坡道上自然倾斜），外缘垂一圈地基立面读作台基侧壁。
/// 与地形/水面分家后，改砖纹或改桥体厚度只影响本层。
/// </summary>
public partial class RoadLayer : MapLayer
{
    /// <summary>路面顶面顶点色：中性近白，砖色/明暗全由砖纹贴图承载（贴图×顶点色）。</summary>
    private static readonly Color RoadTopColor = new(0.97f, 0.96f, 0.93f);

    /// <summary>主路白石小方砖（错缝）：砖 0.81m 见方、缝 0.026m，两行错半砖（宋御街砖石甃砌的白石街面）。</summary>
    private static readonly Color MainBrickColor = new(0.91f, 0.90f, 0.86f);
    /// <summary>辅路青砖长条（错缝）：砖 0.76×0.42m、缝 0.024~0.028m（宋砖淡青灰色，砖长宽约二比一）。</summary>
    private static readonly Color SideBrickColor = new(0.53f, 0.55f, 0.52f);
    /// <summary>小路深青砖小方砖（错缝）：砖 0.50m、缝 0.016m（坊巷青砖小砖，色更深）。</summary>
    private static readonly Color LaneBrickColor = new(0.40f, 0.41f, 0.39f);

    /// <summary>主路砖纹：128×256（两行错缝周期），行周期 128px＝0.84m（砖 124px + 缝 4px）。</summary>
    private static readonly ImageTexture MainRoadTexture = MakeBrickTexture(MainBrickColor, 0.72f, 124, 124, 4);
    /// <summary>辅路砖纹：256×256（两行错缝周期），横周期 256px＝0.78m、纵行周期 128px＝0.45m（砖 248×120px + 缝 8px）。</summary>
    private static readonly ImageTexture SideRoadTexture = MakeBrickTexture(SideBrickColor, 0.72f, 248, 120, 8);
    /// <summary>小路砖纹：128×256（两行错缝周期），行周期 128px＝0.52m（砖 124px + 缝 4px）。</summary>
    private static readonly ImageTexture LaneRoadTexture = MakeBrickTexture(LaneBrickColor, 0.72f, 124, 124, 4);

    private StandardMaterial3D _mainMat;
    private StandardMaterial3D _sideMat;
    private StandardMaterial3D _laneMat;
    private StandardMaterial3D _bridgeMat;

    private MeshInstance3D[] _main;
    private MeshInstance3D[] _side;
    private MeshInstance3D[] _lane;
    private MeshInstance3D[] _bridge;

    public override void Build()
    {
        // 三类道路砖纹材质（贴图×顶点色）：各自 Uv1Scale 把世界坐标 UV 平铺成砖周期
        // （主 0.84m / 辅 0.78×0.45m / 小路 0.52m）；双面：地基立面低角内外侧都可见
        _mainMat = new StandardMaterial3D
        {
            AlbedoTexture = MainRoadTexture,
            Uv1Scale = new Vector3(1f / 0.84f, 1f / 0.84f, 1f),
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _sideMat = new StandardMaterial3D
        {
            AlbedoTexture = SideRoadTexture,
            Uv1Scale = new Vector3(1f / 0.78f, 1f / 0.45f, 1f),
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _laneMat = new StandardMaterial3D
        {
            AlbedoTexture = LaneRoadTexture,
            Uv1Scale = new Vector3(1f / 0.52f, 1f / 0.52f, 1f),
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _bridgeMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // 双面：低角看桥底不漏面
        };

        int n = ChunksPerSide * ChunksPerSide;
        _main = new MeshInstance3D[n];
        _side = new MeshInstance3D[n];
        _lane = new MeshInstance3D[n];
        _bridge = new MeshInstance3D[n];
        for (int i = 0; i < n; i++)
        {
            _main[i] = NewMesh($"RoadMain{i}", _mainMat);
            _side[i] = NewMesh($"RoadSide{i}", _sideMat);
            _lane[i] = NewMesh($"RoadLane{i}", _laneMat);
            _bridge[i] = NewMesh($"Bridge{i}", _bridgeMat);
        }
    }

    /// <summary>路面网格节点：不投影（路面贴地，自阴影只会脏化砖纹）。</summary>
    private MeshInstance3D NewMesh(string name, Material mat)
    {
        var mi = new MeshInstance3D
        {
            Name = name,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mi);
        return mi;
    }

    /// <summary>把分块的三类路面与桥面缓冲各刷成一张网格：空缓冲挂 null（节点不绘制）。</summary>
    public void ApplyChunk(int index, ChunkGeometry geo)
    {
        _main[index].Mesh = LayerKit.MeshFrom(geo.RoadMain);
        _side[index].Mesh = LayerKit.MeshFrom(geo.RoadSide);
        _lane[index].Mesh = LayerKit.MeshFrom(geo.RoadLane);
        _bridge[index].Mesh = LayerKit.MeshFrom(geo.Bridge);
    }

    /// <summary>程序化生成错缝砖纹（宋砖石路面用，免外部贴图）：纹理宽=一行砖周期、高=两行
    /// （偶数行错半砖，平铺后砖缝交错如砌墙）；砖缝不另配色，取砖色×mortarDarken 加深（刻痕感）；
    /// 砖面按砖格伪随机微扰明度（±3% 石色不均），砖内四宫格再微扰（±2.5% 磨面质感）。
    /// 世界尺度由材质 Uv1Scale 给定，纹理像素只定图案。</summary>
    private static ImageTexture MakeBrickTexture(Color brick, float mortarDarken, int brickPxW, int brickPxH, int mortarPx)
    {
        int rowPxW = brickPxW + mortarPx, rowPxH = brickPxH + mortarPx;
        int w = rowPxW, h = rowPxH * 2;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        int halfOffset = rowPxW / 2; // 偶数行错半砖
        for (int py = 0; py < h; py++)
        {
            bool inMortarRow = py % rowPxH >= brickPxH;
            int row = py / rowPxH; // 0/1：两行错缝周期
            int offset = row == 1 ? halfOffset : 0;
            int by = py / rowPxH;
            int qy = (py % brickPxH) / (brickPxH / 2);
            for (int px = 0; px < w; px++)
            {
                int sx = (px + rowPxW - offset) % rowPxW; // 行内错缝后的水平位置
                bool inMortar = inMortarRow || sx % rowPxW >= brickPxW;
                Color col;
                if (inMortar)
                {
                    col = brick * mortarDarken;
                }
                else
                {
                    int bx = sx / rowPxW; // 逻辑砖格
                    int qx = (sx % brickPxW) / (brickPxW / 2);
                    float brickShade = 0.97f + 0.06f * Hash01(bx, by);                     // 砖级 ±3%
                    float grain = 1f + (Hash01(bx * 7 + qx, by * 13 + qy) - 0.5f) * 0.05f; // 砖内四宫格 ±2.5%
                    col = brick * Mathf.Clamp(brickShade * grain, 0.85f, 1.08f);
                }
                img.SetPixel(px, py, col);
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>整数哈希 → [0,1)：砖格级伪随机，同砖同值（避免逐像素噪声的闪烁观感）。</summary>
    private static float Hash01(int a, int b)
    {
        int h = a * 73856093 ^ b * 19349663;
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h & 0x7fffffff) % 1000) / 1000f;
    }
}
