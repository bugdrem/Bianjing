using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 游戏菜单：启动进入主菜单（新游戏/读档/设置/退出），游戏中 ESC 呼出暂停菜单。
/// 打开时暂停整棵场景树（本节点 ProcessMode=Always 不受影响），关闭恢复。
/// 子页面：新游戏命名 / 存档命名与覆盖 / 读档列表 / 设置（含自动保存间隔）/ 退出确认。
/// </summary>
public partial class GameMenu : CanvasLayer
{
    private readonly Action<string> _onNewGame;
    private readonly Action<string> _onSaveNamed;
    private readonly Func<string, bool> _onLoadSlot;
    private readonly Action _onReturnTitle;

    private VBoxContainer _titleBox;
    private VBoxContainer _pauseBox;
    private VBoxContainer _newGameBox;
    private VBoxContainer _saveBox;
    private VBoxContainer _loadBox;
    private VBoxContainer _settingsBox;
    private VBoxContainer _quitBox;
    private VBoxContainer[] _allBoxes;
    private VBoxContainer _backTarget; // 设置/读档页的返回去向

    private LineEdit _cityNameEdit;
    private LineEdit _saveNameEdit;
    private ItemList _saveList;
    private ItemList _loadList;
    private List<SaveInfo> _saveInfos = new();
    private List<SaveInfo> _loadInfos = new();
    private Label _loadHint;

    private bool _inGame;
    private string _lastSaveName = "";

    public GameMenu(Action<string> onNewGame, Action<string> onSaveNamed,
        Func<string, bool> onLoadSlot, Action onReturnTitle)
    {
        _onNewGame = onNewGame;
        _onSaveNamed = onSaveNamed;
        _onLoadSlot = onLoadSlot;
        _onReturnTitle = onReturnTitle;
    }

    public override void _Ready()
    {
        Layer = 10;
        ProcessMode = ProcessModeEnum.Always;

        // 半透明遮罩，拦截下层鼠标操作
        var dim = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(dim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer();
        center.AddChild(panel);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        panel.AddChild(margin);

        margin.AddChild(BuildTitleBox());
        margin.AddChild(BuildPauseBox());
        margin.AddChild(BuildNewGameBox());
        margin.AddChild(BuildSaveBox());
        margin.AddChild(BuildLoadBox());
        margin.AddChild(BuildSettingsBox());
        margin.AddChild(BuildQuitBox());
        _allBoxes = new[] { _titleBox, _pauseBox, _newGameBox, _saveBox, _loadBox, _settingsBox, _quitBox };

        Open();
    }

    // ---- 各页面 ----

    private VBoxContainer BuildTitleBox()
    {
        _titleBox = NewBox();

        var title = new Label { Text = "汴 京", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 36);
        _titleBox.AddChild(title);

        AddButton(_titleBox, "新游戏", () => ShowBox(_newGameBox));
        AddButton(_titleBox, "读取存档", () => OpenLoadBox(_titleBox));
        AddButton(_titleBox, "设置", () => OpenSettings(_titleBox));
        AddButton(_titleBox, "退出游戏", () => GetTree().Quit());
        return _titleBox;
    }

    private VBoxContainer BuildPauseBox()
    {
        _pauseBox = NewBox();

        var title = new Label { Text = "汴 京", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 36);
        _pauseBox.AddChild(title);

        AddButton(_pauseBox, "继续游戏", Resume);
        AddButton(_pauseBox, "保存存档", OpenSaveBox);
        AddButton(_pauseBox, "读取存档", () => OpenLoadBox(_pauseBox));
        AddButton(_pauseBox, "设置", () => OpenSettings(_pauseBox));
        AddButton(_pauseBox, "返回主菜单", () => _onReturnTitle?.Invoke());
        AddButton(_pauseBox, "退出游戏", () => ShowBox(_quitBox));
        return _pauseBox;
    }

    private VBoxContainer BuildNewGameBox()
    {
        _newGameBox = NewBox();

        var title = new Label { Text = "为你的城池命名", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        _newGameBox.AddChild(title);

        _cityNameEdit = new LineEdit { Text = "汴京", PlaceholderText = "城市名", MaxLength = 12 };
        _newGameBox.AddChild(_cityNameEdit);

        AddButton(_newGameBox, "开始建城", () =>
        {
            string name = _cityNameEdit.Text.Trim();
            if (name.Length == 0)
                name = "汴京";
            _inGame = true;
            _lastSaveName = name;
            _onNewGame?.Invoke(name);
            Resume();
        });
        AddButton(_newGameBox, "返回", () => ShowBox(_titleBox));
        return _newGameBox;
    }

    private VBoxContainer BuildSaveBox()
    {
        _saveBox = NewBox();

        var title = new Label { Text = "保存存档", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        _saveBox.AddChild(title);

        _saveNameEdit = new LineEdit { PlaceholderText = "存档名", MaxLength = 20 };
        _saveBox.AddChild(_saveNameEdit);

        var hint = new Label { Text = "点选已有存档可覆盖：" };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        _saveBox.AddChild(hint);

        _saveList = new ItemList { CustomMinimumSize = new Vector2(300, 140) };
        _saveList.ItemSelected += i => _saveNameEdit.Text = _saveInfos[(int)i].SaveName;
        _saveBox.AddChild(_saveList);

        AddButton(_saveBox, "保存", () =>
        {
            string name = _saveNameEdit.Text.Trim();
            if (name.Length == 0)
                name = GameState.I.CityName;
            _lastSaveName = name;
            _onSaveNamed?.Invoke(name);
            ShowBox(_pauseBox);
        });
        AddButton(_saveBox, "返回", () => ShowBox(_pauseBox));
        return _saveBox;
    }

    private VBoxContainer BuildLoadBox()
    {
        _loadBox = NewBox();

        var title = new Label { Text = "读取存档", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        _loadBox.AddChild(title);

        _loadList = new ItemList { CustomMinimumSize = new Vector2(300, 180) };
        _loadBox.AddChild(_loadList);

        _loadHint = new Label { Text = "" };
        _loadHint.AddThemeFontSizeOverride("font_size", 12);
        _loadHint.AddThemeColorOverride("font_color", new Color(0.9f, 0.6f, 0.5f));
        _loadBox.AddChild(_loadHint);

        AddButton(_loadBox, "载入", () =>
        {
            var selected = _loadList.GetSelectedItems();
            if (selected.Length == 0)
            {
                _loadHint.Text = "请先选择一个存档";
                return;
            }
            var info = _loadInfos[selected[0]];
            if (_onLoadSlot != null && _onLoadSlot(info.Slot))
            {
                _inGame = true;
                _lastSaveName = info.SaveName;
                Resume();
            }
            else
            {
                _loadHint.Text = "读取失败：存档不完整";
            }
        });
        AddButton(_loadBox, "返回", () => ShowBox(_backTarget ?? _titleBox));
        return _loadBox;
    }

    private VBoxContainer BuildSettingsBox()
    {
        _settingsBox = NewBox();

        var title = new Label { Text = "设置", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        _settingsBox.AddChild(title);

        var fullscreen = new CheckButton { Text = "全屏显示", ButtonPressed = GameSettings.Fullscreen };
        fullscreen.Toggled += on =>
        {
            GameSettings.Fullscreen = on;
            GameSettings.Apply();
            GameSettings.Save();
        };
        _settingsBox.AddChild(fullscreen);

        var vsync = new CheckButton { Text = "垂直同步", ButtonPressed = GameSettings.VSync };
        vsync.Toggled += on =>
        {
            GameSettings.VSync = on;
            GameSettings.Apply();
            GameSettings.Save();
        };
        _settingsBox.AddChild(vsync);

        var autoRow = new HBoxContainer();
        autoRow.AddChild(new Label { Text = "自动保存：" });
        var autoOpt = new OptionButton();
        int[] minutes = { 0, 5, 10, 20 };
        string[] labels = { "关闭", "每5分钟", "每10分钟", "每20分钟" };
        for (int i = 0; i < minutes.Length; i++)
        {
            autoOpt.AddItem(labels[i], i);
            if (minutes[i] == GameSettings.AutoSaveMinutes)
                autoOpt.Select(i);
        }
        autoOpt.ItemSelected += i =>
        {
            GameSettings.AutoSaveMinutes = minutes[i];
            GameSettings.Save();
        };
        autoRow.AddChild(autoOpt);
        _settingsBox.AddChild(autoRow);

        var hint = new Label { Text = "快捷键：F5 快速保存 / F9 快速读档 / Ctrl+F 帧率 / 空格 暂停" };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        _settingsBox.AddChild(hint);

        AddButton(_settingsBox, "返回", () => ShowBox(_backTarget ?? _titleBox));
        return _settingsBox;
    }

    private VBoxContainer BuildQuitBox()
    {
        _quitBox = NewBox();

        var title = new Label { Text = "是否保存当前进度？", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 20);
        _quitBox.AddChild(title);

        AddButton(_quitBox, "保存并退出", () =>
        {
            string name = _lastSaveName.Length > 0 ? _lastSaveName : GameState.I.CityName;
            _onSaveNamed?.Invoke(name);
            GetTree().Quit();
        });
        AddButton(_quitBox, "直接退出", () => GetTree().Quit());
        AddButton(_quitBox, "取消", () => ShowBox(_pauseBox));
        return _quitBox;
    }

    // ---- 页面切换 ----

    private static VBoxContainer NewBox()
    {
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(260, 0), Visible = false };
        box.AddThemeConstantOverride("separation", 10);
        return box;
    }

    private static Button AddButton(VBoxContainer box, string text, Action onPressed)
    {
        var btn = new Button { Text = text };
        btn.Pressed += () => onPressed();
        box.AddChild(btn);
        return btn;
    }

    private void ShowBox(VBoxContainer target)
    {
        foreach (var box in _allBoxes)
            box.Visible = box == target;
    }

    private void OpenSettings(VBoxContainer back)
    {
        _backTarget = back;
        ShowBox(_settingsBox);
    }

    private void OpenLoadBox(VBoxContainer back)
    {
        _backTarget = back;
        _loadHint.Text = "";
        _loadInfos = SaveService.ListSaves();
        _loadList.Clear();
        foreach (var info in _loadInfos)
            _loadList.AddItem(FormatSave(info));
        if (_loadInfos.Count == 0)
            _loadHint.Text = "暂无历史存档";
        ShowBox(_loadBox);
    }

    private void OpenSaveBox()
    {
        _saveNameEdit.Text = _lastSaveName.Length > 0 ? _lastSaveName : GameState.I.CityName;
        _saveInfos = SaveService.ListSaves();
        _saveList.Clear();
        foreach (var info in _saveInfos)
            _saveList.AddItem(FormatSave(info));
        ShowBox(_saveBox);
    }

    private static string FormatSave(SaveInfo info)
    {
        string time = DateTimeOffset.FromUnixTimeSeconds(info.SavedAtUnix).ToLocalTime()
            .ToString("MM-dd HH:mm");
        return $"{info.CityName}·{info.SaveName}  第{info.Year}年{info.Month}月  {time}";
    }

    // ---- 打开/关闭 ----

    /// <summary>读档成功后由外部标记已进入游戏（如 F9 快速读档）。</summary>
    public void MarkInGame() => _inGame = true;

    private void Open()
    {
        Visible = true;
        GetTree().Paused = true;
        ShowBox(_inGame ? _pauseBox : _titleBox);
    }

    private void Resume()
    {
        if (!_inGame)
            return; // 主菜单模式下无游戏可回
        Visible = false;
        GetTree().Paused = false;
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            return;

        if (Visible)
        {
            if (_inGame && _pauseBox.Visible)
                Resume();
            else
                ShowBox(_inGame ? _pauseBox : _titleBox); // 子页面先退回上级
        }
        else
        {
            Open();
        }
        GetViewport().SetInputAsHandled();
    }
}
