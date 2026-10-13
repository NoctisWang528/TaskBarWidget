using System;

namespace AFMediaBar.Classes.Services;

/// <summary>滑杆的调节方向。 / Adjustment direction of a slider.</summary>
public enum SliderAdjustmentDirection
{
    /// <summary>向最小值方向调节。 / Adjust towards the minimum.</summary>
    Decrease = -1,

    /// <summary>向最大值方向调节。 / Adjust towards the maximum.</summary>
    Increase = 1
}

/// <summary>
/// 设置页滑杆的步进与长按加速。全部为纯计算，因此可以直接单元测试，界面只负责把结果写回 <c>Slider.Value</c>。
/// Step resolution and hold acceleration for settings-page sliders. Everything here is pure arithmetic so it can be unit tested
/// directly, leaving the interface with nothing to do but write the result back into <c>Slider.Value</c>.
/// </summary>
public static class SliderAdjustmentPolicy
{
    /// <summary>长按开始加速前的等待时间；这段时间内只走一步，短按因此就是一次一格。 / Delay before a held key starts accelerating; one step is applied inside it, so a short press is exactly one step.</summary>
    public static readonly TimeSpan AccelerationStartDelay = TimeSpan.FromMilliseconds(420);

    /// <summary>加速期间每次重复的间隔。 / Interval between repeats while accelerating.</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(110);

    /// <summary>倍率翻倍所需的长按时间。 / Hold time required to double the multiplier.</summary>
    public static readonly TimeSpan AccelerationDoublingPeriod = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// 单次重复的倍率上限。没有上限时长按会一步跨过整个区间，用户松手时再也回不到想要的取值。
    /// Upper bound of one repeat's multiplier. Without it a long hold crosses the whole range in a single step and the user can no
    /// longer land on the value they wanted.
    /// </summary>
    public const int MaximumRepeatMultiplier = 16;

    /// <summary>
    /// 一个步长至少要占区间的比例（1%）。大区间滑杆（例如固定宽度 120–4096 DIP）沿用它自己的刻度或小步长时，
    /// 一次按键只移动千分之几，用户按十几次才看到读数变化一次；这个下限保证任何滑杆的"一格"都是看得见的。
    /// Minimum share of the range a single step must cover (1%). When a wide-range slider (the fixed width from 120 to 4096 DIP, for
    /// example) uses its own tick or small change, one press moves a few thousandths and the reading only changes after a dozen
    /// presses; this floor guarantees that one step of any slider is visible.
    /// </summary>
    public const double MinimumStepRangeFraction = 0.01;

    /// <summary>
    /// 解析一个滑杆的基础步进：先取刻度间距，其次取小步长，两者都不可用时按区间的百分之一折算，
    /// 最后与区间下限取较大者。因此"一格"在小滑杆上仍是原来的精细步长，在大滑杆上是看得见的距离。
    /// Resolves a slider's base step: the tick spacing first, then the small change, then a hundredth of the range, and finally the
    /// larger of that and the range floor. One step therefore stays fine on a small slider and becomes visible on a wide one.
    /// </summary>
    /// <param name="minimum">滑杆下限。/ Slider minimum.</param>
    /// <param name="maximum">滑杆上限。/ Slider maximum.</param>
    /// <param name="tickFrequency">刻度间距；未启用吸附时为 0。/ Tick spacing, or 0 when snapping is off.</param>
    /// <param name="smallChange">键盘小步长。/ Keyboard small change.</param>
    public static double ResolveBaseStep(double minimum, double maximum, double tickFrequency, double smallChange)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return 0;

        var range = maximum - minimum;
        var floor = RoundUpToNiceValue(range * MinimumStepRangeFraction);
        var declared = double.IsFinite(tickFrequency) && tickFrequency > 0
            ? tickFrequency
            : double.IsFinite(smallChange) && smallChange > 0
                ? smallChange
                : floor;
        return Math.Min(Math.Max(declared, floor), range);
    }

    /// <summary>
    /// 把比例算出的下限向上取到一个"整"值，使大区间滑杆的步长落在 40 这样的数上，而不是 39.76。
    /// Rounds a ratio-derived floor up onto a round value so a wide-range slider steps by something like 40 rather than 39.76.
    /// </summary>
    private static double RoundUpToNiceValue(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            return 0;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        return Math.Ceiling(value / magnitude) * magnitude;
    }

    /// <summary>
    /// 把取值吸附到步长网格上。界面可能读到一个不在网格上的旧值（早期设置文件），
    /// 不先吸附的话第一次调节会落在一个滑杆自己表示不出来的位置上。
    /// Snaps a value onto the step grid. The interface can read an off-grid value from an older settings file, and without this
    /// first snap the very first adjustment would land somewhere the slider cannot express.
    /// </summary>
    /// <param name="value">待吸附的取值。/ Value to snap.</param>
    /// <param name="minimum">滑杆下限。/ Slider minimum.</param>
    /// <param name="maximum">滑杆上限。/ Slider maximum.</param>
    /// <param name="step">步长；非正数时只做夹取。/ Step, or a non-positive value to only clamp.</param>
    public static double Snap(double value, double minimum, double maximum, double step)
    {
        if (!double.IsFinite(value))
            return minimum;
        var clamped = Math.Clamp(value, minimum, maximum);
        if (!double.IsFinite(step) || step <= 0)
            return clamped;

        var steps = Math.Round((clamped - minimum) / step, MidpointRounding.AwayFromZero);
        return Math.Clamp(minimum + steps * step, minimum, maximum);
    }

    /// <summary>按方向把取值推进一步并夹取。 / Advances a value by one step in the given direction and clamps it.</summary>
    /// <param name="value">当前取值。/ Current value.</param>
    /// <param name="step">本步的目标步长（长按加速时已经乘过倍率）。/ Target step for this move, already multiplied while accelerating.</param>
    /// <param name="direction">调节方向。/ Adjustment direction.</param>
    /// <param name="minimum">滑杆下限。/ Slider minimum.</param>
    /// <param name="maximum">滑杆上限。/ Slider maximum.</param>
    public static double Adjust(double value, double step, SliderAdjustmentDirection direction, double minimum, double maximum)
    {
        if (maximum <= minimum)
            return minimum;

        var aligned = Snap(value, minimum, maximum, step);
        var next = aligned + step * (int)direction;
        // 越界时直接返回边界：把越界值交给 Snap 会先被夹到边界、再按网格取整，一个大于整个区间的步长会因此退回最小值，
        // 用户按住方向键反而看到取值往反方向跳。
        // Out-of-range targets return the bound directly: handing them to Snap would clamp to the bound and then round onto the
        // grid, so a step larger than the whole range would fall back to the minimum and a held arrow key would appear to move the
        // value backwards.
        if (next <= minimum)
            return minimum;
        if (next >= maximum)
            return maximum;
        return Snap(next, minimum, maximum, step);
    }

    /// <summary>
    /// 长按时的重复倍率：前 0.6 秒为 1，之后每 0.6 秒翻一倍，并以 <see cref="MaximumRepeatMultiplier"/> 封顶。
    /// Repeat multiplier while a key is held: one for the first 0.6 seconds, doubling every 0.6 seconds after that and capped at
    /// <see cref="MaximumRepeatMultiplier"/>.
    /// </summary>
    /// <param name="held">该方向已经按住的时间。/ Time the direction has been held.</param>
    public static int ResolveRepeatMultiplier(TimeSpan held)
    {
        if (held <= AccelerationStartDelay)
            return 1;

        var doubling = (int)((held - AccelerationStartDelay).TotalMilliseconds / AccelerationDoublingPeriod.TotalMilliseconds);
        return doubling <= 0 ? 1 : Math.Min(1 << Math.Min(doubling, 30), MaximumRepeatMultiplier);
    }

    /// <summary>长按时的实际步长。 / Effective step while a key is held.</summary>
    /// <param name="baseStep">基础步进。/ Base step.</param>
    /// <param name="held">该方向已经按住的时间。/ Time the direction has been held.</param>
    /// <param name="minimum">滑杆下限。/ Slider minimum.</param>
    /// <param name="maximum">滑杆上限。/ Slider maximum.</param>
    public static double ResolveRepeatStep(double baseStep, TimeSpan held, double minimum, double maximum)
    {
        if (!double.IsFinite(baseStep) || baseStep <= 0)
            return 0;

        var step = baseStep * ResolveRepeatMultiplier(held);
        var range = maximum - minimum;
        return double.IsFinite(range) && range > 0 ? Math.Min(step, range) : step;
    }
}
