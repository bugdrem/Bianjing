using System;
using Godot;

namespace Bianjing;

/// <summary>
/// 世界生成加载画面：全屏遮罩 + 城市名 + 阶段文字 + 进度条。
/// ProcessMode=Always（生成期间可能整树暂停，本层仍需刷新与轮询）；
/// 每帧轮询完成/阶段/进度源（默认 WorldGenerator 的 volatile 字段，也可注入自定义源如读档），
/// 完成后回调 OnFinished（主线程）并自毁。
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
    /// <summary>加载完成回调（在主线程 _Process 中触发，可安全操作场景树）。</summary>
    public System.Action OnFinished;

    /// <summary>自定义完成轮询源（null 时读 WorldGenerator.Done）：读档等非生成任务可注入。</summary>
    private readonly Func<bool> _isDone;

    /// <summary>自定义阶段文案源（null 时读 WorldGenerator.Stage）。</summary>
    private readonly Func<string> _stage;

    /// <summary>自定义进度源（null 时读 WorldGenerator.Progress）。</summary>
    private readonly Func<float> _progress;

    private readonly string _cityName;
    private Label _stageLabel;
    private ProgressBar _bar;
    private bool _finished;

    public LoadingScreen(string cityName, Func<bool> isDone = null, Func<string> stage = null, Func<float> progress = null)
    {
        _cityName = cityName;
        _isDone = isDone;
        _stage = stage;
        _progress = progress;
    }

    public override void _Ready()
    {
        Layer = 90; // 盖住 HUD/菜单层之下的一切游戏画面
        ProcessMode = ProcessModeEnum.Always;

        // 全屏深色底
        var dim = new ColorRect { Color = new Color(0.08f, 0.07f, 0.06f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(dim);

        // 居中竖排：城名 / 阶段 / 进度条
        var box = new VBoxContainer();
        box.SetAnchorsPreset(Control.LayoutPreset.Center);
        box.GrowHorizontal = Control.GrowDirection.Both;
        box.GrowVertical = Control.GrowDirection.Both;
        box.AddThemeConstantOverride("separation", 18);
        AddChild(box);

        var title = new Label
        {
            Text = _cityName,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 40);
        title.AddThemeColorOverride("font_color", new Color(0.88f, 0.82f, 0.68f));
        box.AddChild(title);

        _stageLabel = new Label
        {
            Text = "\u52fe\u753b\u5c71\u5ddd\u2026", // 勾画山川…
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _stageLabel.AddThemeFontSizeOverride("font_size", 18);
        _stageLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.66f, 0.58f));
        box.AddChild(_stageLabel);

        _bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, ShowPercentage = false,
            CustomMinimumSize = new Vector2(360, 10),
        };
        box.AddChild(_bar);
    }

    public override void _Process(double delta)
    {
        if (_finished)
            return;
        // 轮询进度源（volatile 读安全；自定义源如读档闭包）；进度条平滑追进免跳变
        _stageLabel.Text = (_stage?.Invoke() ?? WorldGenerator.Stage) + "\u2026";
        _bar.Value = Mathf.Lerp((float)_bar.Value, _progress?.Invoke() ?? WorldGenerator.Progress, 0.2f);

        if (_isDone?.Invoke() ?? WorldGenerator.Done)
        {
            _finished = true;
            OnFinished?.Invoke(); // 主线程回调：装配世界节点/恢复暂停
            QueueFree();
        }
    }
}
