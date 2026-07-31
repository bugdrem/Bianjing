using System;
using Godot;

namespace Bianjing;

/// <summary>
/// 新地图树木生成（密度场方案）：手写双八度 value noise 生成每格林木密度——
/// 密核处接近密林密度、边缘平滑过渡到无树，取代旧版均匀圆形树簇的"孤岛感"。
/// 播种前按抽样估算做总量校准，初始树数稳定在目标值附近，不随噪声形态波动。
/// </summary>
public static class TreeGenerator
{
    /// <summary>密度阈值：噪声值低于此线的格子无树（决定林地覆盖率）。</summary>
    private const float Threshold = PlantConfig.ForestNoiseThreshold;

    /// <summary>密核落树概率上限（棵/格）：约 0.2 已是遮天蔽日的密林观感。</summary>
    private const float MaxDensity = PlantConfig.ForestMaxDensity;

    /// <summary>初始树木目标总量（校准基准；此后由月度散播自然消长，上限 MaxPlants）。</summary>
    private const int TargetTrees = PlantConfig.InitialTreeTarget;

    public static void Scatter(GameState gs, Random rng)
    {
        // 双八度 value noise：低频定林区轮廓（64 米），高频添斑驳细节（32 米）
        var coarse = MakeLattice(rng, 64);
        var fine = MakeLattice(rng, 32);

        // 1) 抽样校准：以 1/16 抽样（双轴步长 4）估算全图落树期望，反推全局缩放系数
        double sampleSum = 0;
        for (int x = 0; x < MapGrid.Size; x += 4)
            for (int y = 0; y < MapGrid.Size; y += 4)
                sampleSum += ChanceAt(coarse, fine, x, y);
        double scale = sampleSum > 0 ? TargetTrees / (sampleSum * 16) : 0;

        // 2) 正式播种：逐格按校准后的概率落树（水面/占用格由 AddPlant 自动拦截）；
        // 噪声密度场不看海拔，图缘山带高地照常成林（高于 ForageMaxHeight 的为景观树）
        for (int x = 0; x < MapGrid.Size; x++)
        {
            for (int y = 0; y < MapGrid.Size; y++)
            {
                double chance = ChanceAt(coarse, fine, x, y) * scale;
                if (chance <= 0 || rng.NextDouble() >= Math.Min(0.5, chance))
                    continue;
                // 月龄随机（林子老幼混杂）；约每十一株出一株果树（果树:普通树≈1:10）
                gs.AddPlant(new Vector2I(x, y), 6 + rng.Next(19), rng.Next(11) == 0);
            }
        }
    }

    /// <summary>某格未校准的落树概率：双八度合成密度 v，低于阈值无树，
    /// 阈上按 1.5 次幂拉升——密核逼近 MaxDensity，边缘平滑过渡到 0。</summary>
    private static float ChanceAt(float[,] coarse, float[,] fine, int x, int y)
    {
        float v = 0.65f * SampleLattice(coarse, 64, x, y) + 0.35f * SampleLattice(fine, 32, x, y);
        if (v <= Threshold)
            return 0f;
        float t = (v - Threshold) / (1f - Threshold);
        return MaxDensity * Mathf.Pow(t, 1.5f);
    }

    /// <summary>生成一层噪声格点（间距 cellSize 米的随机值网格，多留一圈防插值越界）。</summary>
    private static float[,] MakeLattice(Random rng, int cellSize)
    {
        int n = MapGrid.Size / cellSize + 2;
        var lattice = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                lattice[i, j] = (float)rng.NextDouble();
        return lattice;
    }

    /// <summary>对噪声格点做平滑双线性插值采样（smoothstep 缓和格点棱线），返回 0-1。</summary>
    private static float SampleLattice(float[,] lattice, int cellSize, int x, int y)
    {
        float fx = (float)x / cellSize;
        float fy = (float)y / cellSize;
        int ix = (int)fx, iy = (int)fy;
        float tx = fx - ix, ty = fy - iy;
        tx = tx * tx * (3f - 2f * tx); // smoothstep
        ty = ty * ty * (3f - 2f * ty);

        float a = lattice[ix, iy], b = lattice[ix + 1, iy];
        float c = lattice[ix, iy + 1], d = lattice[ix + 1, iy + 1];
        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
    }
}
