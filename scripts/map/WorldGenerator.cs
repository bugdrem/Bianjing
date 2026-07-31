using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Bianjing;

/// <summary>
/// 世界生成总控管线（批次四十九起，批次五十重排水系）：
/// ① WorldSketch 128² 草图规划（趋势/峰点/山脊/独立山 + 草图级侵蚀，纯地形无水系）→
/// ② 双线性上采样映射 1025² 顶点高度场 + 高频 fBm 细节（坡度削减，防山脚毛刺）→
/// ③ 全图 droplet 水力侵蚀（冲沟/冲积扇纹理）→
/// ④ 热侵蚀塌方松弛（磨平侵蚀残留的坡脚毛刺，保留冲沟纹理）→
/// ⑤ 水系落地：在成品地形上循坡走线（逐格水位、下限 0、湖岛自然涌现）+ 河床下压 →
/// ⑥ 树木/野物播种照旧。
/// 「主动限制」全部集中在收尾单步（ClampHeights 上下限），不侵入基础地形算法。
/// 全程纯数据操作（Map/Plants/Animals），可在后台线程运行；
/// 进度经 volatile 字段暴露给 LoadingScreen 主线程轮询。
/// </summary>
public static class WorldGenerator
{
    // ---- 进度报告（后台线程写，主线程读：volatile 保证可见性）----

    /// <summary>当前阶段文案（加载画面显示）。</summary>
    public static volatile string Stage = "";

    /// <summary>总进度 0~1（按阶段权重粗估）。</summary>
    public static volatile float Progress;

    /// <summary>生成是否完成（LoadingScreen 轮询到 true 即回调收尾）。</summary>
    public static volatile bool Done;

    /// <summary>后台异步生成：Task.Run 包一层，异常兜底记日志后仍置 Done（避免加载画面卡死）。</summary>
    public static void GenerateAsync(GameState gs)
    {
        Done = false;
        Progress = 0f;
        Task.Run(() =>
        {
            try
            {
                Generate(gs, new Random());
            }
            catch (Exception e)
            {
                GD.PushError($"世界生成异常：{e}");
            }
            finally
            {
                Done = true;
            }
        });
    }

    /// <summary>同步生成入口（headless 冒烟/测试也可直接调用）。</summary>
    public static void Generate(GameState gs, Random rng)
    {
        Report("勾画山川", 0.05f);
        var sketch = WorldSketch.Build(rng);

        Report("铺陈大地", 0.2f);
        UpsampleToHeightField(sketch, gs.Map.Height, rng);

        Report("冲刷侵蚀", 0.3f);
        HydraulicEroder.Erode(gs.Map.Height.Raw, HeightField.VertsPerSide,
            TerrainConfig.ErodeDropletsFull, TerrainConfig.ErodeBrushRadius, rng);

        Report("坡脚归整", 0.55f);
        HydraulicEroder.ThermalRelax(gs.Map.Height.Raw, HeightField.VertsPerSide);
        ClampHeights(gs.Map.Height.Raw);

        Report("引水成河", 0.7f);
        // 峰点草图坐标 ×8 放大到世界格坐标，供取河源（峰间鞍部）
        var peaks = new List<(Vector2 pos, float h)>();
        foreach (var (pos, h) in sketch.Peaks)
            peaks.Add((pos * TerrainConfig.SketchScale, h));
        RiverGenerator.BuildWaterSystem(gs.Map, peaks, rng);

        Report("播种林木", 0.85f);
        TreeGenerator.Scatter(gs, rng);

        Report("放归野物", 0.95f);
        new WildlifeSystem().SeedInitial(gs);

        Report("落成", 1f);
        PrintWorldStats(gs); // 生成指标一行日志（headless 冒烟/调参依据）
    }

    private static void Report(string stage, float progress)
    {
        Stage = stage;
        Progress = progress;
    }

    // ---- ② 上采样映射 + fBm 细节（坡度削减）----

    /// <summary>草图（128²，1 格=8m）双线性上采样到 1025² 顶点，再叠加高频 fBm 细节；
    /// 细节幅度按基础地形坡度削减（陡坡少叠噪声，专治山脚毛刺），平地细节保持。
    /// 两遍处理：先铺基础场，再读邻点坡度叠细节。</summary>
    private static void UpsampleToHeightField(WorldSketch sketch, HeightField hf, Random rng)
    {
        int s = TerrainConfig.SketchSize;
        float scale = TerrainConfig.SketchScale;
        int vps = HeightField.VertsPerSide;
        var detail = new ValueNoise(rng, TerrainConfig.DetailFbmWaveMeters, 3, vps);
        var raw = hf.Raw;

        // 第一遍：双线性上采样铺基础场
        for (int vy = 0; vy < vps; vy++)
        {
            for (int vx = 0; vx < vps; vx++)
            {
                // 草图浮点坐标（钳制在最后一格内做双线性）
                float fx = Math.Min(vx / scale, s - 1.001f);
                float fy = Math.Min(vy / scale, s - 1.001f);
                int ix = (int)fx, iy = (int)fy;
                float tx = fx - ix, ty = fy - iy;
                float a = sketch.H[iy * s + ix], b = sketch.H[iy * s + ix + 1];
                float c = sketch.H[(iy + 1) * s + ix], d = sketch.H[(iy + 1) * s + ix + 1];
                raw[vy * vps + vx] = Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
            }
        }

        // 第二遍：叠加高频细节——幅度随海拔微增（山体纹理略强）、随坡度削减（陡坡防毛刺）
        for (int vy = 0; vy < vps; vy++)
        {
            for (int vx = 0; vx < vps; vx++)
            {
                int i = vy * vps + vx;
                float h = raw[i];
                float slope = Mathf.Max(
                    Mathf.Abs(raw[Math.Min(i + 1, raw.Length - 1)] - h),
                    Mathf.Abs(raw[Math.Min(i + vps, raw.Length - 1)] - h));
                float amp = TerrainConfig.DetailFbmAmp
                    * (0.6f + 0.4f * Mathf.Clamp(h / 20f, 0f, 1f))
                    / (1f + slope * TerrainConfig.DetailSlopeDamp);
                raw[i] = h + (detail.Sample(vx, vy) - 0.5f) * 2f * amp;
            }
        }
        ClampHeights(raw);
    }

    /// <summary>全场高度钳制（收尾的「主动限制」单步，不侵入基础算法）：
    /// 上限 MaxTerrainHeight、下限 MinTerrainHeight（卷轴画布/裙板垫在其下）。</summary>
    private static void ClampHeights(float[] raw)
    {
        for (int i = 0; i < raw.Length; i++)
            raw[i] = Mathf.Clamp(raw[i], TerrainConfig.MinTerrainHeight, TerrainConfig.MaxTerrainHeight);
    }

    // ---- 生成指标（调参依据，headless 冒烟直接可读）----

    /// <summary>关键占比一行日志：山地（>5m）/ 水面 / 可用平原（非水、坡度可走、<5m）/ 最高点。</summary>
    private static void PrintWorldStats(GameState gs)
    {
        int total = MapGrid.Size * MapGrid.Size;
        int mountain = 0, water = 0, usable = 0;
        float maxH = float.MinValue;
        for (int y = 0; y < MapGrid.Size; y++)
        {
            for (int x = 0; x < MapGrid.Size; x++)
            {
                var c = new Vector2I(x, y);
                if (gs.Map.CellAt(c).HasWater)
                {
                    water++;
                    continue;
                }
                float h = gs.Map.Height.CellCenterH(c);
                maxH = Math.Max(maxH, h);
                if (h > 5f)
                    mountain++;
                else if (gs.Map.Height.CellSlopeDeg(c) <= TerrainConfig.MaxWalkSlopeDeg)
                    usable++;
            }
        }
        GD.Print($"[worldgen] 山地(>5m) {100f * mountain / total:F1}% | 水面 {100f * water / total:F1}% | " +
            $"可用平原 {100f * usable / total:F1}% | 最高 {maxH:F1}m");
    }
}
