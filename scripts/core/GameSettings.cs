using Godot;

namespace Bianjing;

/// <summary>用户设置持久化（user://settings.cfg）：全屏/垂直同步/自动保存间隔。</summary>
public static class GameSettings
{
    private const string CfgPath = "user://settings.cfg";

    /// <summary>自动保存间隔（分钟），0 为关闭。</summary>
    public static int AutoSaveMinutes = 5;
    public static bool Fullscreen;
    public static bool VSync = true;

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(CfgPath) != Error.Ok)
            return;
        AutoSaveMinutes = (int)cfg.GetValue("general", "auto_save_minutes", 5);
        Fullscreen = (bool)cfg.GetValue("general", "fullscreen", false);
        VSync = (bool)cfg.GetValue("general", "vsync", true);
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("general", "auto_save_minutes", AutoSaveMinutes);
        cfg.SetValue("general", "fullscreen", Fullscreen);
        cfg.SetValue("general", "vsync", VSync);
        cfg.Save(CfgPath);
    }

    /// <summary>把窗口相关设置应用到显示服务。</summary>
    public static void Apply()
    {
        DisplayServer.WindowSetMode(
            Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetVsyncMode(
            VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
    }
}
