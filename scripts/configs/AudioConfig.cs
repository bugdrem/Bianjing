using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 音频配置（批次一百零一）：古风 BGM 与交互音效的「提示表」——cue id → 资源路径 / 音量 / 变调。
///
/// **为什么是「提示表 + 静默降级」而不是直接把音频提交进仓库**：
/// ① 音频文件体积大（一首古风 BGM 3~8MB），进 git 会让克隆变慢、历史膨胀；
/// ② 授权需要逐件核对（CC0 可免署名商用，CC-BY 必须署名），提交前应确认每一件的许可；
/// ③ 开发期没配齐也该能跑——所以**路径为空或文件缺失时静默不发声**，不报错、不卡启动。
///
/// 接入方式：把 CC0 音频按下面的文件名放进 <c>res://assets/audio/</c> 即生效，无需改代码。
/// 推荐来源（详见 specs/VISUAL_ARCH.md §7）：
///   · Pixabay Music（全站 CC0，免署名，有 "China / Chinese Music" 分类）——BGM 首选；
///   · Freesound（逐件许可，筛 CC0）——音效首选，搜 "wood knock" "stone" "chime"；
///   · OpenGameArt（CC0/CC-BY 套装）——成套 UI/交互音效。
/// </summary>
public static class AudioConfig
{
    /// <summary>音频资产根目录。</summary>
    public const string AssetRoot = "res://assets/audio/";

    /// <summary>BGM 子目录。</summary>
    public const string MusicDir = "music/";

    /// <summary>音效子目录。</summary>
    public const string SfxDir = "sfx/";

    /// <summary>主音量（0~1）。</summary>
    public const float MasterVolume = 0.8f;

    /// <summary>BGM 默认音量（0~1）：背景乐要轻，不能盖过操作反馈。</summary>
    public const float MusicVolume = 0.35f;

    /// <summary>音效默认音量（0~1）。</summary>
    public const float SfxVolume = 0.7f;

    /// <summary>BGM 交叉淡入淡出时长（秒）：换曲时不硬切。</summary>
    public const float CrossFade = 2.0f;

    /// <summary>同时播放的音效上限：防止密集点击把音效池撑爆（Godot 默认 32 路音频总线）。</summary>
    public const int MaxConcurrentSfx = 12;

    /// <summary>音效变调范围（±）：同一音效每次播放微调音高，避免连续点击听起来像复读机。</summary>
    public const float SfxPitchJitter = 0.12f;

    /// <summary>
    /// 古风 BGM 播放表：按城市氛围选曲。key = cue id，value = 文件名（相对 <see cref="MusicDir"/>）。
    /// 同一 cue 可配多首轮流放（见 <see cref="Tracks"/>）。
    /// </summary>
    public static readonly Dictionary<string, string[]> Tracks = new()
    {
        // 晨间/村落：清疏笛箫
        { "morning_village", new[] { "guzheng_morning.ogg", "xiao_dawn.ogg" } },
        // 市井/白日：稍热闹的丝竹
        { "market_day", new[] { "pipa_market.ogg", "guzheng_street.ogg" } },
        // 夜间/宵禁：低缓慢板
        { "night_calm", new[] { "guqin_night.ogg", "erhu_dusk.ogg" } },
        // 晋级/庆典：鼓钹齐鸣
        { "celebration", new[] { "drum_celebrate.ogg" } },
    };

    /// <summary>
    /// 交互音效表：key = cue id，value = (文件名, 音量倍率)。
    /// 音效命名遵循「材质 × 动作」，便于按点选对象替换。
    /// </summary>
    public static readonly Dictionary<string, (string File, float Gain)> Sfx = new()
    {
        // —— UI 交互 ——
        { "ui_click",    ("ui_click.ogg",     1.0f) },  // 通用按钮/面板
        { "ui_open",     ("ui_open.ogg",      1.0f) },  // 开面板
        { "ui_close",    ("ui_close.ogg",     0.9f) },  // 关面板
        { "ui_error",    ("ui_error.ogg",     1.0f) },  // 操作被拒（钱不够/位置非法）
        { "ui_build",    ("ui_build.ogg",     1.0f) },  // 建筑落成
        { "ui_demolish", ("ui_demolish.ogg",  1.0f) },  // 拆除

        // —— 点选人物 ——
        { "pick_citizen",  ("pick_citizen.ogg",  1.0f) }, // 选中村民
        { "pick_elder",    ("pick_elder.ogg",    0.9f) }, // 选中老人（音色低一档）
        { "pick_child",    ("pick_child.ogg",    1.1f) }, // 选中孩童（音色高一档）

        // —— 点选建筑/物品（按材质）——
        { "pick_wood",     ("pick_wood.ogg",     1.0f) }, // 木构建筑/木器
        { "pick_stone",    ("pick_stone.ogg",    1.0f) }, // 石作（台基/井栏/采石场）
        { "pick_tile",     ("pick_tile.ogg",     0.9f) }, // 瓦顶
        { "pick_goods",    ("pick_goods.ogg",    1.0f) }, // 货堆
        { "pick_water",    ("pick_water.ogg",    0.9f) }, // 水井/河岸
        { "pick_animal",   ("pick_animal.ogg",   1.0f) }, // 动物
        { "pick_tree",     ("pick_tree.ogg",     0.9f) }, // 树木

        // —— 世界事件 ——
        { "news",          ("news.ogg",          1.0f) }, // 公告栏播报
        { "milestone",     ("milestone.ogg",     1.2f) }, // 城市晋级
        { "tech",          ("tech.ogg",          1.1f) }, // 科技研成
    };

    /// <summary>取 BGM 某一 cue 的文件名列表；未配置返回空数组。</summary>
    public static string[] TrackFiles(string cue)
        => Tracks.TryGetValue(cue, out var v) ? v : System.Array.Empty<string>();

    /// <summary>取 BGM 某一 cue 的第 i 首全路径；越界返回空串。</summary>
    public static string TrackPath(string cue, int index)
    {
        var files = TrackFiles(cue);
        return index >= 0 && index < files.Length ? AssetRoot + MusicDir + files[index] : "";
    }

    /// <summary>取音效某一 cue 的全路径；未配置返回空串。</summary>
    public static string SfxPath(string cue)
        => Sfx.TryGetValue(cue, out var v) ? AssetRoot + SfxDir + v.File : "";

    /// <summary>取音效某一 cue 的音量倍率；未配置返回 1。</summary>
    public static float SfxGain(string cue)
        => Sfx.TryGetValue(cue, out var v) ? v.Gain : 1f;

    /// <summary>是否配置了任何 BGM（false 时音频系统不建播放器，零开销）。</summary>
    public static bool HasMusic => Tracks.Count > 0;

    /// <summary>是否配置了任何音效。</summary>
    public static bool HasSfx => Sfx.Count > 0;
}
