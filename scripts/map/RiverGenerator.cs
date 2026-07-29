using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 新地图水系生成：一条自西向东曲折贯穿的主河（汴河意象）+ 2-4 条支流 + 3-6 个不规则湖泊。
/// 主河/支流共用「游走点沿途刻圆盘」的雕刻方式；支流从主河中心线取源，撞上其它水体即视为汇流。
/// 水面按格随存档保存（SaveService），生成器只在新图时运行一次。
/// </summary>
public static class RiverGenerator
{
    public static void Carve(MapGrid map, Random rng)
    {
        // 1) 主河：贯穿全图，沿途记录中心线点列供支流取源
        var spine = CarveMainRiver(map, rng);

        // 2) 支流：2-4 条，从主河随机点垂直出发，比主河窄且更蜿蜒
        int branches = 2 + rng.Next(3);
        for (int i = 0; i < branches; i++)
            CarveBranch(map, rng, spine);

        // 3) 湖泊：3-6 个不规则水面，与河重叠即自然相连（不做特殊处理）
        int lakes = 3 + rng.Next(4);
        for (int i = 0; i < lakes; i++)
            CarveLake(map, rng);
    }

    /// <summary>主河：自西向东曲折走势，宽 8-12 米（沿用旧版观感）；返回中心线点列。</summary>
    private static List<Vector2I> CarveMainRiver(MapGrid map, Random rng)
    {
        var spine = new List<Vector2I>();
        int y = MapGrid.Size / 4 + rng.Next(MapGrid.Size / 2);
        int width = 8 + rng.Next(5);

        for (int x = 0; x < MapGrid.Size; x++)
        {
            // 随机曲折：小概率上下摆动，偶尔变宽变窄
            if (rng.NextDouble() < 0.35)
                y += rng.Next(-1, 2);
            y = Math.Clamp(y, 16, MapGrid.Size - 17);
            if (rng.NextDouble() < 0.05)
                width = 8 + rng.Next(5);

            for (int dy = 0; dy < width; dy++)
            {
                var c = new Vector2I(x, y + dy);
                if (MapGrid.InBounds(c))
                    map.CellAt(c).HasWater = true;
            }
            spine.Add(new Vector2I(x, y + width / 2));
        }
        return spine;
    }

    /// <summary>支流：从主河中心线随机点出发，初始方向垂直主河（随机一侧），宽 4-6 米；
    /// 逐米步进沿途刻圆盘，方向角随机摆动（比主河蜿蜒）；出图或撞上其它水体（离源头 >32 米，视为汇流）即止。</summary>
    private static void CarveBranch(MapGrid map, Random rng, List<Vector2I> spine)
    {
        var src = spine[rng.Next(spine.Count)];
        int width = 4 + rng.Next(3);
        double angle = rng.Next(2) == 0 ? -Math.PI / 2 : Math.PI / 2;
        double px = src.X, py = src.Y;
        int length = 128 + rng.Next(257); // 行进 128-384 米

        for (int step = 0; step < length; step++)
        {
            px += Math.Cos(angle);
            py += Math.Sin(angle);
            var c = new Vector2I((int)px, (int)py);
            if (!MapGrid.InBounds(c))
                return; // 出图即止
            // 汇流检测：探测点要在自身圆盘前缘之外（圆盘半径大于步长，否则会撞上自己刚刻的水面）
            var probe = new Vector2I(
                (int)(px + Math.Cos(angle) * (width / 2f + 1.5)),
                (int)(py + Math.Sin(angle) * (width / 2f + 1.5)));
            if (step > 32 && MapGrid.InBounds(probe) && map.CellAt(probe).HasWater)
                return; // 汇入其它河/湖，停笔（前 32 米刚离主河，不算撞水）
            CarveDisk(map, c, width / 2f);
            angle += (rng.NextDouble() - 0.5) * 0.35; // 支流摆动幅度比主河大
        }
    }

    /// <summary>湖泊：随机湖心（距图边 ≥24 米），由 3 个随机偏移圆（半径 8-20 米）叠成不规则水面。</summary>
    private static void CarveLake(MapGrid map, Random rng)
    {
        var center = new Vector2I(24 + rng.Next(MapGrid.Size - 48), 24 + rng.Next(MapGrid.Size - 48));
        for (int i = 0; i < 3; i++)
        {
            float radius = 8 + rng.Next(13);
            var off = new Vector2I(rng.Next(-8, 9), rng.Next(-8, 9));
            CarveDisk(map, center + off, radius);
        }
    }

    /// <summary>以 center 为圆心刻一片水面圆盘（半径 radius 米），越界格自动跳过。</summary>
    private static void CarveDisk(MapGrid map, Vector2I center, float radius)
    {
        int r = Mathf.CeilToInt(radius);
        for (int ox = -r; ox <= r; ox++)
        {
            for (int oy = -r; oy <= r; oy++)
            {
                if (ox * ox + oy * oy > radius * radius)
                    continue;
                var c = center + new Vector2I(ox, oy);
                if (MapGrid.InBounds(c))
                    map.CellAt(c).HasWater = true;
            }
        }
    }
}
