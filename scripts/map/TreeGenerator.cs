using System;
using Godot;

namespace Bianjing;

/// <summary>新地图树木生成：随机撒若干片树林（簇状），模拟城郊野地。
/// 1m 格下参数按米换算：簇数随世界面积 ×4，半径按米不变（格数 ×4），
/// 峰值密度除 16 保持每平米树数与旧版一致（树冠尺寸是米制，不除会密成一片）。</summary>
public static class TreeGenerator
{
    private const int ClusterCount = 104;
    private const int ClusterRadiusMin = 8;
    private const int ClusterRadiusMax = 24;
    private const float ClusterDensity = 0.035f;

    public static void Scatter(GameState gs, Random rng)
    {
        for (int i = 0; i < ClusterCount; i++)
        {
            var center = new Vector2I(rng.Next(MapGrid.Size), rng.Next(MapGrid.Size));
            int radius = rng.Next(ClusterRadiusMin, ClusterRadiusMax + 1);

            for (int x = center.X - radius; x <= center.X + radius; x++)
            {
                for (int y = center.Y - radius; y <= center.Y + radius; y++)
                {
                    var c = new Vector2I(x, y);
                    if (!MapGrid.InBounds(c))
                        continue;

                    // 圆形衰减：越靠近簇心越密
                    float dist = new Vector2(x - center.X, y - center.Y).Length();
                    if (dist > radius)
                        continue;
                    float chance = ClusterDensity * (1f - dist / (radius + 1f));
                    if (rng.NextDouble() >= chance)
                        continue;

                    // 植物实体入图：月龄随机，新图林子老幼混杂；约每十一株出一株果树（果树:普通树≈1:10）
                    gs.AddPlant(c, 6 + rng.Next(19), rng.Next(11) == 0);
                }
            }
        }
    }
}
