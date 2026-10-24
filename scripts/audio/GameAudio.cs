using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 音频导演（批次一百零一，挂 Autoload）：古风 BGM 播放 + 交互音效触发。
///
/// **为什么用 Autoload 而不是挂在 Hud 下**：音效的触发点分散在 UI（点选/建造/面板）、
/// 世界（公告/晋级/科技）、模拟层（事件播报）三处，且加载画面阶段就要能起 BGM。
/// Autoload 单例让任何地方一行 <c>GameAudio.Sfx("ui_click")</c> 即可，无需层层传引用。
///
/// **结构**：BGM 用两个 AudioStreamPlayer 做交叉淡入淡出（换曲不硬切）；
/// 音效用一个**预建池**（避免每次播放 new 节点 + 音效密集时 GC 抖动），
/// 池内节点循环复用，超过 <see cref="AudioConfig.MaxConcurrentSfx"/> 路则丢弃新请求
/// （密集连点时天然限流，也防止撑爆音频总线）。
///
/// 音量分组走 Godot 音频总线（Master / Music / Sfx），玩家可在系统设置里分别调——
/// 总线需在 project.godot 的 [audio] 段声明（见同文件 bus 布局注释）。
/// </summary>
public partial class GameAudio : Node
{
    /// <summary>单例（Autoload 注入）。</summary>
    public static GameAudio I { get; private set; }

    private const string BusMaster = "Master";
    private const string BusMusic = "Music";
    private const string BusSfx = "Sfx";

    // BGM：双播放器交叉淡变
    private AudioStreamPlayer _musicA;
    private AudioStreamPlayer _musicB;
    private bool _useA = true;
    private string _currentCue = "";
    private int _currentTrack;
    private float _musicFade;      // 淡变进度（秒）

    // 音效池
    private readonly List<AudioStreamPlayer> _sfxPool = new();
    private int _sfxCursor;

    public override void _Ready()
    {
        I = this;
        ProcessMode = ProcessModeEnum.Always; // 暂停时仍能播 UI 音

        EnsureBus(BusMusic);
        EnsureBus(BusSfx);
        // 批次一百零二：从用户设置推音量（静音时主音量归零）
        GameSettings.ApplyAudio();

        if (!AudioConfig.HasMusic && !AudioConfig.HasSfx)
        {
            GD.Print("[audio] 未配置任何音频提示，音频导演空转");
            return;
        }

        _musicA = NewPlayer(BusMusic, GameSettings.MusicVolume);
        _musicB = NewPlayer(BusMusic, GameSettings.MusicVolume);
        AddChild(_musicA);
        AddChild(_musicB);

        for (int i = 0; i < AudioConfig.MaxConcurrentSfx; i++)
        {
            var p = NewPlayer(BusSfx, GameSettings.SfxVolume);
            AddChild(p);
            _sfxPool.Add(p);
        }

        GD.Print($"[audio] 音频导演就绪（音效池 {_sfxPool.Count} 路）");
    }

    public override void _ExitTree()
    {
        if (I == this)
            I = null;
    }

    // ---- BGM ----

    /// <summary>播放某个 BGM cue（从第 0 首起）。已在播同一 cue 则不重启。</summary>
    public void PlayMusic(string cue, int fromTrack = 0)
    {
        if (cue == _currentCue)
            return;
        _currentCue = cue;
        _currentTrack = fromTrack;
        StartTrack(cue, fromTrack);
    }

    /// <summary>切到下一首（当前曲播完时由 <see cref="_Process"/> 自动调用，也可手动切）。</summary>
    public void NextTrack()
    {
        if (_currentCue.Length == 0)
            return;
        int n = AudioBank.MusicCount(_currentCue);
        if (n <= 0)
            return;
        _currentTrack = (_currentTrack + 1) % n;
        StartTrack(_currentCue, _currentTrack);
    }

    /// <summary>停止 BGM（淡出后静音）。</summary>
    public void StopMusic()
    {
        _currentCue = "";
        _musicFade = -AudioConfig.CrossFade; // 负数 = 淡出阶段
    }

    private void StartTrack(string cue, int index)
    {
        var stream = AudioBank.Music(cue, index);
        if (stream == null)
            return; // 该曲缺失：保持静默（ cue 可能整组都没配）
        var into = _useA ? _musicB : _musicA;   // 淡入到「另一个」播放器
        var from = _useA ? _musicA : _musicB;
        into.Stream = stream;
        into.VolumeLinear = 0f;
        into.Play();
        from.VolumeLinear = AudioConfig.MusicVolume;
        _useA = !_useA;
        _musicFade = 0f;
    }

    public override void _Process(double delta)
    {
        if (_musicA == null)
            return;
        float dt = (float)delta;

        // 淡变推进：_musicFade 从 0 → CrossFade 为淡入，负值为淡出
        if (_musicFade >= 0f && _musicFade < AudioConfig.CrossFade)
        {
            _musicFade += dt;
            float t = Mathf.Clamp(_musicFade / AudioConfig.CrossFade, 0f, 1f);
            var into = _useA ? _musicA : _musicB;
            var from = _useA ? _musicB : _musicA;
            into.VolumeLinear = Mathf.Lerp(0f, AudioConfig.MusicVolume, t);
            from.VolumeLinear = Mathf.Lerp(AudioConfig.MusicVolume, 0f, t);
            if (t >= 1f)
                from.Stop();
        }
        else if (_musicFade < 0f)
        {
            _musicFade += dt;
            float t = Mathf.Clamp(1f + _musicFade / AudioConfig.CrossFade, 0f, 1f);
            _musicA.VolumeLinear = Mathf.Lerp(0f, AudioConfig.MusicVolume, t);
            _musicB.VolumeLinear = Mathf.Lerp(0f, AudioConfig.MusicVolume, t);
            if (t <= 0f)
            {
                _musicA.Stop();
                _musicB.Stop();
                _musicFade = 0f;
            }
        }

        // 当前曲播完 → 自动接下一首（仅当仍在播同一 cue）
        var active = _useA ? _musicA : _musicB;
        if (_currentCue.Length > 0 && !active.Playing && _musicFade >= AudioConfig.CrossFade)
            NextTrack();
    }

    // ---- 音效 ----

    /// <summary>播一个交互音效。未配置/文件缺失静默返回；池满则丢弃（天然限流）。</summary>
    public void Sfx(string cue, float gainScale = 1f)
    {
        if (_sfxPool.Count == 0)
            return;
        var stream = AudioBank.Sfx(cue);
        if (stream == null)
            return;

        // 找一路空闲的；找不到就覆盖最老的一路（_sfxCursor 循环）
        var p = _sfxPool[_sfxCursor];
        _sfxCursor = (_sfxCursor + 1) % _sfxPool.Count;

        // 同一路上次没播完也允许覆盖：连点时后者自然压掉前者，符合操作反馈直觉
        p.Stream = stream;
        p.VolumeLinear = AudioConfig.SfxVolume * AudioConfig.SfxGain(cue) * gainScale;
        // 变调抖动：避免连续点击听起来像复读机
        p.PitchScale = 1f + (float)GD.RandRange(-AudioConfig.SfxPitchJitter, AudioConfig.SfxPitchJitter);
        p.Play();
    }

    // ---- 音量 ----

    /// <summary>设置某一总线的线性音量（0~1）。</summary>
    public static void SetBusVolume(string bus, float linear)
    {
        int idx = AudioServer.GetBusIndex(bus);
        if (idx < 0)
            return;
        AudioServer.SetBusVolumeLinear(idx, Mathf.Clamp(linear, 0f, 1f));
    }

    /// <summary>读取某一总线的线性音量（0~1）；总线不存在返回 0。</summary>
    public static float GetBusVolume(string bus)
    {
        int idx = AudioServer.GetBusIndex(bus);
        return idx < 0 ? 0f : AudioServer.GetBusVolumeLinear(idx);
    }

    // ---- 内部 ----

    private static AudioStreamPlayer NewPlayer(string bus, float volume)
    {
        var p = new AudioStreamPlayer { VolumeLinear = volume };
        int idx = AudioServer.GetBusIndex(bus);
        if (idx >= 0)
            p.Bus = bus;
        return p;
    }

    /// <summary>确保某条音频总线存在（project.godot 未声明时运行时补建，音量默认 1）。</summary>
    private static void EnsureBus(string name)
    {
        if (AudioServer.GetBusIndex(name) >= 0)
            return;
        int idx = AudioServer.BusCount;
        AudioServer.AddBus(idx);
        AudioServer.SetBusName(idx, name);
        AudioServer.SetBusSend(idx, BusMaster);
        AudioServer.SetBusVolumeLinear(idx, 1f);
    }
}
