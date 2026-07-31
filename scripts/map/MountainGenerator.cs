using System;
using Godot;

namespace Bianjing;

/// <summary>
/// 新地图地形成形（水系之前运行一次，顶点高度场随存档保存）：
/// ① 图缘山带——随机两条相邻图缘向内的 L 形基底隆起（约覆盖半图），另半图保持平原；
/// ② 连绵山脉——若干条蠕蜒脊线集中在山带内叠加（脊顶 30~64m），沿脊高低起伏，
///    余弦剖面中腰坡 ≈51° 远超可走上限——高山不可攀，成天然屏障，村民仅山脚缓坡可达；
/// ③ 平原缓丘——双八度 value noise 阈上平滑隆起（0~HillAmplitude 米），高低差连续无台阶。
/// 连续高度场天然平滑，无需旧整数台地时代的削壁/侵蚀收尾；平地占比由 HillThreshold 控制。
/// 水系其后生成并按深度下压河床（山体被河切穿处自然成峡谷）。
/// </summary>
public static class MountainGenerator
{
    public static void Raise(MapGrid map, Random rng)
    {
        var hf = map.Height;
        int corner = rng.Next(4); // 山带所依的角：0=西北 1=东北 2=东南 3=西南（决定两条相邻图缘）
        RaiseBelt(hf, rng, corner);   // 图缘山带基底先铺：半图群山半图平原的大格局
        RaiseRanges(hf, rng, corner); // 脊线山脉叠在带内基底上，成连绵群山
        RaiseHills(hf, rng);
    }

    /// <summary>顶点到山带所依两条图缘的距离（米）：取两缘距离的较小者，越小越深入山区。</summary>
    private static float EdgeDist(int corner, int vx, int vy)
    {
        int last = HeightField.VertsPerSide - 1;
        float dx = (corner == 0 || corner == 3) ? vx : last - vx; // 西缘 / 东缘
        float dy = (corner == 0 || corner == 1) ? vy : last - vy; // 北缘 / 南缘
        return Math.Min(dx, dy);
    }

    /// <summary>① 图缘山带基底：两条相邻图缘向内 BeltDepth 米的 L 形地带平滑隆起（缘高 BeltBaseHeight → 带界 0），
    /// 带界由噪声推拉蜿蜒，基底高度另叠大尺度起伏噪声免成均匀斜坡；纯地貌造型，不考虑通行。</summary>
    private static void RaiseBelt(HeightField hf, Random rng, int corner)
    {
        var edgeNoise = MakeLattice(rng, TerrainConfig.BeltNoiseWave);   // 带界扭曲
        var reliefNoise = MakeLattice(rng, 64);                          // 基底起伏调制
        for (int vx = 0; vx < HeightField.VertsPerSide; vx++)
        {
            for (int vy = 0; vy < HeightField.VertsPerSide; vy++)
            {
                // 带界噪声推拉 ±BeltNoiseAmp：山缘蜿蜒成自然山脚线
                float d = EdgeDist(corner, vx, vy)
                    + (SampleLattice(edgeNoise, TerrainConfig.BeltNoiseWave, vx, vy) - 0.5f) * 2f * TerrainConfig.BeltNoiseAmp;
                if (d >= TerrainConfig.BeltDepth)
                    continue;
                float t = 1f - Math.Max(0f, d) / TerrainConfig.BeltDepth;
                t = t * t * (3f - 2f * t); // smoothstep：山脚接平原无棱线
                // 起伏调制 0.6~1.3：基底本身高低起伏，免成单调斜面
                float relief = 0.6f + 0.7f * SampleLattice(reliefNoise, 64, vx, vy);
                float extra = TerrainConfig.BeltBaseHeight * t * relief;
                if (hf.VertexH(vx, vy) < extra)
                    hf.SetVertex(vx, vy, extra);
            }
        }
    }

    /// <summary>② 连绵山脉：脊线逐米蠕蜒推进，沿脊高度随正弦起伏（连绵而非等高），
    /// 两侧按余弦剖面连续降到平地（峰高/半宽见 TerrainConfig.RangeExtra/RangeHalfWidth）；
    /// 起点采样限在图缘山带内，脊线叠在带基底上成群山主体；陡缓由剖面自身决定，不为通行让步。</summary>
    private static void RaiseRanges(HeightField hf, Random rng, int corner)
    {
        int count = TerrainConfig.MinRanges + rng.Next(TerrainConfig.MaxRanges - TerrainConfig.MinRanges + 1);
        for (int i = 0; i < count; i++)
        {
            // 起点重采样到山带内（留 0.85 余带免贴带界）；采不中则退而求其次用最后一次采样
            double px = 0, py = 0;
            for (int tries = 0; tries < 40; tries++)
            {
                px = 40 + rng.Next(MapGrid.Size - 80);
                py = 40 + rng.Next(MapGrid.Size - 80);
                if (EdgeDist(corner, (int)px, (int)py) < TerrainConfig.BeltDepth * 0.85f)
                    break;
            }
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

    /// <summary>山脊横断面：以 (cx,cy) 为脊心，半宽 RangeHalfWidth 内的顶点按余弦剖面抬高
    /// （中心 peak → 缘 0，中腰坡 ≈51° 不可走，高山即屏障），峰值 clamp 世界上限。</summary>
    private static void RaiseRidgeBand(HeightField hf, int cx, int cy, float peak)
    {
        int hw = TerrainConfig.RangeHalfWidth;
        for (int ox = -hw; ox <= hw; ox++)
            for (int oy = -hw; oy <= hw; oy++)
            {
                float d = new Vector2(ox, oy).Length() / hw;
                if (d > 1f)
                    continue;
                // 余弦剖面：顶平缓、中腰最陡（≈πp/2hw）、山脚渐平，比二次 falloff 更像山体
                float extra = Mathf.Min(peak, TerrainConfig.MaxTerrainHeight)
                    * (0.5f + 0.5f * Mathf.Cos(Mathf.Pi * d));
                int vx = cx + ox, vy = cy + oy;
                float h = hf.VertexH(vx, vy);
                if (h < extra)
                    hf.SetVertex(vx, vy, extra); // 取高不叠加：脊带重叠处平滑衔接
            }
    }

    /// <summary>③ 平原缓丘：双八度 value noise（与树木密度场同款手法），阈上部分平滑映射为 0~HillAmplitude 米附加高度；
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
