using System;
using Godot;

namespace Bianjing;

/// <summary>新地图河流生成：一条自西向东曲折走势的大河（汴河意象），宽 8-12 米，需架桥通行。</summary>
public static class RiverGenerator
{
    public static void Carve(MapGrid map, Random rng)
    {
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
                if (!MapGrid.InBounds(c))
                    continue;
                map.CellAt(c).HasWater = true;
            }
        }
    }
}
