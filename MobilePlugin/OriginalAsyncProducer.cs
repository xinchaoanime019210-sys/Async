using StArray.ModManager.Android.Native;

namespace AsyncInput.Mobile;

/// <summary>
/// Completes only the missing producer side of the mobile port.
/// </summary>
/// <remarks>
/// The producer owns Android pointer bookkeeping and serializes accepted
/// edges as SkyHookEvent values. After that point it has no mask, key-time, or
/// judgment state of its own: <c>AsyncInputManager.keyQueue</c>, the game's
/// original <c>scrController.UpdateInput</c>, and its player code remain
/// authoritative.
/// </remarks>
internal sealed class OriginalAsyncProducer
{
    private const int PointerSlotCount = 16;
    private const long FutureEventToleranceNanos = 1_000_000L;
    private const long TicksPerMillisecond = TimeSpan.TicksPerMillisecond;

    private readonly int[] _pointerIds = new int[PointerSlotCount];
    private readonly HashSet<int> _ignoredPointerIds = new();

    private ulong _activeMask;
    private long _lastObservedDropCount;

    internal long ProducedEvents { get; private set; }

    internal long RejectedEvents { get; private set; }

    internal long ProcessFailures { get; private set; }

    internal long IgnoredUiEvents { get; private set; }

    internal int LastFlushRawCount { get; private set; }

    internal int LastFlushProducedCount { get; private set; }

    internal double LastDispatchAgeMilliseconds { get; private set; }

    internal bool HasActivePointers => _activeMask != 0UL;

    internal void Reset()
    {
        Array.Fill(_pointerIds, -1);
        _ignoredPointerIds.Clear();
        _activeMask = 0UL;
        _lastObservedDropCount = TouchQueue.DroppedCount;
        LastFlushRawCount = 0;
        LastFlushProducedCount = 0;
        LastDispatchAgeMilliseconds = 0d;
    }

    internal void ResetStatistics()
    {
        ProducedEvents = 0L;
        RejectedEvents = 0L;
        ProcessFailures = 0L;
        IgnoredUiEvents = 0L;
        LastFlushRawCount = 0;
        LastFlushProducedCount = 0;
        LastDispatchAgeMilliseconds = 0d;
    }

    /// <summary>
    /// Drains raw Android edges immediately before the game's own consumer in
    /// <c>scrController.UpdateInput</c>. The edge timestamp is
    /// preserved in the generated SkyHookEvent; only its delivery is
    /// synchronized to the game's normal player-update point.
    /// </summary>
    internal bool Flush(
        GameApi game,
        nint controller,
        OriginalAsyncClock clock,
        float offsetMilliseconds)
    {
        LastFlushRawCount = 0;
        LastFlushProducedCount = 0;

        if (game == null || controller == 0)
            return false;

        if (!clock.IsReady && !clock.TryGetCurrentDateTimeTicks(out _))
            return false;

        long monotonicNowNanos = OriginalAsyncClock.GetMonotonicNanos();
        if (monotonicNowNanos <= 0L)
            return false;

        long dropped = TouchQueue.DroppedCount;
        if (dropped != _lastObservedDropCount)
        {
            // A lost edge makes the pointer state unknowable. Drop the tail
            // and let the next complete gesture establish a new state.
            TouchQueue.Clear();
            game.ClearOriginalAsyncInputState();
            Reset();
            _lastObservedDropCount = dropped;
            ProcessFailures++;
            return true;
        }

        while (TouchQueue.TryDequeueMobileAsyncDue(
                   monotonicNowNanos,
                   FutureEventToleranceNanos,
                   out MobileTouchEdge input))
        {
            LastFlushRawCount++;

            if (input.EventTimeNanos <= 0L)
            {
                RejectedEvents++;
                continue;
            }

            bool accepted = input.Action switch
            {
                AndroidInput.MotionAction.Down
                    or AndroidInput.MotionAction.PointerDown
                    => ProcessDown(game, controller, clock, input, monotonicNowNanos, offsetMilliseconds),
                AndroidInput.MotionAction.Up
                    or AndroidInput.MotionAction.PointerUp
                    => ProcessUp(game, clock, input, monotonicNowNanos, offsetMilliseconds),
                AndroidInput.MotionAction.Cancel
                    => ProcessCancel(game, clock, input, monotonicNowNanos, offsetMilliseconds),
                _ => true,
            };

            if (!accepted)
            {
                // The queue API or timestamp conversion failed. Clearing the
                // native state avoids leaving an unpaired virtual key held.
                game.ClearOriginalAsyncInputState();
                Reset();
                ProcessFailures++;
                return false;
            }
        }

        return true;
    }

    private bool ProcessDown(
        GameApi game,
        nint controller,
        OriginalAsyncClock clock,
        MobileTouchEdge input,
        long monotonicNowNanos,
        float offsetMilliseconds)
    {
        int pointerId = input.PointerId;
        if (pointerId < 0)
        {
            RejectedEvents++;
            return true;
        }

        // ACTION_DOWN starts a new Android gesture. If an old gesture was
        // left active, close it in the native queue before accepting the new
        // primary pointer so keyMask cannot retain a stale key.
        if (input.Action == AndroidInput.MotionAction.Down)
        {
            if (_activeMask != 0UL
                && !EmitReleaseAll(game, clock, input.EventTimeNanos - 100L,
                    monotonicNowNanos, offsetMilliseconds))
            {
                return false;
            }

            _ignoredPointerIds.Clear();
        }

        if (FindPointerSlot(pointerId) >= 0 || _ignoredPointerIds.Contains(pointerId))
        {
            RejectedEvents++;
            return true;
        }

        // This is the same semantic gate as the mobile scrPlayer path, but
        // evaluated on the Unity main thread rather than in the Android input
        // callback. The callback only stores coordinates in TouchQueue.
        if (game.IsScreenPointInsideUi(controller, input.X, input.Y))
        {
            _ignoredPointerIds.Add(pointerId);
            IgnoredUiEvents++;
            return true;
        }

        int slot = FindFreeSlot();
        if (slot < 0)
        {
            RejectedEvents++;
            return true;
        }

        if (!Emit(
                game,
                clock,
                input.EventTimeNanos,
                pressed: true,
                slot,
                monotonicNowNanos,
                offsetMilliseconds))
        {
            return false;
        }

        _pointerIds[slot] = pointerId;
        _activeMask |= 1UL << slot;
        return true;
    }

    private bool ProcessUp(
        GameApi game,
        OriginalAsyncClock clock,
        MobileTouchEdge input,
        long monotonicNowNanos,
        float offsetMilliseconds)
    {
        int pointerId = input.PointerId;
        if (pointerId >= 0 && _ignoredPointerIds.Remove(pointerId))
            return true;

        if (pointerId < 0)
            return EmitReleaseAll(game, clock, input.EventTimeNanos,
                monotonicNowNanos, offsetMilliseconds);

        int slot = FindPointerSlot(pointerId);
        if (slot < 0)
        {
            RejectedEvents++;
            return true;
        }

        if (!Emit(
                game,
                clock,
                input.EventTimeNanos,
                pressed: false,
                slot,
                monotonicNowNanos,
                offsetMilliseconds))
        {
            return false;
        }

        _pointerIds[slot] = -1;
        _activeMask &= ~(1UL << slot);
        return true;
    }

    private bool ProcessCancel(
        GameApi game,
        OriginalAsyncClock clock,
        MobileTouchEdge input,
        long monotonicNowNanos,
        float offsetMilliseconds)
    {
        _ignoredPointerIds.Clear();
        return EmitReleaseAll(
            game,
            clock,
            input.EventTimeNanos,
            monotonicNowNanos,
            offsetMilliseconds);
    }

    private bool EmitReleaseAll(
        GameApi game,
        OriginalAsyncClock clock,
        long eventTimeNanos,
        long monotonicNowNanos,
        float offsetMilliseconds)
    {
        ulong active = _activeMask;
        if (active == 0UL)
        {
            Array.Fill(_pointerIds, -1);
            return true;
        }

        long releaseTime = eventTimeNanos > 100L ? eventTimeNanos - 100L : eventTimeNanos;
        for (int slot = 0; slot < PointerSlotCount; slot++)
        {
            if ((active & (1UL << slot)) == 0UL)
                continue;

            if (!Emit(
                    game,
                    clock,
                    releaseTime,
                    pressed: false,
                    slot,
                    monotonicNowNanos,
                    offsetMilliseconds))
            {
                return false;
            }
        }

        Array.Fill(_pointerIds, -1);
        _activeMask = 0UL;
        return true;
    }

    private bool Emit(
        GameApi game,
        OriginalAsyncClock clock,
        long eventTimeNanos,
        bool pressed,
        int slot,
        long monotonicNowNanos,
        float offsetMilliseconds)
    {
        if (!clock.TryConvertEventTime(eventTimeNanos, out long dateTimeTicks))
        {
            RejectedEvents++;
            return false;
        }

        if (float.IsFinite(offsetMilliseconds) && offsetMilliseconds != 0f)
        {
            double adjustment = offsetMilliseconds * TicksPerMillisecond;
            if (!double.IsFinite(adjustment)
                || adjustment < long.MinValue
                || adjustment > long.MaxValue)
            {
                RejectedEvents++;
                return false;
            }

            try
            {
                // Positive user offset means “judge earlier”, matching the
                // setting semantics of the former fallback implementation.
                dateTimeTicks = checked(dateTimeTicks - (long)Math.Round(adjustment));
            }
            catch
            {
                RejectedEvents++;
                return false;
            }
        }

        if (dateTimeTicks <= 0L
            || !game.EnqueueOriginalAsyncEvent(dateTimeTicks, pressed, slot))
        {
            RejectedEvents++;
            return false;
        }

        ProducedEvents++;
        LastFlushProducedCount++;
        LastDispatchAgeMilliseconds =
            (monotonicNowNanos - eventTimeNanos) / 1_000_000d;
        return true;
    }

    private int FindPointerSlot(int pointerId)
    {
        for (int slot = 0; slot < PointerSlotCount; slot++)
        {
            if ((_activeMask & (1UL << slot)) != 0UL
                && _pointerIds[slot] == pointerId)
            {
                return slot;
            }
        }
        return -1;
    }

    private int FindFreeSlot()
    {
        for (int slot = 0; slot < PointerSlotCount; slot++)
        {
            if ((_activeMask & (1UL << slot)) == 0UL)
                return slot;
        }
        return -1;
    }
}
