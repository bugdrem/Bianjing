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
/// ④ 主峰二次隆升（侵蚀会削顶，按草图登记的目标高度余弦羽化补回）→ 高度钳制 →
/// ⑤ 水系三连：FlowRouter 在成品地形上求流向场 → RiverNetwork 提河网 → LakeGenerator 点湖泊
///    → RiverGenerator 刻水 / 按洪泛拓扑序统一水位 / 按水体尺寸下压河床 →
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

    /// <summary>生成是否失败（生成异常吞没后置真）：LoadingScreen 据此报错而非收尾进世界，
    /// 避免半成品地图被当成新世界装配（修复前异常被 catch 吞掉、Done 仍置真 → 残缺地图直接进游戏）。</summary>
    public static volatile bool Failed;

    /// <summary>生成失败原因（主线程展示用；随 Failed 一起写入，Done 置真后主线程读取即稳定）。</summary>
    public static string Error = "";

    /// <summary>后台异步生成：Task.Run 包一层，异常兜底置失败标志后仍置 Done（避免加载画面卡死），
    /// 由 LoadingScreen 区分「成功 / 失败」决定收尾方式。
    /// 种子由调用方提供（新游戏地图预览页掷定）：同种子从头重跑草图 → 与预览完全一致。</summary>
    public static void GenerateAsync(GameState gs, int seed)
    {
        Done = false;
        Failed = false;
        Error = "";
        Progress = 0f;
        Task.Run(() =>
        {
            try
            {
                Generate(gs, new Random(seed));
            }
            catch (Exception e)
            {
                Failed = true;
                Error = e.ToString();
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

        Report("铺陈大地", 0.15f);
        UpsampleToHeightField(sketch, gs.Map.Height, rng);

        Report("冲刷侵蚀", 0.28f);
        HydraulicEroder.Erode(gs.Map.Height.Raw, HeightField.VertsPerSide,
            TerrainConfig.ErodeDropletsFull, TerrainConfig.ErodeBrushRadius, rng);

        Report("坡脚归整", 0.45f);
        HydraulicEroder.ThermalRelax(gs.Map.Height.Raw, HeightField.VertsPerSide);
        ReapplyPrimaryPeaks(sketch, gs.Map.Height); // 侵蚀削顶：主峰按目标高度补回
        ClampHeights(gs.Map.Height.Raw);

        // 水系三连（批次六十九）：成品地形上求流向 → 提河网 → 点湖泊 → 刻水落盘
        Report("疏理水系", 0.56f);
        var flow = FlowRouter.Build(gs.Map.Height);

        Report("勾连河网", 0.64f);
        var rivers = RiverNetwork.Build(flow, gs.Map.Height.SampleCell, rng);

        Report("点染湖泊", 0.72f);
        var lakes = LakeGenerator.Build(gs.Map, flow, rng);

        Report("引水成河", 0.8f);
        RiverGenerator.BuildWaterSystem(gs.Map, flow, rivers, lakes);

        Report("播种林木", 0.88f);
        TreeGenerator.Scatter(gs, rng);

        Report("放归野物", 0.94f);
        new WildlifeSystem().SeedInitial(gs);

        Report("落成", 1f);
        PrintWorldStats(gs, rivers, lakes); // 生成指标一行日志（headless 冒烟/调参依据）
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

    /// <summary>主峰二次隆升（批次六十九）：水力侵蚀与热松弛会削掉峰顶，
    /// 主峰高 88~105m 是「一两张王牌」，不能任其磨平——按草图登记的目标高度补回。
    /// 做法：对主峰覆盖的顶点，把当前高度朝目标高斯面插值，权重由峰心 1 余弦渐隐到缘 0，
    /// 只抬高不压低，边缘羽化——不留硬台、不与既有山脊冲突。</summary>
    private static void ReapplyPrimaryPeaks(WorldSketch sketch, HeightField hf)
    {
        float scale = TerrainConfig.SketchScale;
        foreach (var (pos, targetH, rMeters) in sketch.PrimaryPeaks)
        {
            float cx = pos.X * scale, cy = pos.Y * scale;
            int r = Mathf.CeilToInt(rMeters);
            int vx0 = Math.Max(0, Mathf.FloorToInt(cx) - r), vx1 = Math.Min(HeightField.VertsPerSide - 1, Mathf.CeilToInt(cx) + r);
            int vy0 = Math.Max(0, Mathf.FloorToInt(cy) - r), vy1 = Math.Min(HeightField.VertsPerSide - 1, Mathf.CeilToInt(cy) + r);

            for (int vy = vy0; vy <= vy1; vy++)
            {
                for (int vx = vx0; vx <= vx1; vx++)
                {
                    float dx = vx - cx, dy = vy - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / rMeters;
                    if (d > 1f)
                        continue;
                    float target = targetH * MathF.Exp(-3f * d * d);
                    float cur = hf.VertexH(vx, vy);
                    if (cur >= target)
                        continue;
                    // 余弦羽化：峰心全量补回、峰缘渐隐，与既有地形无缝衔接
                    float w = 0.5f + 0.5f * MathF.Cos(MathF.PI * d);
                    hf.SetVertex(vx, vy, Mathf.Lerp(cur, target, w));
                }
            }
        }
    }

    /// <summary>全场高度钳制（收尾的「主动限制」单步，不侵入基础算法）：
    /// 上限 MaxTerrainHeight、下限 MinTerrainHeight（卷轴画布/裙板垫在其下）。</summary>
    private static void ClampHeights(float[] raw)
    {
        for (int i = 0; i < raw.Length; i++)
            raw[i] = Mathf.Clamp(raw[i], TerrainConfig.MinTerrainHeight, TerrainConfig.MaxTerrainHeight);
    }

    // ---- 生成指标（调参依据，headless 冒烟直接可读）----

    /// <summary>关键占比一行日志：山地（&gt;5m）/ 水面 / 可用平原（非水、坡度可走、&lt;5m）/ 最高点，
    /// 并附河道条数、湖群座数与前 5 高峰——headless 冒烟与调参的直接依据。</summary>
    private static void PrintWorldStats(GameState gs, List<RiverPath> rivers, List<LakeShape> lakes)
    {
        int total = MapGrid.Size * MapGrid.Size;
        int mountain = 0, water = 0, usable = 0;
        float maxH = float.MinValue;
        var top = new float[5];
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
                if (h > maxH)
                    maxH = h;
                // 前 5 高峰（降序插位；只统计未被水覆盖的陆地）
                if (h > top[4])
                {
                    top[4] = h;
                    for (int k = 3; k >= 0; k--)
                    {
                        if (top[k] >= top[k + 1])
                            break;
                        (top[k], top[k + 1]) = (top[k + 1], top[k]);
                    }
                }
                if (h > 5f)
                    mountain++;
                else if (gs.Map.Height.CellSlopeDeg(c) <= TerrainConfig.MaxWalkSlopeDeg)
                    usable++;
            }
        }

        int lakeCells = 0;
        foreach (var lk in lakes)
            lakeCells += lk.Cells.Count;

        GD.Print($"[worldgen] 山地(>5m) {100f * mountain / total:F1}% | 水面 {100f * water / total:F1}% " +
            $"(湖 {100f * lakeCells / total:F1}%) | 可用平原 {100f * usable / total:F1}% | " +
            $"河道 {rivers.Count} 条 / 湖 {lakes.Count} 座 | 最高 {maxH:F1}m | 前5峰 " +
            $"{top[0]:F0}/{top[1]:F0}/{top[2]:F0}/{top[3]:F0}/{top[4]:F0}m");
    }
}
