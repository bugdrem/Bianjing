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

    /// <summary>窗口模式下的分辨率（全屏时忽略，沿用显示器分辨率）。</summary>
    public static int WindowWidth = 1280;
    public static int WindowHeight = 720;

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(CfgPath) != Error.Ok)
            return;
        AutoSaveMinutes = (int)cfg.GetValue("general", "auto_save_minutes", 5);
        Fullscreen = (bool)cfg.GetValue("general", "fullscreen", false);
        VSync = (bool)cfg.GetValue("general", "vsync", true);
        InfiniteMoney = (bool)cfg.GetValue("general", "infinite_money", false);
        WindowWidth = (int)cfg.GetValue("display", "window_width", 1280);
        WindowHeight = (int)cfg.GetValue("display", "window_height", 720);
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("general", "auto_save_minutes", AutoSaveMinutes);
        cfg.SetValue("general", "fullscreen", Fullscreen);
        cfg.SetValue("general", "vsync", VSync);
        cfg.SetValue("general", "infinite_money", InfiniteMoney);
        cfg.SetValue("display", "window_width", WindowWidth);
        cfg.SetValue("display", "window_height", WindowHeight);
        cfg.Save(CfgPath);
    }

    /// <summary>把窗口相关设置应用到显示服务：全屏直接切全屏，否则切窗口并按选定分辨率调尺寸居中。</summary>
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
