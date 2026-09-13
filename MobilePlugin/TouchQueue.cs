using System.Collections.Concurrent;
using StArray.ModManager.Android.Native;

namespace AsyncInput.Mobile;

/// <summary>
/// The complete raw edge needed by the direct mobile async producer. Android
/// reports the position with the edge, and the game uses that position to
/// reject touches over its own UI before treating a Down as gameplay input.
/// </summary>
internal readonly record struct MobileTouchEdge(
    AndroidInput.MotionAction Action,
    int PointerId,
    long EventTimeNanos,
    float X,
    float Y);

/// <summary>
/// Android 原始触摸边沿的跨线程有界队列。
/// </summary>
/// <remarks>
/// Android 输入线程只把值类型快照放进队列；所有关联、过期清理和游戏对象访问都在
/// Unity 主线程完成。按下与抬起分开保存，避免普通点按消费 Down 时把长按的 Up 一并丢掉。
/// </remarks>
internal static class TouchQueue
{
    private const int CapacityPerEdge = 128;
    // The PC producer uses an unbounded ConcurrentQueue.  Keep a safety cap
    // for a stalled Unity thread, but make it large enough that a short
    // render/audio hiccup cannot turn into a lost input burst.
    private const int AsyncCapacity = 4096;

    private static readonly ConcurrentQueue<TouchTimestampInfo> Presses = new();
    private static readonly ConcurrentQueue<TouchTimestampInfo> Releases = new();
    // The mobile async bridge needs the original cross-edge order. Keep this
    // independent of the fallback's press/release association queues.
    private static readonly ConcurrentQueue<MobileTouchEdge> AsyncEvents = new();

    // 仅由 Unity 主线程访问。边沿先进入 ConcurrentQueue，再在主线程转入列表，
    // 这样可以窥视候选事件，等原版确实消费 keyTimes 后再提交移除。
    private static readonly List<TouchTimestampInfo> PendingPresses = new(CapacityPerEdge);
    private static readonly List<TouchTimestampInfo> PendingReleases = new(CapacityPerEdge);

    private static bool _subscribed;
    private static int _captureEnabled;
    // The direct mobile implementation and the legacy angle-projection
    // fallback have different consumers. Never mirror bridge events into the
    // fallback queues, otherwise their unconsumed copies look like latency.
    private static int _mobileAsyncCaptureMode;
    private static readonly object QueueLock = new();
    private static long _receivedCount;
    private static long _receivedPressCount;
    private static long _receivedReleaseCount;
    private static long _droppedCount;
    private static long _staleCount;
    // A raw Android callback can arrive after Unity has already consumed the
    // matching keyTimes item. Keep the cutoff after draining so that late
    // events cannot be paired with the next physical game input.
    private static long _discardPressesThroughNanos;

    /// <summary>当前积压的原始边沿数量，供调试 HUD 显示。</summary>
    internal static int Count => Presses.Count + Releases.Count + AsyncEvents.Count
                                  + PendingPresses.Count + PendingReleases.Count;

    internal static int MobileAsyncCount => AsyncEvents.Count;

    internal static long ReceivedCount => Interlocked.Read(ref _receivedCount);

    internal static long ReceivedPressCount => Interlocked.Read(ref _receivedPressCount);

    internal static long ReceivedReleaseCount => Interlocked.Read(ref _receivedReleaseCount);

    internal static long DroppedCount => Interlocked.Read(ref _droppedCount);

    internal static long StaleCount => Interlocked.Read(ref _staleCount);

    internal static void Subscribe()
    {
        if (_subscribed)
            return;

        // The direct bridge must preserve the same UI-position filtering as
        // scrPlayer.ValidInputWasTriggered. The timestamp-only channel omits
        // coordinates, so subscribe to the full snapshot and ignore Move here.
        InputEvents.OnTouch += OnTouch;
        _subscribed = true;
    }

    internal static void Unsubscribe()
    {
        SetCaptureEnabled(false);
        if (!_subscribed)
            return;

        InputEvents.OnTouch -= OnTouch;
        _subscribed = false;
        Clear();
    }

    /// <summary>
    /// 只在 Mod 可用时捕获原始触摸。关闭时清空队列，避免菜单、暂停或设置页的触摸
    /// 在恢复后被错误地当作歌曲输入。
    /// </summary>
    internal static void SetCaptureEnabled(bool enabled)
    {
        if (!enabled)
            AdvancePressDiscardWatermark(ClockSync.GetMonotonicNanos());

        lock (QueueLock)
        {
            int requested = enabled ? 1 : 0;
            Volatile.Write(ref _captureEnabled, requested);
            // Disabling is the session boundary and must discard menu/pause
            // input. Enabling must not clear a callback that arrived while
            // the gameplay gate was being opened; the queue is already empty
            // after the preceding disable/reset path.
            if (!enabled)
                ClearUnsafe();
        }
    }

    internal static void SetMobileAsyncCaptureMode(bool enabled)
    {
        lock (QueueLock)
        {
            int requested = enabled ? 1 : 0;
            if (Volatile.Read(ref _mobileAsyncCaptureMode) == requested)
                return;

            Volatile.Write(ref _mobileAsyncCaptureMode, requested);
            ClearUnsafe();
        }
    }

    internal static void Clear()
    {
        AdvancePressDiscardWatermark(ClockSync.GetMonotonicNanos());
        lock (QueueLock)
            ClearUnsafe();
    }

    /// <summary>
    /// 取出与已由 Unity 采样到的 <c>keyTimes</c> 项相匹配的 Down。
    /// </summary>
    /// <remarks>
    /// Android 的原始回调和 Unity 的触摸采样不在同一线程。一个 Down 能对应此游戏项的
    /// 必要条件是它发生在该项创建之前（加极小的时钟容差）且没有早到超过关联窗口；
    /// 只按“当前仍新鲜”取队头会把下一次 Down 错配到当前项。
    /// </remarks>
    internal static bool TryTakePressForGameInput(
        long sourceCreatedNanos,
        long monotonicNowNanos,
        long maxAgeNanos,
        long futureToleranceNanos,
        out TouchTimestampInfo eventInfo)
    {
        eventInfo = default;
        if (sourceCreatedNanos <= 0L || monotonicNowNanos <= 0L
            || maxAgeNanos < 0L || futureToleranceNanos < 0L)
        {
            return false;
        }

        DrainPresses();
        long latestDue = monotonicNowNanos > long.MaxValue - futureToleranceNanos
            ? long.MaxValue
            : monotonicNowNanos + futureToleranceNanos;
        long latestForSource = sourceCreatedNanos > long.MaxValue - futureToleranceNanos
            ? long.MaxValue
            : sourceCreatedNanos + futureToleranceNanos;
        long latestAllowed = Math.Min(latestDue, latestForSource);

        long earliestFresh = monotonicNowNanos < long.MinValue + maxAgeNanos
            ? long.MinValue
            : monotonicNowNanos - maxAgeNanos;
        long earliestForSource = sourceCreatedNanos < long.MinValue + maxAgeNanos
            ? long.MinValue
            : sourceCreatedNanos - maxAgeNanos;
        long earliestAllowed = Math.Max(earliestFresh, earliestForSource);
        long discardThrough = Volatile.Read(ref _discardPressesThroughNanos);

        while (PendingPresses.Count > 0
               && (PendingPresses[0].EventTimeNanos <= discardThrough
                   || PendingPresses[0].EventTimeNanos < earliestAllowed))
        {
            PendingPresses.RemoveAt(0);
            Interlocked.Increment(ref _staleCount);
        }

        if (PendingPresses.Count == 0
            || latestAllowed < earliestAllowed
            || PendingPresses[0].EventTimeNanos > latestAllowed)
            return false;

        eventInfo = PendingPresses[0];
        PendingPresses.RemoveAt(0);
        return true;
    }

    /// <summary>
    /// 在主线程侧整理按下队列。这个调用不关联任何游戏输入，只移除明显过期的事件；
    /// 仍有机会在下一帧被 Unity 采样的 Down 必须保留。
    /// </summary>
    internal static void DiscardStalePresses(long monotonicNowNanos, long maxAgeNanos)
    {
        DrainPresses();
        long earliestFresh = monotonicNowNanos < long.MinValue + maxAgeNanos
            ? long.MinValue
            : monotonicNowNanos - maxAgeNanos;
        long discardThrough = Volatile.Read(ref _discardPressesThroughNanos);
        while (PendingPresses.Count > 0
               && (PendingPresses[0].EventTimeNanos <= discardThrough
                   || PendingPresses[0].EventTimeNanos < earliestFresh))
        {
            PendingPresses.RemoveAt(0);
            Interlocked.Increment(ref _staleCount);
        }
    }

    /// <summary>
    /// 丢弃不晚于某个已消费 Unity 输入采样时刻的 Down。它处理 Android 广播在
    /// <c>HitAutoFloors</c> / <c>UpdateHoldKeys</c> 之后才被主线程观察到的竞态，
    /// 防止该 Down 被下一次游戏输入误用。
    /// </summary>
    internal static void DiscardPressesThrough(long latestEventNanos)
    {
        AdvancePressDiscardWatermark(latestEventNanos);
        DrainPresses();
        long discardThrough = Volatile.Read(ref _discardPressesThroughNanos);
        while (PendingPresses.Count > 0
               && PendingPresses[0].EventTimeNanos <= discardThrough)
        {
            PendingPresses.RemoveAt(0);
            Interlocked.Increment(ref _staleCount);
        }
    }

    /// <summary>校准页只关联最新一次按下，并主动丢弃更早的积压项。</summary>
    internal static bool TryDequeueLatestPress(out TouchTimestampInfo eventInfo)
    {
        eventInfo = default;
        DrainPresses();
        DiscardPendingPressesThrough(Volatile.Read(ref _discardPressesThroughNanos));
        if (PendingPresses.Count == 0)
            return false;

        eventInfo = PendingPresses[^1];
        PendingPresses.Clear();
        return true;
    }

    /// <summary>
    /// 以 Android 原始边沿顺序取出已经发生的事件，供移动端 async bridge 在同一帧
    /// 内按时间批处理。它不能复用 Presses/Releases，因为两个独立队列会丢失 Down/Up
    /// 的全局顺序。
    /// </summary>
    internal static bool TryDequeueMobileAsyncDue(
        long monotonicNowNanos,
        long futureToleranceNanos,
        out MobileTouchEdge eventInfo)
    {
        eventInfo = default;
        _ = monotonicNowNanos;
        _ = futureToleranceNanos;

        // Match scrController.UpdateInput's PC behavior: once an event has
        // reached the game's producer queue, consume it immediately and let
        // the original ConcurrentQueue/PriorityQueue path order it.  Android
        // may deliver an event whose kernel timestamp is a little ahead of
        // the Unity main-thread clock.  Waiting for that timestamp at the
        // queue head used to block every later edge behind it.
        if (!AsyncEvents.TryDequeue(out eventInfo))
            return false;
        return true;
    }

    /// <summary>
    /// 查找长按开始之后最近一次已经发生的 Up/Cancel，但不立即移除。
    /// 调用方先让游戏执行 <c>ValidInputWasReleased</c>；只有游戏确实接受了释放，
    /// 才通过 <see cref="RemoveRelease"/> 确认消费该事件。
    /// </summary>
    internal static bool TryPeekLatestReleaseSince(
        long notBeforeNanos,
        long monotonicNowNanos,
        long maxAgeNanos,
        long futureToleranceNanos,
        int pointerId,
        out TouchTimestampInfo eventInfo)
    {
        eventInfo = default;
        DrainReleases();

        long latestDue = monotonicNowNanos > long.MaxValue - futureToleranceNanos
            ? long.MaxValue
            : monotonicNowNanos + futureToleranceNanos;
        long earliestFresh = monotonicNowNanos < long.MinValue + maxAgeNanos
            ? long.MinValue
            : monotonicNowNanos - maxAgeNanos;

        for (int i = PendingReleases.Count - 1; i >= 0; i--)
        {
            TouchTimestampInfo candidate = PendingReleases[i];
            if (candidate.EventTimeNanos < notBeforeNanos || candidate.EventTimeNanos < earliestFresh)
            {
                PendingReleases.RemoveAt(i);
                Interlocked.Increment(ref _staleCount);
            }
        }

        bool found = false;
        for (int i = 0; i < PendingReleases.Count; i++)
        {
            TouchTimestampInfo candidate = PendingReleases[i];
            if (candidate.EventTimeNanos < notBeforeNanos
                || candidate.EventTimeNanos > latestDue
                || pointerId >= 0 && candidate.PointerId != pointerId
                && candidate.Action != AndroidInput.MotionAction.Cancel)
                continue;

            if (!found || candidate.EventTimeNanos >= eventInfo.EventTimeNanos)
            {
                eventInfo = candidate;
                found = true;
            }
        }

        return found;
    }

    /// <summary>确认删除一项此前通过 <see cref="TryPeekLatestReleaseSince"/> 观察到的释放事件。</summary>
    internal static bool RemoveRelease(TouchTimestampInfo expected)
    {
        DrainReleases();
        for (int i = 0; i < PendingReleases.Count; i++)
        {
            if (PendingReleases[i] != expected)
                continue;

            PendingReleases.RemoveAt(i);
            return true;
        }
        return false;
    }

    internal static void ResetStatistics()
    {
        Interlocked.Exchange(ref _receivedCount, 0L);
        Interlocked.Exchange(ref _receivedPressCount, 0L);
        Interlocked.Exchange(ref _receivedReleaseCount, 0L);
        Interlocked.Exchange(ref _droppedCount, 0L);
        Interlocked.Exchange(ref _staleCount, 0L);
    }

    private static void DrainReleases()
    {
        while (Releases.TryDequeue(out TouchTimestampInfo release))
        {
            if (PendingReleases.Count >= CapacityPerEdge)
            {
                PendingReleases.RemoveAt(0);
                Interlocked.Increment(ref _droppedCount);
            }
            PendingReleases.Add(release);
        }
    }

    private static void DrainPresses()
    {
        while (Presses.TryDequeue(out TouchTimestampInfo press))
        {
            if (press.EventTimeNanos <= Volatile.Read(ref _discardPressesThroughNanos))
            {
                Interlocked.Increment(ref _staleCount);
                continue;
            }

            if (PendingPresses.Count >= CapacityPerEdge)
            {
                PendingPresses.RemoveAt(0);
                Interlocked.Increment(ref _droppedCount);
            }
            PendingPresses.Add(press);
        }
    }

    private static void AdvancePressDiscardWatermark(long latestEventNanos)
    {
        if (latestEventNanos <= 0L)
            return;

        while (true)
        {
            long observed = Volatile.Read(ref _discardPressesThroughNanos);
            if (latestEventNanos <= observed
                || Interlocked.CompareExchange(
                    ref _discardPressesThroughNanos,
                    latestEventNanos,
                    observed) == observed)
            {
                return;
            }
        }
    }

    private static void DiscardPendingPressesThrough(long latestEventNanos)
    {
        while (PendingPresses.Count > 0
               && PendingPresses[0].EventTimeNanos <= latestEventNanos)
        {
            PendingPresses.RemoveAt(0);
            Interlocked.Increment(ref _staleCount);
        }
    }

    private static void ClearUnsafe()
    {
        Presses.Clear();
        Releases.Clear();
        AsyncEvents.Clear();
        PendingPresses.Clear();
        PendingReleases.Clear();
    }

    /// <summary>Android 输入线程回调。这里只做固定成本的边沿分类和入队。</summary>
    private static void OnTouch(TouchEventInfo raw)
    {
        TouchTimestampInfo info = new(raw.Action, raw.PointerId, raw.EventTimeNanos);
        lock (QueueLock)
        {
            if (Volatile.Read(ref _captureEnabled) == 0 || info.EventTimeNanos <= 0L)
                return;

            bool isPress = info.Action is AndroidInput.MotionAction.Down
                or AndroidInput.MotionAction.PointerDown;
            bool isRelease = info.Action is AndroidInput.MotionAction.Up
                or AndroidInput.MotionAction.PointerUp
                or AndroidInput.MotionAction.Cancel;
            if (!isPress && !isRelease)
                return;

            if (isPress
                && info.EventTimeNanos <= Volatile.Read(ref _discardPressesThroughNanos))
            {
                Interlocked.Increment(ref _staleCount);
                return;
            }

            if (Volatile.Read(ref _mobileAsyncCaptureMode) != 0)
            {
                while (AsyncEvents.Count >= AsyncCapacity && AsyncEvents.TryDequeue(out _))
                    Interlocked.Increment(ref _droppedCount);
                AsyncEvents.Enqueue(new MobileTouchEdge(
                    info.Action,
                    info.PointerId,
                    info.EventTimeNanos,
                    raw.X,
                    raw.Y));
            }
            else
            {
                ConcurrentQueue<TouchTimestampInfo> target = isPress ? Presses : Releases;
                while (target.Count >= CapacityPerEdge && target.TryDequeue(out _))
                    Interlocked.Increment(ref _droppedCount);
                target.Enqueue(info);
            }
            Interlocked.Increment(ref _receivedCount);
            if (isPress)
                Interlocked.Increment(ref _receivedPressCount);
            else
                Interlocked.Increment(ref _receivedReleaseCount);
        }
    }
}
