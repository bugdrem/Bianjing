using System;
using Godot;

namespace Bianjing;

/// <summary>游戏时钟：暂停/1x/2x/3x，按「月」为最小结算单位。</summary>
public partial class GameClock : Node
{
    /// <summary>1x 速度下一个游戏月对应的真实秒数。</summary>
    public const float SecondsPerMonth = 5f;

    /// <summary>0=暂停, 1/2/3=倍速。</summary>
    public int Speed { get; set; } = 1;

    public int Year { get; private set; } = 1;
    public int Month { get; private set; } = 1;

    public event Action MonthPassed;

    private float _acc;
    private int _resumeSpeed = 1;

    public override void _Process(double delta)
    {
        if (Speed <= 0)
            return;

        _acc += (float)delta * Speed;
        while (_acc >= SecondsPerMonth)
        {
            _acc -= SecondsPerMonth;
            Month++;
            if (Month > 12)
            {
                Month = 1;
                Year++;
            }
            MonthPassed?.Invoke();
        }
    }

    /// <summary>读档时恢复日期。</summary>
    public void SetDate(int year, int month)
    {
        Year = year;
        Month = month;
        _acc = 0f;
    }

    public void TogglePause()
    {
        if (Speed > 0)
        {
            _resumeSpeed = Speed;
            Speed = 0;
        }
        else
        {
            Speed = _resumeSpeed;
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey key || !key.Pressed || key.Echo)
            return;

        switch (key.Keycode)
        {
            case Key.Space: TogglePause(); break;
            case Key.Key1: Speed = 1; break;
            case Key.Key2: Speed = 2; break;
            case Key.Key3: Speed = 3; break;
        }
    }
}
