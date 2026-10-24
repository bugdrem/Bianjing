using Godot;

namespace Bianjing;

/// <summary>用户设置持久化（游戏根目录/settings.cfg）：分辨率/全屏/垂直同步/自动保存间隔。
/// 画面自适应由 project.godot 的 stretch=canvas_items + aspect=expand 保证：窗体尺寸变化时
/// 3D 视口与锚定 UI 自动重排，无需额外代码处理拖拽缩放。</summary>
public static class GameSettings
{
    private static string CfgPath => GamePaths.SettingsFile;

    /// <summary>自动保存间隔（分钟），0 为关闭。</summary>
    public static int AutoSaveMinutes = 5;
    public static bool Fullscreen;
    public static bool VSync = true;

    /// <summary>无限钱：启用后建造不再校验资金（钱可扣至负数仍可建造）。
    /// 目前可在设置里随时开关，后期拟改为仅建局时选定。</summary>
    public static bool InfiniteMoney;

    /// <summary>鼠标移至屏幕边缘自动平移视野：光标贴边即推镜头，默认开启；
    /// 关闭后仅保留 WASD 平移，边缘不再触发（防菜单/拖拽误触）。</summary>
    public static bool EdgePanEnabled = true;

    /// <summary>窗口模式下的分辨率（全屏时忽略，沿用显示器分辨率）。</summary>
    public static int WindowWidth = 1280;
    public static int WindowHeight = 720;

    // ---- 音频（批次一百零二：音量设置 + 程序化兜底）----

    /// <summary>主音量（0~1 线性）。作用于全部总线。</summary>
    public static float MasterVolume = 0.8f;

    /// <summary>音乐音量（0~1 线性）。BGM 要轻，不能盖过操作反馈。</summary>
    public static float MusicVolume = 0.35f;

    /// <summary>音效音量（0~1 线性）。</summary>
    public static float SfxVolume = 0.7f;

    /// <summary>静音（一键全静音，优先级高于各分路音量；设置成独立开关而非把主音量归零，
    /// 是为了记住用户原来的主音量，取消静音时回去）。</summary>
    public static bool Muted;

    /// <summary>是否启用程序化合成音频兜底（未放置 CC0 文件时用算法现场生成古风拨弦与音效）。
    /// 关掉则完全静默——供想「只用自己的音频文件」的开发者使用。</summary>
    public static bool ProceduralAudio = true;

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(CfgPath) != Error.Ok)
            return;
        AutoSaveMinutes = (int)cfg.GetValue("general", "auto_save_minutes", 5);
        Fullscreen = (bool)cfg.GetValue("general", "fullscreen", false);
        VSync = (bool)cfg.GetValue("general", "vsync", true);
        InfiniteMoney = (bool)cfg.GetValue("general", "infinite_money", false);
        EdgePanEnabled = (bool)cfg.GetValue("general", "edge_pan_enabled", true);
        WindowWidth = (int)cfg.GetValue("display", "window_width", 1280);
        WindowHeight = (int)cfg.GetValue("display", "window_height", 720);
        MasterVolume = (float)(double)cfg.GetValue("audio", "master_volume", 0.8);
        MusicVolume = (float)(double)cfg.GetValue("audio", "music_volume", 0.35);
        SfxVolume = (float)(double)cfg.GetValue("audio", "sfx_volume", 0.7);
        Muted = (bool)cfg.GetValue("audio", "muted", false);
        ProceduralAudio = (bool)cfg.GetValue("audio", "procedural_audio", true);
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("general", "auto_save_minutes", AutoSaveMinutes);
        cfg.SetValue("general", "fullscreen", Fullscreen);
        cfg.SetValue("general", "vsync", VSync);
        cfg.SetValue("general", "infinite_money", InfiniteMoney);
        cfg.SetValue("general", "edge_pan_enabled", EdgePanEnabled);
        cfg.SetValue("display", "window_width", WindowWidth);
        cfg.SetValue("display", "window_height", WindowHeight);
        cfg.SetValue("audio", "master_volume", MasterVolume);
        cfg.SetValue("audio", "music_volume", MusicVolume);
        cfg.SetValue("audio", "sfx_volume", SfxVolume);
        cfg.SetValue("audio", "muted", Muted);
        cfg.SetValue("audio", "procedural_audio", ProceduralAudio);
        cfg.Save(CfgPath);
    }

    /// <summary>把窗口与音频设置应用到显示服务 / 音频服务。</summary>
    public static void Apply()
    {
        if (Fullscreen)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
        }
        else
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetSize(new Vector2I(WindowWidth, WindowHeight));
            CenterWindow();
        }
        DisplayServer.WindowSetVsyncMode(
            VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        ApplyAudio();
    }

    /// <summary>把音量设置推到音频总线。静音时主音量归零（各分路音量保留记忆）。</summary>
    public static void ApplyAudio()
    {
        float master = Muted ? 0f : MasterVolume;
        GameAudio.SetBusVolume("Master", master);
        GameAudio.SetBusVolume("Music", MusicVolume);
        GameAudio.SetBusVolume("Sfx", SfxVolume);
    }

    /// <summary>把窗口移到当前显示器中央（换分辨率后避免窗口跑到屏幕外）。</summary>
    private static void CenterWindow()
    {
        int screen = DisplayServer.WindowGetCurrentScreen();
        Vector2I screenPos = DisplayServer.ScreenGetPosition(screen);
        Vector2I screenSize = DisplayServer.ScreenGetSize(screen);
        Vector2I winSize = DisplayServer.WindowGetSize();
        DisplayServer.WindowSetPosition(screenPos + (screenSize - winSize) / 2);
    }
}
