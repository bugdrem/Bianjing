using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 卷轴装裱层（地图外）：游戏世界坐落在一幅横卷「画」上——胡桃木色桌面（淡色横向木纹）垫底并向四周
/// 延展到超出相机视角（远端由深度雾化融入雾色，雾化开关见 Main），其上铺大于地图的长方形绢帛纸面
/// （纸面混铺多种程序化祥云：如意云/流云/卷云/层云，色调淡雅），地图四周外扩一层白底过渡，
/// 东西两端各横卧一根卷轴圆柱。全部网格置于 RenderLayers.Scroll 层，与地图内（RenderLayers.Map）分层渲染、可独立开关。
/// 东西两侧纸面留白设装饰区锚点（EastMargin/WestMargin），供后期题写古诗词与钤印（AddPoem/AddSeal）。
/// </summary>
public partial class ScrollBackdrop : Node3D
{
    /// <summary>祥云纹平铺单元边长（米）：纸面 UV 按此尺度平铺，值越小云纹排列越细密。</summary>
    private const float CloudTileSize = 90f;

    /// <summary>桌面木纹平铺单元边长（米）。</summary>
    private const float WoodTileSize = 60f;

    /// <summary>卷轴圆柱半径（米）。</summary>
    private const float RollerRadius = 14f;

    /// <summary>东西两侧装饰区锚点（地图外纸面留白处，后期诗词/印章挂此）。</summary>
    public Node3D EastMargin { get; private set; }
    public Node3D WestMargin { get; private set; }

    private float _paperY;

    public override void _Ready()
    {
        float mapSize = MapGrid.Size * MapGrid.CellSize;
        float baseY = TerrainConfig.MinTerrainHeight - 0.2f;   // 白底 = 裙板底，地形断面→裙板→白底无缝
        _paperY = baseY - 0.4f;                                 // 纸面垫在白底之下
        float paperX = (mapSize + 440f) * 2f;  // 东西向（卷轴圆柱所在方向）加宽，卷轴画更宽展
        float paperZ = mapSize + 180f;         // 南北向留窄白边，成横卷比例

        // 木制桌面（胡桃色 + 淡色横向木纹）：卷轴铺在桌上——桌面远超卷轴并向四周延到相机视角之外，
        // 远端由深度雾化融入雾色。单张大平面仅两三角面，尺寸再大也无渲染负担。
        float tableSide = CameraConfig.FarClip * 2.2f; // 超出远裁剪面所及，俯视/低角度都看不到桌边
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(tableSide, tableSide) },
            Position = new Vector3(0f, _paperY - 0.5f, 0f),
            Layers = RenderLayers.Scroll,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.36f, 0.24f, 0.15f), // 胡桃木色
                AlbedoTexture = MakeWoodTexture(),
                Uv1Scale = new Vector3(tableSide / WoodTileSize, tableSide / WoodTileSize, 1f),
                Roughness = 0.85f,
            },
        });

        // 白底：地图四周外扩 MapEdgeExtend 的纯白底色，垫在地图与卷轴纸面之间
        float baseSize = mapSize + 2f * WorldConfig.MapEdgeExtend;
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(baseSize, baseSize) },
            Position = new Vector3(0f, baseY, 0f),
            Layers = RenderLayers.Scroll,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.96f, 0.96f, 0.94f), // 白底（略暖白）
            },
        });

        // 纸面：绢帛暖米色 + 程序化祥云纹混铺（多种母题，淡雅低对比）
        var paperMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.84f, 0.78f, 0.62f), // 绢帛暖米色（宋画手卷纸面）
            AlbedoTexture = MakeCloudTexture(),
            Uv1Scale = new Vector3(paperX / CloudTileSize, paperZ / CloudTileSize, 1f),
        };
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(paperX, paperZ) },
            Position = new Vector3(0f, _paperY, 0f),
            Layers = RenderLayers.Scroll,
            MaterialOverride = paperMat,
        });

        // 两侧卷轴：深色漆木圆柱横卧东西两端（轴向南北，即绕 X 轴旋 90°），底部与纸面画布相切
        var rollerMesh = new CylinderMesh
        {
            TopRadius = RollerRadius,
            BottomRadius = RollerRadius,
            Height = paperZ + 60f, // 两端微出纸面，像轴头
        };
        var rollerMat = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.20f, 0.12f) }; // 深色漆木
        foreach (float sx in new[] { -1f, 1f })
        {
            AddChild(new MeshInstance3D
            {
                Mesh = rollerMesh,
                MaterialOverride = rollerMat,
                Layers = RenderLayers.Scroll,
                // 圆柱默认轴向 Y：绕 X 轴旋 90° 后轴向 Z（南北横卧）
                RotationDegrees = new Vector3(90f, 0f, 0f),
                // 底部与纸面相切：轴心抬高一个半径（圆柱底刚好落在 paperY）
                Position = new Vector3(sx * (paperX / 2f - RollerRadius * 0.4f), _paperY + RollerRadius, 0f),
            });
        }

        // 东西装饰区锚点：地图两侧纸面留白，约定为诗词/印章装饰区
        float marginX = mapSize / 2f + WorldConfig.MapEdgeExtend + 60f;
        EastMargin = new Node3D { Position = new Vector3(marginX, _paperY + 0.05f, 0f) };
        WestMargin = new Node3D { Position = new Vector3(-marginX, _paperY + 0.05f, 0f) };
        AddChild(EastMargin);
        AddChild(WestMargin);
    }

    /// <summary>拓展接口：在卷轴留白处题写古诗词（占位骨架——后续接竖排墨书渲染，本轮仅落锚点与占位墨条）。</summary>
    public void AddPoem(string text, bool east = true)
    {
        var anchor = east ? EastMargin : WestMargin;
        // 占位：一条墨色竖幅，示意题诗位置；正式竖排文字渲染留待后续
        anchor.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(16f, 90f) },
            Layers = RenderLayers.Scroll,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.18f, 0.16f, 0.25f) },
        });
    }

    /// <summary>拓展接口：在卷轴留白处钤盖印章（占位骨架——后续接印章纹样渲染，本轮仅落朱红方印占位）。</summary>
    public void AddSeal(bool east = true)
    {
        var anchor = east ? EastMargin : WestMargin;
        anchor.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(18f, 18f) },
            Position = new Vector3(0f, 0.02f, -60f),
            Layers = RenderLayers.Scroll,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.16f, 0.12f, 0.85f) }, // 印泥朱红
        });
    }

    // ---- 程序化祥云纹（多种母题混铺；代码绘制不引第三方资源，白底云纹四方连续，与纸色相乘呈淡影）----

    /// <summary>祥云母题圆盘（局部坐标，原点在云心）。</summary>
    private readonly record struct Disk(float X, float Y, float R);

    /// <summary>四种祥云母题（首次访问时各构建一次缓存）：如意云 / 流云 / 卷云 / 层云。</summary>
    private static readonly Disk[][] Motifs = { MotifRuyi(), MotifWisp(), MotifCurl(), MotifLayer() };

    /// <summary>生成祥云纹贴图：白底上混铺多种母题（大小/朝向各异），平铺后与绢帛纸色相乘得淡雅祥云。</summary>
    private static ImageTexture MakeCloudTexture()
    {
        const int S = 256;
        var img = Image.CreateEmpty(S, S, false, Image.Format.Rgba8);
        img.Fill(new Color(1f, 1f, 1f, 1f)); // 白底：与纸面相乘后呈纸色，云纹处略深

        var cloud = new Color(0.74f, 0.67f, 0.54f, 0.20f); // 淡雅云纹：低透明暖灰，混铺叠簇处自然略深
        // 多种母题错落混铺，各自八向复制 → 四方连续无缝
        StampMotif(img, Motifs[0], 52, 58, 1.05f, 0.20f, cloud);
        StampMotif(img, Motifs[1], 178, 92, 1.10f, -0.15f, cloud);
        StampMotif(img, Motifs[2], 112, 188, 0.95f, 0.50f, cloud);
        StampMotif(img, Motifs[3], 206, 200, 0.85f, 0.10f, cloud);
        StampMotif(img, Motifs[0], 200, 28, 0.70f, -0.40f, cloud);
        StampMotif(img, Motifs[1], 40, 150, 0.80f, 0.30f, cloud);

        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>把一枚母题以给定位置/缩放/旋转盖到图上（含八向复制，越界裁回对侧 → 平铺接缝不可见）。</summary>
    private static void StampMotif(Image img, Disk[] motif, float cx, float cy, float scale, float rot, Color col)
    {
        const int S = 256;
        float cos = Mathf.Cos(rot), sin = Mathf.Sin(rot);
        for (int ox = -S; ox <= S; ox += S)
            for (int oy = -S; oy <= S; oy += S)
                foreach (var d in motif)
                {
                    float lx = d.X * scale, ly = d.Y * scale;
                    DrawDisk(img, cx + ox + lx * cos - ly * sin, cy + oy + lx * sin + ly * cos, d.R * scale, col);
                }
    }

    /// <summary>如意云：饱满云头（多团云絮叠簇）+ 左右内卷云卷，宋画典型祥云。</summary>
    private static Disk[] MotifRuyi()
    {
        var ds = new List<Disk>
        {
            new(0, -10, 15), new(-16, -2, 14), new(16, -2, 14),
            new(-30, 7, 11), new(30, 7, 11), new(-9, 5, 12), new(9, 5, 12), new(0, 2, 13),
        };
        AddCurl(ds, -38, 12, -1f);
        AddCurl(ds, 38, 12, 1f);
        return ds.ToArray();
    }

    /// <summary>流云：一道蜿蜒横带的流动云丝，尾端带小卷。</summary>
    private static Disk[] MotifWisp()
    {
        var ds = new List<Disk>();
        for (int i = 0; i < 15; i++)
        {
            float x = -45 + i * 6.4f;
            float y = Mathf.Sin(i * 0.55f) * 9f;
            float r = 9.5f - Mathf.Abs(i - 7) * 0.55f;
            ds.Add(new Disk(x, y, r));
        }
        AddCurl(ds, 48, 6, 1f, turns: 0.7f);
        return ds.ToArray();
    }

    /// <summary>卷云：单枚大旋卷为主、拖一缕短尾，写意抽象。</summary>
    private static Disk[] MotifCurl()
    {
        var ds = new List<Disk>();
        const int N = 30;
        for (int t = 0; t < N; t++)
        {
            float a = t * 0.5f;
            float r = 17f * (1f - t / (float)N) + 2f;
            ds.Add(new Disk(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 4.6f * (1f - t / (float)N) + 1.6f));
        }
        ds.Add(new Disk(20, 4, 6));
        ds.Add(new Disk(28, 8, 4));
        return ds.ToArray();
    }

    /// <summary>层云：三两叠横带分层铺展，两端带卷，似云墙。</summary>
    private static Disk[] MotifLayer()
    {
        var ds = new List<Disk>();
        AddRow(ds, -8, new[] { -30f, -15f, 0f, 15f, 30f }, 10f);
        AddRow(ds, 3, new[] { -22f, -7f, 8f, 23f }, 11f);
        AddRow(ds, 13, new[] { -30f, -15f, 0f, 15f, 30f }, 9f);
        AddCurl(ds, -36, 14, -1f, turns: 0.6f);
        AddCurl(ds, 36, 14, 1f, turns: 0.6f);
        return ds.ToArray();
    }

    private static void AddRow(List<Disk> ds, float y, float[] xs, float r)
    {
        foreach (float x in xs)
            ds.Add(new Disk(x, y, r));
    }

    /// <summary>在 (cx,cy) 处加一枚由外向内旋卷的云卷（dir 左右旋向，turns 控制圈数比例）。</summary>
    private static void AddCurl(List<Disk> ds, float cx, float cy, float dir, float turns = 1f)
    {
        const int N = 20;
        int n = Mathf.RoundToInt(N * turns);
        for (int t = 0; t < n; t++)
        {
            float a = t * 0.42f;
            float r = 11f * (1f - t / (float)N) + 1.5f;
            ds.Add(new Disk(cx + dir * Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r, 3.4f));
        }
    }

    /// <summary>软边圆盘：中心实、边缘淡出，逐像素与底色插值（云纹柔和无硬边）。</summary>
    private static void DrawDisk(Image img, float cx, float cy, float r, Color col)
    {
        if (r <= 0f)
            return;
        int S = img.GetWidth();
        int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - r));
        int x1 = Mathf.Min(S - 1, Mathf.CeilToInt(cx + r));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - r));
        int y1 = Mathf.Min(S - 1, Mathf.CeilToInt(cy + r));
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d >= r)
                    continue;
                float edge = 1f - d / r;       // 中心 1 → 边缘 0
                float t = edge * edge * col.A;  // 软边淡出
                var src = img.GetPixel(x, y);
                img.SetPixel(x, y, src.Lerp(col, t));
            }
        }
    }

    // ---- 程序化木纹（淡色横线，四方连续，与胡桃木色相乘）----

    /// <summary>生成淡色横向木纹贴图：白底上沿 X 铺周期木纹带（含细密丝缕与微波动），与胡桃木色相乘得淡纹桌面。</summary>
    private static ImageTexture MakeWoodTexture()
    {
        const int S = 256;
        var img = Image.CreateEmpty(S, S, false, Image.Format.Rgba8);
        for (int y = 0; y < S; y++)
        {
            float yy = y / (float)S * Mathf.Tau; // 归一到 0..2π，频率取整数倍 → 纵向无缝
            for (int x = 0; x < S; x++)
            {
                // 木纹沿 X（横线）：亮度主要随 y 变化，x 仅做周期微波动令纹理不完全笔直（横向无缝）
                float wobble = Mathf.Sin(x / (float)S * Mathf.Tau * 2f) * 0.5f;
                float band = Mathf.Sin(yy * 3f + wobble);      // 3 条主木纹带/瓦片
                float fine = Mathf.Sin(yy * 13f) * 0.5f + Mathf.Sin(yy * 29f + 1.3f) * 0.3f; // 细密丝缕
                float grain = band * 0.6f + fine * 0.4f;       // -1..1
                float v = 1f - (grain * 0.5f + 0.5f) * 0.16f;  // 0.84..1.0 淡色低对比
                img.SetPixel(x, y, new Color(v, v, v, 1f));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
