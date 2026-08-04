using System.Collections.Concurrent;
using StArray.ModManager.Android.Native;

namespace AsyncInput.Mobile;

/// <summary>
/// Android 原始触摸事件的跨线程有界队列。
/// </summary>
/// <remarks>
/// 入队发生在 Android 输入分发线程，出队发生在 Unity 主线程。队列只存放值类型快照，
/// 不持有原生指针或游戏对象。正常输入路径不加锁、不写日志，也不分配托管对象。
/// </remarks>
internal static class TouchQueue
{
    // 1024 个事件可以覆盖极端低帧率下数秒的高频输入，仍只占很小的内存。
    private const int Capacity = 1024;

    private static readonly ConcurrentQueue<TouchTimestampInfo> Pending = new();
    private static bool _subscribed;
    private static int _captureEnabled;
    private static long _receivedCount;
    private static long _droppedCount;
    private static long _staleCount;

    /// <summary>当前积压的原始事件数量。</summary>
    internal static int Count => Pending.Count;

    internal static long ReceivedCount => Interlocked.Read(ref _receivedCount);

    internal static long DroppedCount => Interlocked.Read(ref _droppedCount);

    internal static long StaleCount => Interlocked.Read(ref _staleCount);

    internal static void Subscribe()
    {
        if (_subscribed)
            return;
        InputEvents.OnTouchTimestamp += OnTouchTimestamp;
        _subscribed = true;
    }

    internal static void Unsubscribe()
    {
        SetCaptureEnabled(false);
        if (!_subscribed)
            return;
        InputEvents.OnTouchTimestamp -= OnTouchTimestamp;
        _subscribed = false;
        Clear();
    }

    /// <summary>
    /// 只在游戏处于可判定状态时接收原始事件，避免菜单和暂停触摸污染下一局。
    /// </summary>
    internal static void SetCaptureEnabled(bool enabled)
    {
        Volatile.Write(ref _captureEnabled, enabled ? 1 : 0);
        if (!enabled)
            Clear();
    }

    internal static void Clear()
    {
#if NET6_0_OR_GREATER
        Pending.Clear();
#else
        while (Pending.TryDequeue(out _))
        {
        }
#endif
    }

    /// <summary>取出一个事件，不做时间过滤。</summary>
    internal static bool TryDequeue(out TouchTimestampInfo eventInfo)
        => Pending.TryDequeue(out eventInfo);

    /// <summary>
    /// 取出最早一个已经到期的事件。时间戳来自同一个单调时钟，因此低帧率时可一次取出整批。
    /// </summary>
    internal static bool TryDequeueDue(long monotonicNowNanos, long futureToleranceNanos, out TouchTimestampInfo eventInfo)
    {
        eventInfo = default;
        if (!Pending.TryPeek(out TouchTimestampInfo candidate))
            return false;

        long latestDue = monotonicNowNanos > long.MaxValue - futureToleranceNanos
            ? long.MaxValue
            : monotonicNowNanos + futureToleranceNanos;
        if (candidate.EventTimeNanos > latestDue)
            return false;

        return Pending.TryDequeue(out eventInfo);
    }

    /// <summary>
    /// 按预期的硬件时间取出一个 Down。前面的过期 Up/Cancel 或明显错位的 Down 会被丢弃，
    /// 从而避免 UI 触摸把真实判定的时间戳整体错位。
    /// </summary>
    internal static bool TryDequeueDownNear(
        long expectedEventNanos,
        long toleranceNanos,
        out TouchTimestampInfo eventInfo)
    {
        eventInfo = default;
        long earliest = expectedEventNanos < long.MinValue + toleranceNanos
            ? long.MinValue
            : expectedEventNanos - toleranceNanos;
        long latest = expectedEventNanos > long.MaxValue - toleranceNanos
            ? long.MaxValue
            : expectedEventNanos + toleranceNanos;

        while (Pending.TryPeek(out TouchTimestampInfo candidate))
        {
            if (candidate.Action is not (AndroidInput.MotionAction.Down or AndroidInput.MotionAction.PointerDown))
            {
                if (Pending.TryDequeue(out _))
                    Interlocked.Increment(ref _staleCount);
                continue;
            }

            if (candidate.EventTimeNanos < earliest)
            {
                if (Pending.TryDequeue(out _))
                    Interlocked.Increment(ref _staleCount);
                continue;
            }

            if (candidate.EventTimeNanos > latest)
                return false;

            return Pending.TryDequeue(out eventInfo);
        }

        return false;
    }

    /// <summary>FIFO 取出一个 Down，用于无法读取游戏 keyTimes 的旧版本回退路径。</summary>
    internal static bool TryDequeueDown(out TouchTimestampInfo eventInfo)
    {
        eventInfo = default;
        while (Pending.TryDequeue(out TouchTimestampInfo candidate))
        {
            if (candidate.Action is AndroidInput.MotionAction.Down or AndroidInput.MotionAction.PointerDown)
            {
                eventInfo = candidate;
                return true;
            }

            Interlocked.Increment(ref _staleCount);
        }

        return false;
    }

    /// <summary>
    /// 校准页每次只需要最近一次按下。清理整批旧事件，避免进入页面前的触摸被当作样本。
    /// </summary>
    internal static bool TryDequeueLatestDown(out TouchTimestampInfo eventInfo)
    {
        eventInfo = default;
        bool found = false;
        while (Pending.TryDequeue(out TouchTimestampInfo candidate))
        {
            if (candidate.Action is AndroidInput.MotionAction.Down or AndroidInput.MotionAction.PointerDown)
            {
                eventInfo = candidate;
                found = true;
            }
        }
        return found;
    }

    internal static void ResetStatistics()
    {
        Interlocked.Exchange(ref _receivedCount, 0L);
        Interlocked.Exchange(ref _droppedCount, 0L);
        Interlocked.Exchange(ref _staleCount, 0L);
    }

    /// <summary>输入分发线程回调。</summary>
    private static void OnTouchTimestamp(TouchTimestampInfo info)
    {
        if (Volatile.Read(ref _captureEnabled) == 0)
            return;

        if (info.Action is not (AndroidInput.MotionAction.Down
            or AndroidInput.MotionAction.PointerDown
            or AndroidInput.MotionAction.Up
            or AndroidInput.MotionAction.PointerUp
            or AndroidInput.MotionAction.Cancel))
        {
            return;
        }

        if (info.EventTimeNanos <= 0L)
            return;

        // 满载时丢弃最旧事件。主线程会通过时间匹配和状态重建避免把后续事件静默错配。
        while (Pending.Count >= Capacity && Pending.TryDequeue(out _))
            Interlocked.Increment(ref _droppedCount);

        Pending.Enqueue(info);
        Interlocked.Increment(ref _receivedCount);
    }
}
