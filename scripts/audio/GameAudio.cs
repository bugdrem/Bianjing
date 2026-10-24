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

    // 人群环境音（批次一百零四）：相机靠近人群时按密度播放的嘈杂人声
    private AudioStreamPlayer _ambA;
    private AudioStreamPlayer _ambB;
    private bool _ambUseA = true;
    private int _ambTrack = -1;        // 当前播到第几首（−1 = 未起播）
    private float _ambLevel;           // 平滑后的密度系数（0~1）
    private float _ambTarget;          // 外部设置的目标密度
    private float _ambFade = -1f;      // 换曲淡变进度（负 = 无换曲进行中）
    private float _ambSilence;         // 持续零密度计时
    private bool _ambStarted;          // 是否已起播过（首播直接进，不淡变）

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

        // 人群环境音：两个播放器做换曲交叉淡变（单循环听久了会听出重复，六首轮播 + 淡变）
        _ambA = NewPlayer(BusSfx, 0f);
        _ambB = NewPlayer(BusSfx, 0f);
        AddChild(_ambA);
        AddChild(_ambB);

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

        // 人群环境音：密度平滑 + 换曲交叉淡变
        UpdateAmbience(dt);
    }

    // ---- 人群环境音（批次一百零四）----

    /// <summary>
    /// 设置当前相机周边的人群密度（0~1）。由 HUD 按 4Hz 扫描半径内市民数后调用；
    /// 音量平滑（涨 1.5s / 退 2.5s）在这里逐帧推进，调用方无需关心过渡。
    /// </summary>
    public void SetCrowdLevel(float level)
    {
        _ambTarget = Mathf.Clamp(level, 0f, 1f);
    }

    /// <summary>当前人群环境音的实际音量系数（诊断/设置页展示用）。</summary>
    public float CrowdLevel => _ambLevel;

    private void UpdateAmbience(float dt)
    {
        if (_ambA == null)
            return;

        // ① 密度平滑：向目标靠拢。涨潮快、退潮慢——靠近人群时人声较快起，
        //    走开时余音多留一会，避免"擦肩而过"式的抽摘感。
        float rate = _ambTarget > _ambLevel
            ? 1f / AudioConfig.AmbienceFadeIn
            : 1f / AudioConfig.AmbienceFadeOut;
        _ambLevel = Mathf.MoveToward(_ambLevel, _ambTarget, rate * dt);

        float vol = _ambLevel * AudioConfig.AmbienceVolume;

        // ② 长时间零密度：停掉播放器（空转播放器仍占一路音频混合，且再起播有初始延迟）
        if (_ambLevel < 0.002f)
        {
            _ambSilence += dt;
            _ambA.VolumeLinear = 0f;
            _ambB.VolumeLinear = 0f;
            if (_ambSilence >= AudioConfig.AmbienceSilenceStop)
            {
                _ambA.Stop();
                _ambB.Stop();
                _ambTrack = -1;
                _ambFade = -1f;
            }
            return;
        }
        _ambSilence = 0f;

        // ③ 从未起播（或刚被停过）→ 起播一首，淡变从下一帧推进
        if (_ambTrack < 0)
        {
            StartAmbienceTrack(NextAmbienceIndex());
            return;
        }

        // _ambUseA 指向「持有当前曲」的播放器（约定同 BGM：StartAmbienceTrack 里翻转）
        var cur = _ambUseA ? _ambA : _ambB;
        var nxt = _ambUseA ? _ambB : _ambA;

        // ④ 换曲淡变推进中
        if (_ambFade >= 0f)
        {
            _ambFade += dt;
            float t = Mathf.Clamp(_ambFade / AudioConfig.AmbienceCrossFade, 0f, 1f);
            cur.VolumeLinear = Mathf.Lerp(0f, vol, t);   // cur = 淡入的新曲
            nxt.VolumeLinear = Mathf.Lerp(vol, 0f, t);  // nxt = 淡出的旧曲
            if (t >= 1f)
            {
                nxt.Stop();
                _ambFade = -1f;
            }
            return;
        }

        // ⑤ 正常播放：当前曲播完 → 换下一首（交叉淡变）；否则音量跟随密度
        if (!cur.Playing)
            StartAmbienceTrack(NextAmbienceIndex());
        else
        {
            _ambA.VolumeLinear = vol;
            _ambB.VolumeLinear = vol;
        }
    }

    /// <summary>起播一首环境音到「另一个」播放器并置淡变流程（约定同 <see cref="StartTrack"/>）。</summary>
    private void StartAmbienceTrack(int index)
    {
        var stream = AudioBank.Ambience("crowd_day", index);
        if (stream == null)
            return;
        _ambTrack = index;
        var into = _ambUseA ? _ambB : _ambA;   // 淡入到「另一个」播放器
        into.Stream = stream;
        into.VolumeLinear = 0f;
        into.Play();
        _ambUseA = !_ambUseA;
        _ambFade = 0f;
    }

    /// <summary>下一首的索引：随机但不重复上一首（连续两首相同会听出"跳带"）。</summary>
    private int NextAmbienceIndex()
    {
        int n = AudioConfig.AmbienceFiles("crowd_day").Length;
        if (n <= 0)
            return 0;
        if (n == 1)
            return 0;
        int next;
        do { next = GD.RandRange(0, n - 1); } while (next == _ambTrack);
        return next;
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
