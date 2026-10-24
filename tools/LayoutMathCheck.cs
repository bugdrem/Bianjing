// 一次性平面校验：SongHouseLayout 的同构镜像。核对「无出界 / 重叠可控 / 座数符合形制 /
// 临路面正确 / 主体墙高最高 / 尺度推导」。用法同 tools/RigMathCheck.cs。验完即删。
// ⚠️ 改 scripts/render/SongHouseLayout.cs 后必须同步改这里，否则验的是旧逻辑。
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

enum RoadClass { None, Lane, Side, Main }
enum SongLayout { FrontHouseBackYard, FrontYardBackHouse, CourtyardEnclosed }
enum Tier { Single, YardSimple, ThreeSided, FourSided, OneCourt, TwoCourt }
enum HouseRole { MainHouse, WingEast, WingWest, RearHall, Attached, GateTower, ShopFront, WorkShed, BackDwell, ScreenWall }

record Unit(Vector2I Cell, int W, int D, HouseRole Role, float WallRatio, bool FacesStreet);

static class Cfg
{
    public const int ThreeSidedArea = 9, FourSidedArea = 16, OneCourtArea = 25, TwoCourtArea = 49;
    public const float WingWallRatio = 0.72f, RearHallWallRatio = 0.78f, GateTowerWallRatio = 0.85f, AttachedWallRatio = 0.68f;
    public static int Idx(int u, int deep, int rs) => rs > 0 ? deep - 1 - u : u;
}

static class Layout
{
    public static SongLayout Resolve(string defId, int fx, int fy, RoadClass road)
    {
        int area = Math.Max(1, fx * fy);
        bool commercial = defId is "shop" or "workshop";
        if (commercial && road is RoadClass.Main or RoadClass.Side) return SongLayout.FrontHouseBackYard;
        if (area >= Cfg.OneCourtArea) return SongLayout.CourtyardEnclosed;
        return SongLayout.FrontYardBackHouse;
    }

    public static Tier TierOf(int area) => area switch
    {
        <= 4 => Tier.Single,
        < Cfg.ThreeSidedArea => Tier.YardSimple,
        < Cfg.FourSidedArea => Tier.ThreeSided,
        < Cfg.OneCourtArea => Tier.FourSided,
        < Cfg.TwoCourtArea => Tier.OneCourt,
        _ => Tier.TwoCourt,
    };

    static int BandStart(int uf, int ut, int deep, int rs) => rs > 0 ? deep - 1 - ut : uf;
    static Unit Band(bool z, int fx, int fy, int deep, int rs, int uf, int ut, HouseRole role, float wr, bool face, int inset = 0)
    {
        int b = ut - uf + 1, s = BandStart(uf, ut, deep, rs);
        return z ? new Unit(new Vector2I(inset, s), Math.Max(1, fx - inset * 2), b, role, wr, face)
                 : new Unit(new Vector2I(s, inset), b, Math.Max(1, fy - inset * 2), role, wr, face);
    }
    static Unit Cross(bool z, int fx, int fy, int deep, int rs, int uf, int ut, bool hi, HouseRole role, float wr, int inset = 0)
    {
        int b = ut - uf + 1, s = BandStart(uf, ut, deep, rs), along = (z ? fx : fy) - inset * 2;
        return z ? new Unit(new Vector2I(hi ? inset + along - 1 : inset, s), 1, b, role, wr, false)
                 : new Unit(new Vector2I(s, hi ? inset + along - 1 : inset), b, 1, role, wr, false);
    }
    static Unit Center(bool z, int fx, int fy, int deep, int rs, int u, HouseRole role, float wr, bool face, int inset = 0)
    {
        int s = Cfg.Idx(u, deep, rs), along = (z ? fx : fy) - inset * 2, mid = inset + Math.Max(0, along / 2 - 1);
        return z ? new Unit(new Vector2I(mid, s), 2, 1, role, wr, face)
                 : new Unit(new Vector2I(s, mid), 1, 2, role, wr, face);
    }

    public static (List<Unit> houses, Rect2I? yard) Build(string defId, int fx, int fy, RoadClass road, Vector2I rd)
    {
        var layout = Resolve(defId, fx, fy, road);
        var h = new List<Unit>();
        Rect2I? yard = layout switch
        {
            SongLayout.FrontHouseBackYard => FrontBack(defId, fx, fy, rd, h),
            SongLayout.FrontYardBackHouse => FrontYard(fx, fy, rd, h),
            _ => Enclosed(fx, fy, rd, h),
        };
        return (h, yard);
    }

    static Rect2I? FrontBack(string defId, int fx, int fy, Vector2I rd, List<Unit> h)
    {
        bool z = Math.Abs(rd.Y) > 0;
        int rs = z ? Math.Sign(rd.Y) : Math.Sign(rd.X);
        int deep = z ? fy : fx;
        int shopDeep = Math.Clamp(deep / 3, 1, Math.Max(1, (int)(deep * 0.6f)));
        int yardDeep = deep - shopDeep;
        var back = defId == "workshop" ? HouseRole.WorkShed : HouseRole.BackDwell;
        h.Add(Band(z, fx, fy, deep, rs, 0, shopDeep - 1, HouseRole.ShopFront, 1f, true));
        if (yardDeep > 0) h.Add(Band(z, fx, fy, deep, rs, shopDeep, deep - 1, back, 0.8f, false));
        if (yardDeep <= 0) return null;
        int ys = Cfg.Idx(shopDeep, deep, rs);
        return z ? new Rect2I(new Vector2I(0, ys), new Vector2I(fx, yardDeep))
                 : new Rect2I(new Vector2I(ys, 0), new Vector2I(yardDeep, fy));
    }

    static Rect2I? FrontYard(int fx, int fy, Vector2I rd, List<Unit> h)
    {
        int area = fx * fy;
        if (area <= 4) { h.Add(new Unit(new Vector2I(0, 0), fx, fy, HouseRole.MainHouse, 1f, true)); return null; }
        bool z = Math.Abs(rd.Y) > 0;
        int rs = z ? Math.Sign(rd.Y) : Math.Sign(rd.X);
        int deep = z ? fy : fx;
        int yardDeep = Math.Clamp(deep / 3, 1, 2);
        bool fourSided = area >= Cfg.FourSidedArea;
        int hFrom = fourSided ? yardDeep + 1 : yardDeep, hTo = deep - 1;
        if (area < Cfg.ThreeSidedArea)
        {
            h.Add(Band(z, fx, fy, deep, rs, hFrom, hTo, HouseRole.MainHouse, 1f, false));
            int ys = Cfg.Idx(0, deep, rs);
            return z ? new Rect2I(new Vector2I(0, ys), new Vector2I(fx, yardDeep))
                     : new Rect2I(new Vector2I(ys, 0), new Vector2I(yardDeep, fy));
        }
        h.Add(Band(z, fx, fy, deep, rs, hFrom, hTo, HouseRole.MainHouse, 1f, false));
        h.Add(Cross(z, fx, fy, deep, rs, 0, yardDeep - 1, false, HouseRole.WingWest, Cfg.WingWallRatio));
        h.Add(Cross(z, fx, fy, deep, rs, 0, yardDeep - 1, true, HouseRole.WingEast, Cfg.WingWallRatio));
        int yFrom = 0, yTo = yardDeep - 1;
        if (fourSided)
        {
            h.Add(Band(z, fx, fy, deep, rs, 0, 0, HouseRole.RearHall, Cfg.RearHallWallRatio, false));
            h[^2] = Cross(z, fx, fy, deep, rs, 1, yardDeep, false, HouseRole.WingWest, Cfg.WingWallRatio);
            h[^1] = Cross(z, fx, fy, deep, rs, 1, yardDeep, true, HouseRole.WingEast, Cfg.WingWallRatio);
            yFrom = 1; yTo = yardDeep;
        }
        int yx = Cfg.Idx(yFrom, deep, rs), yw = yTo - yFrom + 1;
        return z ? new Rect2I(new Vector2I(1, yx), new Vector2I(Math.Max(1, fx - 2), yw))
                 : new Rect2I(new Vector2I(yx, 1), new Vector2I(yw, Math.Max(1, fy - 2)));
    }

    static Rect2I? Enclosed(int fx, int fy, Vector2I rd, List<Unit> h)
    {
        int area = fx * fy;
        bool z = Math.Abs(rd.Y) > 0;
        int rs = z ? Math.Sign(rd.Y) : Math.Sign(rd.X);
        int deep = z ? fy : fx;
        if (area >= Cfg.TwoCourtArea)
        {
            TwoCourt(fx, fy, deep, rs, z, h);
            int mid = Math.Max(2, deep / 2);
            int cs = Cfg.Idx(1, deep, rs), cw = Math.Max(1, mid - 2);
            return z ? new Rect2I(new Vector2I(1, cs), new Vector2I(Math.Max(1, fx - 2), cw))
                     : new Rect2I(new Vector2I(cs, 1), new Vector2I(cw, Math.Max(1, fy - 2)));
        }
        Ring(fx, fy, deep, rs, z, h);
        int inset = Math.Min(2, Math.Max(1, Math.Min(z ? fx : fy, deep) / 5));
        return new Rect2I(new Vector2I(inset, inset),
            new Vector2I(Math.Max(1, fx - inset * 2), Math.Max(1, fy - inset * 2)));
    }

    static void Ring(int fx, int fy, int deep, int rs, bool z, List<Unit> h)
    {
        int backBand = Math.Clamp(deep / 4, 1, 2), backFrom = deep - backBand;
        bool withAtt = (z && fx >= 6) || (!z && fy >= 6);
        int inset = withAtt ? 1 : 0;
        h.Add(Band(z, fx, fy, deep, rs, 0, 0, HouseRole.RearHall, Cfg.RearHallWallRatio, false));
        h.Add(Band(z, fx, fy, deep, rs, backFrom, deep - 1, HouseRole.MainHouse, 1f, false, inset));
        h.Add(Cross(z, fx, fy, deep, rs, 1, backFrom - 1, false, HouseRole.WingWest, Cfg.WingWallRatio));
        h.Add(Cross(z, fx, fy, deep, rs, 1, backFrom - 1, true, HouseRole.WingEast, Cfg.WingWallRatio));
        h.Add(Center(z, fx, fy, deep, rs, 0, HouseRole.GateTower, Cfg.GateTowerWallRatio, true));
        if (withAtt)
        {
            int a = BandStart(backFrom, deep - 1, deep, rs);
            if (z)
            {
                h.Add(new Unit(new Vector2I(0, a), 1, backBand, HouseRole.Attached, Cfg.AttachedWallRatio, false));
                h.Add(new Unit(new Vector2I(fx - 1, a), 1, backBand, HouseRole.Attached, Cfg.AttachedWallRatio, false));
            }
            else
            {
                h.Add(new Unit(new Vector2I(a, 0), backBand, 1, HouseRole.Attached, Cfg.AttachedWallRatio, false));
                h.Add(new Unit(new Vector2I(a, fy - 1), backBand, 1, HouseRole.Attached, Cfg.AttachedWallRatio, false));
            }
        }
    }

    static void TwoCourt(int fx, int fy, int deep, int rs, bool z, List<Unit> h)
    {
        int mid = Math.Max(2, deep / 2), backBand = 2, backFrom = deep - backBand;
        bool withAtt = (z && fx >= 6) || (!z && fy >= 6);
        int inset = withAtt ? 1 : 0;
        h.Add(Band(z, fx, fy, deep, rs, 0, 0, HouseRole.RearHall, Cfg.RearHallWallRatio, false));
        h.Add(Cross(z, fx, fy, deep, rs, 2, mid - 1, false, HouseRole.WingWest, Cfg.WingWallRatio));
        h.Add(Cross(z, fx, fy, deep, rs, 2, mid - 1, true, HouseRole.WingEast, Cfg.WingWallRatio));
        h.Add(Center(z, fx, fy, deep, rs, 0, HouseRole.GateTower, Cfg.GateTowerWallRatio, true));
        h.Add(Center(z, fx, fy, deep, rs, 1, HouseRole.ScreenWall, 1f, false));
        h.Add(Center(z, fx, fy, deep, rs, mid, HouseRole.GateTower, Cfg.GateTowerWallRatio, true));
        h.Add(Band(z, fx, fy, deep, rs, backFrom, deep - 1, HouseRole.MainHouse, 1f, false, inset));
        h.Add(Cross(z, fx, fy, deep, rs, mid + 1, backFrom - 1, false, HouseRole.WingWest, Cfg.WingWallRatio));
        h.Add(Cross(z, fx, fy, deep, rs, mid + 1, backFrom - 1, true, HouseRole.WingEast, Cfg.WingWallRatio));
        if (withAtt)
        {
            int a = BandStart(backFrom, deep - 1, deep, rs);
            if (z)
            {
                h.Add(new Unit(new Vector2I(0, a), 1, backBand, HouseRole.Attached, Cfg.AttachedWallRatio, false));
                h.Add(new Unit(new Vector2I(fx - 1, a), 1, backBand, HouseRole.Attached, Cfg.AttachedWallRatio, false));
            }
            else
            {
                h.Add(new Unit(new Vector2I(a, 0), backBand, 1, HouseRole.Attached, Cfg.AttachedWallRatio, false));
                h.Add(new Unit(new Vector2I(a, fy - 1), backBand, 1, HouseRole.Attached, Cfg.AttachedWallRatio, false));
            }
        }
    }
}

class Program
{
    static int fails = 0;
    static void Bad(string m) { Console.WriteLine("  FAIL " + m); fails++; }

    static bool InBounds(Unit u, int fx, int fy)
        => u.Cell.X >= 0 && u.Cell.Y >= 0 && u.Cell.X + u.W <= fx && u.Cell.Y + u.D <= fy && u.W > 0 && u.D > 0;

    static long OverlapArea(Unit a, Unit b)
    {
        int x0 = Math.Max(a.Cell.X, b.Cell.X), x1 = Math.Min(a.Cell.X + a.W, b.Cell.X + b.W);
        int y0 = Math.Max(a.Cell.Y, b.Cell.Y), y1 = Math.Min(a.Cell.Y + a.D, b.Cell.Y + b.D);
        return x1 > x0 && y1 > y0 ? (long)(x1 - x0) * (y1 - y0) : 0;
    }

    // 有意的重叠：门楼开在倒座上；影壁贴在门楼内。这两类不计入重叠指标。
    static bool Intentional(Unit a, Unit b)
        => a.Role == HouseRole.ScreenWall || b.Role == HouseRole.ScreenWall
        || (a.Role == HouseRole.GateTower && b.Role == HouseRole.RearHall)
        || (b.Role == HouseRole.GateTower && a.Role == HouseRole.RearHall);

    static void Main()
    {
        var dirs = new[] { new Vector2I(0, -1), new Vector2I(0, 1), new Vector2I(-1, 0), new Vector2I(1, 0) };
        var cases = new (string, int, int, RoadClass)[]
        {
            ("house", 2, 2, RoadClass.Lane), ("house", 3, 3, RoadClass.Lane), ("house", 4, 4, RoadClass.Lane),
            ("house", 5, 5, RoadClass.Lane), ("house", 6, 6, RoadClass.Lane), ("house", 7, 7, RoadClass.Lane),
            ("house", 8, 8, RoadClass.Lane), ("house", 3, 4, RoadClass.Lane), ("house", 4, 3, RoadClass.Lane),
            ("house", 2, 5, RoadClass.Lane), ("house", 5, 2, RoadClass.Lane),
            ("shop", 2, 2, RoadClass.Main), ("shop", 3, 3, RoadClass.Main), ("shop", 4, 4, RoadClass.Side),
            ("workshop", 3, 3, RoadClass.Side), ("workshop", 4, 4, RoadClass.Main),
            ("shop", 3, 3, RoadClass.Lane), ("shop", 3, 3, RoadClass.None),
            ("mansion", 4, 4, RoadClass.Lane), ("mansion", 5, 5, RoadClass.Lane), ("mansion", 7, 7, RoadClass.Lane),
            ("prince_mansion", 12, 12, RoadClass.Main), ("palace", 16, 16, RoadClass.Main),
            ("prince_mansion", 10, 8, RoadClass.Main), ("palace", 12, 10, RoadClass.Side),
            ("well", 2, 2, RoadClass.Lane), ("inn", 4, 4, RoadClass.Main), ("yamen", 8, 8, RoadClass.Main),
            ("refugee_camp", 6, 6, RoadClass.Lane), ("repairhouse", 8, 8, RoadClass.Main),
        };

        Console.WriteLine("== A：无出界 / 无零尺寸 ==");
        foreach (var (id, fx, fy, road) in cases)
            foreach (var d in dirs)
            {
                var (h, _) = Layout.Build(id, fx, fy, road, d);
                if (h.Count == 0) Bad($"{id} {fx}x{fy} {road} {d} 无屋");
                foreach (var u in h)
                    if (!InBounds(u, fx, fy))
                        Bad($"{id} {fx}x{fy} {road} {d} 出界: {u.Role} ({u.Cell.X},{u.Cell.Y}) {u.W}x{u.D}");
            }
        Console.WriteLine(fails == 0 ? "  通过" : "  见上");

        int before = fails;
        Console.WriteLine("== B：临路侧必须有门面要素（朝路屋 / 院）==");
        foreach (var (id, fx, fy, road) in cases)
            foreach (var d in dirs)
            {
                var (h, yard) = Layout.Build(id, fx, fy, road, d);
                bool z = Math.Abs(d.Y) > 0;
                int rs = z ? Math.Sign(d.Y) : Math.Sign(d.X);
                bool onRoadSide(Unit u) => z
                    ? (rs > 0 ? u.Cell.Y + u.D == fy : u.Cell.Y == 0)
                    : (rs > 0 ? u.Cell.X + u.W == fx : u.Cell.X == 0);
                bool yardOnRoad = yard.HasValue && (z
                    ? (rs > 0 ? yard.Value.End.Y == fy : yard.Value.Position.Y == 0)
                    : (rs > 0 ? yard.Value.End.X == fx : yard.Value.Position.X == 0));
                if (!h.Any(onRoadSide) && !yardOnRoad)
                    Bad($"{id} {fx}x{fy} {road} {d} 临路侧既无屋也无院");
            }
        Console.WriteLine(fails == before ? "  通过" : "  见上");

        before = fails;
        Console.WriteLine("== C：座数符合形制 ==");
        void Expect(string label, string id, int fx, int fy, RoadClass road, int minH, int maxH)
        {
            var (h, _) = Layout.Build(id, fx, fy, road, new Vector2I(0, -1));
            bool ok = h.Count >= minH && h.Count <= maxH;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {label,-26} {h.Count} 座（期望 {minH}~{maxH}）");
            if (!ok) fails++;
        }
        Expect("民居 2x2 一字形", "house", 2, 2, RoadClass.Lane, 1, 1);
        Expect("民居 3x3 三合院", "house", 3, 3, RoadClass.Lane, 3, 3);
        Expect("民居 4x4 四合院", "house", 4, 4, RoadClass.Lane, 4, 4);
        Expect("民居 5x5 一进大院", "house", 5, 5, RoadClass.Lane, 5, 7);
        Expect("民居 7x7 二进院", "house", 7, 7, RoadClass.Lane, 8, 12);
        Expect("民居 8x8 二进院", "house", 8, 8, RoadClass.Lane, 8, 12);
        Expect("商铺 3x3 临主路", "shop", 3, 3, RoadClass.Main, 2, 2);
        Expect("工坊 4x4 临主路", "workshop", 4, 4, RoadClass.Main, 2, 2);
        Expect("商铺 3x3 小巷", "shop", 3, 3, RoadClass.Lane, 3, 3);
        Expect("宅邸 4x4 四合院", "mansion", 4, 4, RoadClass.Lane, 4, 4);
        Expect("宅邸 7x7 二进院", "mansion", 7, 7, RoadClass.Lane, 8, 12);
        Expect("王爷府 12x12 二进院", "prince_mansion", 12, 12, RoadClass.Main, 8, 14);
        Expect("宫殿 16x16 二进院", "palace", 16, 16, RoadClass.Main, 8, 14);

        before = fails;
        Console.WriteLine("== D：主体屋（正房/铺面）墙高最高 ==");
        foreach (var (id, fx, fy, road) in cases)
        {
            var (h, _) = Layout.Build(id, fx, fy, road, new Vector2I(0, -1));
            var principal = h.Where(u => u.Role is HouseRole.MainHouse or HouseRole.ShopFront).ToList();
            if (principal.Count == 0) { Bad($"{id} {fx}x{fy} 无主体屋"); continue; }
            float maxOther = h.Where(u => u.Role is not (HouseRole.MainHouse or HouseRole.ShopFront))
                .Select(u => u.WallRatio).DefaultIfEmpty(0f).Max();
            if (principal[0].WallRatio < maxOther) Bad($"{id} {fx}x{fy} 主体墙高 {principal[0].WallRatio} 低于附属 {maxOther}");
        }
        Console.WriteLine(fails == before ? "  通过" : "  见上");

        before = fails;
        Console.WriteLine("== E：非有意重叠 = 0 ==");
        foreach (var (id, fx, fy, road) in cases)
            foreach (var d in dirs)
            {
                var (h, _) = Layout.Build(id, fx, fy, road, d);
                for (int i = 0; i < h.Count; i++)
                    for (int j = i + 1; j < h.Count; j++)
                        if (!Intentional(h[i], h[j]) && OverlapArea(h[i], h[j]) > 0)
                            Bad($"{id} {fx}x{fy} {road} {d} {h[i].Role}×{h[j].Role} 意外重叠");
            }
        Console.WriteLine(fails == before ? "  通过" : "  见上");

        before = fails;
        Console.WriteLine("== F：布局解析正确性 ==");
        void ExpectLayout(string label, string id, int fx, int fy, RoadClass road, SongLayout want)
        {
            var got = Layout.Resolve(id, fx, fy, road);
            bool ok = got == want;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {label,-30} → {got}");
            if (!ok) fails++;
        }
        ExpectLayout("商铺 3x3 临主路", "shop", 3, 3, RoadClass.Main, SongLayout.FrontHouseBackYard);
        ExpectLayout("工坊 4x4 临辅路", "workshop", 4, 4, RoadClass.Side, SongLayout.FrontHouseBackYard);
        ExpectLayout("商铺 3x3 小巷", "shop", 3, 3, RoadClass.Lane, SongLayout.FrontYardBackHouse);
        ExpectLayout("民居 2x2 小巷", "house", 2, 2, RoadClass.Lane, SongLayout.FrontYardBackHouse);
        ExpectLayout("民居 6x6", "house", 6, 6, RoadClass.Lane, SongLayout.CourtyardEnclosed);
        ExpectLayout("王爷府 12x12", "prince_mansion", 12, 12, RoadClass.Main, SongLayout.CourtyardEnclosed);
        ExpectLayout("水井 2x2 公共设施", "well", 2, 2, RoadClass.Lane, SongLayout.FrontYardBackHouse);

        before = fails;
        Console.WriteLine("== G：尺度推导 ==");
        float WallOf(int a, int lv = 1)
        {
            float h = 0.55f * MathF.Sqrt(a) * (1f + 0.08f * (lv - 1));
            return Math.Clamp(h, 0.95f, 3.6f);
        }
        void ExpectWall(string label, int a, float want)
        {
            float got = WallOf(a);
            bool ok = MathF.Abs(got - want) < 0.02f;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {label,-22} 墙高 {got:F2}m（期望 ≈{want:F2}）");
            if (!ok) fails++;
        }
        ExpectWall("2x2 民居", 4, 1.10f);
        ExpectWall("3x3 民居", 9, 1.65f);
        ExpectWall("4x4 宅邸", 16, 2.20f);
        ExpectWall("12x12 王爷府", 144, 3.60f);
        float person = 1.35f * 0.42f;
        Console.WriteLine($"  参考：村民实际高 = {person:F2}m");
        Console.WriteLine($"  2x2 民居总高 = {WallOf(4) * 1.5f:F2}m = {(WallOf(4) * 1.5f) / person:F1} 倍人高（旧值 1.05m = 1.8 倍）");
        Console.WriteLine($"  4x4 宅邸总高 = {WallOf(16) * 1.5f:F2}m = {(WallOf(16) * 1.5f) / person:F1} 倍人高");
        Console.WriteLine($"  12x12 王府总高 = {WallOf(144) * 1.5f:F2}m = {(WallOf(144) * 1.5f) / person:F1} 倍人高");

        Console.WriteLine(fails == 0 ? "\n全部通过 ✅" : $"\n失败 {fails} 项 ❌");
        System.Environment.Exit(fails == 0 ? 0 : 1);
    }
}
