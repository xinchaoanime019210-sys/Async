using System.Runtime.InteropServices;

namespace AsyncInput.Mobile;

/// <summary>
/// Converts Android monotonic touch timestamps to the DateTime tick domain
/// used by <c>SkyHookEvent.GetTimeInTicks</c>, while keeping the original
/// async offset aligned with a precise monotonic/DSP sample.
/// </summary>
/// <remarks>
/// The queue consumer and player state machine remain in the APK. The clock
/// keeps the useful Iridium v3 policy: recover from a large DSP discontinuity
/// and correct persistent offset drift from a 30-sample window.
/// </remarks>
internal sealed class OriginalAsyncClock
{
    private const int ClockMonotonic = 1;
    private const long NanosPerSecond = 1_000_000_000L;
    private const long FallbackAsyncXrunThresholdTicks = 100 * TimeSpan.TicksPerMillisecond;
    private const long FallbackAsyncAverageCorrectionThresholdTicks = 5 * TimeSpan.TicksPerMillisecond;
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const int SampleCount = 30;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc.so", EntryPoint = "clock_gettime", ExactSpelling = true, SetLastError = true)]
    private static extern int clock_gettime(int clockId, out Timespec timespec);

    private readonly ulong[] _asyncOffsetSamples = new ulong[SampleCount];

    private long _wallOffsetTicks;
    private bool _wallOffsetReady;

    private ulong _asyncOffsetTicks;
    private bool _asyncOffsetReady;
    private int _asyncSampleIndex;
    private int _asyncSampleCount;

    internal bool IsReady => _wallOffsetReady;

    // Iridium uses a fixed QPC-to-DateTime bias. The mobile equivalent is one
    // monotonic-to-DateTime anchor per gameplay session, not a per-frame wall
    // clock adjustment.
    internal int WallSampleCount => _wallOffsetReady ? 1 : 0;

    internal int AsyncSampleCount => _asyncSampleCount;

    internal double WallOffsetMilliseconds
        => _wallOffsetTicks / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>Reads the same Android monotonic source as MotionEvent.</summary>
    internal static long GetMonotonicNanos()
    {
        try
        {
            if (clock_gettime(ClockMonotonic, out Timespec now) != 0
                || now.Seconds <= 0L
                || now.Nanoseconds < 0L
                || now.Nanoseconds >= NanosPerSecond)
            {
                return 0L;
            }

            return checked(now.Seconds * NanosPerSecond + now.Nanoseconds);
        }
        catch
        {
            return 0L;
        }
    }

    internal void Reset()
    {
        _wallOffsetTicks = 0L;
        _wallOffsetReady = false;

        _asyncOffsetTicks = 0UL;
        _asyncOffsetReady = false;
        _asyncSampleIndex = 0;
        _asyncSampleCount = 0;
        Array.Clear(_asyncOffsetSamples);
    }

    internal void ResetAsyncOffsetCorrection()
    {
        _asyncOffsetTicks = 0UL;
        _asyncOffsetReady = false;
        ResetAsyncSamples();
    }

    /// <summary>
    /// Establishes the fixed monotonic-to-DateTime bias used by the game for
    /// the current session and returns the current precise DateTime tick.
    /// </summary>
    internal bool TryGetCurrentDateTimeTicks(out ulong dateTimeTicks)
    {
        dateTimeTicks = 0UL;
        long now = GetMonotonicNanos();
        if (now <= 0L)
            return false;

        if (!_wallOffsetReady)
        {
            long before = now;
            long wallTicks;
            try
            {
                wallTicks = DateTime.Now.Ticks;
            }
            catch
            {
                return false;
            }

            long after = GetMonotonicNanos();
            if (after < before)
                return false;

            long midpoint = before + (after - before) / 2L;
            try
            {
                _wallOffsetTicks = checked(wallTicks - midpoint / 100L);
            }
            catch
            {
                return false;
            }

            _wallOffsetReady = true;
        }

        return TryAddDateTicks(now / 100L, _wallOffsetTicks, out dateTimeTicks);
    }

    /// <summary>Converts one Android touch timestamp to a DateTime tick.</summary>
    internal bool TryConvertEventTime(long eventTimeNanos, out long dateTimeTicks)
    {
        dateTimeTicks = 0L;
        if (eventTimeNanos <= 0L)
            return false;

        if (!_wallOffsetReady && !TryGetCurrentDateTimeTicks(out _))
            return false;

        if (!TryAddDateTicks(
                eventTimeNanos / 100L,
                _wallOffsetTicks,
                out ulong converted)
            || converted > long.MaxValue)
        {
            return false;
        }

        dateTimeTicks = (long)converted;
        return dateTimeTicks > 0L;
    }

    /// <summary>
    /// Applies the Iridium v3-style XRUN and 30-sample correction policy to
    /// the offset calculated by the game's original UpdateOffsetTime.
    /// </summary>
    internal bool TryCorrectAsyncOffset(
        ulong measuredOffset,
        ulong originalOffset,
        double audioBufferSeconds,
        out ulong correctedOffset)
    {
        correctedOffset = 0UL;
        if (measuredOffset == 0UL)
            return false;

        if (!_asyncOffsetReady)
        {
            _asyncOffsetReady = true;
            _asyncOffsetTicks = measuredOffset;
            ResetAsyncSamples();
            correctedOffset = measuredOffset;
            return true;
        }

        ulong baseline = originalOffset != 0UL ? originalOffset : _asyncOffsetTicks;
        long xrunThreshold = GetAudioThreshold(
            audioBufferSeconds,
            TicksPerSecond * 4L,
            FallbackAsyncXrunThresholdTicks);
        long averageCorrectionThreshold = GetAudioThreshold(
            audioBufferSeconds,
            TicksPerSecond / 2L,
            FallbackAsyncAverageCorrectionThresholdTicks);

        long difference = SignedDifference(measuredOffset, baseline);
        if (Math.Abs(difference) > xrunThreshold)
        {
            _asyncOffsetTicks = measuredOffset;
            ResetAsyncSamples();
            correctedOffset = measuredOffset;
            return true;
        }

        StoreAsyncSample(measuredOffset);
        ulong candidate = baseline;
        if (_asyncSampleCount == SampleCount)
        {
            ulong average = AverageAsyncSamples();
            long averageDelta = SignedDifference(average, baseline);
            if (Math.Abs(averageDelta) > averageCorrectionThreshold)
                candidate = average;
        }

        _asyncOffsetTicks = candidate;
        correctedOffset = candidate;
        return true;
    }

    private static long GetAudioThreshold(
        double audioBufferSeconds,
        long ticksPerBufferMultiplier,
        long fallback)
    {
        if (!double.IsFinite(audioBufferSeconds) || audioBufferSeconds <= 0d)
            return fallback;

        double value = audioBufferSeconds * ticksPerBufferMultiplier;
        if (!double.IsFinite(value) || value <= 0d)
            return fallback;
        if (value >= long.MaxValue)
            return long.MaxValue;
        return Math.Max(1L, (long)value);
    }

    private void StoreAsyncSample(ulong sample)
    {
        _asyncOffsetSamples[_asyncSampleIndex] = sample;
        _asyncSampleIndex = (_asyncSampleIndex + 1) % SampleCount;
        if (_asyncSampleCount < SampleCount)
            _asyncSampleCount++;
    }

    private ulong AverageAsyncSamples()
    {
        ulong baseline = _asyncOffsetTicks;
        double delta = 0d;
        for (int i = 0; i < SampleCount; i++)
            delta += SignedDifference(_asyncOffsetSamples[i], baseline);

        double average = baseline + delta / SampleCount;
        if (!double.IsFinite(average) || average <= 0d || average >= ulong.MaxValue)
            return baseline;
        return (ulong)average;
    }

    private void ResetAsyncSamples()
    {
        _asyncSampleIndex = 0;
        _asyncSampleCount = 0;
        Array.Clear(_asyncOffsetSamples);
    }

    private static long SignedDifference(ulong left, ulong right)
    {
        if (left >= right)
        {
            ulong delta = left - right;
            return delta > long.MaxValue ? long.MaxValue : (long)delta;
        }

        ulong reverse = right - left;
        return reverse > long.MaxValue ? long.MinValue : -(long)reverse;
    }

    private static bool TryAddDateTicks(
        long monotonicTicks,
        long offsetTicks,
        out ulong dateTimeTicks)
    {
        dateTimeTicks = 0UL;
        try
        {
            long value = checked(monotonicTicks + offsetTicks);
            if (value <= 0L)
                return false;
            dateTimeTicks = (ulong)value;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
