using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 游戏菜单：启动进入主菜单（新游戏/读档/设置/退出），游戏中 ESC 呼出暂停菜单。
/// 打开时暂停整棵场景树（本节点 ProcessMode=Always 不受影响），关闭恢复。
/// 子页面：新游戏命名+地图预览（随机/确认）/ 存档命名与覆盖 / 读档列表（异步）/ 设置（含自动保存间隔）/ 退出确认。
/// </summary>
public partial class GameMenu : CanvasLayer
{
    private readonly Action<string, int> _onNewGame;
    private readonly Action<string> _onSaveNamed;
    private readonly Action<string> _onLoadSlot;
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
    /// <summary>128×128 地图俯视预览：同种子生成，确认建城即此地形（所见即所得）。</summary>
    private TextureRect _mapPreview;
    private Label _seedLabel;
    /// <summary>当前预览种子：确认建城后以此种子生成真实地图，与预览完全一致。</summary>
    private int _seed;
    private LineEdit _saveNameEdit;
    private ItemList _saveList;
    private ItemList _loadList;
    private List<SaveInfo> _saveInfos = new();
    private List<SaveInfo> _loadInfos = new();
    private Label _loadHint;
    /// <summary>待确认删除的存档序号：首次点「删除所选」记下，再次点同一项才真删，防误触。</summary>
    private int _pendingDeleteIndex = -1;

    private bool _inGame;
    private string _lastSaveName = "";

    public GameMenu(Action<string, int> onNewGame, Action<string> onSaveNamed,
        Action<string> onLoadSlot, Action onReturnTitle)
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

        var title = new Label { Text = "勾画山河，为城池命名", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        _newGameBox.AddChild(title);

        _cityNameEdit = new LineEdit { Text = "汴京", PlaceholderText = "城市名", MaxLength = 12 };
        _newGameBox.AddChild(_cityNameEdit);

        // 128×128 地图俯视预览：与真实地图同种子生成，确认建城即此地形（所见即所得）
        _mapPreview = new TextureRect
        {
            CustomMinimumSize = new Vector2(384, 384),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest, // 像素放大 3 倍防糊
        };
        _newGameBox.AddChild(_mapPreview);

        _seedLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _seedLabel.AddThemeFontSizeOverride("font_size", 12);
        _seedLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        _newGameBox.AddChild(_seedLabel);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        AddButton(row, "随机", RollMap);
        AddButton(row, "确认建城", ConfirmNewGame);
        _newGameBox.AddChild(row);

        AddButton(_newGameBox, "返回", () => ShowBox(_titleBox));

        RollMap(); // 进入页面即掷定首幅预览
        return _newGameBox;
    }

    /// <summary>重掷种子并重绘 128×128 地形预览：同种子从头重跑草图 → 与确认后真实地图完全一致；
    /// 逐像素高度着色（草图无水面，纯高度色阶）。</summary>
    private void RollMap()
    {
        _seed = Random.Shared.Next();
        _seedLabel.Text = $"种子 #{_seed}";
        var sketch = WorldSketch.Build(new Random(_seed));
        const int S = TerrainConfig.SketchSize;
        var img = Image.CreateEmpty(S, S, false, Image.Format.Rgb8);
        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
                img.SetPixel(x, y, HeightColor(sketch.H[y * S + x]));
        }
        DrawRivers(img, sketch, S); // 叠加河流定线：预览所见即最终河的位置
        _mapPreview.Texture = ImageTexture.CreateFromImage(img);
    }

    /// <summary>在预览图上叠加河流定线（浅蓝 1px）：草图路径 8 邻连续，逐点着色即可。</summary>
    private static void DrawRivers(Image img, WorldSketch sketch, int s)
    {
        var river = WaterConfig.PreviewRiverColor;
        foreach (var path in sketch.Rivers)
        {
            foreach (var p in path)
            {
                if (p.X < 0 || p.Y < 0 || p.X >= s || p.Y >= s)
                    continue;
                img.SetPixel(p.X, p.Y, river);
            }
        }
    }

    /// <summary>预览高度着色（与成品地形视觉一致的色阶；草图无水面，纯高度插值）：
    /// h≤0 深青绿 → 0 翠绿 → 12m 黄绿 → 24m 黄褐 → 40m 灰褐 → ≥64m 灰白。</summary>
    private static Color HeightColor(float h)
    {
        if (h <= 0f) return new Color(0.10f, 0.34f, 0.26f);
        if (h < 12f) return new Color(0.16f, 0.48f, 0.22f).Lerp(new Color(0.42f, 0.55f, 0.20f), h / 12f);
        if (h < 24f) return new Color(0.42f, 0.55f, 0.20f).Lerp(new Color(0.58f, 0.47f, 0.24f), (h - 12f) / 12f);
        if (h < 40f) return new Color(0.58f, 0.47f, 0.24f).Lerp(new Color(0.52f, 0.50f, 0.46f), (h - 24f) / 16f);
        if (h < 64f) return new Color(0.52f, 0.50f, 0.46f).Lerp(new Color(0.78f, 0.78f, 0.74f), (h - 40f) / 24f);
        return new Color(0.78f, 0.78f, 0.74f);
    }

    /// <summary>确认建城：校验城名后携种子回调 Main（同种子生成真实地图），随即关菜单恢复游戏。</summary>
    private void ConfirmNewGame()
    {
        string name = _cityNameEdit.Text.Trim();
        if (name.Length == 0)
            name = "汴京";
        _inGame = true;
        _lastSaveName = name;
        Resume(); // 先关菜单（NewGame 会立即重新暂停并挂加载面板）
        _onNewGame?.Invoke(name, _seed);
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
            _lastSaveName = info.SaveName;
            // 异步读档：Main 挂加载面板后台读取，完成回调再 MarkInGame/Resume（失败 NotifyLoadFailed）
            _onLoadSlot?.Invoke(info.Slot);
        });
        AddButton(_loadBox, "删除所选", () =>
        {
            var selected = _loadList.GetSelectedItems();
            if (selected.Length == 0)
            {
                _loadHint.Text = "请先选择一个存档";
                return;
            }
            int index = selected[0];
            // 两次点击确认：首次只提示，再次点击同一项才执行删除
            if (_pendingDeleteIndex != index)
            {
                _pendingDeleteIndex = index;
                _loadHint.Text = $"再次点击「删除所选」确认删除：{_loadInfos[index].SaveName}";
                return;
            }
            var info = _loadInfos[index];
            bool ok = SaveService.DeleteSave(info.Slot);
            RefreshLoadList();
            _loadHint.Text = ok ? $"已删除存档：{info.SaveName}" : "删除失败：存档目录无法移除";
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

        // 分辨率下拉（仅窗口模式生效，全屏时置灰）
        var resRow = new HBoxContainer();
        resRow.AddChild(new Label { Text = "分辨率：" });
        var resOpt = new OptionButton();
        Vector2I[] resolutions =
        {
            new(1280, 720), new(1366, 768), new(1600, 900), new(1920, 1080), new(2560, 1440),
        };
        for (int i = 0; i < resolutions.Length; i++)
        {
            resOpt.AddItem($"{resolutions[i].X} × {resolutions[i].Y}", i);
            if (resolutions[i].X == GameSettings.WindowWidth && resolutions[i].Y == GameSettings.WindowHeight)
                resOpt.Select(i);
        }
        resOpt.Disabled = GameSettings.Fullscreen; // 全屏下分辨率不可选
        resOpt.ItemSelected += i =>
        {
            var r = resolutions[i];
            GameSettings.WindowWidth = r.X;
            GameSettings.WindowHeight = r.Y;
            GameSettings.Apply();
            GameSettings.Save();
        };
        resRow.AddChild(resOpt);
        _settingsBox.AddChild(resRow);

        var fullscreen = new CheckButton { Text = "全屏显示", ButtonPressed = GameSettings.Fullscreen };
        fullscreen.Toggled += on =>
        {
            GameSettings.Fullscreen = on;
            resOpt.Disabled = on; // 切全屏时禁用分辨率下拉，退回窗口再开放
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

        var infMoney = new CheckButton { Text = "无限钱（可负债建造）", ButtonPressed = GameSettings.InfiniteMoney };
        infMoney.Toggled += on =>
        {
            GameSettings.InfiniteMoney = on;
            GameSettings.Save();
        };
        _settingsBox.AddChild(infMoney);

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

    private static Button AddButton(BoxContainer box, string text, Action onPressed)
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
        RefreshLoadList();
        if (_loadInfos.Count == 0)
            _loadHint.Text = "暂无历史存档";
        ShowBox(_loadBox);
    }

    /// <summary>重拉存档列表并重置删除确认状态（列表变动后旧序号失效）。</summary>
    private void RefreshLoadList()
    {
        _pendingDeleteIndex = -1;
        _loadInfos = SaveService.ListSaves();
        _loadList.Clear();
        foreach (var info in _loadInfos)
            _loadList.AddItem(FormatSave(info));
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

    /// <summary>读档失败提示（异步读档完成后由 Main 调用）：留在读档页并显示原因。</summary>
    public void NotifyLoadFailed(string msg)
    {
        _loadHint.Text = msg;
        ShowBox(_loadBox);
    }

    private void Open()
    {
        Visible = true;
        GetTree().Paused = true;
        ShowBox(_inGame ? _pauseBox : _titleBox);
    }

    /// <summary>关闭菜单恢复游戏（异步读档完成后由 Main 调用；主菜单模式下无游戏可回）。</summary>
    public void Resume()
    {
        if (!_inGame)
            return;
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
