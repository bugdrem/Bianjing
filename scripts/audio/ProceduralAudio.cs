using System;
using Godot;

namespace Bianjing;

/// <summary>
/// 程序化音频合成（批次一百零二）：**未放置任何音频文件时，用算法现场生成古风拨弦与音效**，
/// 让游戏开箱即响；放入 CC0 文件后自动让位给文件（见 <see cref="AudioBank"/>）。
///
/// **为什么值得做**：音频文件不进 git（体积大 + 授权逐件核对），但「没声音」对玩家来说
/// 是个残缺的第一印象。用算法合成一份「够用、不刺耳、有古风味」的兜底音，
/// 比让玩家先去找素材再听到声音要好得多。且它是**可替换的**——真素材到了就自动退场。
///
/// **古风味从哪里来**：三件事，都不需要采样库——
///   ① **五声音阶**（宫商角徵羽，C D E G A）。没有半音，怎么拨都不难听，这是中国音乐
///      最显著的识别特征之一；
///   ② **拨弦模型 Karplus-Strong**：一段噪声冲激励一个延迟线，反馈时做低通平滑——
///      出来的就是「有木质感、先噪后余音」的拨弦声，正是古筝/古琴的音色骨架；
///   ③ **慢起慢落的音符间隔 + 长余音**：古琴留白多，音符密度低（1.5~3s 一个音），
///      余音 2s 以上，合成出来自然「疏」而不是「一串电子音」。
/// </summary>
public static class ProceduralAudio
{
    /// <summary>合成采样率。32kHz 对拨弦/音效足够，且比 44.1k 省 30% 内存。</summary>
    private const int Rate = 32000;

    /// <summary>激励噪声的低通截止（Hz）：拨子瞬态的带限。2.5kHz 保留"一击"的质感，
    /// 削掉造成数字 Click 的高频边带（见 <see cref="Pluck"/> 内注释）。</summary>
    private const float ExciteCutoff = 2500f;

    /// <summary>五声音阶（C 大调五声：宫 商 角 徵 羽）的频率倍率，相对主音。</summary>
    private static readonly double[] Pentatonic = { 1.0, 9.0 / 8.0, 5.0 / 4.0, 3.0 / 2.0, 5.0 / 3.0 };

    /// <summary>主音基准（Hz）：F3 附近，落在筝的常用中低音区。</summary>
    private const double BaseFreq = 174.61;

    private static readonly Random Rng = new(20261024);

    // ---- 波形原语 ----

    /// <summary>
    /// Karplus-Strong 拨弦：一段噪声冲激励一个延迟线，反馈时做一阶低通平滑，
    /// 出来的就是「先噪后余音、有木质感」的拨弦声——古筝/古琴的音色骨架。
    ///
    /// **两个旋钮必须分开**（这里栽过一次）：早先版本把「音色明暗」的低通系数与「衰减时长」
    /// 的回路增益乘在一起，结果每周期衰减 0.7，音符只响 16 毫秒就没了（古筝该响 3 秒），
    /// 整段音乐变成一串电子噪声、RMS 低到听不见。现在：
    ///   · <c>decay</c> = 衰减到 1/e 的秒数 → 决定回路增益 <c>exp(-1/(freq·decay))</c>，只管响多久；
    ///   · <c>brightness</c> = 低通系数 → 决定余音多闷，只管音色。
    /// 两者互不干扰，调任何一个都不会顺带改坏另一个。
    /// </summary>
    private static float[] Pluck(float freq, float seconds, float brightness, float decay = 3.2f)
    {
        int n = Math.Max(2, (int)(Rate * seconds));
        var buf = new float[n];
        int period = Math.Max(2, (int)(Rate / Math.Max(20f, freq)));
        var delay = new float[period];

        // 激励：一段加权噪声（前段最响，模拟拨子的瞬态）。
        // **先过低通再入线**（一阶 2.5kHz）：全频带白噪声是"刺耳"的真正来源——
        // 10ms 起音 ramp 只摊薄了它的幅度，摊不掉每样本的跳变（实测起音陡度 0.41，
        // 相当于一个 5kHz 满幅成分）。带限后激励只剩 2.5kHz 以下，
        // 既保留"拨子那一击"的木质感，又不会留下数字 Click。
        float exciteLp = 1f - Mathf.Exp(-Mathf.Tau * ExciteCutoff / Rate);
        float excitePrev = 0f;
        for (int i = 0; i < period; i++)
        {
            float white = (float)(Rng.NextDouble() * 2.0 - 1.0) * (1f - (float)i / period * 0.35f);
            excitePrev += (white - excitePrev) * exciteLp;
            delay[i] = excitePrev;
        }

        // 回路增益：让振幅在 decay 秒内衰减到 1/e
        float loopGain = (float)Math.Exp(-1.0 / (Math.Max(20f, freq) * Math.Max(0.05f, decay)));
        // 低通系数：0 = 不过滤（金属般亮），1 = 全平均（木头般闷）
        float lp = Mathf.Lerp(0.04f, 1.0f, Mathf.Clamp(brightness, 0f, 1f));

        for (int i = 0; i < n; i++)
        {
            float cur = delay[i % period];
            buf[i] = cur;
            float nxt = delay[(i + 1) % period];
            float filtered = cur + lp * (nxt - cur);           // 一阶低通（DC 增益 1，不伤音量）
            // 极微抖动：防止延迟线衰减到纯数字静音导致的"干净得发假"
            delay[i % period] = filtered * loopGain + (float)(Rng.NextDouble() * 2.0 - 1.0) * 1e-5f;
        }
        Normalize(buf, 0.68f);
        return buf;
    }

    /// <summary>带通噪声：用于木/石/水等材质音。center 为归一化截止（0~1，越小越闷）。</summary>
    private static float[] Noise(float seconds, float center, float attack, float release)
    {
        int n = Math.Max(2, (int)(Rate * seconds));
        var buf = new float[n];
        float lp = 0f, hp = 0f;
        float a = Mathf.Clamp(attack, 0.001f, 0.9f);
        float r = Mathf.Clamp(release, 0.001f, 0.9f);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float white = (float)(Rng.NextDouble() * 2.0 - 1.0);
            // 一阶低通做「闷」，再用原信号减低通得到「亮边」→ 简单的带通
            lp += (white - lp) * Mathf.Clamp(center, 0.01f, 1f);
            float band = white - lp;
            hp += (band - hp) * 0.6f;
            float env = Env(t, a, r);
            buf[i] = (lp * 0.7f + hp * 0.3f) * env;
        }
        Normalize(buf, 0.55f);
        return buf;
    }

    /// <summary>ADSR 式包络（attack 上升 + release 衰减，中段保持）。</summary>
    private static float Env(float t, float attack, float release)
    {
        if (t < attack)
            return t / attack;
        float rel = 1f - (t - attack) / Mathf.Max(0.001f, 1f - attack);
        return Mathf.Clamp(rel / Mathf.Max(0.001f, release) * release, 0f, 1f);
    }

    /// <summary>正弦音（点缀/蜂鸣）。</summary>
    private static float[] Tone(float freq, float seconds, float attack, float release)
    {
        int n = Math.Max(2, (int)(Rate * seconds));
        var buf = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            buf[i] = Mathf.Sin(Mathf.Tau * freq * i / Rate) * Env(t, attack, release);
        }
        Normalize(buf, 0.5f);
        return buf;
    }

    /// <summary>按 RMS 归一化到目标响度，再把峰值夹到 ceiling 以内。
    /// 拨波是「峰值高、RMS 低」的稀疏波形（峰均比可达 50:1），只按峰值归一化会让
    /// 音乐整体轻到听不见——所以音乐类一律走 RMS 归一，音效类走峰值归一。</summary>
    private static void NormalizeRms(float[] buf, float targetRms, float ceiling = 0.95f)
    {
        double sum = 0;
        foreach (var v in buf)
            sum += (double)v * v;
        float rms = (float)Math.Sqrt(sum / Math.Max(1, buf.Length));
        if (rms < 1e-6f)
            return;
        float k = targetRms / rms;
        float peak = 0f;
        foreach (var v in buf)
            peak = MathF.Max(peak, MathF.Abs(v));
        if (peak * k > ceiling)
            k = ceiling / MathF.Max(peak, 1e-6f);
        for (int i = 0; i < buf.Length; i++)
            buf[i] *= k;
    }

    private static void Normalize(float[] buf, float peak)
    {
        float max = 0f;
        foreach (var v in buf)
            max = MathF.Max(max, MathF.Abs(v));
        if (max < 1e-6f)
            return;
        float k = peak / max;
        for (int i = 0; i < buf.Length; i++)
            buf[i] *= k;
    }

    /// <summary>把多段波形按给定时刻叠加成一条（用于把音符排成乐句）。</summary>
    private static float[] Mix(params (float[] Wave, float At, float Gain)[] parts)
    {
        int total = 0;
        foreach (var (w, at, _) in parts)
            total = Math.Max(total, (int)(at * Rate) + w.Length);
        var buf = new float[total];
        foreach (var (w, at, gain) in parts)
        {
            int off = (int)(at * Rate);
            for (int i = 0; i < w.Length && off + i < buf.Length; i++)
                buf[off + i] += w[i] * gain;
        }
        return buf;
    }

    private static AudioStreamWav Wrap(float[] mono, bool loop = false)
    {
        var data = new byte[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            short v = (short)Mathf.Clamp(mono[i] * 32767f, -32767f, 32767f);
            data[i * 2] = (byte)(v & 0xFF);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        var st = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Rate,
            Stereo = false,
            Data = data,
        };
        if (loop)
            st.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        return st;
    }

    // ---- 柔化（音效专用，批次一百零三）----

    /// <summary>柔化低通截止频率（Hz）：3kHz、两级一阶 ≈ 12dB/oct。
    /// 点击反馈的「身子」在 150~900Hz，这条曲线完整保留；只削 3kHz 以上的
    /// 「刺」——正是实机反馈"太尖"的那部分能量。</summary>
    private const float SoftenCutoff = 3000f;

    /// <summary>柔化起音时长（秒）：10ms 升余弦。Karplus-Strong 的激励是 0 样本直冲的
    /// 噪声串，不处理就是一个数字 Click 瞬态（极高频、极响）。</summary>
    private const float SoftenAttack = 0.010f;

    /// <summary>柔化后的峰值上限：0.42（原 0.68）。点击音是「提示」不是「敲击」，
    /// 留足 headroom 给同帧可能触发的其他音效叠加。</summary>
    private const float SoftenPeak = 0.42f;

    /// <summary>
    /// 把一段音效波形柔化：两级一阶低通 + 升余弦起音 + 峰值归一。
    /// 顺序有讲究——**先滤波再起音再归一**：滤波要把高频削掉（起音斜坡只影响时域包络，
    /// 不改变频谱，所以必须在归一前）；起音斜坡会略微降峰值，故放最后归一。
    /// </summary>
    private static void Soften(float[] buf)
    {
        if (buf.Length < 4)
            return;

        // ① 两级一阶低通（12dB/oct @ SoftenCutoff）
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

        // ② 升余弦起音（smoothstep，两端导数为 0，无二次Click）
        int n = Mathf.Clamp((int)(Rate * SoftenAttack), 1, buf.Length / 2);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float ramp = t * t * (3f - 2f * t); // smoothstep
            buf[i] *= ramp;
        }

        // ③ 峰值归一
        Normalize(buf, SoftenPeak);
    }

    /// <summary>音效出口：先柔化再打包（BGM 不走这条路，见 <see cref="Music"/>）。</summary>
    private static AudioStreamWav WrapSfx(float[] mono)
    {
        Soften(mono);
        return Wrap(mono);
    }

    // ---- 对外入口：按 cue 生成 ----

    /// <summary>生成某个 cue 的程序化音频；未识别的 cue 返回一个通用短音。
    /// music:* / amb:* 走循环 Wrap（BGM 需要亮度不柔化、环境音是长底噪）；其余走 WrapSfx（柔化）。</summary>
    public static AudioStreamWav Make(string cue)
    {
        var wave = Build(cue);
        return cue.StartsWith("music:", StringComparison.Ordinal)
            ? Wrap(wave, loop: true)
            : cue.StartsWith("amb:", StringComparison.Ordinal)
                ? Wrap(wave, loop: true)
                : WrapSfx(wave);
    }

    /// <summary>
    /// 生成某个 cue 的波形（音效类）。
    ///
    /// **柔和化取向**（批次一百零三）：实机反馈「鼠标点击对象时的生成式声音太刺耳」。
    /// 刺耳的来源是三件事——① 激励噪声里 4kHz 以上的高频边带；② 起音是 0 样本直冲
    /// （数字.Click 瞬态）；③ 峰值归一化到 0.68 偏响。对应三道柔化处理（见
    /// <see cref="Soften"/>）：两级一阶低通（3.5kHz，12dB/oct）+ 10ms 升余弦起音 +
    /// 峰值压到 0.42。各 cue 的 brightness 也普遍从 0.85+ 降到 0.5 以下——
    /// 拨弦的「亮」来自余音里的高频，而点击反馈要的是「温」。
    /// </summary>
    private static float[] Build(string cue) => cue switch
    {
        // —— BGM：古筝风格慢板乐句，循环（BGM 不走柔化：已验证指标良好，且音乐需要亮度）——
        "music:morning_village:0" or "music:market_day:0" or "music:market_day:1"
            or "music:market_day:2" or "music:morning_village:1" or "music:morning_village:2"
            => Music("guzheng"),
        "music:night_calm:0" or "music:night_calm:1" => Music("guqin"),
        "music:celebration:0" => Music("celebration"),

        // —— 人群环境音（批次一百零四）：无素材文件时的合成「市井人声底噪」——
        _ when cue.StartsWith("amb:", StringComparison.Ordinal) => Crowd(),

        // —— UI（亮度全部压到 0.5 以下：温而不尖）——
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

        // —— 点人（三档音色：孩童高、老人低；全部降亮度 + 降频）——
        "sfx:pick_child" => Pluck(660f, 0.2f, 0.5f, 8f),
        "sfx:pick_citizen" => Pluck(523f, 0.22f, 0.42f, 8f),
        "sfx:pick_elder" => Pluck(349f, 0.26f, 0.32f, 7f),

        // —— 点物（按材质；噪声类压低 center + 放慢起音）——
        "sfx:pick_wood" => Mix((Noise(0.09f, 0.35f, 0.006f, 0.25f), 0f, 1f), (Pluck(294f, 0.22f, 0.4f, 7f), 0f, 0.55f)),
        "sfx:pick_stone" => Mix((Noise(0.07f, 0.14f, 0.004f, 0.2f), 0f, 1f), (Tone(147f, 0.12f, 0.004f, 0.4f), 0f, 0.5f)),
        "sfx:pick_tile" => Mix((Noise(0.06f, 0.45f, 0.004f, 0.22f), 0f, 1f), (Tone(740f, 0.08f, 0.004f, 0.5f), 0f, 0.35f)),
        "sfx:pick_goods" => Noise(0.13f, 0.3f, 0.015f, 0.3f),
        "sfx:pick_water" => Noise(0.34f, 0.45f, 0.05f, 0.7f),
        "sfx:pick_animal" => Mix((Tone(330f, 0.1f, 0.03f, 0.5f), 0f, 1f), (Tone(247f, 0.14f, 0.03f, 0.6f), 0.09f, 0.8f)),
        "sfx:pick_tree" => Noise(0.2f, 0.25f, 0.015f, 0.5f),

        // —— 事件 ——
        "sfx:news" => Pluck(494f, 0.3f, 0.5f, 8f),
        "sfx:milestone" => Celebration(0.9f),
        "sfx:tech" => Mix(
            (Pluck(440f, 0.3f, 0.5f, 8f), 0f, 1f),
            (Pluck(523f, 0.3f, 0.5f, 8f), 0.1f, 0.85f),
            (Pluck(587f, 0.45f, 0.5f, 8f), 0.2f, 0.75f)),

        _ => Pluck(494f, 0.2f, 0.45f, 8f),
    };

    // ---- BGM 乐句生成 ----

    /// <summary>古筝风格乐句：五声音阶随机漫步 + 长余音，循环。</summary>
    private static float[] Music(string style)
    {
        bool slow = style == "guqin";
        float length = slow ? 32f : 28f;
        int notes = slow ? 9 : 13;

        // 音符在时间轴上的位置：不均匀分布（古琴留白），用「间隔 1.4~3.2s 抖动」排布
        var times = new float[notes];
        float t = 0.4f;
        for (int i = 0; i < notes; i++)
        {
            times[i] = t;
            t += (slow ? 2.6f : 1.7f) + (float)Rng.NextDouble() * (slow ? 1.4f : 1.2f);
            if (t > length - 1.5f)
                break;
        }

        // 音高：五声音阶内的随机漫步（每步 ±1~2 个音级，偶尔跳 octave）
        var parts = new System.Collections.Generic.List<(float[], float, float)>();
        int degree = 4;
        for (int i = 0; i < times.Length; i++)
        {
            degree += Rng.Next(3) - 1;
            if (Rng.NextDouble() < 0.18)
                degree += Rng.Next(2) == 0 ? 5 : -5; // 偶尔换八度
            degree = Math.Clamp(degree, 0, 12);
            double f = BaseFreq * Pentatonic[degree % 5] * Math.Pow(2.0, degree / 5);
            // 低音区音符更闷更久，高音区更亮更短（真实筝的面板特性）
            float bright = Mathf.Clamp((float)(f / BaseFreq - 0.6) * 0.9f, 0.15f, 0.95f);
            float secs = slow ? 3.4f : 2.4f;
            parts.Add((Pluck((float)f, secs, bright, slow ? 2.6f : 3.2f), times[i], slow ? 0.85f : 0.8f));
        }
        // 低音衬底：每 8s 一个主音长音，给乐句一个「地」
        for (float bt = 0f; bt < length - 2f; bt += 8f)
            parts.Add((Pluck((float)BaseFreq * 0.5f, 4f, 0.2f, 2.2f), bt, 0.5f));

        var wave = Mix(parts.ToArray());
        // 首尾各留 0.3s 淡入淡出，避免循环接缝爆音
        FadeEdges(wave, 0.3f);
        // 稀疏拨波峰均比极高，按 RMS 归一到"听得见"的响度（目标约 −18dBFS）
        NormalizeRms(wave, 0.12f);
        return wave;
    }

    /// <summary>
    /// 人群底噪（批次一百零四）：无环境音素材文件时的合成兜底——「远处一群人说话」的嗡鸣。
    ///
    /// 人群声的物理特征与乐器正好相反，所以这里**不用拨弦**：
    ///   ① **褐噪声**（白噪漏积分）——能量集中在低频，像一群人混在一起的嗡鸣，
    ///      而不是某个人的嗓音；
    ///   ② **话音频带**（一阶低通 1.4kHz + 一阶高通 180Hz）——人声清晰的 2~5kHz
    ///      不在场（那是「近在耳边」的特征），远处人群只剩 200~1400Hz 的闷吵；
    ///   ③ **双正弦缓慢调幅**（0.7Hz + 1.9Hz，深度约 ±45%）——人群的「涨落」：
    ///      这边笑一阵、那边争两句，总体音量像潮汐一样起伏，而不是平稳噪声。
    ///
    /// 时长 45s 循环：兜底音不与素材文件同台（有文件就走文件），长度够避免听出循环。
    /// </summary>
    private static float[] Crowd()
    {
        const float Seconds = 45f;
        int n = (int)(Rate * Seconds);
        var buf = new float[n];

        float brown = 0f;   // 褐噪声积分器
        float lp = 0f;      // 低通状态（话音频带上沿）
        float hp = 0f;      // 高通状态（话音频带下沿）
        float lpK = 1f - Mathf.Exp(-Mathf.Tau * 1400f / Rate);
        float hpK = 1f - Mathf.Exp(-Mathf.Tau * 180f / Rate);
        // 两个慢调制源的相位固定（不用随机）：保证每次生成的底噪形态一致，可复现
        const float Ph1 = 0.7f, Ph2 = 2.3f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float white = (float)(Rng.NextDouble() * 2.0 - 1.0);
            brown = brown * 0.995f + white * 0.05f;          // 漏积分 → 褐噪声（−6dB/oct）
            lp += (brown - lp) * lpK;                         // 上沿 1.4kHz
            float band = lp - hp;                             // 减高通状态 = 带通
            hp += band * hpK;                                 // 下沿 180Hz

            // 双正弦调幅（0.55~1.45 倍）+ 极慢的第三层（让 45s 内也有大小潮）
            float mod = 0.55f
                + 0.28f * Mathf.Sin(Mathf.Tau * Ph1 * t)
                + 0.17f * Mathf.Sin(Mathf.Tau * Ph2 * t + 1.1f)
                + 0.10f * Mathf.Sin(Mathf.Tau * 0.11f * t + 2.7f);
            buf[i] = band * mod;
        }

        NormalizeRms(buf, 0.10f);
        FadeEdges(buf, 0.5f); // 循环接缝：比 BGM 更长的淡变，底噪循环更容易听出接缝
        return buf;
    }

    /// <summary>庆典乐句：五声音阶上行琶音 + 低音鼓点，短促不循环。</summary>
    private static float[] Celebration(float gain)
    {
        var parts = new System.Collections.Generic.List<(float[], float, float)>();
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

    private static void FadeEdges(float[] buf, float seconds)
    {
        int n = Math.Min((int)(Rate * seconds), buf.Length / 2);
        for (int i = 0; i < n; i++)
        {
            float k = (float)i / n;
            buf[i] *= k;
            buf[buf.Length - 1 - i] *= k;
        }
    }

    /// <summary>已生成的 cue 数（诊断用）。</summary>
    public static int CueCount => 24;
}
