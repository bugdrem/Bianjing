using System;
using Godot;

namespace Bianjing;

/// <summary>
/// 新地图地形成形（水系之前运行一次，顶点高度场随存档保存）：
/// ① 连绵山脉——若干条蠕蜒脊线，沿脊高低起伏、两侧二次 falloff 降到平地，成"连绵起伏"的可走山体；
/// ② 平原缓丘——双八度 value noise 阈上平滑隆起（0~HillAmplitude 米），高低差连续无台阶；
/// ③ 桂林石峰——若干孤峰（超椭圆剖面：顶平壁陡），一格落差数米，坡度天然超上限、人不可攀。
/// 连续高度场天然平滑，无需旧整数台地时代的削壁/侵蚀收尾；平地占比由 HillThreshold 控制。
/// 水系其后生成并按深度下压河床（山体被河切穿处自然成峡谷）。
/// </summary>
public static class MountainGenerator
{
    public static void Raise(MapGrid map, Random rng)
    {
        var hf = map.Height;
        RaiseRanges(hf, rng);  // 山脉先立，缓丘可在其上叠纹理
        RaiseHills(hf, rng);
        RaisePillars(hf, rng);
    }

    /// <summary>① 连绵山脉：脊线逐米蠕蜒推进，沿脊高度随正弦起伏（连绵而非等高），
    /// 两侧按二次 falloff 连续降到平地——半宽 14m 内最多爬升 2.5m，坡度天然可走。</summary>
    private static void RaiseRanges(HeightField hf, Random rng)
    {
        int count = TerrainConfig.MinRanges + rng.Next(TerrainConfig.MaxRanges - TerrainConfig.MinRanges + 1);
        for (int i = 0; i < count; i++)
        {
            double px = 40 + rng.Next(MapGrid.Size - 80);
            double py = 40 + rng.Next(MapGrid.Size - 80);
            double angle = rng.NextDouble() * Math.PI * 2;
            int length = TerrainConfig.RangeLenMin + rng.Next(TerrainConfig.RangeLenMax - TerrainConfig.RangeLenMin + 1);
            double phase = rng.NextDouble() * Math.PI * 2;
            float peakMax = Mathf.Lerp(TerrainConfig.RangeExtraMin, TerrainConfig.RangeExtraMax, (float)rng.NextDouble());

            for (int step = 0; step < length; step++)
            {
                px += Math.Cos(angle);
                py += Math.Sin(angle);
                if (px < 2 || py < 2 || px > MapGrid.Size - 3 || py > MapGrid.Size - 3)
                    break;
                // 沿脊线起伏：峰高随 step 正弦波动，令山脉连绵起伏
                double undu = 0.5 + 0.5 * Math.Sin(step * 2 * Math.PI / TerrainConfig.RangeUndulateWave + phase);
                float peak = peakMax * (0.45f + 0.55f * (float)undu);
                RaiseRidgeBand(hf, (int)px, (int)py, peak);
                angle += (rng.NextDouble() - 0.5) * TerrainConfig.RangeWaver;
            }
        }
    }

    /// <summary>山脊横断面：以 (cx,cy) 为脊心，半宽 RangeHalfWidth 内的顶点按二次 falloff 抬高（中心 peak → 缘 0）。</summary>
    private static void RaiseRidgeBand(HeightField hf, int cx, int cy, float peak)
    {
        int hw = TerrainConfig.RangeHalfWidth;
        for (int ox = -hw; ox <= hw; ox++)
            for (int oy = -hw; oy <= hw; oy++)
            {
                float d = new Vector2(ox, oy).Length() / hw;
                if (d > 1f)
                    continue;
                float extra = peak * (1f - d) * (1f - d); // 二次 falloff 更圆润
                int vx = cx + ox, vy = cy + oy;
                float h = hf.VertexH(vx, vy);
                if (h < extra)
                    hf.SetVertex(vx, vy, extra); // 取高不叠加：脊带重叠处平滑衔接
            }
    }

    /// <summary>② 平原缓丘：双八度 value noise（与树木密度场同款手法），阈上部分平滑映射为 0~HillAmplitude 米附加高度；
    /// smoothstep 映射保证丘缘与平地连续衔接、无台阶。</summary>
    private static void RaiseHills(HeightField hf, Random rng)
    {
        int wave = TerrainConfig.HillWavelength;
        var coarse = MakeLattice(rng, wave);
        var fine = MakeLattice(rng, wave / 2);

        for (int vx = 0; vx < HeightField.VertsPerSide; vx++)
        {
            for (int vy = 0; vy < HeightField.VertsPerSide; vy++)
            {
                float v = 0.65f * SampleLattice(coarse, wave, vx, vy) + 0.35f * SampleLattice(fine, wave / 2, vx, vy);
                if (v <= TerrainConfig.HillThreshold)
                    continue;
                float t = (v - TerrainConfig.HillThreshold) / (1f - TerrainConfig.HillThreshold);
                t = t * t * (3f - 2f * t); // smoothstep：丘顶浑圆、丘脚渐平
                float extra = t * TerrainConfig.HillAmplitude;
                float h = hf.VertexH(vx, vy);
                if (h < extra)
                    hf.SetVertex(vx, vy, extra);
            }
        }
    }

    /// <summary>③ 桂林石峰：孤峰散布（避图缘），超椭圆剖面 1-(d/r)^k——k 越大顶越平、壁越陡；
    /// 峰壁一格落差数米，坡角远超上限，村民不可攀，成为纯景观地标。顶面叠细噪声免死平。</summary>
    private static void RaisePillars(HeightField hf, Random rng)
    {
        var topNoise = MakeLattice(rng, 8); // 峰顶细噪声（8m 波长，±0.5m 起伏）
        int count = TerrainConfig.MinPillars + rng.Next(TerrainConfig.MaxPillars - TerrainConfig.MinPillars + 1);
        for (int i = 0; i < count; i++)
        {
            int radius = TerrainConfig.PillarMinRadius + rng.Next(TerrainConfig.PillarMaxRadius - TerrainConfig.PillarMinRadius + 1);
            // 峰心避开图缘一圈（半径+8米）
            int cx = radius + 8 + rng.Next(MapGrid.Size - 2 * (radius + 8));
            int cy = radius + 8 + rng.Next(MapGrid.Size - 2 * (radius + 8));
            float peak = Mathf.Lerp(TerrainConfig.PillarMinHeight, TerrainConfig.PillarMaxHeight, (float)rng.NextDouble());

            for (int vx = cx - radius; vx <= cx + radius; vx++)
            {
                for (int vy = cy - radius; vy <= cy + radius; vy++)
                {
                    float d = new Vector2(vx - cx, vy - cy).Length() / radius;
                    if (d > 1f)
                        continue;
                    // 超椭圆剖面：中心 1 → 缘 0，PillarShapePower 越大顶部越平坦
                    float t = 1f - Mathf.Pow(d, TerrainConfig.PillarShapePower);
                    // 顶面细噪声：±0.5m 平滑起伏（保留"顶平"大观感，不再是死平一块）
                    float jitter = (SampleLattice(topNoise, 8, vx, vy) - 0.5f) * t;
                    float h = hf.VertexH(vx, vy) + peak * t + jitter;
                    if (h > hf.VertexH(vx, vy))
                        hf.SetVertex(vx, vy, Mathf.Min(h, TerrainConfig.MaxTerrainHeight));
                }
            }
        }
    }

    // ---- value noise 工具（与 TreeGenerator 同款手法，本文件自持一份免跨类耦合）----

    /// <summary>噪声格点阵（间距 cellSize 米，多留一圈防插值越界）。</summary>
    private static float[,] MakeLattice(Random rng, int cellSize)
    {
        int n = MapGrid.Size / cellSize + 2;
        var lattice = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                lattice[i, j] = (float)rng.NextDouble();
        return lattice;
    }

    /// <summary>平滑双线性采样（smoothstep 缓和格点棱线），返回 0-1。</summary>
    private static float SampleLattice(float[,] lattice, int cellSize, int x, int y)
    {
        float fx = (float)x / cellSize;
        float fy = (float)y / cellSize;
        int ix = (int)fx, iy = (int)fy;
        float tx = fx - ix, ty = fy - iy;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);

        float a = lattice[ix, iy], b = lattice[ix + 1, iy];
        float c = lattice[ix, iy + 1], d = lattice[ix + 1, iy + 1];
        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
    }
}
