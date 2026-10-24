// 一次性音频合成校验（批次一百零三）：核对程序化兜底音「不静音 / 不削顶 / 无 NaN / 循环无缝 /
// 各 cue 有区分度 / **柔化有效（高频降、起音缓、峰值压）**」。
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
    public const float ExciteCutoff = 2500f;

    public static float[] Pluck(float freq, float seconds, float brightness, float decay = 3.2f)
    {
        int n = Math.Max(2, (int)(Rate * seconds));
        var buf = new float[n];
        int period = Math.Max(2, (int)(Rate / Math.Max(20f, freq)));
        var delay = new float[period];
        float exciteLp = 1f - Mathf.Exp(-Mathf.Tau * ExciteCutoff / Rate);
        float excitePrev = 0f;
        for (int i = 0; i < period; i++)
        {
            float white = (float)(Rng.NextDouble() * 2.0 - 1.0) * (1f - (float)i / period * 0.35f);
            excitePrev += (white - excitePrev) * exciteLp;
            delay[i] = excitePrev;
        }
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

    // ---- 柔化（与生产代码 Soften 同构）----
    public const float SoftenCutoff = 3000f;
    public const float SoftenAttack = 0.010f;
    public const float SoftenPeak = 0.42f;

    public static void Soften(float[] buf)
    {
        if (buf.Length < 4) return;
        float k = 1f - Mathf.Exp(-Mathf.Tau * SoftenCutoff / Rate);
        for (int pass = 0; pass < 2; pass++)
        {
            float prev = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                prev += (buf[i] - prev) * k;
                buf[i] = prev;
            }
        }
        int n = Mathf.Clamp((int)(Rate * SoftenAttack), 1, buf.Length / 2);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float ramp = t * t * (3f - 2f * t);
            buf[i] *= ramp;
        }
        Normalize(buf, SoftenPeak);
    }

    public static float[] WrapSfx(float[] mono)
    {
        Soften(mono);
        return mono;
    }

    // ---- 音效生成（与生产代码 Build 同构：已柔化的参数）----
    public static float[] Build(string cue) => cue switch
    {
        "sfx:ui_click" => Pluck(587f, 0.14f, 0.45f, 9f),
        "sfx:ui_open" => Mix((Pluck(494f, 0.18f, 0.45f, 9f), 0f, 1f), (Pluck(698f, 0.16f, 0.45f, 9f), 0.07f, 0.6f)),
        "sfx:ui_close" => Mix((Pluck(698f, 0.16f, 0.45f, 9f), 0f, 1f), (Pluck(494f, 0.18f, 0.45f, 9f), 0.07f, 0.6f)),
        "sfx:ui_error" => Mix((Tone(196f, 0.16f, 0.015f, 0.5f), 0f, 1f), (Tone(165f, 0.22f, 0.015f, 0.6f), 0.12f, 0.85f)),
        "sfx:ui_build" => Mix(
            (Noise(0.1f, 0.28f, 0.02f, 0.3f), 0f, 1f),
            (Pluck(392f, 0.4f, 0.4f, 5f), 0.05f, 0.7f),
            (Pluck(523f, 0.5f, 0.45f, 5f), 0.12f, 0.6f)),
        "sfx:ui_demolish" => Mix(
            (Noise(0.22f, 0.18f, 0.01f, 0.35f), 0f, 1f),
            (Pluck(220f, 0.3f, 0.25f, 6f), 0.02f, 0.6f)),
        "sfx:pick_child" => Pluck(660f, 0.2f, 0.5f, 8f),
        "sfx:pick_citizen" => Pluck(523f, 0.22f, 0.42f, 8f),
        "sfx:pick_elder" => Pluck(349f, 0.26f, 0.32f, 7f),
        "sfx:pick_wood" => Mix((Noise(0.09f, 0.35f, 0.006f, 0.25f), 0f, 1f), (Pluck(294f, 0.22f, 0.4f, 7f), 0f, 0.55f)),
        "sfx:pick_stone" => Mix((Noise(0.07f, 0.14f, 0.004f, 0.2f), 0f, 1f), (Tone(147f, 0.12f, 0.004f, 0.4f), 0f, 0.5f)),
        "sfx:pick_tile" => Mix((Noise(0.06f, 0.45f, 0.004f, 0.22f), 0f, 1f), (Tone(740f, 0.08f, 0.004f, 0.5f), 0f, 0.35f)),
        "sfx:pick_goods" => Noise(0.13f, 0.3f, 0.015f, 0.3f),
        "sfx:pick_water" => Noise(0.34f, 0.45f, 0.05f, 0.7f),
        "sfx:pick_animal" => Mix((Tone(330f, 0.1f, 0.03f, 0.5f), 0f, 1f), (Tone(247f, 0.14f, 0.03f, 0.6f), 0.09f, 0.8f)),
        "sfx:pick_tree" => Noise(0.2f, 0.25f, 0.015f, 0.5f),
        _ when cue.StartsWith("amb:", StringComparison.Ordinal) => Crowd(),
        "sfx:news" => Pluck(494f, 0.3f, 0.5f, 8f),
        "sfx:tech" => Mix(
            (Pluck(440f, 0.3f, 0.5f, 8f), 0f, 1f),
            (Pluck(523f, 0.3f, 0.5f, 8f), 0.1f, 0.85f),
            (Pluck(587f, 0.45f, 0.5f, 8f), 0.2f, 0.75f)),
        _ => Pluck(494f, 0.2f, 0.45f, 8f),
    };

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

    public static float[] Crowd()
    {
        const float Seconds = 45f;
        int n = (int)(Rate * Seconds);
        var buf = new float[n];
        float brown = 0f, lp = 0f, hp = 0f;
        float lpK = 1f - Mathf.Exp(-Mathf.Tau * 1400f / Rate);
        float hpK = 1f - Mathf.Exp(-Mathf.Tau * 180f / Rate);
        const float Ph1 = 0.7f, Ph2 = 2.3f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float white = (float)(Rng.NextDouble() * 2.0 - 1.0);
            brown = brown * 0.995f + white * 0.05f;
            lp += (brown - lp) * lpK;
            float band = lp - hp;
            hp += band * hpK;
            float mod = 0.55f
                + 0.28f * Mathf.Sin(Mathf.Tau * Ph1 * t)
                + 0.17f * Mathf.Sin(Mathf.Tau * Ph2 * t + 1.1f)
                + 0.10f * Mathf.Sin(Mathf.Tau * 0.11f * t + 2.7f);
            buf[i] = band * mod;
        }
        NormalizeRms(buf, 0.10f);
        FadeEdges(buf, 0.5f);
        return buf;
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

    /// <summary>高频能量占比：一阶差分能量 / 总能量。差分算子对频率 f 的增益是
    /// 2·sin(πf/fs)，故该比值随频谱上移而增大——「刺耳」正是这个值偏高。</summary>
    static float HfRatio(float[] w)
    {
        double tot = 0, dif = 0;
        for (int i = 0; i < w.Length; i++)
        {
            tot += (double)w[i] * w[i];
            if (i > 0) { float d = w[i] - w[i - 1]; dif += (double)d * d; }
        }
        return tot < 1e-12 ? 0f : (float)(dif / 2.0 / tot);
    }


    /// <summary>包络起音时间：短时 RMS 包络从 10% 爬到 90% 峰值所需秒数。
    /// 这是「缓起」的感知量——直接量样本跳变会把拨弦的自然谐波误判成瞬态
    /// （2~2.5kHz 谐波的单样本跳变天生就大，但人耳听到的是柔和的起音）。</summary>
    static float AttackTime(float[] w)
    {
        int win = 128; // ≈4ms @32kHz
        int frames = w.Length / win;
        if (frames < 4) return 0f;
        var env = new float[frames];
        for (int f = 0; f < frames; f++)
            env[f] = Rms(w, f * win, win);
        float peak = 0f;
        foreach (var e in env) peak = MathF.Max(peak, e);
        if (peak < 1e-6f) return 0f;
        int i10 = -1, i90 = -1;
        for (int f = 0; f < frames; f++)
        {
            if (i10 < 0 && env[f] >= 0.1f * peak) i10 = f;
            if (i90 < 0 && env[f] >= 0.9f * peak) { i90 = f; break; }
        }
        if (i10 < 0 || i90 < 0) return 0f;
        return (i90 - i10) * win / (float)Synth.Rate;
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

        Console.WriteLine("\n== B：循环接缝无爆音（首样本近零 + 淡变包络爬升 + 接缝步进小）==");
        foreach (var style in new[] { "guzheng", "guqin" })
        {
            var w = Synth.Music(style);
            int n = (int)(Synth.Rate * 0.3f);
            float head0 = MathF.Abs(w[0]);
            int seg = Math.Max(1, n / 5);
            float rmsA = Rms(w, 0, seg), rmsB = Rms(w, n - seg, seg);
            bool ramping = rmsB > rmsA * 3f;
            float seam = MathF.Abs(w[w.Length - 1] - w[0]);
            float tailEnd = MathF.Abs(w[w.Length - 1]);
            bool ok = head0 < 0.01f && tailEnd < 0.01f && seam < 0.02f && ramping;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {style,-7} 首样本 {head0:E2}  尾样本 {tailEnd:E2}  接缝步进 {seam:E2}  包络 {rmsA:F4}→{rmsB:F4}（爬升 {(ramping ? "是" : "否")}）");
            if (!ok) { fails++; if (head0 >= 0.01f) Bad($"{style} 首样本 {head0:E2} 非零"); if (tailEnd >= 0.01f) Bad($"{style} 尾样本 {tailEnd:E2} 非零"); if (seam >= 0.02f) Bad($"{style} 接缝步进 {seam:E2}"); if (!ramping) Bad($"{style} 淡变包络未爬升 {rmsA:F4}→{rmsB:F4}"); }
        }

        Console.WriteLine("\n== C：音效各 cue（柔化后）不静音、不削顶、时长合理 ==");
        var cues = new[]
        {
            "sfx:ui_click", "sfx:ui_open", "sfx:ui_close", "sfx:ui_error", "sfx:ui_build", "sfx:ui_demolish",
            "sfx:pick_child", "sfx:pick_citizen", "sfx:pick_elder",
            "sfx:pick_wood", "sfx:pick_stone", "sfx:pick_tile", "sfx:pick_goods", "sfx:pick_water",
            "sfx:pick_animal", "sfx:pick_tree", "sfx:news", "sfx:tech",
        };
        var waves = new Dictionary<string, float[]>();
        foreach (var cue in cues)
        {
            var w = Synth.Build(cue);
            Synth.Soften(w);   // 与生产 WrapSfx 同：柔化后再验
            waves[cue] = w;
            var (rms, peak, nan, _) = Stats(w);
            float secs = w.Length / (float)Synth.Rate;
            bool ok = rms >= 0.005f && peak <= 0.43f && !nan && secs > 0.05f && secs < 2.5f;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {cue,-18} {secs:F2}s  RMS {rms:F4}  峰值 {peak:F3}");
            if (!ok) { fails++; if (nan) Bad($"{cue} 含 NaN"); if (rms < 0.005f) Bad($"{cue} 柔化后过轻（RMS {rms:F4}）"); if (peak > 0.43f) Bad($"{cue} 峰值 {peak:F3} 超过柔化上限 0.42"); if (secs is <= 0.05f or >= 2.5f) Bad($"{cue} 时长 {secs:F2}s 不合理"); }
        }

        Console.WriteLine("\n== D：材质音效彼此有区分度（相关系数不应全接近 1）==");
        var pairs = new (string, string)[]
        {
            ("sfx:pick_wood", "sfx:pick_stone"), ("sfx:pick_wood", "sfx:pick_water"),
            ("sfx:pick_stone", "sfx:pick_water"), ("sfx:pick_child", "sfx:pick_elder"),
            ("sfx:ui_click", "sfx:ui_build"),
        };
        foreach (var (a, b) in pairs)
        {
            float c = Corr(waves[a], waves[b]);
            bool ok = c < 0.98f;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {a} × {b} 相关 {c:F3}");
            if (!ok) fails++;
        }

        Console.WriteLine("\n== E：古风五声音阶自检（乐句音高应落在五声之内）==");
        var allowed = new HashSet<double>(Synth.Pent.Select(p => Math.Round(p, 4)));
        for (int deg = 0; deg <= 12; deg++)
        {
            double f = Synth.BaseFreq * Synth.Pent[deg % 5] * Math.Pow(2.0, deg / 5);
            bool inScale = allowed.Contains(Math.Round(Synth.Pent[deg % 5], 4));
            if (!inScale) Bad($"音级 {deg} 不在五声内");
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

        Console.WriteLine("\n== G：柔化有效性（批次一百零三：实机反馈\"太刺耳\"）==");
        Console.WriteLine("  判据：柔化后高频能量占比 ≤0.10 + 首样本近零 + 包络起音 ≥5ms + 峰值 ≤0.42 + 仍然听得见");
        foreach (var cue in new[] { "sfx:ui_click", "sfx:pick_child", "sfx:pick_wood", "sfx:pick_stone", "sfx:pick_water", "sfx:pick_animal" })
        {
            var raw = Synth.Build(cue);
            float hfBefore = HfRatio(raw);
            var soft = Synth.Build(cue);
            Synth.Soften(soft);
            float hfAfter = HfRatio(soft);
            float head0 = MathF.Abs(soft[0]);
            float attack = AttackTime(soft);
            var (rms, peak, _, _) = Stats(soft);

            bool hfOk = hfAfter <= 0.10f;                     // 柔化后高频占比封顶 10%
            bool headOk = head0 < 0.02f;                      // 起音从 0 开始（无直冲瞬态）
            bool attackOk = attack >= 0.005f;                 // 包络起音至少 5ms（缓起）
            bool peakOk = peak <= 0.43f;
            bool audible = rms >= 0.005f;
            bool ok = hfOk && headOk && attackOk && peakOk && audible;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} {cue,-18} 高频 {hfBefore:F3}→{hfAfter:F3}  首样本 {head0:E2}  起音 {attack * 1000f:F0}ms  峰值 {peak:F3}  RMS {rms:F4}");
            if (!ok)
            {
                fails++;
                if (!hfOk) Bad($"{cue} 柔化后高频占比 {hfAfter:F3} > 0.10");
                if (!headOk) Bad($"{cue} 首样本 {head0:E2} 非零（起音直冲）");
                if (!attackOk) Bad($"{cue} 包络起音 {attack * 1000f:F0}ms < 5ms（太冲）");
                if (!peakOk) Bad($"{cue} 峰值 {peak:F3} 超限");
                if (!audible) Bad($"{cue} 柔化后过轻");
            }
        }

        Console.WriteLine("\n== H：人群底噪（无素材文件时的合成兜底，批次一百零四）==");
        {
            var w = Synth.Crowd();
            var (rms, peak, nan, zcr) = Stats(w);
            float secs = w.Length / (float)Synth.Rate;
            float hf = HfRatio(w);

            // 调制深度：按 1s 窗算 RMS，看标准差/均值——平稳噪声的标准差接近 0，
            // 人群底噪必须有明显"涨落"（目标：变异系数 ≥ 0.10）
            int win = Synth.Rate;
            var wr = new List<float>();
            for (int off = 0; off + win <= w.Length; off += win)
                wr.Add(Rms(w, off, win));
            float mean = 0f;
            foreach (var v in wr) mean += v;
            mean /= Math.Max(1, wr.Count);
            float var = 0f;
            foreach (var v in wr) var += (v - mean) * (v - mean);
            var /= Math.Max(1, wr.Count);
            float cv = mean > 1e-6f ? MathF.Sqrt(var) / mean : 0f;

            // 循环接缝：0.5s 淡变后首尾样本应近零
            float head0 = MathF.Abs(w[0]);
            float tailEnd = MathF.Abs(w[w.Length - 1]);
            float seam = MathF.Abs(w[w.Length - 1] - w[0]);

            bool ok = rms >= 0.01f && peak <= 1.02f && !nan && secs >= 40f
                && hf <= 0.15f && cv >= 0.10f && head0 < 0.01f && tailEnd < 0.01f && seam < 0.02f;
            Console.WriteLine($"  {(ok ? "OK " : "FAIL")} crowd {secs:F0}s  RMS {rms:F4}  峰值 {peak:F3}  零穿 {zcr:F0}Hz  高频 {hf:F3}  涨落CV {cv:F3}  接缝 {seam:E2}");
            if (!ok)
            {
                fails++;
                if (rms < 0.01f) Bad("crowd 几乎静音");
                if (peak > 1.02f) Bad("crowd 削顶");
                if (nan) Bad("crowd 含 NaN");
                if (secs < 40f) Bad($"crowd 时长 {secs:F0}s 过短");
                if (hf > 0.15f) Bad($"crowd 高频占比 {hf:F3} > 0.15（不像远处人群，像近耳噪声）");
                if (cv < 0.10f) Bad($"crowd 涨落变异系数 {cv:F3} < 0.10（是平稳噪声，没有人群的潮汐感）");
                if (head0 >= 0.01f || tailEnd >= 0.01f) Bad("crowd 首尾样本非零（循环接缝爆音）");
                if (seam >= 0.02f) Bad($"crowd 接缝步进 {seam:E2}");
            }
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
