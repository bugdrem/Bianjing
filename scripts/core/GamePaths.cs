using System.IO;
using Godot;

namespace Bianjing;

/// <summary>
/// 游戏数据路径：配置与存档统一放在游戏根目录下——
/// 编辑器运行为工程目录，导出后为 exe 所在目录（绿色便携，随游戏文件夹整体迁移）。
/// </summary>
public static class GamePaths
{
    public static string Root => OS.HasFeature("editor")
        ? ProjectSettings.GlobalizePath("res://")
        : OS.GetExecutablePath().GetBaseDir();

    /// <summary>用户设置文件（游戏根目录/settings.cfg）。</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.cfg");

    /// <summary>存档根目录（游戏根目录/saves）。</summary>
    public static string SavesDir => Path.Combine(Root, "saves");
}
