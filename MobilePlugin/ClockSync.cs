using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AsyncInput.Mobile;

/// <summary>
/// Android 单调时钟、Unity DSP 时钟和官方 AsyncInput 的 wall-tick 时钟之间的换算。
/// </summary>
/// <remarks>
/// 触摸事件使用 <c>CLOCK_MONOTONIC</c>，官方 <c>ProcessKeyInputs</c> 使用 DateTime-like
/// tick，而角度回退路径使用 <c>AudioSettings.dspTime</c>。三者都在主线程用夹心采样建立锚点，
/// 不把渲染帧时间当成输入发生时间。
/// </remarks>
internal sealed class ClockSync
{
    private const int ClockRealtime = 0;
    private const int ClockMonotonic = 1;
    private const double NanosPerSecond = 1_000_000_000d;
    private const long NanosPerDateTimeTick = 100L;
    private const long UnixEpochTicks = 621355968000000000L;

    private const int NativeClockUnknown = 0;
    private const int NativeClockAvailable = 1;
    private const int NativeClockUnavailable = 2;

    private const int MonotonicSourceUnknown = 0;
    private const int MonotonicSourceNative = 1;
    private const int MonotonicSourceStopwatch = 2;

    /// <summary>DSP 锚点跳变阈值，覆盖音频重建、暂停恢复和场景切换。</summary>
    private const double DspJumpThresholdSeconds = 0.05d;

    /// <summary>wall-tick 锚点跳变阈值，20ms 足以区分时钟漂移和系统挂起。</summary>
    private const long WallJumpThresholdTicks = 200_000L;

    /// <summary>
    /// 夹心采样被调度器长时间打断时不更新原点；否则调度延迟会被错误地当作时钟偏移。
    /// </summary>
    private const long MaxSampleSpanNanos = 2_000_000L;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc.so", EntryPoint = "clock_gettime", ExactSpelling = true, SetLastError = true)]
    private static extern int clock_gettime(int clockId, out Timespec timespec);

    // P/Invoke 失败时不要在每个渲染帧重复抛异常。Stopwatch 在 Android 上同样使用
    // 单调时钟，且和 MotionEvent 的 elapsed/monotonic 时间轴保持一致。
    private static int _nativeClockState;
    private static int _monotonicSource;

    // 单调时钟与 DSP 时钟的差值（秒）：monotonic - dspTime。
    private double _dspAnchor;
    private bool _hasDspAnchor;
    private double _dspSampleSpanMicros;

    // DateTime-like UTC tick 与单调时钟 tick 的差值。
    private long _wallOriginTicks;
    private bool _hasWallOrigin;
    private double _wallSampleSpanMicros;

    /// <summary>当前 DSP 锚点，单位毫秒。</summary>
    internal double AnchorMillis => _dspAnchor * 1000d;

    /// <summary>最近 DSP 夹心采样覆盖的时间，单位微秒。</summary>
    internal double SampleSpanMicros => _dspSampleSpanMicros;

    /// <summary>最近 wall-tick 夹心采样覆盖的时间，单位微秒。</summary>
    internal double WallSampleSpanMicros => _wallSampleSpanMicros;

    internal bool IsReady => _hasDspAnchor;

    internal bool IsWallReady => _hasWallOrigin;

    internal static string MonotonicSourceName => Volatile.Read(ref _monotonicSource) switch
    {
        MonotonicSourceNative => "CLOCK_MONOTONIC",
        MonotonicSourceStopwatch => "Stopwatch 单调时钟回退",
        _ => "未检测",
    };

    /// <summary>读取与 Android MotionEvent 同源的单调时钟，纳秒。</summary>
    internal static long GetMonotonicNanos()
    {
        int source = Volatile.Read(ref _monotonicSource);
        if (source != MonotonicSourceStopwatch
            && TryGetNativeTime(ClockMonotonic, out Timespec nativeNow))
        {
            long nativeNanos = TimespecToNanos(nativeNow);
            if (nativeNanos > 0L)
            {
                Volatile.Write(ref _monotonicSource, MonotonicSourceNative);
                return nativeNanos;
            }
        }

        if (source == MonotonicSourceUnknown)
            Interlocked.CompareExchange(
                ref _monotonicSource,
                MonotonicSourceStopwatch,
                MonotonicSourceUnknown);
        else if (source == MonotonicSourceNative)
            Volatile.Write(ref _monotonicSource, MonotonicSourceStopwatch);

        return GetStopwatchNanos();
    }

    /// <summary>
    /// 通过 libc 读取 timespec。Android/桌面运行时的库名解析不同，因此 native 调用
    /// 失败后永久切换到托管回退，避免热路径持续产生 DllNotFoundException。
    /// </summary>
    private static bool TryGetNativeTime(int clockId, out Timespec now)
    {
        now = default;
        if (Volatile.Read(ref _nativeClockState) == NativeClockUnavailable)
            return false;

        try
        {
            if (clock_gettime(clockId, out now) != 0
                || now.Nanoseconds < 0L
                || now.Nanoseconds >= 1_000_000_000L)
            {
                return false;
            }

            Volatile.Write(ref _nativeClockState, NativeClockAvailable);
            return true;
        }
        catch
        {
            Volatile.Write(ref _nativeClockState, NativeClockUnavailable);
            now = default;
            return false;
        }
    }

    private static long TimespecToNanos(Timespec now)
    {
        try
        {
            return checked(now.Seconds * 1_000_000_000L + now.Nanoseconds);
        }
        catch (OverflowException)
        {
            return 0L;
        }
    }

    private static long GetStopwatchNanos()
    {
        long timestamp = Stopwatch.GetTimestamp();
        long frequency = Stopwatch.Frequency;
        if (timestamp <= 0L || frequency <= 0L)
            return 0L;

        try
        {
            long seconds = timestamp / frequency;
            long remainder = timestamp % frequency;
            long fractionalNanos = (long)(remainder * (1_000_000_000d / frequency));
            return checked(seconds * 1_000_000_000L + fractionalNanos);
        }
        catch (OverflowException)
        {
            return 0L;
        }
    }

    /// <summary>
    /// 读取 UTC DateTime-like tick。官方 PC async 输入使用同一数量级的 wall tick。
    /// </summary>
    internal static long GetRealtimeTicks()
    {
        if (TryGetNativeTime(ClockRealtime, out Timespec nativeNow))
        {
            try
            {
                return checked(
                    UnixEpochTicks
                    + checked(nativeNow.Seconds * 10_000_000L)
                    + nativeNow.Nanoseconds / NanosPerDateTimeTick);
            }
            catch (OverflowException)
            {
                // 交给托管时钟回退。
            }
        }

        // 在非 Android 的构建/测试环境中回退到托管时钟。
        return DateTime.UtcNow.Ticks;
    }

    /// <summary>场景切换、重开、暂停恢复后丢弃所有时钟锚点。</summary>
    internal void Reset()
    {
        _hasDspAnchor = false;
        _dspAnchor = 0d;
        _dspSampleSpanMicros = 0d;
        _hasWallOrigin = false;
        _wallOriginTicks = 0L;
        _wallSampleSpanMicros = 0d;
    }

    /// <summary>用一次夹心采样更新 monotonic 到 DSP 的锚点。</summary>
    internal bool Update(
        double dspTime,
        long monotonicBeforeNanos,
        long monotonicAfterNanos)
    {
        if (dspTime <= 0d
            || monotonicBeforeNanos <= 0L
            || monotonicAfterNanos < monotonicBeforeNanos)
        {
            return false;
        }

        long spanNanos = monotonicAfterNanos - monotonicBeforeNanos;
        if (spanNanos > MaxSampleSpanNanos)
            return false;
        long midpointNanos = monotonicBeforeNanos + spanNanos / 2L;
        double measured = midpointNanos / NanosPerSecond - dspTime;
        _dspSampleSpanMicros = spanNanos / 1000d;

        bool reset = !_hasDspAnchor || Math.Abs(measured - _dspAnchor) > DspJumpThresholdSeconds;
        if (reset)
        {
            _dspAnchor = measured;
            _hasDspAnchor = true;
            return true;
        }

        // 不做平滑。事件时间精度比历史平均值更重要，且夹心采样已限制调用时序误差。
        _dspAnchor = measured;
        return false;
    }

    /// <summary>
    /// 更新 monotonic 到 wall-tick 的锚点。
    /// 返回 true 仅表示已有锚点发生了不连续跳变；首次建立锚点返回 false。
    /// </summary>
    internal bool UpdateWallAnchor(
        long wallTicks,
        long monotonicBeforeNanos,
        long monotonicAfterNanos)
    {
        if (wallTicks <= 0L
            || monotonicBeforeNanos <= 0L
            || monotonicAfterNanos < monotonicBeforeNanos)
        {
            return false;
        }

        long spanNanos = monotonicAfterNanos - monotonicBeforeNanos;
        if (spanNanos > MaxSampleSpanNanos)
            return false;
        long midpointNanos = monotonicBeforeNanos + spanNanos / 2L;
        long measured = wallTicks - midpointNanos / NanosPerDateTimeTick;
        _wallSampleSpanMicros = spanNanos / 1000d;

        bool hadOrigin = _hasWallOrigin;
        bool jumped = hadOrigin && Math.Abs(measured - _wallOriginTicks) > WallJumpThresholdTicks;
        _wallOriginTicks = measured;
        _hasWallOrigin = true;
        return jumped;
    }

    /// <summary>把触摸事件的单调时间换算为官方 ProcessKeyInputs 使用的 wall tick。</summary>
    internal long ToWallTicks(long eventTimeNanos)
    {
        if (!_hasWallOrigin || eventTimeNanos <= 0L)
            return 0L;
        return _wallOriginTicks + eventTimeNanos / NanosPerDateTimeTick;
    }

    /// <summary>把当前单调时间换算为同一 wall tick 时间轴。</summary>
    internal long GetWallTicks(long monotonicNanos)
    {
        if (!_hasWallOrigin || monotonicNanos <= 0L)
            return 0L;
        return _wallOriginTicks + monotonicNanos / NanosPerDateTimeTick;
    }

    /// <summary>把输入事件的单调时钟时间戳换算成 AudioSettings.dspTime 时间轴。</summary>
    internal double ToDspTime(long eventTimeNanos)
    {
        return eventTimeNanos / NanosPerSecond - _dspAnchor;
    }
}
