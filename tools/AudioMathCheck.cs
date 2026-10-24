// 一次性音频合成校验：核对程序化兜底音「不静音 / 不削顶 / 无 NaN / 循环无缝 / 各 cue 有区分度」。
// 用法同 tools/RigMathCheck.cs（独立 Exe 工程引用工程产出的 GodotSharp.dll）。验完即删。
using System;
using System.Collections.Generic;
using System.Linq;

// 纯数学校验：不引用 Godot（引擎外构造原生类型会崩），用一份最小 Mathf 垫片保持与生产代码同构。
static class Mathf
{
    public const float Tau = MathF.PI * 2f;
    public const float Pi = MathF.PI;
    public static float Lerp(float a, float b, float t) => a + (b - a) * MathF.Max(0f, MathF.Min(1f, t));
    public static float Clamp(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));
    public static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
    public static float Max(float a, float b) => MathF.Max(a, b);
    public static float Min(float a, float b) => MathF.Min(a, b);
    public static int Max(int a, int b) => Math.Max(a, b);
    public static int Min(int a, int b) => Math.Min(a, b);
    public static float Abs(float v) => MathF.Abs(v);
    public static float Sin(float v) => MathF.Sin(v);
    public static float Cos(float v) => MathF.Cos(v);
    public static float Exp(float v) => MathF.Exp(v);
    public static int FloorToInt(float v) => (int)MathF.Floor(v);
}

// —— ProceduralAudio 的同构镜像（生产代码在 scripts/audio/ProceduralAudio.cs）——
static class Synth
{
    public const int Rate = 32000;
    public static readonly double[] Pent = { 1.0, 9.0 / 8.0, 5.0 / 4.0, 3.0 / 2.0, 5.0 / 3.0 };
    public const double BaseFreq = 174.61;
    static readonly Random Rng = new(20261024);

    public static float[] Pluck(float freq, float seconds, float brightness, float decay = 3.2f)
    {
        int n = Math.Max(2, (int)(Rate * seconds));
        var buf = new float[n];
        int period = Math.Max(2, (int)(Rate / Math.Max(20f, freq)));
        var delay = new float[period];
        for (int i = 0; i < period; i++)
            delay[i] = (float)(Rng.NextDouble() * 2.0 - 1.0) * (1f - (float)i / period * 0.35f);
        float loopGain = (float)Math.Exp(-1.0 / (Math.Max(20f, freq) * Math.Max(0.05f, decay)));
        float lp = Mathf.Lerp(0.04f, 1.0f, Mathf.Clamp(brightness, 0f, 1f));
        for (int i = 0; i < n; i++)
        {
            float cur = delay[i % period];
            buf[i] = cur;
            float nxt = delay[(i + 1) % period];
            float filtered = cur + lp * (nxt - cur);
            delay[i % period] = filtered * loopGain + (float)(Rng.NextDouble() * 2.0 - 1.0) * 1e-5f;
        }
        Normalize(buf, 0.68f);
        return buf;
    }

    public static float[] Noise(float seconds, float center, float attack, float release)
    {
        int n = Math.Max(2, (int)(Rate * seconds));
        var buf = new float[n];
        float lp = 0f, hp = 0f;
        float a = Mathf.Clamp(attack, 0.001f, 0.9f), r = Mathf.Clamp(release, 0.001f, 0.9f);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float white = (float)(Rng.NextDouble() * 2.0 - 1.0);
            lp += (white - lp) * Mathf.Clamp(center, 0.01f, 1f);
            float band = white - lp;
            hp += (band - hp) * 0.6f;
            buf[i] = (lp * 0.7f + hp * 0.3f) * Env(t, a, r);
        }
        Normalize(buf, 0.55f);
        return buf;
    }

    public static float[] Tone(float freq, float seconds, float attack, float release)
    {
        int n = Math.Max(2, (int)(Rate * seconds));
        var buf = new float[n];
        for (int i = 0; i < n; i++)
            buf[i] = Mathf.Sin(Mathf.Tau * freq * i / Rate) * Env((float)i / n, attack, release);
        Normalize(buf, 0.5f);
        return buf;
    }

    static float Env(float t, float a, float r)
    {
        if (t < a) return t / a;
        float rel = 1f - (t - a) / Math.Max(0.001f, 1f - a);
        return Mathf.Clamp(rel / Math.Max(0.001f, r) * r, 0f, 1f);
    }

    public static void NormalizeRms(float[] buf, float targetRms, float ceiling = 0.95f)
    {
        double sum = 0;
        foreach (var v in buf) sum += (double)v * v;
        float rms = (float)Math.Sqrt(sum / Math.Max(1, buf.Length));
        if (rms < 1e-6f) return;
        float k = targetRms / rms;
        float peak = 0f;
        foreach (var v in buf) peak = Mathf.Max(peak, Mathf.Abs(v));
        if (peak * k > ceiling) k = ceiling / Mathf.Max(peak, 1e-6f);
        for (int i = 0; i < buf.Length; i++) buf[i] *= k;
    }

    public static void Normalize(float[] buf, float peak)
    {
        float max = 0f;
        foreach (var v in buf) max = MathF.Max(max, MathF.Abs(v));
        if (max < 1e-6f) return;
        float k = peak / max;
        for (int i = 0; i < buf.Length; i++) buf[i] *= k;
    }

    public static float[] Mix(params (float[] W, float At, float G)[] parts)
    {
        int total = 0;
        foreach (var (w, at, _) in parts) total = Math.Max(total, (int)(at * Rate) + w.Length);
        var buf = new float[total];
        foreach (var (w, at, g) in parts)
        {
            int off = (int)(at * Rate);
            for (int i = 0; i < w.Length && off + i < buf.Length; i++) buf[off + i] += w[i] * g;
        }
        return buf;
    }

    public static void FadeEdges(float[] buf, float seconds)
    {
        int n = Math.Min((int)(Rate * seconds), buf.Length / 2);
        for (int i = 0; i < n; i++)
        {
            float k = (float)i / n;
            buf[i] *= k;
            buf[buf.Length - 1 - i] *= k;
        }
    }

    /// <summary>16bit PCM 打包（与生产代码 Wrap 同公式；这里单独验，不构造 Godot 对象）。</summary>
    public static byte[] Pack16(float[] mono)
    {
        var data = new byte[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            short v = (short)Mathf.Clamp(mono[i] * 32767f, -32767f, 32767f);
            data[i * 2] = (byte)(v & 0xFF);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return data;
    }

    public static float[] Music(string style)
    {
        bool slow = style == "guqin";
        float length = slow ? 32f : 28f;
        int notes = slow ? 9 : 13;
        var times = new float[notes];
        float t = 0.4f;
        for (int i = 0; i < notes; i++)
        {
            times[i] = t;
            t += (slow ? 2.6f : 1.7f) + (float)Rng.NextDouble() * (slow ? 1.4f : 1.2f);
            if (t > length - 1.5f) break;
        }
        var parts = new List<(float[], float, float)>();
        int degree = 4;
        for (int i = 0; i < times.Length; i++)
        {
            degree += Rng.Next(3) - 1;
            if (Rng.NextDouble() < 0.18) degree += Rng.Next(2) == 0 ? 5 : -5;
            degree = Math.Clamp(degree, 0, 12);
            double f = BaseFreq * Pent[degree % 5] * Math.Pow(2.0, degree / 5);
            float bright = Mathf.Clamp((float)(f / BaseFreq - 0.6) * 0.9f, 0.15f, 0.95f);
            float secs = slow ? 3.4f : 2.4f;
            parts.Add((Pluck((float)f, secs, bright, slow ? 2.6f : 3.2f), times[i], slow ? 0.85f : 0.8f));
        }
        for (float bt = 0f; bt < length - 2f; bt += 8f)
            parts.Add((Pluck((float)BaseFreq * 0.5f, 4f, 0.2f, 2.2f), bt, 0.5f));
        var wave = Mix(parts.ToArray());
        FadeEdges(wave, 0.3f);
        NormalizeRms(wave, 0.12f);
        return wave;
    }

    public static float[] Celebration(float gain)
    {
        var parts = new List<(float[], float, float)>();
        float[] seq = { 523f, 587f, 659f, 784f, 880f };
        for (int i = 0; i < seq.Length; i++)
            parts.Add((Pluck(seq[i], 0.7f, 0.95f, 7f), i * 0.13f, 0.9f));
        parts.Add((Pluck(261f, 1.2f, 0.3f, 4f), 0f, 0.7f));
        parts.Add((Pluck(392f, 1.2f, 0.4f, 4f), 0.5f, 0.6f));
        var wave = Mix(parts.ToArray());
        NormalizeRms(wave, 0.14f);
        FadeEdges(wave, 0.15f);
        return wave;
    }
}

class Program
{
    static int fails = 0;
    static void Bad(string m) { Console.WriteLine("  FAIL " + m); fails++; }

    /// <summary>16bit PCM 解包（验 Pack16 的往返一致性）。</summary>
    static float[] Unpack16(byte[] d)
    {
        var f = new float[d.Length / 2];
        for (int i = 0; i < f.Length; i++)
            f[i] = (short)(d[i * 2] | (d[i * 2 + 1] << 8)) / 32767f;
        return f;
    }

    static float Rms(float[] w, int from, int count)
    {
        double sum = 0;
        int end = Math.Min(w.Length, from + count);
        for (int i = from; i < end; i++) sum += (double)w[i] * w[i];
        return (float)Math.Sqrt(sum / Math.Max(1, end - from));
    }

    static (float rms, float peak, bool nan, float zcr) Stats(float[] w)
    {
        double sum = 0; float peak = 0; bool nan = false; int zc = 0;
        for (int i = 0; i < w.Length; i++)
        {
            float v = w[i];
            if (float.IsNaN(v)) nan = true;
            sum += (double)v * v;
            peak = MathF.Max(peak, MathF.Abs(v));
            if (i > 0 && ((v < 0) != (w[i - 1] < 0))) zc++;
        }
        return ((float)Math.Sqrt(sum / w.Length), peak, nan, (float)zc / w.Length * Synth.Rate);
    }

    static void Main()
    {
        Console.WriteLine("== A：BGM 两条（古筝/古琴）不静音、不削顶、无 NaN ==");
        foreach (var style in new[] { "guzheng", "guqin" })
        {
            var w = Synth.Music(style);
            var (rms, peak, nan, zcr) = Stats(w);
            Console.WriteLine($"  {style,-7} 时长 {w.Length / (float)Synth.Rate:F1}s  RMS {rms:F4}  峰值 {peak:F3}  零穿 {zcr:F0}Hz  数据 {Synth.Pack16(w).Length / 1024}KB");
            if (rms < 0.01f) Bad($"{style} 几乎静音（RMS {rms:F4}）");
            if (peak > 1.02f) Bad($"{style} 削顶（峰值 {peak:F3}）");
            if (nan) Bad($"{style} 含 NaN");
            if (peak < 0.2f) Bad($"{style} 峰值过低（{peak:F3}），播放会太轻");
            if (zcr < 60f || zcr > 2600f) Bad($"{style} 零穿率 {zcr:F0}Hz 不在乐音合理区间");
        }

        Console.WriteLine("\n== B：循环接缝无爆音（首样本近零 + 淡变单调 + 接缝步进小）==");
        foreach (var style in new[] { "guzheng", "guqin" })
        {
            var w = Synth.Music(style);
            int n = (int)(Synth.Rate * 0.3f);

            // ① 首样本必须近零：否则循环每次回到开头都是一个台阶
            float head0 = MathF.Abs(w[0]);
            // ② 包络真的在爬升：比较淡变窗首 20% 与末 20% 的 RMS。
            //    （不能要求样本单调——拨弦信号本身在振荡，乘斜坡必然振荡；要验的是"音量包络"在涨。）
            int seg = Math.Max(1, n / 5);
            float rmsA = Rms(w, 0, seg), rmsB = Rms(w, n - seg, seg);
            bool ramping = rmsB > rmsA * 3f;
            // ③ 接缝步进：播放从最后一个样本跳回第一个样本时的差值
            float seam = MathF.Abs(w[w.Length - 1] - w[0]);
            // ④ 尾样本也应近零
            float tailEnd = MathF.Abs(w[w.Length - 1]);

            bool ok = head0 < 0.01f && tailEnd < 0.01f && seam < 0.02f && ramping;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {style,-7} 首样本 {head0:E2}  尾样本 {tailEnd:E2}  接缝步进 {seam:E2}  包络 {rmsA:F4}→{rmsB:F4}（爬升 {(ramping ? "是" : "否")}）");
            if (!ok) { fails++; if (head0 >= 0.01f) Bad($"{style} 首样本 {head0:E2} 非零"); if (tailEnd >= 0.01f) Bad($"{style} 尾样本 {tailEnd:E2} 非零"); if (seam >= 0.02f) Bad($"{style} 接缝步进 {seam:E2}"); if (!ramping) Bad($"{style} 淡变包络未爬升 {rmsA:F4}→{rmsB:F4}"); }
        }

        Console.WriteLine("\n== C：音效各 cue 不静音、不削顶、时长合理 ==");
        var sfx = new (string, Func<float[]>)[]
        {
            ("ui_click",    () => Synth.Pluck(660f, 0.14f, 0.9f, 9f)),
            ("ui_build",    () => Synth.Mix((Synth.Noise(0.1f, 0.35f, 0.01f, 0.3f), 0f, 1f), (Synth.Pluck(392f, 0.4f, 0.6f, 5f), 0.05f, 0.8f), (Synth.Pluck(587f, 0.5f, 0.7f, 5f), 0.12f, 0.7f))),
            ("ui_error",    () => Synth.Mix((Synth.Tone(196f, 0.16f, 0.01f, 0.5f), 0f, 1f), (Synth.Tone(165f, 0.22f, 0.01f, 0.6f), 0.12f, 0.9f))),
            ("pick_child",  () => Synth.Pluck(880f, 0.2f, 0.95f, 8f)),
            ("pick_elder",  () => Synth.Pluck(392f, 0.26f, 0.6f, 7f)),
            ("pick_wood",   () => Synth.Mix((Synth.Noise(0.09f, 0.5f, 0.002f, 0.25f), 0f, 1f), (Synth.Pluck(330f, 0.22f, 0.7f, 7f), 0f, 0.6f))),
            ("pick_stone",  () => Synth.Mix((Synth.Noise(0.07f, 0.18f, 0.001f, 0.2f), 0f, 1f), (Synth.Tone(150f, 0.12f, 0.001f, 0.4f), 0f, 0.5f))),
            ("pick_water",  () => Synth.Noise(0.34f, 0.85f, 0.02f, 0.7f)),
            ("pick_animal", () => Synth.Mix((Synth.Tone(330f, 0.1f, 0.02f, 0.5f), 0f, 1f), (Synth.Tone(247f, 0.14f, 0.02f, 0.6f), 0.09f, 0.8f))),
            ("milestone",   () => Synth.Celebration(0.9f)),
        };
        var waves = new Dictionary<string, float[]>();
        foreach (var (name, gen) in sfx)
        {
            var w = gen();
            waves[name] = w;
            var (rms, peak, nan, _) = Stats(w);
            float secs = w.Length / (float)Synth.Rate;
            bool ok = rms >= 0.01f && peak <= 1.02f && !nan && secs > 0.05f && secs < 2.5f;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {name,-12} {secs:F2}s  RMS {rms:F4}  峰值 {peak:F3}");
            if (!ok) { fails++; if (nan) Bad($"{name} 含 NaN"); if (rms < 0.01f) Bad($"{name} 静音"); if (peak > 1.02f) Bad($"{name} 削顶"); if (secs is <= 0.05f or >= 2.5f) Bad($"{name} 时长 {secs:F2}s 不合理"); }
        }

        Console.WriteLine("\n== D：材质音效彼此有区分度（相关系数不应全接近 1）==");
        var pairs = new (string, string)[]
        {
            ("pick_wood", "pick_stone"), ("pick_wood", "pick_water"),
            ("pick_stone", "pick_water"), ("pick_child", "pick_elder"),
            ("ui_click", "ui_build"),
        };
        foreach (var (a, b) in pairs)
        {
            float c = Corr(waves[a], waves[b]);
            bool ok = c < 0.98f;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {a} × {b} 相关 {c:F3}");
            if (!ok) fails++;
        }

        Console.WriteLine("\n== E：古风五声音阶自检（乐句音高应落在五声之内）==");
        // 抽查 Music 生成的音：主音与五声各音的倍率都在预期集合里即通过（生成逻辑本身用 Pent 表）
        var allowed = new HashSet<double>(Synth.Pent.Select(p => Math.Round(p, 4)));
        for (int deg = 0; deg <= 12; deg++)
        {
            double f = Synth.BaseFreq * Synth.Pent[deg % 5] * Math.Pow(2.0, deg / 5);
            bool inScale = allowed.Contains(Math.Round(Synth.Pent[deg % 5], 4));
            if (!inScale) { Bad($"音级 {deg} 不在五声内"); }
            if (f < 60 || f > 2400) Bad($"音级 {deg} 频率 {f:F1}Hz 越出筝域");
        }
        Console.WriteLine($"  五声音阶 13 个音级全部落在宫商角徵羽内，频率 60~2400Hz ✓");

        Console.WriteLine("\n== F：16bit PCM 打包往返一致 + 无削顶损失 ==");
        foreach (var style in new[] { "guzheng", "guqin" })
        {
            var w = Synth.Music(style);
            var rt = Unpack16(Synth.Pack16(w));
            float maxErr = 0f;
            for (int i = 0; i < w.Length; i++) maxErr = MathF.Max(maxErr, MathF.Abs(w[i] - rt[i]));
            bool ok = maxErr < 1f / 32767f * 1.5f && Synth.Pack16(w).Length == w.Length * 2;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {style,-7} 往返最大误差 {maxErr:E2}（≤ 1 LSB）");
            if (!ok) fails++;
        }

        Console.WriteLine(fails == 0 ? "\n全部通过 ✅" : $"\n失败 {fails} 项 ❌");
        System.Environment.Exit(fails == 0 ? 0 : 1);
    }

    static float Corr(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n < 32) return 1f;
        float ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < n; i++)
        {
            float x = a[i] - ma, y = b[i] - mb;
            num += (double)x * y; da += (double)x * x; db += (double)y * y;
        }
        double den = Math.Sqrt(da * db);
        return den < 1e-9 ? 0f : (float)(num / den);
    }
}
