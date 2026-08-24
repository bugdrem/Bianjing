using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 宋代建筑造型工厂（纯代码、零外部资产）：把一座建筑按 Category / 占地 / 等级拆成若干「角色」
/// （地基 / 房体 / 主坡顶 / 庑殿端坡 / 檐口 / 屋脊 / 立柱 / 招幌 / 灯笼），每个角色是一组
/// 原始体（Box/Prism/Cylinder/Sphere）的变换+颜色，供 GridRenderer 用独立 MultiMesh 实例化、
/// 供 BuildController 用同源逻辑填放置预览，避免预览误导。
///
/// 造型原则：屋身半透 Box（可透视屋内）、主坡屋顶用 Prism 三棱柱（gable）、檐口薄 Box 环一圈、
/// 屋脊细 Box、立柱 Cylinder（按类取朱红/深木/土褐）、招幌仅商铺、灯笼点缀官署商铺。
/// 颜色以 BuildingDef.Color 为基调，屋顶统一压暗作灰瓦，木作取深木/朱红色板。
/// </summary>
public static class BuildingModelFactory
{
    // ---- 共享视觉常量（与 GridRenderer 同源，避免两边漂移）----
    public static readonly Color FoundationColor = new(0.52f, 0.48f, 0.42f); // 夯土灰褐
    public static readonly Color MainDoorColor = new(0.85f, 0.70f, 0.35f);    // 大门亮金
    public static readonly Color BackDoorColor = new(0.45f, 0.32f, 0.20f);   // 后门暗木
    public static readonly Color StepColor = new(0.60f, 0.56f, 0.50f);       // 台基石阶
    public static readonly Color WallColor = new(0.82f, 0.76f, 0.62f);       // 院墙夯土·偏浅土黄（参考宋院图：墙比房淡、比地亮）
    public static readonly Color WallTopColor = new(0.20f, 0.16f, 0.13f);   // 院墙压顶深色瓦条——参考图最显眼的"深色顶冠线"
    public static readonly Color WindowGlass = new(0.35f, 0.45f, 0.55f);     // 窗（半透灰玻）
    public const float BodyAlpha = 0.92f;     // 民居半透（让玩家透视看屋内居民）
    public const float SolidBodyAlpha = 1.0f;  // 官署/王府/宫殿/宅邸——完全不透，避半透色偏
    public const float WindowAlpha = 0.55f;    // 窗半透灰玻
    public const float WallOpacity = 1.0f;

    private static readonly Color EaveColor = new(0.23f, 0.18f, 0.13f);  // 深木檐口
    private static readonly Color RidgeColor = new(0.20f, 0.15f, 0.11f); // 暗脊
    private static readonly Color LanternColor = new(0.80f, 0.18f, 0.16f); // 朱红灯
    private static readonly Color BannerColor = new(0.78f, 0.30f, 0.26f); // 米红招幌
    private static readonly Color FieldColor = new(0.56f, 0.68f, 0.30f); // 田绿

    /// <summary>一座建筑的所有角色变换+颜色（元组列表）。</summary>
    public sealed class BuildRoleLists
    {
        public readonly Role Found = new();
        public readonly Role Step = new();
        public readonly Role Body = new();
        public readonly Role Roof = new();
        public readonly Role RoofEnd = new();
        public readonly Role Eave = new();
        public readonly Role Ridge = new();
        public readonly Role Pillar = new();
        public readonly Role Banner = new();
        public readonly Role Lantern = new();
        public readonly Role Window = new();
        public readonly Role Wall = new();

        public sealed class Role
        {
            public readonly List<Transform3D> X = new();
            public readonly List<Color> C = new();
        }

        public void Clear()
        {
            Found.X.Clear(); Found.C.Clear();
            Step.X.Clear(); Step.C.Clear();
            Body.X.Clear(); Body.C.Clear();
            Roof.X.Clear(); Roof.C.Clear();
            RoofEnd.X.Clear(); RoofEnd.C.Clear();
            Eave.X.Clear(); Eave.C.Clear();
            Ridge.X.Clear(); Ridge.C.Clear();
            Pillar.X.Clear(); Pillar.C.Clear();
            Banner.X.Clear(); Banner.C.Clear();
            Lantern.X.Clear(); Lantern.C.Clear();
            Window.X.Clear(); Window.C.Clear();
            Wall.X.Clear(); Wall.C.Clear();
        }
    }

    // ---- 公开入口 ----

    /// <summary>把已放置建筑 b 的各角色追加进 ctx（不持有网格，网格在 GridRenderer）。</summary>
    public static void AppendAssembly(GameState gs, BuildingInstance b, BuildRoleLists ctx)
    {
        float cs = MapGrid.CellSize;
        var center = MapGrid.CellToWorld(b.Origin);
        float groundY = gs.Map.GroundY(b.Origin);
        float baseY = groundY + WorldConfig.BuildingBaseLift;
        float w = b.FootX * cs * 0.9f;
        float d = b.FootY * cs * 0.9f;
        float height = b.Def.Height * (1f + 0.35f * (b.Level - 1));
        center += new Vector3((b.FootX - 1) * cs / 2f, baseY + height / 2f, (b.FootY - 1) * cs / 2f);
        Compose(b.Def, b.FootX, b.FootY, b.Level, b.Condition, w, d, height, baseY, center, ctx, gs, b);
    }

    /// <summary>放置预览：用同源造型逻辑产出单栋预览装配（局部坐标，原点贴地）。保证与正式渲染不漂移。</summary>
    public static BuildRoleLists MakePreview(BuildingDef def, float groundY, int level = 1)
    {
        float cs = MapGrid.CellSize;
        float w = def.SizeX * cs * 0.9f;
        float d = def.SizeY * cs * 0.9f;
        float height = def.Height * (1f + 0.35f * (level - 1));
        float baseY = groundY + WorldConfig.BuildingBaseLift;
        var center = new Vector3((def.SizeX - 1) * cs / 2f, baseY + height / 2f, (def.SizeY - 1) * cs / 2f);
        var ctx = new BuildRoleLists();
        Compose(def, def.SizeX, def.SizeY, level, 100f, w, d, height, baseY, center, ctx, gs: null, b: null);
        return ctx;
    }

    // 是否为「民居类」（grown 宅舍）：带围墙院子规则仅对这类生效
    private static bool IsHouseLike(BuildingDef def) => def.Id is "house" or "mansion";

    // ---- 核心造型 ----

    private static void Compose(BuildingDef def, int footX, int footY, int level, float condition,
        float w, float d, float height, float baseY, Vector3 center, BuildRoleLists ctx,
        GameState gs, BuildingInstance b)
    {
        var col = def.GodotColor;
        if (condition < 50f)
            col = col.Darkened(0.35f * (1f - condition / 50f));
        float roofH = Mathf.Clamp(height * 0.35f, 0.5f, 2.2f);

        // 地基：不透明基座从房体底面向下延伸
        ctx.Found.X.Add(new Transform3D(Basis.FromScale(new Vector3(w, WorldConfig.FoundationDepth, d)),
            new Vector3(center.X, baseY - WorldConfig.FoundationDepth / 2f, center.Z)));
        ctx.Found.C.Add(FoundationColor);

        // 农田（NoRoof + field）：只画地块板 + 按等级加谷廪
        if (def.NoRoof && def.Category == "field")
        {
            ctx.Body.X.Add(new Transform3D(Basis.FromScale(new Vector3(w, Mathf.Max(0.05f, height), d)), center));
            ctx.Body.C.Add(WithAlpha(FieldColor, 0.92f));
            int silos = Mathf.Clamp(level, 0, 4);
            float sx = w * 0.22f, sz = d * 0.22f;
            float off = 0.28f;
            for (int i = 0; i < silos; i++)
            {
                float ox = (i % 2 == 0 ? -1f : 1f) * off;
                float oz = (i < 2 ? -1f : 1f) * off;
                float sh = 0.5f;
                ctx.Body.X.Add(new Transform3D(Basis.FromScale(new Vector3(sx, sh, sz)),
                    new Vector3(center.X + ox, baseY + sh / 2f, center.Z + oz)));
                ctx.Body.C.Add(WithAlpha(new Color(0.62f, 0.50f, 0.28f), 1f));
            }
            return;
        }

        // 主体：威严类（王阳府/宫殿/官署/宅邸）完全不透——避免半透颜色叠 sky 偏成糖果色；
        // 民居小房保持 BodyAlpha 半透，可透视看到屋内居民。
        bool solid = def.Id is "prince_mansion" or "palace" or "mansion"
            || def.Category is "official" or "court";
        float bodyA = solid ? SolidBodyAlpha : BodyAlpha;

        // 民居类（house/mansion）若占地大于基准 2×2，则房体居中、外围加围墙成院落；
        // 其余（含 2×2 民居、所有官营）房体即占地本体。
        bool courtyard = IsHouseLike(def) && (footX > 2 || footY > 2);

        // 路向感知：找最近路格 → 朝路方向 → 据路种类决定「房靠路」还是「院靠路」。
        //   主/辅路（Main/Side）→ 房体紧贴路侧（房-门-路），院落在房反方向（用户：主/辅路规则）；
        //   小路（Lane）       → 院墙紧贴路侧（门-院-路），房体缩在院子深处（用户：小路规则）。
        //   无邻路（空地）      → 默认院落在 +Z（背向默认正面 -Z），房体靠 -Z。
        // 落点结果存在 `roadDir ∈ 方向 unit vec + 路种类 + 是否有邻路`。
        FindAdjacentRoadInfo(gs, b, out Vector2 roadDir, out RoadKind roadKind, out bool hasRoad);
        bool faceRoadSide = hasRoad && roadKind is RoadKind.Main or RoadKind.Side;
        // faceRoadSide = true  → 房体靠路侧（前门开向路）：
        //   房体起点 = 占地路方向 1/3；院落在反方向 2/3；围墙只有「背侧 + 两侧」三面，前（路侧）围墙不建。
        // faceRoadSide = false → 院子靠路侧（前门开向路，进入院子→再到房）：
        //   房体占反方向 60% 居中；院墙四面完整，前墙留门朝路。

        float bodyW = w, bodyD = d, bodyH = height;
        float wallFrontGap = 0f; // 房体在占地路方向留前院深度（房侧 vs 路侧偏移）
        if (courtyard)
        {
            if (faceRoadSide)
            {
                // 房子靠路：房体占沿路方向 60% 紧贴路（前墙贴路=院开门在路），剩余 40% 在背侧院子。
                // 借用 bodyW/bodyD 的较长边为「沿路方向」，算前院深度 frontYard。
                float longSide = Mathf.Max(w, d);
                bodyW = longSide * 0.6f;
                bodyD = longSide * 0.6f;
                wallFrontGap = longSide - bodyW; // 房体让出 ~40% 给前（背）院子
            }
            else
            {
                // 院子靠路（默认）：房体居中缩到 60%，四面院墙完整
                bodyW = Mathf.Max(w * 0.6f, MapGrid.CellSize * 1.8f);
                bodyD = Mathf.Max(d * 0.6f, MapGrid.CellSize * 1.8f);
            }
        }

        float bodyCY = center.Y; // 房体中心 y 不变（高度 top 改以房体算）
        float bodyTop = baseY + bodyH;

        // 房体在占地内的中心位置：faceRoadSide 时把房体贴着路侧偏移，反之居中。
        // 路方位用 (roadDir.X, roadDir.Y) 朝向「路位于...」：我们让房体相对占地中心向「路那侧」偏移。
        float bodyCx = center.X, bodyCz = center.Z;
        if (courtyard && faceRoadSide)
        {
            float offX = (w - bodyW) / 2f * roadDir.X; // 路在 ±X 方向时贴 X
            float offY = (d - bodyD) / 2f * roadDir.Y; // 路在 ±Y（Z）时贴 Z
            bodyCx = center.X + offX;
            bodyCz = center.Z + offY;
        }

        ctx.Body.X.Add(new Transform3D(Basis.FromScale(new Vector3(bodyW, bodyH, bodyD)),
            new Vector3(bodyCx, bodyCY, bodyCz)));
        ctx.Body.C.Add(WithAlpha(col, bodyA));

        // 台基：房体正下方的石阶基座（比房体略大一圈，从 baseY 向下延伸 FoundationDepth，
        // 顶露 0.12m 压阑石感），让建筑脱离「贴地薄板」读起来有台基。
        float stepRing = 0.22f;
        AddBox(ctx.Step, new Vector3(bodyCx, baseY - WorldConfig.FoundationDepth / 2f + 0.12f, bodyCz),
            new Vector3(bodyW + stepRing * 2f, WorldConfig.FoundationDepth + 0.12f, bodyD + stepRing * 2f), StepColor);

        // 门窗：前门朝路方向（前 = -roadDir 单位向量反方向 = 路侧），窗两侧对称。
        // 规则：faceRoadSide 房子靠路 → 房体已贴路，房门朝路；
        //       faceRoadSide=false 院子靠路 → 房体居中，房门仍朝路（因为路在房前）。
        // doorFace = -roadDir（统一约定朝向） → ComputeFrontFaceAt 把方向量化到具体局部轴。
        Vector2 doorFace = -roadDir; // 朝路方向
        AddDoorAndWindows(ctx, solid, bodyW, bodyD, baseY, bodyH, new Vector3(bodyCx, center.Y, bodyCz), doorFace);

        if (courtyard)
        {
            // 院墙：路侧前墙根据规则不同处理（详见 AddCourtyardWalls 内部分支）
            AddCourtyardWalls(ctx, w, d, bodyW, bodyD, bodyCx, bodyCz,
                baseY, new Vector3(center.X, center.Y, center.Z), roadDir, faceRoadSide);
        }

        if (!def.NoRoof)
        {
            // 主坡屋顶：三棱柱，脊沿长边；外扩 1.18 作出檐（基于房体尺寸，院落模式房体已缩小）
            var roofBasis = bodyW >= bodyD
                ? Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f)) * Basis.FromScale(new Vector3(bodyD * 1.18f, roofH, bodyW * 1.18f))
                : Basis.FromScale(new Vector3(bodyW * 1.18f, roofH, bodyD * 1.18f));
            ctx.Roof.X.Add(new Transform3D(roofBasis, new Vector3(bodyCx, bodyTop + roofH / 2f, bodyCz)));
            ctx.Roof.C.Add(col.Darkened(0.45f));

            // 檐口：薄 Box 环四面，置于房体顶
            float eaveD = bodyD * 1.18f, eaveW = bodyW * 1.18f, eaveT = 0.12f;
            AddBox(ctx.Eave, new Vector3(bodyCx, bodyTop + 0.02f, bodyCz - eaveD / 2f), new Vector3(eaveW, eaveT, 0.2f), EaveColor);
            AddBox(ctx.Eave, new Vector3(bodyCx, bodyTop + 0.02f, bodyCz + eaveD / 2f), new Vector3(eaveW, eaveT, 0.2f), EaveColor);
            AddBox(ctx.Eave, new Vector3(bodyCx - eaveW / 2f, bodyTop + 0.02f, bodyCz), new Vector3(0.2f, eaveT, eaveD), EaveColor);
            AddBox(ctx.Eave, new Vector3(bodyCx + eaveW / 2f, bodyTop + 0.02f, bodyCz), new Vector3(0.2f, eaveT, eaveD), EaveColor);

            // 屋脊：细 Box 沿脊轴（长边）置于顶
            float ridgLen = (bodyW >= bodyD ? bodyD : bodyW) * 1.16f;
            var ridgScale = bodyW >= bodyD ? new Vector3(0.14f, 0.14f, ridgLen) : new Vector3(ridgLen, 0.14f, 0.14f);
            AddBox(ctx.Ridge, new Vector3(bodyCx, bodyTop + roofH - 0.02f, bodyCz), ridgScale, RidgeColor);

            // 威仪/大占地：加垂直端坡作庑殿/歇山感（与主坡叠加，取暗一档）
            bool grand = def.Id is "palace" or "prince_mansion"
                || (def.Category == "official" && (footX >= 4 || footY >= 4));
            if (grand)
            {
                var endBasis = bodyW >= bodyD
                    ? Basis.FromScale(new Vector3(bodyW * 1.16f, roofH * 0.96f, bodyD * 1.16f))
                    : Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f)) * Basis.FromScale(new Vector3(bodyD * 1.16f, roofH * 0.96f, bodyW * 1.16f));
                ctx.RoofEnd.X.Add(new Transform3D(endBasis, new Vector3(bodyCx, bodyTop + roofH * 0.96f / 2f, bodyCz)));
                ctx.RoofEnd.C.Add(col.Darkened(0.5f));
            }
        }

        // 立柱：四角 + 大占地边中 + 殿宇前廊（基于房体中心 bodyCx/bodyCz）
        AddPillars(ctx, def, footX, footY, bodyW, bodyD, baseY, bodyTop, bodyCx, bodyCz);

        // 招幌：仅商铺，前脸挂竖条，朝向路的反方向（即房体朝着路的那个墙面前挂）
        if (def.Id == "shop")
        {
            BannerPosition(ctx, bodyW, bodyD, bodyTop, bodyCx, bodyCz, doorFace);
        }

        // 灯笼：殿宇 4 / 官署商铺宅邸 2 / 民居 1 / 其余 0，悬于檐下四角
        int lanterns = def.Id is "palace" or "prince_mansion" ? 4
            : (def.Category == "official" || def.Id is "shop" or "mansion") ? 2
            : def.Id == "house" ? 1 : 0;
        AddLanterns(ctx, bodyW, bodyD, bodyTop, lanterns, bodyCx, bodyCz);
    }

    /// <summary>招幌位置：商铺的招牌垂于房体朝路一侧墙面外缘（上 0.7m 高，0.16m 宽窄条）。</summary>
    private static void BannerPosition(BuildRoleLists ctx, float bodyW, float bodyD, float bodyTop,
        float bodyCx, float bodyCz, Vector2 doorFace)
    {
        // doorFace 单位向量（指向路）。招幌的 z 偏移 = doorFace 方向，-doorFace.Y × bodyD/2
        // 假定 doorFace ∈ {(±1,0),(0,±1)} —— FindAdjacentRoadInfo 给出离散四向。
        float hangZ = bodyCz - doorFace.Y * bodyD / 2f;
        float hangX = bodyCx + doorFace.X * bodyW / 2f;
        AddBox(ctx.Banner, new Vector3(hangX, bodyTop - 0.35f, hangZ),
            new Vector3(0.16f, 0.7f, 0.04f), BannerColor);
    }

    /// <summary>路向感知：找相邻最近路格，返回路在房体外的方向单位向量 + 路种类。
    /// 路方向约定为「路格相对房体中心的方位」：如路在房体 +X 侧 → (1,0)；在 -Z 侧 → (0,-1)。</summary>
    private static void FindAdjacentRoadInfo(GameState gs, BuildingInstance b,
        out Vector2 roadDir, out RoadKind kind, out bool hasRoad)
    {
        roadDir = new Vector2(0f, -1f);   // 默认朝向 -Z（主路通常在玩家可视方向）
        kind = RoadKind.None;
        hasRoad = false;
        if (gs.Map is null) return;
        var adj = gs.Map.FindAdjacentRoad(b.Origin, b.FootX, b.FootY);
        if (!adj.HasValue) return;
        hasRoad = true;
        // 取路格所在方向：从占地中心 c → 路格 r，归一化到 4 个基本方向
        var c = new Vector2I(b.Origin.X + b.FootX / 2, b.Origin.Y + b.FootY / 2);
        var r = adj.Value;
        int dx = Mathf.Sign(r.X - c.X), dy = Mathf.Sign(r.Y - c.Y);
        roadDir = new Vector2(dx, dy);
        kind = gs.Map.CellAt(r).RoadKind;
    }

    private static void AddPillars(BuildRoleLists ctx, BuildingDef def, int footX, int footY,
        float w, float d, float baseY, float top, float bodyCx, float bodyCz)
    {
        float ph = top - baseY;
        float inset = 0.18f;
        float px = w / 2f - inset, pz = d / 2f - inset;
        var pts = new List<(float, float)>
        {
            (-px, -pz), (px, -pz), (px, pz), (-px, pz),
        };
        if (footX >= 4) { pts.Add((0f, -pz)); pts.Add((0f, pz)); }
        if (footY >= 4) { pts.Add((-px, 0f)); pts.Add((px, 0f)); }
        if (def.Id is "palace" or "prince_mansion")
        {
            // 前廊两根外凸
            pts.Add((-px * 0.5f, -pz - 0.2f));
            pts.Add((px * 0.5f, -pz - 0.2f));
        }
        var pcol = PillarColor(def);
        foreach (var (ox, oz) in pts)
        {
            ctx.Pillar.X.Add(new Transform3D(Basis.FromScale(new Vector3(1f, ph, 1f)),
                new Vector3(bodyCx + ox, baseY + ph / 2f, bodyCz + oz)));
            ctx.Pillar.C.Add(pcol);
        }
    }

    private static void AddLanterns(BuildRoleLists ctx, float w, float d, float top, int n, float bodyCx, float bodyCz)
    {
        if (n <= 0) return;
        float ly = top - 0.18f;
        float lx = w / 2f * 0.7f, lz = d / 2f * 0.7f;
        void Add(float ox, float oz) => AddBox(ctx.Lantern, new Vector3(bodyCx + ox, ly, bodyCz + oz),
            new Vector3(0.14f, 0.18f, 0.14f), LanternColor);
        if (n >= 4) { Add(-lx, -lz); Add(lx, -lz); Add(lx, lz); Add(-lx, lz); }
        else if (n == 2) { Add(-lx, -lz); Add(lx, lz); }
        else Add(0f, -lz);
    }

    private static Color PillarColor(BuildingDef def) => def.Id is "palace" or "prince_mansion"
        ? new Color(0.61f, 0.18f, 0.18f)                                  // 朱红
        : def.Category is "court" or "official"
            ? new Color(0.35f, 0.27f, 0.20f)                             // 深木
            : new Color(0.42f, 0.31f, 0.16f);                           // 土褐

    /// <summary>门+窗：按 doorFace 单位向量定位门面。doorFace 四向 (±X,0) 或 (0,±Y)，
    /// 房门贴朝路墙面外缘，朝外 0.02m 防 z-fight；窗户对称贴两侧。
    /// doorFace.X → world X；doorFace.Y → world Z。</summary>
    private static void AddDoorAndWindows(BuildRoleLists ctx, bool solid, float bodyW, float bodyD,
        float baseY, float bodyH, Vector3 center, Vector2 doorFace)
    {
        float fx = doorFace.X, fz = doorFace.Y;
        float dw = Mathf.Min(bodyW * 0.42f, 1.1f);
        float dh = Mathf.Min(bodyH * 0.78f, 0.92f);

        // 门贴朝路墙面外缘 0.02m
        Vector3 doorPos;
        if (Mathf.Abs(fx) > 0.5f)
            doorPos = new Vector3(center.X + fx * (bodyW / 2f + 0.02f), baseY + dh / 2f, center.Z);
        else
            doorPos = new Vector3(center.X, baseY + dh / 2f, center.Z + fz * (bodyD / 2f + 0.02f));
        AddBox(ctx.Window, doorPos, new Vector3(dw, dh, 0.06f), MainDoorColor);

        float shortSide = Mathf.Min(bodyW, bodyD);
        if (shortSide >= 1.6f)
        {
            // 墙面内切线（左右偏移方向）
            float tx = fz, tz = -fx;
            float side = shortSide / 2f - 0.28f;
            float wy = baseY + bodyH * 0.55f;
            float wh = Mathf.Min(bodyH * 0.32f, 0.5f);
            float ww = Mathf.Min((shortSide - dw) / 2f - 0.12f, 0.5f);
            AddBox(ctx.Window, new Vector3(doorPos.X + tx * side, wy, doorPos.Z + tz * side),
                new Vector3(ww, wh, 0.05f), WithAlpha(WindowGlass, WindowAlpha));
            AddBox(ctx.Window, new Vector3(doorPos.X - tx * side, wy, doorPos.Z - tz * side),
                new Vector3(ww, wh, 0.05f), WithAlpha(WindowGlass, WindowAlpha));
        }
        else
        {
            // 窄房：门上方加一横窗
            float wy = baseY + bodyH * 0.6f;
            float wh = Mathf.Min(bodyH * 0.22f, 0.34f);
            Vector3 winPos = (Mathf.Abs(fx) > 0.5f)
                ? new Vector3(center.X + fx * (bodyW / 2f + 0.02f), wy, center.Z)
                : new Vector3(center.X, wy, center.Z + fz * (bodyD / 2f + 0.02f));
            AddBox(ctx.Window, winPos, new Vector3(Mathf.Min(shortSide * 0.6f, 0.9f), wh, 0.05f),
                WithAlpha(WindowGlass, WindowAlpha));
        }
    }

    /// <summary>院落围墙：占地 footprint 外缘一圈夯土墙。
    /// faceRoadSide=true（房子靠路）→ 朝路前墙不建，只余 3 面 + 后墙后门（暗木 1.0m）。
    /// faceRoadSide=false（院子靠路）→ 四面完整，前墙朝路开门（金 1.2m）；后墙后门（暗木 1.0m）。
    /// roadDir 是路在房体外的方向单位向量（房体相对路侧 = -roadDir）。
    /// 后墙基 Y 用 baseY（由 ctx 透传，简化：调用者负责传入）。</summary>
    private static void AddCourtyardWalls(BuildRoleLists ctx, float w, float d,
        float bodyW, float bodyD, float bodyCx, float bodyCz,
        float baseY, Vector3 center, Vector2 roadDir, bool faceRoadSide)
    {
        // 院墙：参考宋院图比例（标注 totalH/wallH ≈ 2.2）。本场景房体 ~2m，墙高取 1.1m 占 55%；
        // 墙厚 0.12m（参考图视觉是「细线」，之前 0.3m 太粗像砖墩）。墙顶加一条 0.06m 高 * 0.18m 宽的
        // 深色压顶瓦条——参考图最显眼的轮廓就是这条深色边线，没有它墙就不像宋院。
        float wallH = 1.1f;
        float t = 0.12f;       // 墙厚（参考图 ~0.12m）
        float capH = 0.06f;    // 压顶瓦条高
        float capW = 0.18f;    // 压顶瓦条宽
        float halfW = w / 2f, halfD = d / 2f;
        float yc = baseY + wallH / 2f;
        float capY = baseY + wallH + capH / 2f;

        // 两侧山墙（沿 X 厚度 t，长度 d+capW 让压顶贯穿不漏色）
        AddBox(ctx.Wall, new Vector3(center.X - halfW, yc, center.Z), new Vector3(t, wallH, d + capW), WallColor);
        AddBox(ctx.Wall, new Vector3(center.X + halfW, yc, center.Z), new Vector3(t, wallH, d + capW), WallColor);
        // 山墙压顶
        AddBox(ctx.Wall, new Vector3(center.X - halfW, capY, center.Z), new Vector3(capW, capH, d + capW), WallTopColor);
        AddBox(ctx.Wall, new Vector3(center.X + halfW, capY, center.Z), new Vector3(capW, capH, d + capW), WallTopColor);

        bool roadSideIsZ = Mathf.Abs(roadDir.Y) > 0.5f;
        float sideSign = roadSideIsZ ? Mathf.Sign(roadDir.Y) : Mathf.Sign(roadDir.X);

        if (faceRoadSide)
        {
            // 房子靠路：前墙不建；后墙 = 反符号侧，留后门
            if (roadSideIsZ)
            {
                float zBack = center.Z - sideSign * halfD;
                AddWallSegmentWithDoor(ctx, center.X, yc, capY, zBack, sideSign, true, w, 1.0f,
                    BackDoorColor, 0.85f, baseY, capW, capH, t);
            }
            else
            {
                float xBack = center.X - sideSign * halfW;
                AddWallSegmentWithDoor(ctx, xBack, yc, capY, center.Z, sideSign, false, d, 1.0f,
                    BackDoorColor, 0.85f, baseY, capW, capH, t);
            }
        }
        else
        {
            // 院子靠路：四面，前朝路开门（金），后朝院子开门（暗木）
            if (roadSideIsZ)
            {
                float zFront = center.Z + sideSign * halfD;
                AddWallSegmentWithDoor(ctx, center.X, yc, capY, zFront, sideSign, true, w, 1.2f,
                    MainDoorColor, 0.9f, baseY, capW, capH, t);
                float zBack = center.Z - sideSign * halfD;
                AddWallSegmentWithDoor(ctx, center.X, yc, capY, zBack, sideSign, true, w, 1.0f,
                    BackDoorColor, 0.85f, baseY, capW, capH, t);
            }
            else
            {
                float xFront = center.X + sideSign * halfW;
                AddWallSegmentWithDoor(ctx, xFront, yc, capY, center.Z, sideSign, false, d, 1.2f,
                    MainDoorColor, 0.9f, baseY, capW, capH, t);
                float xBack = center.X - sideSign * halfW;
                AddWallSegmentWithDoor(ctx, xBack, yc, capY, center.Z, sideSign, false, d, 1.0f,
                    BackDoorColor, 0.85f, baseY, capW, capH, t);
            }
        }
    }

    /// <summary>围墙段 + 中央门洞 + 门扇 + 墙顶压顶瓦条。在 (cx,yc,cz) 处沿 alongX 轴铺墙 fullLen 长，
    /// 中央 gap 留门。门扇朝外侧（sideSign 方向）突出。压顶瓦条同步铺两段。</summary>
    private static void AddWallSegmentWithDoor(BuildRoleLists ctx, float cx, float yc, float capY, float cz,
        float sideSign, bool alongX, float fullLen, float gap, Color doorCol, float doorH, float baseY,
        float capW, float capH, float t)
    {
        float wallH = (yc - baseY) * 2f;
        float len = fullLen - gap;
        if (len > 0.01f)
        {
            float seg = len / 2f;
            if (alongX)
            {
                // 左右墙段（厚 t，长 seg，高 wallH）
                AddBox(ctx.Wall, new Vector3(cx - gap / 2f - seg / 2f, yc, cz), new Vector3(seg, wallH, t), WallColor);
                AddBox(ctx.Wall, new Vector3(cx + gap / 2f + seg / 2f, yc, cz), new Vector3(seg, wallH, t), WallColor);
                // 左右压顶瓦条
                AddBox(ctx.Wall, new Vector3(cx - gap / 2f - seg / 2f, capY, cz), new Vector3(seg, capH, capW), WallTopColor);
                AddBox(ctx.Wall, new Vector3(cx + gap / 2f + seg / 2f, capY, cz), new Vector3(seg, capH, capW), WallTopColor);
                // 门扇
                AddBox(ctx.Window, new Vector3(cx, baseY + doorH / 2f, cz + sideSign * 0.02f),
                    new Vector3(gap, doorH, 0.08f), doorCol);
            }
            else
            {
                // 沿 Z 轴向（X 端面是墙面）
                AddBox(ctx.Wall, new Vector3(cx, yc, cz - gap / 2f - seg / 2f), new Vector3(t, wallH, seg), WallColor);
                AddBox(ctx.Wall, new Vector3(cx, yc, cz + gap / 2f + seg / 2f), new Vector3(t, wallH, seg), WallColor);
                AddBox(ctx.Wall, new Vector3(cx, capY, cz - gap / 2f - seg / 2f), new Vector3(capW, capH, seg), WallTopColor);
                AddBox(ctx.Wall, new Vector3(cx, capY, cz + gap / 2f + seg / 2f), new Vector3(capW, capH, seg), WallTopColor);
                AddBox(ctx.Window, new Vector3(cx + sideSign * 0.02f, baseY + doorH / 2f, cz),
                    new Vector3(0.08f, doorH, gap), doorCol);
            }
        }
        else
        {
            // 全宽门（退化）：单门扇 + 门楣压顶
            if (alongX)
            {
                AddBox(ctx.Window, new Vector3(cx, baseY + doorH / 2f, cz + sideSign * 0.02f),
                    new Vector3(gap, doorH, 0.08f), doorCol);
                AddBox(ctx.Wall, new Vector3(cx, capY, cz + sideSign * 0.02f), new Vector3(gap + capW * 2f, capH, capW), WallTopColor);
            }
            else
            {
                AddBox(ctx.Window, new Vector3(cx + sideSign * 0.02f, baseY + doorH / 2f, cz),
                    new Vector3(0.08f, doorH, gap), doorCol);
                AddBox(ctx.Wall, new Vector3(cx + sideSign * 0.02f, capY, cz), new Vector3(capW, capH, gap + capW * 2f), WallTopColor);
            }
        }
    }

    private static void AddBox(BuildRoleLists.Role role, Vector3 pos, Vector3 size, Color col)
    {
        role.X.Add(new Transform3D(Basis.FromScale(size), pos));
        role.C.Add(col);
    }

    private static Color WithAlpha(Color c, float a)
    {
        c.A = a;
        return c;
    }
}
