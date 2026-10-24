using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 音频资源库（批次一百零一/一百零二）：按 cue id 取音频流，**文件优先、程序化合成兜底**。
///
/// 取流顺序：
///   ① `res://assets/audio/` 下有对应文件 → 用文件（真素材，音质最好）；
///   ② 文件缺失且 <see cref="GameSettings.ProceduralAudio"/> 开启 → 用
///      <see cref="ProceduralAudio"/> 现场合成（古筝拨弦 + 五声音阶，开箱即响）；
///   ③ 两者都无 → 静默返 null（不报错、不卡启动）。
///
/// 这样「没放素材」从「完全没声音」变成「有算法生成的古风兜底音」，而放入真素材后
/// 自动退位——灰度接入，不需要改任何调用点。
/// </summary>
public static class AudioBank
{
    private static readonly Dictionary<string, AudioStream> Cache = new();
    private static readonly HashSet<string> Probed = new();
    private static int _fileHits;
    private static int _synthHits;

    /// <summary>取（并缓存）某个 cue 的音频流；三者皆无返回 null。</summary>
    public static AudioStream Load(string cue, string path)
    {
        if (Cache.TryGetValue(cue, out var cached))
            return cached;
        if (Probed.Contains(cue))
            return null; // 已探测过且无货：不再重试
        Probed.Add(cue);

        // ① 文件优先
        if (!string.IsNullOrEmpty(path))
        {
            var stream = ResourceLoader.Load<AudioStream>(path);
            if (stream != null)
            {
                Cache[cue] = stream;
                _fileHits++;
                return stream;
            }
            GD.Print($"[audio] 未找到文件 {path}，该 cue 转程序化合成兜底");
        }

        // ② 程序化合成兜底
        if (GameSettings.ProceduralAudio)
        {
            try
            {
                var synth = ProceduralAudio.Make(cue);
                if (synth != null)
                {
                    Cache[cue] = synth;
                    _synthHits++;
                    return synth;
                }
            }
            catch (Exception e)
            {
                GD.PushWarning($"[audio] 程序化合成 {cue} 失败：{e.Message}");
            }
        }

        // ③ 静默
        GD.Print($"[audio] {cue} 无音频（未放文件且未启用程序化合成），静默");
        return null;
    }

    /// <summary>取 BGM cue 的第 i 首（越界或未配置返回 null）。</summary>
    public static AudioStream Music(string cue, int index)
        => Load($"music:{cue}:{index}", AudioConfig.TrackPath(cue, index));

    /// <summary>取环境音 cue 的第 i 首（越界或未配置返回 null）。</summary>
    public static AudioStream Ambience(string cue, int index)
        => Load($"amb:{cue}:{index}", AudioConfig.AmbiencePath(cue, index));

    /// <summary>取音效 cue（未配置返回 null）。</summary>
    public static AudioStream Sfx(string cue)
        => Load($"sfx:{cue}", AudioConfig.SfxPath(cue));

    /// <summary>某 BGM cue 共有几首（用于轮播）。</summary>
    public static int MusicCount(string cue) => AudioConfig.TrackFiles(cue).Length;

    /// <summary>已成功加载的音频数（诊断用）。</summary>
    public static int LoadedCount => Cache.Count;

    /// <summary>其中来自文件的数量（0 = 全部走程序化合成兜底）。</summary>
    public static int FileHitCount => _fileHits;

    /// <summary>其中来自程序化合成的数量。</summary>
    public static int SynthHitCount => _synthHits;

    /// <summary>当前音频来源描述（设置页展示用）：让玩家知道听到的是文件还是算法兜底。</summary>
    public static string SourceLabel => _fileHits > 0 && _synthHits > 0 ? "素材文件 + 程序化兜底"
        : _fileHits > 0 ? "素材文件"
        : _synthHits > 0 ? "程序化合成（未放素材文件）"
        : "无";

    /// <summary>清空缓存（改配置 / 切换程序化音频开关后调用）。</summary>
    public static void Invalidate()
    {
        Cache.Clear();
        Probed.Clear();
        _fileHits = 0;
        _synthHits = 0;
    }
}
