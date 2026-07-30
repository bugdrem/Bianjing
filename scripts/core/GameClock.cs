using System;
using Godot;

namespace Bianjing;

/// <summary>游戏时钟：暂停/1x/2x/4x。日历与流速取自 TimeConfig（每月 12 天、每天 24 时=12 时辰、每年 12 月）；
/// 日常事务按「日」结算，人口老化等大事按「月」结算；金钱与货品不走时钟，由居民动作完成时即时结算。</summary>
public partial class GameClock : Node
{
    /// <summary>日历常量转发自 TimeConfig（调参集中在 configs 目录）。</summary>
    public static int HoursPerDay => TimeConfig.HoursPerDay;
    public static int DaysPerMonth => TimeConfig.DaysPerMonth;
    public static int MonthsPerYear => TimeConfig.MonthsPerYear;

    /// <summary>1x 速度下一个游戏小时对应的真实秒数（取自 TimeConfig）。</summary>
    public static float SecondsPerHour => TimeConfig.SecondsPerGameHour;

    /// <summary>0=暂停，1/2/4=倍速。</summary>
    public int Speed { get; set; } = 1;

    public int Year { get; private set; } = 1;
    public int Month { get; private set; } = 1;
    public int Day { get; private set; } = 1;
    public int Hour { get; private set; } = 6;

    /// <summary>开局以来的绝对天数（从 0 起）：供轮休等周期作息计算。</summary>
    public int AbsoluteDay => ((Year - 1) * MonthsPerYear + (Month - 1)) * DaysPerMonth + (Day - 1);

    /// <summary>每过一游戏日触发（日常结算：生长/求职/税赋/物品等）。</summary>
    public event Action DayPassed;

    /// <summary>每过一游戏月触发（大事结算：老化/生死/动物繁育等）。</summary>
    public event Action MonthPassed;

    private float _acc;
    private int _resumeSpeed = 1;

    /// <summary>十二时辰名，Hour 23-1 点为子时。</summary>
    private static readonly string[] ShichenNames =
        { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };

    /// <summary>当前时辰名（如「午时」）。</summary>
    public string Shichen => ShichenNames[(Hour + 1) / 2 % 12] + "时";

    public override void _Process(double delta)
    {
        if (Speed <= 0)
            return;

        _acc += (float)delta * Speed;
        while (_acc >= SecondsPerHour)
        {
            _acc -= SecondsPerHour;
            AdvanceHour();
        }
    }

    private void AdvanceHour()
    {
        Hour++;
        if (Hour < HoursPerDay)
            return;

        Hour = 0;
        Day++;
        bool monthTurn = Day > DaysPerMonth;
        if (monthTurn)
        {
            Day = 1;
            Month++;
            if (Month > MonthsPerYear)
            {
                Month = 1;
                Year++;
            }
        }

        DayPassed?.Invoke();
        if (monthTurn)
            MonthPassed?.Invoke();
    }

    /// <summary>读档/新开局时恢复日期。</summary>
    public void SetDate(int year, int month, int day = 1, int hour = 6)
    {
        Year = year;
        Month = month;
        Day = Math.Clamp(day, 1, DaysPerMonth);
        Hour = Math.Clamp(hour, 0, HoursPerDay - 1);
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

    /// <summary>用 _Input（优先于 UI）而非 _UnhandledKeyInput：否则点过任意按钮后焦点留在按钮上，
    /// 空格会被当成 ui_accept 触发那个按钮并吞掉事件，表现为“暂停失灵/松手即恢复”；
    /// 正在文本框打字时放行（城市名/存档名输入）。</summary>
    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey key || !key.Pressed || key.Echo)
            return;
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)
            return; // 打字中：空格/数字键归输入框

        switch (key.Keycode)
        {
            case Key.Space: TogglePause(); break;
            case Key.Key1: Speed = 1; break;
            case Key.Key2: Speed = 2; break;
            case Key.Key3: Speed = 4; break;
            default: return; // 非时钟按键不拦截
        }
        GetViewport().SetInputAsHandled(); // 已处理：不再传给焦点按钮防误触
    }
}
