using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Bianjing;

/// <summary>
/// 世界生成总控管线（批次四十九起，替代旧 SeedWorld 散调用）：
/// ① WorldSketch 128² 草图规划（趋势/峰点/谷线河湖/山脊 + 草图级侵蚀）→
/// ② 双线性上采样映射 1025² 顶点高度场 + 高频 fBm 细节（消上采样平滑感）→
/// ③ 全图 droplet 水力侵蚀（冲沟/冲积扇纹理）→
/// ④ 河湖落地：草图河线放大为世界样条，沿线刻水格（源细口宽）+ 湖面 + FlowDir + 河床下压 →
/// ⑤ 树木/野物播种照旧。
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

        Report("铺陈大地", 0.25f);
        UpsampleToHeightField(sketch, gs.Map.Height, rng);

        Report("冲刷侵蚀", 0.35f);
        HydraulicEroder.Erode(gs.Map.Height.Raw, HeightField.VertsPerSide,
            TerrainConfig.ErodeDropletsFull, TerrainConfig.ErodeBrushRadius, rng);
        ClampHeights(gs.Map.Height.Raw);

        Report("开凿江河", 0.7f);
        LayRiversAndLakes(gs.Map, sketch, rng);
        RiverGenerator.CarveBed(gs.Map);

        Report("播种林木", 0.85f);
        TreeGenerator.Scatter(gs, rng);

        Report("放归野物", 0.95f);
        new WildlifeSystem().SeedInitial(gs);

        Report("落成", 1f);
    }

    private static void Report(string stage, float progress)
    {
        Stage = stage;
        Progress = progress;
    }

    // ---- ② 上采样映射 + fBm 细节 ----

    /// <summary>草图（128²，1 格=8m）双线性上采样到 1025² 顶点，并叠加高频 fBm 细节；
    /// 高度统一 clamp [负河床下限, MaxTerrainHeight]。</summary>
    private static void UpsampleToHeightField(WorldSketch sketch, HeightField hf, Random rng)
    {
        int s = TerrainConfig.SketchSize;
        float scale = TerrainConfig.SketchScale;
        var detail = new ValueNoise(rng, TerrainConfig.DetailFbmWaveMeters, 3, HeightField.VertsPerSide);
        var raw = hf.Raw;

        for (int vy = 0; vy < HeightField.VertsPerSide; vy++)
        {
            for (int vx = 0; vx < HeightField.VertsPerSide; vx++)
            {
                // 草图浮点坐标（钳制在最后一格内做双线性）
                float fx = Math.Min(vx / scale, s - 1.001f);
                float fy = Math.Min(vy / scale, s - 1.001f);
                int ix = (int)fx, iy = (int)fy;
                float tx = fx - ix, ty = fy - iy;
                float a = sketch.H[iy * s + ix], b = sketch.H[iy * s + ix + 1];
                float c = sketch.H[(iy + 1) * s + ix], d = sketch.H[(iy + 1) * s + ix + 1];
                float h = Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);

                // 高频细节：幅度随海拔微增（山体纹理略强于平原），中值归零免整体抬升
                float amp = TerrainConfig.DetailFbmAmp * (0.6f + 0.4f * Mathf.Clamp(h / 20f, 0f, 1f));
                h += (detail.Sample(vx, vy) - 0.5f) * 2f * amp;

                raw[vy * HeightField.VertsPerSide + vx] = h;
            }
        }
        ClampHeights(raw);
    }

    /// <summary>全场高度钳制：上限 MaxTerrainHeight，下限 -3m（河床极限之下留裕量）。</summary>
    private static void ClampHeights(float[] raw)
    {
        for (int i = 0; i < raw.Length; i++)
            raw[i] = Mathf.Clamp(raw[i], -3f, TerrainConfig.MaxTerrainHeight);
    }

    // ---- ④ 河湖落地 ----

    /// <summary>把草图河线（×8 放大）落地为水格：沿线刻圆盘（宽度沿程渐宽）、
    /// 流向沿走线切向量化八方向；湖泊用 CarveLake 生成谐波湖缘（含湖中岛，静水）。</summary>
    private static void LayRiversAndLakes(MapGrid map, WorldSketch sketch, Random rng)
    {
        float scale = TerrainConfig.SketchScale;
        for (int r = 0; r < sketch.Rivers.Count; r++)
        {
            var pts = sketch.Rivers[r];
            bool isMain = r == 0;
            float mouthW = isMain ? WaterConfig.RiverWidthMouth : WaterConfig.BranchWidthMouth;

            // 草图点列放大后逐段插值（段长 8m，按 1m 步进补点防断链）
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var w0 = pts[i] * scale;
                var w1 = pts[i + 1] * scale;
                float t = i / (float)Math.Max(1, pts.Count - 1);
                float width = Mathf.Lerp(WaterConfig.RiverWidthSource, mouthW, t);
                byte flow = RiverGenerator.EncodeFlow(Math.Sign(w1.X - w0.X), Math.Sign(w1.Y - w0.Y));

                int steps = Mathf.CeilToInt(w0.DistanceTo(w1));
                for (int st = 0; st <= steps; st++)
                {
                    var p = w0.Lerp(w1, st / (float)Math.Max(1, steps));
                    RiverGenerator.CarveDisk(map, new Vector2I((int)p.X, (int)p.Y), width / 2f, flow);
                }
            }
        }

        // 湖泊：草图圆心/半径放大后落地（湖缘谐波与湖中岛由 CarveLake 内部处理）
        foreach (var (pos, radius) in sketch.Lakes)
            RiverGenerator.CarveLake(map, rng,
                new Vector2I((int)(pos.X * scale), (int)(pos.Y * scale)),
                (int)(radius * scale), islands: true);
    }
}
