using System;
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

    /// <summary>把已放置建筑 b 的各角色追加进 ctx（不持有网格，网格在 GridRenderer）。</summary>
    public static void AppendAssembly(GameState gs, BuildingInstance b, BuildRoleLists ctx)
    {
        float cs = MapGrid.CellSize;
        var center = MapGrid.CellToWorld(b.Origin);
        float groundY = gs.Map.GroundY(b.Origin);
        float baseY = groundY + WorldConfig.BuildingBaseLift;
        int footX = b.FootX, footY = b.FootY;
        float w = footX * cs * 0.9f;
        float d = footY * cs * 0.9f;
        // 墙高按占地推导（SongHouseConfig），不再用各建筑手调的魔法 Height
        center += new Vector3((footX - 1) * cs / 2f, baseY, (footY - 1) * cs / 2f);
        Compose(b.Def, footX, footY, b.Level, b.Condition, w, d, baseY, center, ctx, gs, b);
    }

    /// <summary>放置预览：用同源造型逻辑产出单栋预览装配（局部坐标，原点贴地）。保证与正式渲染不漂移。</summary>
    public static BuildRoleLists MakePreview(BuildingDef def, float groundY, int level = 1)
    {
        float cs = MapGrid.CellSize;
        float w = def.SizeX * cs * 0.9f;
        float d = def.SizeY * cs * 0.9f;
        float baseY = groundY + WorldConfig.BuildingBaseLift;
        var center = new Vector3((def.SizeX - 1) * cs / 2f, baseY, (def.SizeY - 1) * cs / 2f);
        var ctx = new BuildRoleLists();
        Compose(def, def.SizeX, def.SizeY, level, 100f, w, d, baseY, center, ctx, gs: null, b: null);
        return ctx;
    }

    /// <summary>是否走宋式住宅布局引擎（grown 住宅/商铺/工坊）。官营/朝廷/农田另有专门造型。</summary>
    private static bool UsesSongLayout(BuildingDef def) => def.Category == "grown";
    // ---- 核心造型 ----
    private static void Compose(BuildingDef def, int footX, int footY, int level, float condition,
        float w, float d, float baseY, Vector3 center, BuildRoleLists ctx,
        GameState gs, BuildingInstance b)
    {
        var col = def.GodotColor;
        if (condition < 50f)
            col = col.Darkened(0.35f * (1f - condition / 50f));

        // 农田（NoRoof + field）：只画地块板 + 按等级加谷廪
        if (def.NoRoof && def.Category == "field")
        {
            ComposeField(footX, footY, level, w, d, baseY, center, ctx);
            return;
        }

        // 公共设施（官营/朝廷）：各有专门形制，不走住宅三布局
        if (!UsesSongLayout(def))
        {
            ComposePublic(def, footX, footY, level, w, d, baseY, center, ctx, col, gs, b);
            return;
        }

        // —— 住宅/商铺/工坊：走宋式布局引擎 ——
        ctx.Found.X.Add(new Transform3D(Basis.FromScale(new Vector3(w, WorldConfig.FoundationDepth, d)),
            new Vector3(center.X, baseY - WorldConfig.FoundationDepth / 2f, center.Z)));
        ctx.Found.C.Add(FoundationColor);

        FindAdjacentRoadInfo(gs, b, out Vector2 roadDirV, out RoadKind roadKind, out bool hasRoad);
        var roadDir = new Vector2I((int)roadDirV.X, (int)roadDirV.Y);
        var roadClass = hasRoad ? SongHouseLayout.ClassOf(roadKind) : SongHouseLayout.RoadClass.None;
        var plan = SongHouseLayout.Build(def, footX, footY, level, roadClass, roadDir);

        bool solid = def.Category is "official" or "court" || def.Id is "mansion" or "prince_mansion";
        float bodyA = solid ? SolidBodyAlpha : BodyAlpha;
        var form = SongHouseConfig.RoofFormOf(def, level);
        var face = new Vector2(-roadDir.X, -roadDir.Y); // 朝路方向（doorFace 约定）

        foreach (var u in plan.Houses)
            EmitHouseUnit(ctx, def, footX, footY, level, u, baseY, center, col, bodyA, form, plan.OnArtery, face);

        // 院落围墙 + 院门：平面计划给出院落矩形，围墙沿其外缘（临路侧留门）
        if (plan.Yard.HasValue)
            EmitCourtyard(ctx, plan.Yard.Value, footX, footY, baseY, center, roadDir);

        // 铺面招幌：临街敞开的那座挂竖条
        if (plan.OnArtery)
            foreach (var u in plan.Houses)
                if (u.FacesStreet)
                    EmitBanner(ctx, u, footX, footY, baseY, center, roadDir);

        // 灯笼：殿宇 4 / 官署商铺宅邸 2 / 民居 1，悬于主体屋檐下
        int lanterns = def.Id is "palace" or "prince_mansion" ? 4
            : (def.Category == "official" || def.Id is "shop" or "mansion") ? 2
            : def.Id == "house" ? 1 : 0;
        foreach (var u in plan.Houses)
        {
            if (u.Role is SongHouseLayout.HouseRole.MainHouse or SongHouseLayout.HouseRole.ShopFront)
            {
                EmitLanterns(ctx, u, footX, footY, baseY, center, lanterns);
                break;
            }
        }
    }

    /// <summary>把平面计划里的一个房屋单元铺成视觉角色：台基/房体/屋顶/檐口/屋脊/立柱/门窗。</summary>
    private static void EmitHouseUnit(BuildRoleLists ctx, BuildingDef def, int footX, int footY, int level,
        SongHouseLayout.HouseUnit u, float baseY, Vector3 center, Color col, float bodyA,
        SongHouseConfig.RoofForm form, bool onArtery, Vector2 face)
    {
        float cs = MapGrid.CellSize;
        float uw = u.W * cs * 0.9f;
        float ud = u.D * cs * 0.9f;
        float ux = center.X + (u.Cell.X + u.W / 2f) * cs - footX * cs / 2f;
        float uz = center.Z + (u.Cell.Y + u.D / 2f) * cs - footY * cs / 2f;

        // 墙高：整宅基准墙高 × 单元角色比例（正房最高、厢房/倒座/挟屋递减）
        float wallH = SongHouseConfig.WallHeightOf(footX, footY, level) * u.WallRatio;
        float roofH = SongHouseConfig.RoofHeightOf(wallH);
        float top = baseY + wallH;

        // 影壁：不是房屋，是一道立壁，单独 emit 后返回
        if (u.Role == SongHouseLayout.HouseRole.ScreenWall)
        {
            float sw = MathF.Min(uw, SongHouseConfig.CourtyardWallThick);
            float sd = MathF.Max(ud, SongHouseConfig.CourtyardWallThick);
            AddBox(ctx.Wall, new Vector3(ux, baseY + SongHouseConfig.ScreenWallH / 2f, uz),
                new Vector3(sw, SongHouseConfig.ScreenWallH, sd), WallTopColor);
            return;
        }

        // 台基：单元正下方石阶基座
        AddBox(ctx.Step, new Vector3(ux, baseY - WorldConfig.FoundationDepth / 2f + SongHouseConfig.PlinthReveal, uz),
            new Vector3(uw + SongHouseConfig.PlinthRing * 2f, WorldConfig.FoundationDepth + SongHouseConfig.PlinthReveal,
                ud + SongHouseConfig.PlinthRing * 2f), StepColor);

        // 房体：铺面/门楼临街面不砌墙（只立柱），其余砌半透墙
        bool openFront = u.FacesStreet && onArtery
            && u.Role is SongHouseLayout.HouseRole.ShopFront or SongHouseLayout.HouseRole.GateTower;
        if (!openFront)
        {
            ctx.Body.X.Add(new Transform3D(Basis.FromScale(new Vector3(uw, wallH, ud)),
                new Vector3(ux, baseY + wallH / 2f, uz)));
            ctx.Body.C.Add(WithAlpha(col, bodyA));
        }

        // 屋顶：按《营造法式》制式取悬山（三棱柱）/ 庑殿·歇山（加端坡）/ 攒尖（亭榭）
        float over = SongHouseConfig.EaveOverhangOf(form);
        if (form == SongHouseConfig.RoofForm.Pyramid)
        {
            var pyBasis = Basis.FromEuler(new Vector3(0f, Mathf.Pi / 4f, 0f))
                * Basis.FromScale(new Vector3(ud * over, roofH, uw * over));
            ctx.Roof.X.Add(new Transform3D(pyBasis, new Vector3(ux, top + roofH / 2f, uz)));
            ctx.Roof.C.Add(col.Darkened(0.45f));
        }
        else
        {
            var roofBasis = uw >= ud
                ? Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f)) * Basis.FromScale(new Vector3(ud * over, roofH, uw * over))
                : Basis.FromScale(new Vector3(uw * over, roofH, ud * over));
            ctx.Roof.X.Add(new Transform3D(roofBasis, new Vector3(ux, top + roofH / 2f, uz)));
            ctx.Roof.C.Add(col.Darkened(0.45f));

            // 庑殿/歇山：加垂直端坡作四坡顶感（与主坡叠加，取暗一档）
            if (form is SongHouseConfig.RoofForm.Hip or SongHouseConfig.RoofForm.GableHip)
            {
                var endBasis = uw >= ud
                    ? Basis.FromScale(new Vector3(uw * over * 0.96f, roofH * 0.96f, ud * over * 0.96f))
                    : Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f))
                        * Basis.FromScale(new Vector3(ud * over * 0.96f, roofH * 0.96f, uw * over * 0.96f));
                ctx.RoofEnd.X.Add(new Transform3D(endBasis, new Vector3(ux, top + roofH * 0.96f / 2f, uz)));
                ctx.RoofEnd.C.Add(col.Darkened(0.5f));
            }
        }

        // 檐口：薄 Box 环四面，置于房体顶
        float eaveD = ud * over, eaveW = uw * over, eaveT = SongHouseConfig.EaveThickness;
        AddBox(ctx.Eave, new Vector3(ux, top + 0.02f, uz - eaveD / 2f), new Vector3(eaveW, eaveT, 0.2f), EaveColor);
        AddBox(ctx.Eave, new Vector3(ux, top + 0.02f, uz + eaveD / 2f), new Vector3(eaveW, eaveT, 0.2f), EaveColor);
        AddBox(ctx.Eave, new Vector3(ux - eaveW / 2f, top + 0.02f, uz), new Vector3(0.2f, eaveT, eaveD), EaveColor);
        AddBox(ctx.Eave, new Vector3(ux + eaveW / 2f, top + 0.02f, uz), new Vector3(0.2f, eaveT, eaveD), EaveColor);

        // 屋脊：细 Box 沿脊轴（长边）置于顶
        float ridgLen = (uw >= ud ? ud : uw) * 1.16f;
        var ridgScale = uw >= ud ? new Vector3(SongHouseConfig.RidgeThickness, SongHouseConfig.RidgeThickness, ridgLen)
            : new Vector3(ridgLen, SongHouseConfig.RidgeThickness, SongHouseConfig.RidgeThickness);
        AddBox(ctx.Ridge, new Vector3(ux, top + roofH - 0.02f, uz), ridgScale, RidgeColor);

        // 立柱：四角 + 大单元边中（临街敞开时只留柱不砌墙）
        AddPillars(ctx, def, uw, ud, baseY, top, ux, uz);

        // 门窗：临街敞开的面只显门框，其余按朝路方向开门窗
        AddDoorAndWindows(ctx, openFront, uw, ud, baseY, wallH, new Vector3(ux, baseY, uz), face);
    }

    /// <summary>院落：沿院落矩形外缘夯土墙 + 压顶瓦条，临路侧留大门。</summary>
    private static void EmitCourtyard(BuildRoleLists ctx, Rect2I yard, int footX, int footY,
        float baseY, Vector3 center, Vector2I roadDir)
    {
        float cs = MapGrid.CellSize;
        float t = SongHouseConfig.CourtyardWallThick;
        float wallH = SongHouseConfig.WallHeightCourtyard;
        float capH = SongHouseConfig.CourtyardCapH, capW = SongHouseConfig.CourtyardCapW;
        float hx = yard.Size.X * cs, hz = yard.Size.Y * cs;
        float cx = center.X + (yard.Position.X + yard.Size.X / 2f) * cs - footX * cs / 2f;
        float cz = center.Z + (yard.Position.Y + yard.Size.Y / 2f) * cs - footY * cs / 2f;
        float halfW = hx / 2f, halfD = hz / 2f;
        float yc = baseY + wallH / 2f;
        float capY = baseY + wallH + capH / 2f;

        // 两侧山墙（沿 X 厚 t，长度贯通）+ 压顶瓦条
        AddBox(ctx.Wall, new Vector3(cx - halfW, yc, cz), new Vector3(t, wallH, hz + capW), WallColor);
        AddBox(ctx.Wall, new Vector3(cx + halfW, yc, cz), new Vector3(t, wallH, hz + capW), WallColor);
        AddBox(ctx.Wall, new Vector3(cx - halfW, capY, cz), new Vector3(capW, capH, hz + capW), WallTopColor);
        AddBox(ctx.Wall, new Vector3(cx + halfW, capY, cz), new Vector3(capW, capH, hz + capW), WallTopColor);

        // 临路侧那面墙留大门（门扇 + 门楣压顶）
        bool roadOnZ = Math.Abs(roadDir.Y) > 0;
        int roadSign = roadOnZ ? Math.Sign(roadDir.Y) : Math.Sign(roadDir.X);
        float gap = Mathf.Min(Mathf.Min(hx, hz) * 0.28f, SongHouseConfig.MainDoorH * 1.6f);
        if (roadOnZ)
            AddWallSegmentWithDoor(ctx, cx, yc, capY, cz + roadSign * halfD, roadSign, true, hx, gap,
                MainDoorColor, SongHouseConfig.MainDoorH, baseY, capW, capH, t);
        else
            AddWallSegmentWithDoor(ctx, cx + roadSign * halfW, yc, capY, cz, roadSign, false, hz, gap,
                MainDoorColor, SongHouseConfig.MainDoorH, baseY, capW, capH, t);
    }

    /// <summary>铺面招幌：竖条挂在临街那一立面外缘。</summary>
    private static void EmitBanner(BuildRoleLists ctx, SongHouseLayout.HouseUnit u,
        int footX, int footY, float baseY, Vector3 center, Vector2I roadDir)
    {
        float cs = MapGrid.CellSize;
        float wallH = SongHouseConfig.WallHeightOf(footX, footY) * u.WallRatio;
        float ux = center.X + (u.Cell.X + u.W / 2f) * cs - footX * cs / 2f;
        float uz = center.Z + (u.Cell.Y + u.D / 2f) * cs - footY * cs / 2f;
        float uw = u.W * cs * 0.9f, ud = u.D * cs * 0.9f;
        float top = baseY + wallH;
        bool roadOnZ = Math.Abs(roadDir.Y) > 0;
        int roadSign = roadOnZ ? Math.Sign(roadDir.Y) : Math.Sign(roadDir.X);
        var pos = roadOnZ
            ? new Vector3(ux, top - 0.35f, uz + roadSign * (ud / 2f + 0.04f))
            : new Vector3(ux + roadSign * (uw / 2f + 0.04f), top - 0.35f, uz);
        AddBox(ctx.Banner, pos, new Vector3(0.16f, 0.7f, 0.04f), BannerColor);
    }

    /// <summary>灯笼：悬于单元檐下四角（按盏数取位）。</summary>
    private static void EmitLanterns(BuildRoleLists ctx, SongHouseLayout.HouseUnit u, int footX, int footY,
        float baseY, Vector3 center, int n)
    {
        if (n <= 0)
            return;
        float cs = MapGrid.CellSize;
        float wallH = SongHouseConfig.WallHeightOf(footX, footY) * u.WallRatio;
        float ux = center.X + (u.Cell.X + u.W / 2f) * cs - footX * cs / 2f;
        float uz = center.Z + (u.Cell.Y + u.D / 2f) * cs - footY * cs / 2f;
        float uw = u.W * cs * 0.9f, ud = u.D * cs * 0.9f;
        float ly = baseY + wallH - 0.18f;
        float lx = uw / 2f * 0.7f, lz = ud / 2f * 0.7f;
        void Add(float ox, float oz) => AddBox(ctx.Lantern, new Vector3(ux + ox, ly, uz + oz),
            new Vector3(0.14f, 0.18f, 0.14f), LanternColor);
        if (n >= 4) { Add(-lx, -lz); Add(lx, -lz); Add(lx, lz); Add(-lx, lz); }
        else if (n == 2) { Add(-lx, -lz); Add(lx, lz); }
        else Add(0f, -lz);
    }

    /// <summary>农田：地块板 + 按等级加谷廪（无屋顶）。</summary>
    private static void ComposeField(int footX, int footY, int level, float w, float d,
        float baseY, Vector3 center, BuildRoleLists ctx)
    {
        float height = SongHouseConfig.WallHeightOf(footX, footY, level);
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
    }

    /// <summary>
    /// 公共设施（官营/朝廷）：按宋代城市公共建筑形制专门造型，不走住宅三布局。
    /// 统一骨架（地基/台基/房体/屋顶/檐口/屋脊/立柱/门窗）+ 按 Id 追加宋式细部。
    /// </summary>
    private static void ComposePublic(BuildingDef def, int footX, int footY, int level,
        float w, float d, float baseY, Vector3 center, BuildRoleLists ctx, Color col,
        GameState gs, BuildingInstance b)
    {
        ctx.Found.X.Add(new Transform3D(Basis.FromScale(new Vector3(w, WorldConfig.FoundationDepth, d)),
            new Vector3(center.X, baseY - WorldConfig.FoundationDepth / 2f, center.Z)));
        ctx.Found.C.Add(FoundationColor);

        float wallH = SongHouseConfig.WallHeightOf(footX, footY, level);
        float roofH = SongHouseConfig.RoofHeightOf(wallH);
        float top = baseY + wallH;
        var form = SongHouseConfig.RoofFormOf(def, level);

        // 水井/流民营不是「一座屋」：跳过通用房体骨架，只出各自细部
        bool bareGround = def.Id is "well" or "refugee_camp";

        if (!bareGround)
        {
            ctx.Body.X.Add(new Transform3D(Basis.FromScale(new Vector3(w, wallH, d)),
                new Vector3(center.X, baseY + wallH / 2f, center.Z)));
            ctx.Body.C.Add(WithAlpha(col, SolidBodyAlpha));
        }

        AddBox(ctx.Step, new Vector3(center.X, baseY - WorldConfig.FoundationDepth / 2f + SongHouseConfig.PlinthReveal, center.Z),
            new Vector3(w + SongHouseConfig.PlinthRing * 2f, WorldConfig.FoundationDepth + SongHouseConfig.PlinthReveal,
                d + SongHouseConfig.PlinthRing * 2f), StepColor);

        if (!bareGround)
        {
            float over = SongHouseConfig.EaveOverhangOf(form);
            var roofBasis = w >= d
                ? Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f)) * Basis.FromScale(new Vector3(d * over, roofH, w * over))
                : Basis.FromScale(new Vector3(w * over, roofH, d * over));
            ctx.Roof.X.Add(new Transform3D(roofBasis, new Vector3(center.X, top + roofH / 2f, center.Z)));
            ctx.Roof.C.Add(col.Darkened(0.45f));
            if (form is SongHouseConfig.RoofForm.Hip or SongHouseConfig.RoofForm.GableHip)
            {
                var endBasis = w >= d
                    ? Basis.FromScale(new Vector3(w * over * 0.96f, roofH * 0.96f, d * over * 0.96f))
                    : Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f))
                        * Basis.FromScale(new Vector3(d * over * 0.96f, roofH * 0.96f, w * over * 0.96f));
                ctx.RoofEnd.X.Add(new Transform3D(endBasis, new Vector3(center.X, top + roofH * 0.96f / 2f, center.Z)));
                ctx.RoofEnd.C.Add(col.Darkened(0.5f));
            }

            float eaveD = d * over, eaveW = w * over, eaveT = SongHouseConfig.EaveThickness;
            AddBox(ctx.Eave, new Vector3(center.X, top + 0.02f, center.Z - eaveD / 2f), new Vector3(eaveW, eaveT, 0.2f), EaveColor);
            AddBox(ctx.Eave, new Vector3(center.X, top + 0.02f, center.Z + eaveD / 2f), new Vector3(eaveW, eaveT, 0.2f), EaveColor);
            AddBox(ctx.Eave, new Vector3(center.X - eaveW / 2f, top + 0.02f, center.Z), new Vector3(0.2f, eaveT, eaveD), EaveColor);
            AddBox(ctx.Eave, new Vector3(center.X + eaveW / 2f, top + 0.02f, center.Z), new Vector3(0.2f, eaveT, eaveD), EaveColor);

            float ridgLen = (w >= d ? d : w) * 1.16f;
            var ridgScale = w >= d ? new Vector3(SongHouseConfig.RidgeThickness, SongHouseConfig.RidgeThickness, ridgLen)
                : new Vector3(ridgLen, SongHouseConfig.RidgeThickness, SongHouseConfig.RidgeThickness);
            AddBox(ctx.Ridge, new Vector3(center.X, top + roofH - 0.02f, center.Z), ridgScale, RidgeColor);

            AddPillars(ctx, def, w, d, baseY, top, center.X, center.Z);
            AddDoorAndWindows(ctx, false, w, d, baseY, wallH, new Vector3(center.X, baseY, center.Z), new Vector2(0f, -1f));
        }

        EmitPublicDetail(ctx, def, footX, footY, w, d, wallH, baseY, center);
    }

    /// <summary>
    /// 公共设施的宋式细部（依据宋代城市公共建筑图文文献）：
    ///   · 水井   → 圆形石砌井栏（外圈立石环 + 中空井口 + 井台），非房屋；
    ///   · 客栈   → 二层楼阁（"两层楼阁式建筑，仅出现于城市的繁华街道，主要用于酒楼、食店"）；
    ///   · 衙门/税所/修缮房 → 乌头门（"六品以上官员的宅第外部，可以建有乌头门或门屋"）；
    ///   · 林场/采石/采矿/制盐/铸币 → 院中料堆 + 窑罩；
    ///   · 流民营 → 低矮席棚茅顶。
    /// </summary>
    private static void EmitPublicDetail(BuildRoleLists ctx, BuildingDef def, int footX, int footY,
        float w, float d, float wallH, float baseY, Vector3 center)
    {
        switch (def.Id)
        {
            case "well":
                EmitWell(ctx, baseY, center);
                break;
            case "inn":
                EmitUpperFloor(ctx, def, w, d, wallH, baseY, center);
                break;
            case "yamen":
            case "taxoffice":
            case "repairhouse":
                EmitWutouGate(ctx, w, d, wallH, baseY, center);
                break;
            case "lumber_camp":
            case "quarry":
            case "mine":
            case "saltworks":
            case "mint":
                EmitWorkYard(ctx, w, d, baseY, center);
                break;
            case "refugee_camp":
                EmitSheds(ctx, footX, footY, baseY, center);
                break;
        }
    }

    /// <summary>水井：圆形石砌井栏——外圈立石垒成一环、中间空出井口；下砌井台。</summary>
    private static void EmitWell(BuildRoleLists ctx, float baseY, Vector3 center)
    {
        const float outerR = 0.42f, innerR = 0.26f, ringH = 0.42f, plinthH = 0.1f;
        const int n = 10;
        float r = (outerR + innerR) / 2f;
        float thick = outerR - innerR;
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.Tau / n;
            var pos = new Vector3(center.X + Mathf.Cos(a) * r, baseY + plinthH + ringH / 2f,
                center.Z + Mathf.Sin(a) * r);
            var basis = Basis.FromEuler(new Vector3(0f, -a, 0f))
                * Basis.FromScale(new Vector3(thick, ringH, thick * 0.9f));
            ctx.Pillar.X.Add(new Transform3D(basis, pos));
            ctx.Pillar.C.Add(StepColor);
        }
        // 井台：方形薄板托住井栏
        AddBox(ctx.Step, new Vector3(center.X, baseY + plinthH / 2f, center.Z),
            new Vector3(outerR * 2.4f, plinthH, outerR * 2.4f), StepColor);
    }

    /// <summary>二层楼阁：主屋之上再加一层退半步的楼身 + 腰檐 + 攒尖小顶（宋代酒楼形制）。</summary>
    private static void EmitUpperFloor(BuildRoleLists ctx, BuildingDef def, float w, float d,
        float wallH, float baseY, Vector3 center)
    {
        float upperH = wallH * 0.72f;
        float uw = w * 0.82f, ud = d * 0.82f;
        ctx.Body.X.Add(new Transform3D(Basis.FromScale(new Vector3(uw, upperH, ud)),
            new Vector3(center.X, baseY + wallH + upperH / 2f, center.Z)));
        ctx.Body.C.Add(WithAlpha(def.GodotColor, SolidBodyAlpha));
        // 楼层间腰檐
        float eaveT = SongHouseConfig.EaveThickness;
        float ey = baseY + wallH + 0.02f;
        AddBox(ctx.Eave, new Vector3(center.X, ey, center.Z - ud / 2f), new Vector3(uw * 1.1f, eaveT, 0.2f), EaveColor);
        AddBox(ctx.Eave, new Vector3(center.X, ey, center.Z + ud / 2f), new Vector3(uw * 1.1f, eaveT, 0.2f), EaveColor);
        AddBox(ctx.Eave, new Vector3(center.X - uw * 1.1f / 2f, ey, center.Z), new Vector3(0.2f, eaveT, ud), EaveColor);
        AddBox(ctx.Eave, new Vector3(center.X + uw * 1.1f / 2f, ey, center.Z), new Vector3(0.2f, eaveT, ud), EaveColor);
        // 楼顶：攒尖
        float roofH = upperH * SongHouseConfig.RoofToWall;
        var roofBasis = Basis.FromEuler(new Vector3(0f, Mathf.Pi / 4f, 0f))
            * Basis.FromScale(new Vector3(ud * SongHouseConfig.EaveOverhangGrand, roofH,
                uw * SongHouseConfig.EaveOverhangGrand));
        ctx.Roof.X.Add(new Transform3D(roofBasis,
            new Vector3(center.X, baseY + wallH + upperH + roofH / 2f, center.Z)));
        ctx.Roof.C.Add(def.GodotColor.Darkened(0.45f));
    }

    /// <summary>乌头门：官署正门——双柱承一短檐、上有小瓦顶，柱间无墙（《宋史·舆服志》形制）。</summary>
    private static void EmitWutouGate(BuildRoleLists ctx, float w, float d, float wallH,
        float baseY, Vector3 center)
    {
        float gateH = Mathf.Min(wallH * 0.8f, 2.2f);
        float gateW = Mathf.Min(w * 0.5f, 3.2f);
        float zFront = center.Z - d / 2f - 0.15f;
        foreach (float sx in new[] { -gateW / 2f, gateW / 2f })
        {
            ctx.Pillar.X.Add(new Transform3D(Basis.FromScale(new Vector3(0.18f, gateH, 0.18f)),
                new Vector3(center.X + sx, baseY + gateH / 2f, zFront)));
            ctx.Pillar.C.Add(new Color(0.35f, 0.27f, 0.20f));
        }
        AddBox(ctx.Eave, new Vector3(center.X, baseY + gateH, zFront),
            new Vector3(gateW + 0.5f, 0.16f, 0.3f), EaveColor);
        var roofBasis = Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f))
            * Basis.FromScale(new Vector3(0.5f, 0.42f, gateW + 0.7f));
        ctx.Roof.X.Add(new Transform3D(roofBasis,
            new Vector3(center.X, baseY + gateH + 0.16f + 0.21f, zFront)));
        ctx.Roof.C.Add(new Color(0.24f, 0.19f, 0.14f));
    }

    /// <summary>工坊料场：院中堆料（柴捆/矿石/盐垛）+ 窑罩（半圆顶小间）。</summary>
    private static void EmitWorkYard(BuildRoleLists ctx, float w, float d, float baseY, Vector3 center)
    {
        var rng = new System.Random(7);
        for (int i = 0; i < 3; i++)
        {
            float ox = ((float)rng.NextDouble() * 2f - 1f) * w * 0.28f;
            float oz = ((float)rng.NextDouble() * 2f - 1f) * d * 0.28f;
            float ph = 0.35f + (float)rng.NextDouble() * 0.3f;
            ctx.Wall.X.Add(new Transform3D(Basis.FromScale(new Vector3(0.7f, ph, 0.5f)),
                new Vector3(center.X + ox, baseY + ph / 2f, center.Z + oz)));
            ctx.Wall.C.Add(new Color(0.45f, 0.38f, 0.30f));
        }
        float kh = 0.9f;
        ctx.Roof.X.Add(new Transform3D(Basis.FromScale(new Vector3(1.1f, kh, 1.1f)),
            new Vector3(center.X, baseY + kh / 2f, center.Z)));
        ctx.Roof.C.Add(new Color(0.35f, 0.30f, 0.25f));
    }

    /// <summary>流民营：低矮席棚茅顶——一排敞棚 + 陡坡茅草顶。</summary>
    private static void EmitSheds(BuildRoleLists ctx, int footX, int footY, float baseY, Vector3 center)
    {
        float cs = MapGrid.CellSize;
        int n = Mathf.Clamp(footX / 3, 2, 4);
        float shedW = (footX * cs * 0.8f) / n;
        for (int i = 0; i < n; i++)
        {
            float x = center.X - footX * cs * 0.4f + shedW * (i + 0.5f);
            float h = 0.55f;
            ctx.Body.X.Add(new Transform3D(Basis.FromScale(new Vector3(shedW * 0.9f, h, footY * cs * 0.45f)),
                new Vector3(x, baseY + h / 2f, center.Z)));
            ctx.Body.C.Add(WithAlpha(new Color(0.72f, 0.64f, 0.48f), 0.9f));
            var basis = Basis.FromEuler(new Vector3(0f, Mathf.Pi / 2f, 0f))
                * Basis.FromScale(new Vector3(footY * cs * 0.5f, 0.45f, shedW));
            ctx.Roof.X.Add(new Transform3D(basis, new Vector3(x, baseY + h + 0.225f, center.Z)));
            ctx.Roof.C.Add(new Color(0.55f, 0.47f, 0.32f));
        }
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

    private static void AddPillars(BuildRoleLists ctx, BuildingDef def,
        float w, float d, float baseY, float top, float bodyCx, float bodyCz)
    {
        float ph = top - baseY;
        float inset = 0.18f;
        float px = w / 2f - inset, pz = d / 2f - inset;
        var pts = new List<(float, float)>
        {
            (-px, -pz), (px, -pz), (px, pz), (-px, pz),
        };
        // 宽面（≥4m）补边中立柱，大殿宇前廊再外凸两根
        if (w >= 4f) { pts.Add((0f, -pz)); pts.Add((0f, pz)); }
        if (d >= 4f) { pts.Add((-px, 0f)); pts.Add((px, 0f)); }
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
    private static void AddDoorAndWindows(BuildRoleLists ctx, bool openFront, float bodyW, float bodyD,
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
