using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 宋式住宅布局引擎（批次一百零一）：按「临路种类 + 建筑类别 + 占地面积」解析出一种
/// 平面制式，并把制式展开成一组**房屋单元**（位置/开间/进深/角色/墙高），
/// 供 <see cref="BuildingModelFactory"/> 产出视觉。
///
/// **三种基础布局**（用户指定，均有史料出处）：
///
/// ① **前屋后院**（<see cref="SongLayout.FrontHouseBackYard"/>）——临大街的商业与工坊。
///    《清明上河图》即此制："临大街屋宇，多为店铺门面，前头营业，后边住人，
///    或前店、后作、居室三者兼备"。屋贴路、院在后，前脸不砌墙只立柱（铺面敞开），
///    挂招幌；后院堆料/存货。
///
/// ② **前院后屋**（<see cref="SongLayout.FrontYardBackHouse"/>）——背街小巷的民居。
///    "为数众多的一进院，遍布于各个背街小巷"。院朝路（门 + 影壁），屋在院后；
///    占地够则按三合院（正房 + 两厢）→ 四合院（加倒座）逐级加屋。
///
/// ③ **院子包围**（<see cref="SongLayout.CourtyardEnclosed"/>）——大型住宅与王爷府。
///    "四面起屋，中留天井，即成四合院"；大宅"前院后园""二、三进院"，
///    挟屋/门楼/二门/过厅齐备。按占地面积摆 1~N 座屋。
///
/// **占地 → 制式的对应**（核心设计：让「扩建」直接长出一进院子）：
/// <code>
///   面积 ≤ 4   一字形     单檐小屋（"一字形住宅主要是两椽建筑，规模很小"）
///   面积 5~8   前院后屋·简  院带 + 正房
///   面积 9~15  前院后屋·三合 正房 + 两厢
///   面积 16~24 前院后屋·四合 正房 + 两厢 + 倒座
///   面积 25~48 院子包围·一进 四合院 + 挟屋 + 门楼
///   面积 ≥ 49  院子包围·二进 门楼 + 影壁 + 前院 + 二门 + 后院（8 座起）
/// </code>
/// 临街商业/工坊无论大小一律走①（前屋后院），只是后院随占地加深。
///
/// **方向约定（改平面前必读，这里栽过两次）**：<c>roadDir</c> 是从建筑**指向路**的单位格向量，
/// roadDir=+1 表示路在高端格索引一侧。所有铺陈统一走**归一化进深坐标** <see cref="U"/>：
/// u=0 恒为临路侧（前），u=deep−1 恒为背侧，再由 <see cref="Idx"/> 映射回真实格索引。
/// 早先版本手算「前带占 [deep−band, deep) 还是 [0, band)」连错两回（院子铺到背街一侧、
/// 二进院两厢出界），全是这个原因——**永远不要在两个方向上来回换算索引**。
/// </summary>
public static class SongHouseLayout
{
    /// <summary>三种基础布局。</summary>
    public enum SongLayout { FrontHouseBackYard, FrontYardBackHouse, CourtyardEnclosed }

    /// <summary>制式分级：同一种布局下的规模档（决定摆几座屋）。</summary>
    public enum Tier
    {
        Single,      // 一字形：一座屋，无院
        YardSimple,  // 院带 + 正房
        ThreeSided,  // 三合院：正房 + 两厢
        FourSided,   // 四合院：正房 + 两厢 + 倒座
        OneCourt,    // 一进大院：四合院 + 挟屋 + 门楼
        TwoCourt,    // 二进院：门楼 + 影壁 + 前院 + 二门 + 后院
    }

    /// <summary>一座房屋单元：相对建筑原点的格子矩形 + 角色 + 墙高比 + 是否临街。</summary>
    public readonly struct HouseUnit
    {
        /// <summary>相对原点的格子左上角（建筑局部格坐标）。</summary>
        public readonly Vector2I Cell;
        /// <summary>开间（格，沿 X）。</summary>
        public readonly int W;
        /// <summary>进深（格，沿 Z/Y）。</summary>
        public readonly int D;
        /// <summary>角色。</summary>
        public readonly HouseRole Role;
        /// <summary>墙高相对正房的比例。</summary>
        public readonly float WallRatio;
        /// <summary>是否临街面（前脸敞开、可挂招幌）。</summary>
        public readonly bool FacesStreet;

        public HouseUnit(Vector2I cell, int w, int d, HouseRole role, float wallRatio, bool facesStreet)
        {
            Cell = cell; W = w; D = d; Role = role; WallRatio = wallRatio; FacesStreet = facesStreet;
        }

        /// <summary>开间（米）。</summary>
        public float WidthM => W * MapGrid.CellSize;
        /// <summary>进深（米）。</summary>
        public float DepthM => D * MapGrid.CellSize;
    }

    /// <summary>房屋角色（决定细部：正房高而阔、厢房低、铺面临街敞开、门楼带瓦顶）。</summary>
    public enum HouseRole
    {
        MainHouse,   // 正房（上房）：父母尊长居之，最高大
        WingEast,    // 东厢房
        WingWest,    // 西厢房
        RearHall,    // 倒座（南房）：朝院开门的附属屋
        Attached,    // 挟屋：正房两侧接建的小房（《营造法式》厦两头配屋）
        GateTower,   // 门楼：大门上起楼，砖瓦覆顶
        ShopFront,   // 铺面：临街敞开，前脸不砌墙
        WorkShed,    // 作坊/后作：工坊的生产间
        BackDwell,   // 后宅：铺面/作坊之后的居室
        ScreenWall,  // 影壁：进门迎面障门的立壁（非房屋，但同属平面要素）
    }

    /// <summary>布局计划。</summary>
    public readonly struct Plan
    {
        public readonly SongLayout Layout;
        public readonly Tier Tier;
        public readonly List<HouseUnit> Houses;
        /// <summary>院落所在的矩形（相对原点格子）；无院时为 null。</summary>
        public readonly Rect2I? Yard;
        /// <summary>临路方向（路在建筑哪一侧）：单位格向量；无临路为 (0,−1) 默认。</summary>
        public readonly Vector2I RoadDir;
        /// <summary>是否临主路/辅路（决定铺面是否朝路敞开）。</summary>
        public readonly bool OnArtery;

        public Plan(SongLayout layout, Tier tier, List<HouseUnit> houses, Rect2I? yard,
            Vector2I roadDir, bool onArtery)
        {
            Layout = layout; Tier = tier; Houses = houses; Yard = yard;
            RoadDir = roadDir; OnArtery = onArtery;
        }
    }

    /// <summary>道路类别（与 <c>MapGrid.CellAt().RoadKind</c> 对齐）。</summary>
    public enum RoadClass { None, Lane, Side, Main }

    /// <summary>面积阈值（格²）：跨过即升级一档制式。</summary>
    public static class SongLayoutThresholds
    {
        /// <summary>≥ 此面积进三合院（正房 + 两厢）。</summary>
        public const int ThreeSidedArea = 9;
        /// <summary>≥ 此面积进四合院（加倒座）。</summary>
        public const int FourSidedArea = 16;
        /// <summary>≥ 此面积进一进大院（四合院 + 挟屋 + 门楼）。</summary>
        public const int OneCourtArea = 25;
        /// <summary>≥ 此面积进二进院。</summary>
        public const int TwoCourtArea = 49;
    }

    // ---- 归一化进深坐标：u=0 恒为临路侧，u=deep−1 恒为背侧 ----

    /// <summary>归一化坐标 u → 真实格索引。roadSign 为 roadDir 在进深轴上的符号。</summary>
    private static int Idx(int u, int deep, int roadSign) => roadSign > 0 ? deep - 1 - u : u;

    /// <summary>归一化带 [uFrom, uTo] 的真实起始格。
    /// ⚠️ roadSign&gt;0 时「u 越大行号越小」，故起点必须取 <c>Idx(uTo)</c> 而非 <c>Idx(uFrom)</c>——
    /// 用 uFrom 会让整带向下游错一格（与相邻带重叠），这是本类栽过的第三个坑。</summary>
    private static int BandStart(int uFrom, int uTo, int deep, int roadSign)
        => roadSign > 0 ? deep - 1 - uTo : uFrom;

    /// <summary>沿进深轴铺一带房（正房/倒座/铺面用）：占满开间（可按 sideInset 内缩，给挟屋留位）。</summary>
    private static HouseUnit Band(bool roadOnZ, int footX, int footY, int deep, int roadSign,
        int uFrom, int uTo, HouseRole role, float wallRatio, bool facesStreet, int sideInset = 0)
    {
        int band = uTo - uFrom + 1;
        int start = BandStart(uFrom, uTo, deep, roadSign);
        return roadOnZ
            ? new HouseUnit(new Vector2I(sideInset, start), Math.Max(1, footX - sideInset * 2), band,
                role, wallRatio, facesStreet)
            : new HouseUnit(new Vector2I(start, sideInset), band, Math.Max(1, footY - sideInset * 2),
                role, wallRatio, facesStreet);
    }

    /// <summary>沿开间轴铺一带房（厢房用）：占开间轴一列或一行，贴在 sideInset 内缩后的两端。</summary>
    private static HouseUnit CrossBand(bool roadOnZ, int footX, int footY, int deep, int roadSign,
        int uFrom, int uTo, bool atHighSide, HouseRole role, float wallRatio, int sideInset = 0)
    {
        int band = uTo - uFrom + 1;
        int start = BandStart(uFrom, uTo, deep, roadSign);
        int along = (roadOnZ ? footX : footY) - sideInset * 2;
        return roadOnZ
            ? new HouseUnit(new Vector2I(atHighSide ? sideInset + along - 1 : sideInset, start), 1, band,
                role, wallRatio, false)
            : new HouseUnit(new Vector2I(start, atHighSide ? sideInset + along - 1 : sideInset), band, 1,
                role, wallRatio, false);
    }

    /// <summary>沿开间轴**居中**铺一带（门楼/影壁/二门用）：居中于立面，宽/深 2 格。</summary>
    private static HouseUnit CenterBand(bool roadOnZ, int footX, int footY, int deep, int roadSign,
        int u, HouseRole role, float wallRatio, bool facesStreet, int sideInset = 0)
    {
        int start = Idx(u, deep, roadSign);
        int along = (roadOnZ ? footX : footY) - sideInset * 2;
        int mid = sideInset + Math.Max(0, along / 2 - 1);
        return roadOnZ
            ? new HouseUnit(new Vector2I(mid, start), 2, 1, role, wallRatio, facesStreet)
            : new HouseUnit(new Vector2I(start, mid), 1, 2, role, wallRatio, facesStreet);
    }

    // ---- 制式解析 ----

    /// <summary>解析制式：临大街的商业/工坊走前屋后院；占地够大走院子包围；其余（民居）走前院后屋。</summary>
    public static SongLayout ResolveLayout(BuildingDef def, int footX, int footY, RoadClass roadClass)
    {
        int area = Math.Max(1, footX * footY);

        bool commercial = def != null && (def.Id == "shop" || def.Id == "workshop");
        if (commercial && roadClass is RoadClass.Main or RoadClass.Side)
            return SongLayout.FrontHouseBackYard;

        if (area >= SongLayoutThresholds.OneCourtArea)
            return SongLayout.CourtyardEnclosed;

        return SongLayout.FrontYardBackHouse;
    }

    /// <summary>按面积定分级。</summary>
    public static Tier TierOf(int area) => area switch
    {
        <= 4 => Tier.Single,
        < SongLayoutThresholds.ThreeSidedArea => Tier.YardSimple,
        < SongLayoutThresholds.FourSidedArea => Tier.ThreeSided,
        < SongLayoutThresholds.OneCourtArea => Tier.FourSided,
        < SongLayoutThresholds.TwoCourtArea => Tier.OneCourt,
        _ => Tier.TwoCourt,
    };

    // ---- 平面展开 ----

    /// <summary>
    /// 把制式展开成平面计划。roadDir 为「路在建筑哪一侧」的单位格向量
    /// （如路在东侧 = (+1,0)）；无临路传 (0,−1)（默认正面朝 −Z）。
    /// </summary>
    public static Plan Build(BuildingDef def, int footX, int footY, int level, RoadClass roadClass,
        Vector2I roadDir)
    {
        var layout = ResolveLayout(def, footX, footY, roadClass);
        int area = Math.Max(1, footX * footY);
        var tier = TierOf(area);

        var houses = new List<HouseUnit>();
        Rect2I? yard = layout switch
        {
            SongLayout.FrontHouseBackYard => PlanFrontHouseBackYard(def, footX, footY, roadDir, houses),
            SongLayout.FrontYardBackHouse => PlanFrontYardBackHouse(footX, footY, roadDir, houses),
            _ => PlanCourtyardEnclosed(footX, footY, roadDir, houses),
        };

        return new Plan(layout, tier, houses, yard, roadDir,
            roadClass is RoadClass.Main or RoadClass.Side);
    }

    /// <summary>① 前屋后院：铺面/作坊贴路（u=0 一带），后院在背侧。</summary>
    private static Rect2I? PlanFrontHouseBackYard(BuildingDef def, int footX, int footY,
        Vector2I roadDir, List<HouseUnit> houses)
    {
        bool z = Math.Abs(roadDir.Y) > 0;
        int rs = z ? Math.Sign(roadDir.Y) : Math.Sign(roadDir.X);
        int deep = z ? footY : footX;

        // 铺面占进深前 1/3（不小于 1 格，不大于 60%），后院占其余
        int shopDeep = Math.Clamp(deep / 3, 1, Math.Max(1, (int)(deep * 0.6f)));
        int yardDeep = deep - shopDeep;
        bool isWorkshop = def != null && def.Id == "workshop";
        var backRole = isWorkshop ? HouseRole.WorkShed : HouseRole.BackDwell;

        houses.Add(Band(z, footX, footY, deep, rs, 0, shopDeep - 1, HouseRole.ShopFront, 1f, true));
        if (yardDeep > 0)
            houses.Add(Band(z, footX, footY, deep, rs, shopDeep, deep - 1, backRole, 0.8f, false));

        if (yardDeep <= 0)
            return null;
        int ys = Idx(shopDeep, deep, rs);
        return z
            ? new Rect2I(new Vector2I(0, ys), new Vector2I(footX, yardDeep))
            : new Rect2I(new Vector2I(ys, 0), new Vector2I(yardDeep, footY));
    }

    /// <summary>② 前院后屋：院朝路（u=0 一带），正房在院后；占地够则加两厢 → 倒座。</summary>
    private static Rect2I? PlanFrontYardBackHouse(int footX, int footY, Vector2I roadDir,
        List<HouseUnit> houses)
    {
        int area = footX * footY;
        bool z = Math.Abs(roadDir.Y) > 0;
        int rs = z ? Math.Sign(roadDir.Y) : Math.Sign(roadDir.X);
        int deep = z ? footY : footX;

        if (area <= 4)
        {
            // 一字形：单檐小屋，无院
            houses.Add(new HouseUnit(new Vector2I(0, 0), footX, footY, HouseRole.MainHouse, 1f, true));
            return null;
        }

        int yardDeep = Math.Clamp(deep / 3, 1, 2);
        bool fourSided = area >= SongLayoutThresholds.FourSidedArea;
        // 四合院在临路侧多一排倒座，故院带整体后移一格、正房起点也相应后移
        int houseFrom = fourSided ? yardDeep + 1 : yardDeep;
        int houseTo = deep - 1;

        if (area < SongLayoutThresholds.ThreeSidedArea)
        {
            // 院带 + 正房
            houses.Add(Band(z, footX, footY, deep, rs, houseFrom, houseTo, HouseRole.MainHouse, 1f, false));
            int ys = Idx(0, deep, rs);
            return z
                ? new Rect2I(new Vector2I(0, ys), new Vector2I(footX, yardDeep))
                : new Rect2I(new Vector2I(ys, 0), new Vector2I(yardDeep, footY));
        }

        // 三合院：正房 + 两厢夹院（院在 u∈[0, yardDeep−1]，两厢占开间轴两端）
        houses.Add(Band(z, footX, footY, deep, rs, houseFrom, houseTo, HouseRole.MainHouse, 1f, false));
        houses.Add(CrossBand(z, footX, footY, deep, rs, 0, yardDeep - 1, false, HouseRole.WingWest,
            SongHouseConfig.WingWallRatio));
        houses.Add(CrossBand(z, footX, footY, deep, rs, 0, yardDeep - 1, true, HouseRole.WingEast,
            SongHouseConfig.WingWallRatio));

        int yardFrom = 0, yardTo = yardDeep - 1;
        if (fourSided)
        {
            // 四合院：临路侧加倒座，院带整体后移到 u∈[1, yardDeep]
            houses.Add(Band(z, footX, footY, deep, rs, 0, 0, HouseRole.RearHall,
                SongHouseConfig.RearHallWallRatio, false));
            houses[houses.Count - 2] = CrossBand(z, footX, footY, deep, rs, 1, yardDeep, false,
                HouseRole.WingWest, SongHouseConfig.WingWallRatio);
            houses[houses.Count - 1] = CrossBand(z, footX, footY, deep, rs, 1, yardDeep, true,
                HouseRole.WingEast, SongHouseConfig.WingWallRatio);
            yardFrom = 1;
            yardTo = yardDeep;
        }

        int yx = Idx(yardFrom, deep, rs);
        int yw = yardTo - yardFrom + 1;
        return z
            ? new Rect2I(new Vector2I(1, yx), new Vector2I(Math.Max(1, footX - 2), yw))
            : new Rect2I(new Vector2I(yx, 1), new Vector2I(yw, Math.Max(1, footY - 2)));
    }

    /// <summary>③ 院子包围：四面起屋围合天井；按面积加挟屋、门楼，够大则二进院。</summary>
    private static Rect2I? PlanCourtyardEnclosed(int footX, int footY, Vector2I roadDir,
        List<HouseUnit> houses)
    {
        bool z = Math.Abs(roadDir.Y) > 0;
        int rs = z ? Math.Sign(roadDir.Y) : Math.Sign(roadDir.X);
        int deep = z ? footY : footX;
        int area = footX * footY;

        if (area >= SongLayoutThresholds.TwoCourtArea)
        {
            PlaceTwoCourt(footX, footY, deep, rs, z, houses);
            // 二进院的天井取中段（前院与后院之间）
            int mid = Math.Max(2, deep / 2);
            int cs = Idx(1, deep, rs);
            int cw = Math.Max(1, mid - 2);
            return z
                ? new Rect2I(new Vector2I(1, cs), new Vector2I(Math.Max(1, footX - 2), cw))
                : new Rect2I(new Vector2I(cs, 1), new Vector2I(cw, Math.Max(1, footY - 2)));
        }

        PlaceRing(footX, footY, deep, rs, z, houses);

        // 一进大院的天井：四面房带之间
        int inset = Math.Min(2, Math.Max(1, Math.Min(z ? footX : footY, deep) / 5));
        return new Rect2I(new Vector2I(inset, inset),
            new Vector2I(Math.Max(1, footX - inset * 2), Math.Max(1, footY - inset * 2)));
    }

    /// <summary>一进大院的「四面起屋」环：倒座/门楼在 u=0，两厢夹中，正房在背，挟屋贴正房两端。</summary>
    private static void PlaceRing(int footX, int footY, int deep, int rs, bool z, List<HouseUnit> houses)
    {
        int backBand = Math.Clamp(deep / 4, 1, 2);   // 正房带宽
        int backFrom = deep - backBand;              // 正房起始 u
        // 立面够宽才摆挟屋：正房相应内缩 1 格，两端让给挟屋（挟屋即「厦两头」正厅两侧接建的小房）
        bool withAttached = (z && footX >= 6) || (!z && footY >= 6);
        int inset = withAttached ? 1 : 0;

        houses.Add(Band(z, footX, footY, deep, rs, 0, 0, HouseRole.RearHall,
            SongHouseConfig.RearHallWallRatio, false));
        houses.Add(Band(z, footX, footY, deep, rs, backFrom, deep - 1, HouseRole.MainHouse, 1f, false, inset));
        houses.Add(CrossBand(z, footX, footY, deep, rs, 1, backFrom - 1, false, HouseRole.WingWest,
            SongHouseConfig.WingWallRatio));
        houses.Add(CrossBand(z, footX, footY, deep, rs, 1, backFrom - 1, true, HouseRole.WingEast,
            SongHouseConfig.WingWallRatio));
        // 门楼压在倒座带正中（大门开在倒座上，故与倒座重叠是有意的）
        houses.Add(CenterBand(z, footX, footY, deep, rs, 0, HouseRole.GateTower,
            SongHouseConfig.GateTowerWallRatio, true));
        if (withAttached)
        {
            int aStart = BandStart(backFrom, deep - 1, deep, rs);
            if (z)
            {
                houses.Add(new HouseUnit(new Vector2I(0, aStart), 1, backBand, HouseRole.Attached,
                    SongHouseConfig.AttachedWallRatio, false));
                houses.Add(new HouseUnit(new Vector2I(footX - 1, aStart), 1, backBand, HouseRole.Attached,
                    SongHouseConfig.AttachedWallRatio, false));
            }
            else
            {
                houses.Add(new HouseUnit(new Vector2I(aStart, 0), backBand, 1, HouseRole.Attached,
                    SongHouseConfig.AttachedWallRatio, false));
                houses.Add(new HouseUnit(new Vector2I(aStart, footY - 1), backBand, 1, HouseRole.Attached,
                    SongHouseConfig.AttachedWallRatio, false));
            }
        }
    }

    /// <summary>二进院：门楼 + 影壁在 u=0/1，前院两厢，二门居中，正房 + 挟屋 + 两厢在后院。</summary>
    private static void PlaceTwoCourt(int footX, int footY, int deep, int rs, bool z, List<HouseUnit> houses)
    {
        int mid = Math.Max(2, deep / 2);      // 二门所在 u
        int backBand = 2;                     // 正房带宽
        int backFrom = deep - backBand;       // 正房起始 u
        bool withAttached = (z && footX >= 6) || (!z && footY >= 6);
        int inset = withAttached ? 1 : 0;

        // 前院：倒座（u=0）+ 两厢（u∈[2, mid−1]）
        houses.Add(Band(z, footX, footY, deep, rs, 0, 0, HouseRole.RearHall,
            SongHouseConfig.RearHallWallRatio, false));
        houses.Add(CrossBand(z, footX, footY, deep, rs, 2, mid - 1, false, HouseRole.WingWest,
            SongHouseConfig.WingWallRatio));
        houses.Add(CrossBand(z, footX, footY, deep, rs, 2, mid - 1, true, HouseRole.WingEast,
            SongHouseConfig.WingWallRatio));
        // 门楼（u=0，压在倒座上）+ 影壁（u=1）+ 二门（u=mid）
        houses.Add(CenterBand(z, footX, footY, deep, rs, 0, HouseRole.GateTower,
            SongHouseConfig.GateTowerWallRatio, true));
        houses.Add(CenterBand(z, footX, footY, deep, rs, 1, HouseRole.ScreenWall, 1f, false));
        houses.Add(CenterBand(z, footX, footY, deep, rs, mid, HouseRole.GateTower,
            SongHouseConfig.GateTowerWallRatio, true));
        // 后院：正房（u∈[backFrom, deep−1]）+ 两厢（u∈[mid+1, backFrom−1]）
        houses.Add(Band(z, footX, footY, deep, rs, backFrom, deep - 1, HouseRole.MainHouse, 1f, false, inset));
        houses.Add(CrossBand(z, footX, footY, deep, rs, mid + 1, backFrom - 1, false, HouseRole.WingWest,
            SongHouseConfig.WingWallRatio));
        houses.Add(CrossBand(z, footX, footY, deep, rs, mid + 1, backFrom - 1, true, HouseRole.WingEast,
            SongHouseConfig.WingWallRatio));
        if (withAttached)
        {
            int aStart = BandStart(backFrom, deep - 1, deep, rs);
            if (z)
            {
                houses.Add(new HouseUnit(new Vector2I(0, aStart), 1, backBand, HouseRole.Attached,
                    SongHouseConfig.AttachedWallRatio, false));
                houses.Add(new HouseUnit(new Vector2I(footX - 1, aStart), 1, backBand, HouseRole.Attached,
                    SongHouseConfig.AttachedWallRatio, false));
            }
            else
            {
                houses.Add(new HouseUnit(new Vector2I(aStart, 0), backBand, 1, HouseRole.Attached,
                    SongHouseConfig.AttachedWallRatio, false));
                houses.Add(new HouseUnit(new Vector2I(aStart, footY - 1), backBand, 1, HouseRole.Attached,
                    SongHouseConfig.AttachedWallRatio, false));
            }
        }
    }

    /// <summary>把 <c>MapGrid</c> 的 RoadKind 映射成本引擎的道路类别。</summary>
    public static RoadClass ClassOf(RoadKind kind) => kind switch
    {
        RoadKind.Main => RoadClass.Main,
        RoadKind.Side => RoadClass.Side,
        RoadKind.Lane => RoadClass.Lane,
        _ => RoadClass.None,
    };
}
