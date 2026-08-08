using System;
using Godot;

namespace Bianjing;

/// <summary>建造合法性校验。</summary>
public static class PlacementValidator
{
    public static bool CanPlaceRoad(GameState gs, Vector2I c, RoadKind kind = RoadKind.Side)
    {
        if (!gs.PrinceMansionBuilt)
            return false; // 开局首建王爷府前锁定道路
        if (!MapGrid.InBounds(c))
            return false;
        ref var cell = ref gs.Map.CellAt(c);
        // 跨水：无桥的水面格可自动架同宽小桥（预览显示可放），按桥价校验余额
        if (cell.HasWater)
            return !cell.HasBridge && (GameSettings.InfiniteMoney || gs.Money >= GameState.BridgeCost);
        if (!cell.IsEmpty)
            // 高级覆盖低级（批次八十）：已有更低等级路面可升级重铺（主→辅/小、辅→小）；建筑/桥面/同级不覆盖
            return cell.HasRoad && !cell.HasBridge
                && GameState.RoadRank(kind) > GameState.RoadRank(cell.RoadKind)
                && (GameSettings.InfiniteMoney || gs.Money >= GameState.RoadCostOf(kind));
        if (!SlopeWalkable(gs, c)) // 陡壁不可铺路（坡度≤上限才能修路供村民翻山）
            return false;
        return GameSettings.InfiniteMoney || gs.Money >= GameState.RoadCostOf(kind);
    }

    /// <summary>本格坡度是否可供人行/铺路：格内四角顶点最大高差换算坡角 ≤上限（不处在陡壁上）。
    /// 水邻豁免：岸沿格的顶点被河床下压拉斜，落差属水陆分界而非陡坡（下水自有桥渡把关），
    /// 否则沿河一圈全铺不了路。</summary>
    public static bool SlopeWalkable(GameState gs, Vector2I c)
    {
        Vector2I[] dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        foreach (var d in dirs)
        {
            var n = c + d;
            if (MapGrid.InBounds(n) && gs.Map.CellAt(n).HasWater)
                return true; // 贴岸格豁免坡度判定（岸坡属水陆分界）
        }
        return gs.Map.Height.CellSlopeDeg(c) <= TerrainConfig.MaxWalkSlopeDeg;
    }

    /// <summary>桥梁：只能架在没有桥的水面上。</summary>
    public static bool CanPlaceBridge(GameState gs, Vector2I c)
    {
        if (!gs.PrinceMansionBuilt)
            return false; // 开局首建王爷府前锁定桥梁
        if (!MapGrid.InBounds(c))
            return false;
        ref var cell = ref gs.Map.CellAt(c);
        return cell.HasWater && !cell.HasBridge && (GameSettings.InfiniteMoney || gs.Money >= GameState.BridgeCost);
    }

    /// <summary>建筑：里程碑已解锁、占地全部为空格、在界内、占地高差在垫基限内（落位时自动整平）、至少一边临路、钱够。
    /// 开局首建门槛：未建成王爷府前只能放王爷府本体；王爷府免临路要求（作为首建可平地直接落下，自环小路）；全局唯一者不重建。</summary>
    public static bool CanPlaceBuilding(GameState gs, BuildingDef def, Vector2I origin, bool checkCost = true)
    {
        bool isMansion = def.Id == PrinceMansionConfig.DefId;

        // 开局首建：王爷府未建成前不得放其它建筑
        if (!gs.PrinceMansionBuilt && !isMansion)
            return false;

        // 全局唯一：已存在同名唯一建筑则不得再建
        if (def.Unique && gs.CountByDef(def.Id) > 0)
            return false;

        // 里程碑未到不可建（菜单置灰外的双重保险，防热键/mod 绕过）
        if (def.MilestoneRequired > gs.MilestoneLevel)
            return false;

        // 占地逐格空地校验 + 高差汇总：最高-最低顶点超垫基限不可建（落位时整平成台面）
        float minH = float.MaxValue, maxH = float.MinValue;
        for (int x = origin.X; x < origin.X + def.SizeX; x++)
        {
            for (int y = origin.Y; y < origin.Y + def.SizeY; y++)
            {
                var c = new Vector2I(x, y);
                if (!MapGrid.InBounds(c) || !gs.Map.CellAt(c).IsEmpty)
                    return false;
                minH = Math.Min(minH, gs.Map.Height.CellMinH(c));
                maxH = Math.Max(maxH, gs.Map.Height.CellMaxH(c));
            }
        }
        if (maxH - minH > TerrainConfig.MaxBuildFlattenDiff)
            return false; // 坡地高差过大，垫基也填不平

        if (isMansion)
        {
            // 王爷府免临路（首建时全图无路可依，自带小路环），仅验钱；朝廷机构朝廷拨款不验玩家钱
            if (checkCost && !GameSettings.InfiniteMoney && def.Category == "official" && gs.Money < def.Cost)
                return false;
            return true;
        }

        if (gs.Map.FindAdjacentRoad(origin, def.SizeX, def.SizeY) == null)
            return false;

        // 朝廷机构（court）朝廷拨款营造，不校验玩家官库余额（批次七十七）
        if (checkCost && !GameSettings.InfiniteMoney && def.Category == "official" && gs.Money < def.Cost)
            return false;

        return true;
    }

    /// <summary>坊区只能划在空地上（且需先建成王爷府）；水系（河/湖）不可划入可建造区。</summary>
    public static bool CanZone(GameState gs, Vector2I c)
    {
        if (!gs.PrinceMansionBuilt || !MapGrid.InBounds(c))
            return false;
        ref var cell = ref gs.Map.CellAt(c);
        // 水系屏蔽：IsEmpty 已含 !HasWater，此处显式守卫以明意图（分区拖拽预览不高亮水格）
        return !cell.HasWater && cell.IsEmpty;
    }
}
