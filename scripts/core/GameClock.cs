using System;
using Godot;

namespace Bianjing;

/// <summary>游戏时钟：暂停/0.5x/1x/2x/4x。日历与流速取自 TimeConfig（每月 3 旬、每天 24 时、每年 12 月；
/// 旬名以上旬/中旬/下旬显示，昼夜两态替代十二时辰）；
/// 日常事务按「旬」结算，人口老化等大事按「月」结算；金钱与货品不走时钟，由居民动作完成时即时结算。</summary>
public partial class GameClock : Node
{
    /// <summary>日历常量转发自 TimeConfig（调参集中在 configs 目录）。</summary>
    public static int HoursPerDay => TimeConfig.HoursPerDay;
    public static int DaysPerMonth => TimeConfig.DaysPerMonth;
    public static int MonthsPerYear => TimeConfig.MonthsPerYear;

    /// <summary>1x 速度下一个游戏小时对应的真实秒数（取自 TimeConfig）。</summary>
    public static float SecondsPerHour => TimeConfig.SecondsPerGameHour;

    /// <summary>0=暂停，0.5/1/2/4=倍速（浮点以支持 0.5x 慢放）。</summary>
    public float Speed { get; set; } = 1f;

    public int Year { get; private set; } = 1;
    public int Month { get; private set; } = 1;
    public int Day { get; private set; } = 1;
    public int Hour { get; private set; } = 6;

    /// <summary>旬名（批次九十一：每月 3 旬）：第 1 旬=上旬、第 2 旬=中旬、第 3 旬=下旬（顶栏展示）。</summary>
    public string DayName => Day switch { 1 => "上旬", 2 => "中旬", _ => "下旬" };

    /// <summary>是否夜晚（批次七十四）：白天 = [DayStartHour, NightStartHour)，其余为夜。
    /// 顶栏显示与光照联动共用；上下工作息沿用 WorkStartHour/WorkEndHour（与昼夜边界一致）。</summary>
    public bool IsNight => Hour < TimeConfig.DayStartHour || Hour >= TimeConfig.NightStartHour;

    /// <summary>一日六时显示（替代“白天/夜晚”），按真实时段边界划分：
    /// 平旦 5–9（清晨）/ 隅中 9–12（上午）/ 日中 12–15（正午）/ 晡时 15–19（下午）/ 黄昏 19–23（夜晚）/ 夜半 23–次日5（深夜）。
    /// 光照昼夜联动共用 IsNight（黄昏=夜、夜半=深夜）。</summary>
    public string DayPeriodName
    {
        get
        {
            int h = Hour;
            if (h < 5) return "夜半";   // 深夜 23–次日5
            if (h < 9) return "平旦";   // 清晨 5–9
            if (h < 12) return "隅中";  // 上午 9–12
            if (h < 15) return "日中";  // 正午 12–15
            if (h < 19) return "晡时";  // 下午 15–19
            if (h < 23) return "黄昏";  // 夜晚 19–23
            return "夜半";              // 深夜 23–次日5
        }
    }

    /// <summary>开局以来的绝对旬数（从 0 起）：供轮休等周期作息计算。</summary>
    public int AbsoluteDay => ((Year - 1) * MonthsPerYear + (Month - 1)) * DaysPerMonth + (Day - 1);

    /// <summary>每过一游戏旬触发（日常结算：生长/求职/税赋/物品等）。</summary>
    public event Action DayPassed;

    /// <summary>每过一游戏月触发（大事结算：老化/生死/动物繁育等）。</summary>
    public event Action MonthPassed;

    private float _acc;
    private float _resumeSpeed = 1f;

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
            case Key.Key1: Speed = 1f; break;
            case Key.Key2: Speed = 2f; break;
            case Key.Key3: Speed = 4f; break;
            default: return; // 非时钟按键不拦截
        }
        GetViewport().SetInputAsHandled(); // 已处理：不再传给焦点按钮防误触
    }
}
