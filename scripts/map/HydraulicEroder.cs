using System;

namespace Bianjing;

/// <summary>
/// 水力侵蚀器（经典 droplet 水滴模型，纯 C# 无第三方依赖）：
/// 随机落一滴水 → 沿地形梯度下坡流动（带方向惯性）→ 按坡度×流速×水量算携沙容量 →
/// 超容沉积 / 欠容侵蚀（笔刷摊开防尖坑）→ 逐步蒸发直至水尽或步数耗尽。
/// 大量水滴累积即冲出冲沟、山脚冲积扇等自然纹理。
/// 直接操作 float[] 高度数组（行主序），草图（128²）与全图高度场（1025²）共用同一实现；
/// 全部参数集中在 TerrainConfig（Erode* 段）。
/// </summary>
public static class HydraulicEroder
{
    /// <summary>对 size×size 高度数组执行 droplets 滴侵蚀（就地修改）。
    /// brushRadius：侵蚀/沉积摊开半径（格）——全图用 TerrainConfig.ErodeBrushRadius，草图用 1。</summary>
    public static void Erode(float[] h, int size, int droplets, int brushRadius, Random rng)
    {
        // 预生成圆形笔刷偏移与权重（线性衰减归一化）：所有滴复用，免重复分配
        var (brushOff, brushW) = MakeBrush(size, brushRadius);

        for (int i = 0; i < droplets; i++)
        {
            // 随机落点（避开最外圈，梯度采样要读邻格）
            float px = 1 + (float)rng.NextDouble() * (size - 3);
            float py = 1 + (float)rng.NextDouble() * (size - 3);
            float dirX = 0, dirY = 0;      // 流动方向（惯性保持）
            float speed = 1f, water = 1f;  // 流速与水量
            float sediment = 0f;           // 当前携沙量

            for (int life = 0; life < TerrainConfig.ErodeMaxLifetime; life++)
            {
                int ix = (int)px, iy = (int)py;
                float fx = px - ix, fy = py - iy; // 格内偏移（双线性权重）

                // 当前位置的高度与梯度（双线性插值四角）
                (float height, float gradX, float gradY) = SampleHeightGradient(h, size, ix, iy, fx, fy);

                // 方向 = 惯性保持旧向 + (1-惯性) 顺梯度下坡
                dirX = dirX * TerrainConfig.ErodeInertia - gradX * (1 - TerrainConfig.ErodeInertia);
                dirY = dirY * TerrainConfig.ErodeInertia - gradY * (1 - TerrainConfig.ErodeInertia);
                float len = MathF.Sqrt(dirX * dirX + dirY * dirY);
                if (len < 1e-6f)
                    break; // 平地无向：滴停
                dirX /= len;
                dirY /= len;

                px += dirX;
                py += dirY;
                if (px < 1 || py < 1 || px >= size - 2 || py >= size - 2)
                    break; // 出图缘即止

                // 新位置高度与落差（负=下坡）
                int nx = (int)px, ny = (int)py;
                (float newHeight, _, _) = SampleHeightGradient(h, size, nx, ny, px - nx, py - ny);
                float deltaH = newHeight - height;

                // 携沙容量：坡度越陡、流速越快、水量越大，携得越多
                float capacity = MathF.Max(-deltaH, TerrainConfig.ErodeMinSlope)
                    * speed * water * TerrainConfig.ErodeCapacityFactor;

                if (sediment > capacity || deltaH > 0)
                {
                    // 超容（或上坡撞墙）：卸沙沉积在原位置四角（双线性分摊），上坡时最多填平落差
                    float deposit = deltaH > 0
                        ? MathF.Min(deltaH, sediment)
                        : (sediment - capacity) * TerrainConfig.DepositSpeed;
                    sediment -= deposit;
                    int b = iy * size + ix;
                    h[b] += deposit * (1 - fx) * (1 - fy);
                    h[b + 1] += deposit * fx * (1 - fy);
                    h[b + size] += deposit * (1 - fx) * fy;
                    h[b + size + 1] += deposit * fx * fy;
                }
                else
                {
                    // 欠容：按侵蚀速率挖沙（不超过落差，防挖穿成反坡尖坑），笔刷摊到邻域
                    float erode = MathF.Min((capacity - sediment) * TerrainConfig.ErodeSpeed, -deltaH);
                    for (int bi = 0; bi < brushOff.Length; bi++)
                    {
                        int idx = (iy * size + ix) + brushOff[bi];
                        if (idx < 0 || idx >= h.Length)
                            continue;
                        float amount = erode * brushW[bi];
                        h[idx] -= amount;
                        sediment += amount;
                    }
                }

                // 流速随落差加速（下坡增速），水量逐步蒸发
                speed = MathF.Sqrt(MathF.Max(0f, speed * speed + (-deltaH) * TerrainConfig.ErodeGravity));
                water *= 1 - TerrainConfig.ErodeEvaporate;
                if (water < 0.01f)
                    break; // 水尽滴止
            }
        }
    }

    /// <summary>双线性插值某浮点位置的高度与梯度（读 (ix,iy) 起四角）。</summary>
    private static (float h, float gx, float gy) SampleHeightGradient(float[] h, int size, int ix, int iy, float fx, float fy)
    {
        int b = iy * size + ix;
        float h00 = h[b], h10 = h[b + 1], h01 = h[b + size], h11 = h[b + size + 1];
        // 梯度：x/y 向高差按另一轴权重插值
        float gx = (h10 - h00) * (1 - fy) + (h11 - h01) * fy;
        float gy = (h01 - h00) * (1 - fx) + (h11 - h10) * fx;
        float height = h00 * (1 - fx) * (1 - fy) + h10 * fx * (1 - fy) + h01 * (1 - fx) * fy + h11 * fx * fy;
        return (height, gx, gy);
    }

    /// <summary>圆形笔刷：半径内格的一维偏移量与线性衰减权重（总和归一）。</summary>
    private static (int[] offsets, float[] weights) MakeBrush(int size, int radius)
    {
        var offs = new System.Collections.Generic.List<int>();
        var ws = new System.Collections.Generic.List<float>();
        float sum = 0;
        for (int oy = -radius; oy <= radius; oy++)
        {
            for (int ox = -radius; ox <= radius; ox++)
            {
                float d = MathF.Sqrt(ox * ox + oy * oy);
                if (d > radius)
                    continue;
                float w = 1 - d / (radius + 1); // 中心重、边缘轻
                offs.Add(oy * size + ox);
                ws.Add(w);
                sum += w;
            }
        }
        var weights = ws.ToArray();
        for (int i = 0; i < weights.Length; i++)
            weights[i] /= sum; // 归一化：单滴挖沙总量守恒
        return (offs.ToArray(), weights);
    }
}
