using System.Runtime.InteropServices;

namespace AsyncInput.Mobile;

/// <summary>
/// Android 单调时钟与 Unity DSP 时钟之间的直接换算。
/// </summary>
/// <remarks>
/// <para>
/// 触摸时间戳来自 <c>CLOCK_MONOTONIC</c>，歌曲位置最终以
/// <c>AudioSettings.dspTime</c> 为基准。判定当刻直接测量两者的差值，
/// 不经过帧缓存的 <c>Time.unscaledTime</c> 或 <c>scrConductor.dspTime</c>。
/// </para>
/// <para>
/// <b>时钟源必须与 <c>AMotionEvent_getEventTime</c> 一致。</b>
/// 因此直接 P/Invoke <c>clock_gettime(CLOCK_MONOTONIC)</c>，
/// 不使用 <c>Stopwatch.GetTimestamp()</c> —— 后者的底层时钟源不保证相同，
/// 一旦不同源，差值就失去意义，重算出来的角度会系统性偏移。
/// </para>
/// </remarks>
internal sealed class ClockSync
{
    /// <summary>Linux <c>CLOCK_MONOTONIC</c>，与输入事件时间戳同源。</summary>
    private const int ClockMonotonic = 1;

    private const double NanosPerSecond = 1_000_000_000d;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc.so", EntryPoint = "clock_gettime", ExactSpelling = true, SetLastError = true)]
    private static extern int clock_gettime(int clockId, out Timespec timespec);

    /// <summary>
    /// 单调时钟与 DSP 时钟的差值（秒）：<c>monotonic - dspTime</c>。
    /// </summary>
    private double _anchor;

    private bool _hasAnchor;

    private double _sampleSpanMicros;

    /// <summary>当前锚点，单位毫秒，供调试 HUD 显示。</summary>
    internal double AnchorMillis => _anchor * 1000d;

    /// <summary>最近一次夹心采样覆盖的时间，越小表示锚点测量越精确。</summary>
    internal double SampleSpanMicros => _sampleSpanMicros;

    /// <summary>锚点是否已建立。未建立时不得进行任何角度重算。</summary>
    internal bool IsReady => _hasAnchor;

    /// <summary>读取当前单调时钟，纳秒。失败返回 0。</summary>
    internal static long GetMonotonicNanos()
    {
        try
        {
            if (clock_gettime(ClockMonotonic, out Timespec now) != 0
                || now.Seconds <= 0L
                || now.Nanoseconds < 0L
                || now.Nanoseconds >= 1_000_000_000L)
            {
                return 0L;
            }

            return checked(now.Seconds * 1_000_000_000L + now.Nanoseconds);
        }
        catch
        {
            // A desktop runtime or an older Android image may not resolve libc.so.
            // Clock failure must disable timestamp correction for that sample, not
            // escape through an IL2CPP hook and destabilize the game.
            return 0L;
        }
    }

    /// <summary>
    /// 丢弃已有锚点。场景切换、重开、暂停恢复后游戏时间轴会跳变，必须重新建立。
    /// </summary>
    internal void Reset()
    {
        _hasAnchor = false;
        _anchor = 0d;
        _sampleSpanMicros = 0d;
    }

    /// <summary>
    /// 用一次夹心采样更新锚点。
    /// </summary>
    /// <param name="dspTime">在两次单调时钟读取之间取得的 DSP 时间</param>
    /// <param name="monotonicBeforeNanos">读取 DSP 前的单调时钟</param>
    /// <param name="monotonicAfterNanos">读取 DSP 后的单调时钟</param>
    internal void Update(double dspTime, long monotonicBeforeNanos, long monotonicAfterNanos)
    {
        if (dspTime <= 0d || monotonicBeforeNanos <= 0L || monotonicAfterNanos < monotonicBeforeNanos)
            return;

        long spanNanos = monotonicAfterNanos - monotonicBeforeNanos;
        long midpointNanos = monotonicBeforeNanos + spanNanos / 2L;
        double measured = midpointNanos / NanosPerSecond - dspTime;
        _sampleSpanMicros = spanNanos / 1000d;

        // Every event is converted with the latest same-clock measurement.
        // Scene and pause transitions explicitly reset the queue; a measured
        // anchor delta is not itself evidence that an input is invalid.
        _anchor = measured;
        _hasAnchor = true;
    }

    /// <summary>
    /// 把输入事件的单调时钟时间戳直接换算成同一时刻的 DSP 时间。
    /// </summary>
    internal double ToDspTime(long eventTimeNanos)
    {
        return eventTimeNanos / NanosPerSecond - _anchor;
    }
}
