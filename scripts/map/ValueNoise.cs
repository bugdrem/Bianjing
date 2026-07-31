using System;

namespace Bianjing;

/// <summary>
/// 多八度 value noise（fBm）：格点阵 + smoothstep 双线性插值，逐八度波长减半、幅度减半。
/// 供 WorldSketch（平原缓起伏）与 WorldGenerator（全图高频细节）共用；
/// TreeGenerator 沿用自持实现不动（历史稳定，密度校准依赖既有手法）。
/// </summary>
public class ValueNoise
{
    private readonly float[][,] _lattices; // 每八度一张格点阵
    private readonly int[] _waves;         // 每八度的格点间距（采样域格）

    /// <summary>构造：waveCells=首八度波长（采样域格数）、octaves=八度数、domainCells=采样域边长。</summary>
    public ValueNoise(Random rng, int waveCells, int octaves, int domainCells)
    {
        _lattices = new float[octaves][,];
        _waves = new int[octaves];
        int wave = waveCells;
        for (int o = 0; o < octaves; o++)
        {
            wave = Math.Max(2, wave);
            int n = domainCells / wave + 3; // 多留两格防插值越界
            var lattice = new float[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    lattice[i, j] = (float)rng.NextDouble();
            _lattices[o] = lattice;
            _waves[o] = wave;
            wave /= 2;
        }
    }

    /// <summary>采样 0~1（各八度加权平均：幅度逐层减半）。</summary>
    public float Sample(float x, float y)
    {
        float sum = 0, amp = 1, ampSum = 0;
        for (int o = 0; o < _lattices.Length; o++)
        {
            sum += amp * SampleOctave(o, x, y);
            ampSum += amp;
            amp *= 0.5f;
        }
        return sum / ampSum;
    }

    /// <summary>单八度平滑双线性采样（smoothstep 缓和格点棱线）。</summary>
    private float SampleOctave(int o, float x, float y)
    {
        var lattice = _lattices[o];
        float fx = x / _waves[o], fy = y / _waves[o];
        int ix = (int)fx, iy = (int)fy;
        // 钳制到阵内（采样域边缘防越界）
        ix = Math.Clamp(ix, 0, lattice.GetLength(0) - 2);
        iy = Math.Clamp(iy, 0, lattice.GetLength(1) - 2);
        float tx = Math.Clamp(fx - ix, 0f, 1f), ty = Math.Clamp(fy - iy, 0f, 1f);
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        float a = lattice[ix, iy], b = lattice[ix + 1, iy];
        float c = lattice[ix, iy + 1], d = lattice[ix + 1, iy + 1];
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }
}
