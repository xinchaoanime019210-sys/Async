using StArray.ModManager.Android.Native;

namespace AsyncInput.Mobile;

/// <summary>
/// Owns the missing mobile asynchronous-input producer: Android timestamped
/// touch edges, pointer state, and event batches. The game has no mobile
/// producer of its own; ProcessKeyInputs is used only as the existing judgment
/// executor for the virtual mobile input batch.
///
/// Legacy compatibility implementation. The active branch uses
/// <see cref="OriginalAsyncProducer"/> and the game's own keyQueue instead;
/// this class is retained so the earlier user work remains available without
/// being installed by <see cref="AsyncInputPlugin.OnLoad"/>.
/// </summary>
internal sealed class MobileAsyncBridge
{
    private const int PointerSlotCount = 16;
    private const long FutureEventToleranceNanos = 1_000_000L;
    private const double DspTicksPerSecond = 10_000_000d;

    private readonly int[] _pointerIds = new int[PointerSlotCount];
    private ulong _heldMask;
    private ulong _pointerActiveMask;
    private long _lastObservedDropCount;

    internal long ProcessedFrames { get; private set; }

    internal long ProcessedEvents { get; private set; }

    internal long RejectedEvents { get; private set; }

    internal long ProcessFailures { get; private set; }

    internal long IgnoredUiEvents { get; private set; }

    internal int LastBatchCount { get; private set; }

    internal double LastDispatchAgeMillis { get; private set; }

    internal void ResetStatistics()
    {
        ProcessedFrames = 0L;
        ProcessedEvents = 0L;
        RejectedEvents = 0L;
        ProcessFailures = 0L;
        IgnoredUiEvents = 0L;
        LastBatchCount = 0;
        LastDispatchAgeMillis = 0d;
    }

    internal void Reset(GameApi? game)
    {
        _heldMask = 0UL;
        _pointerActiveMask = 0UL;
        Array.Fill(_pointerIds, -1);
        _lastObservedDropCount = TouchQueue.DroppedCount;
        LastBatchCount = 0;
        LastDispatchAgeMillis = 0d;

        game?.ClearMobileAsyncInputState();
        game?.SetMobileAsyncInputTypes(false);
    }

    internal bool PrepareFrame(GameApi game)
    {
        long dropped = TouchQueue.DroppedCount;
        if (dropped == _lastObservedDropCount)
            return true;

        // A lost edge leaves the held state unknowable. Reset it instead of
        // allowing a later release or press to be paired with the wrong key.
        // The remaining queue tail is no longer a valid sequence either, so
        // discard it at this explicit producer-state boundary.
        TouchQueue.Clear();
        Reset(game);
        _lastObservedDropCount = dropped;
        return true;
    }

    internal bool ProcessFrame(
        GameApi game,
        nint controller,
        long monotonicNowNanos,
        ulong frameTick,
        ulong offsetTick,
        ClockSync clock,
        float offsetMs)
    {
        LastBatchCount = 0;
        if (controller == 0 || monotonicNowNanos <= 0L || frameTick == 0UL || offsetTick == 0UL)
            return false;

        if (!game.SetMobileAsyncInputTypes(true))
        {
            ProcessFailures++;
            return false;
        }

        try
        {
            ulong frameMask = _heldMask;
            ulong frameDownMask = 0UL;
            ulong frameUpMask = 0UL;
            ulong batchDownMask = 0UL;
            ulong batchUpMask = 0UL;
            ulong currentTick = 0UL;
            bool haveBatch = false;
            int batchCount = 0;

            while (TouchQueue.TryDequeueMobileAsyncDue(
                       monotonicNowNanos,
                       FutureEventToleranceNanos,
                       out MobileTouchEdge input))
            {
                long ageNanos = monotonicNowNanos - input.EventTimeNanos;
                // Do not impose a maximum correction age here. A main-thread
                // stall can delay delivery by several frames, but the Android
                // timestamp remains valid and must still be judged at its
                // original song time. Session transitions clear the queue.
                if (ageNanos < -FutureEventToleranceNanos)
                {
                    RejectedEvents++;
                    ResetPointerState();
                    continue;
                }

                // The original mobile path rejects a Unity Touch Down over
                // the controller UI. This producer receives native edges
                // before Unity creates that Touch, so preserve that gate here
                // before assigning the edge a virtual async key.
                if (IsPress(input) && game.IsScreenPointInsideUi(controller, input.X, input.Y))
                {
                    IgnoredUiEvents++;
                    continue;
                }

                if (!TryGetTargetTick(clock, input.EventTimeNanos, offsetTick, offsetMs, out ulong targetTick))
                {
                    RejectedEvents++;
                    continue;
                }

                if (!CanApply(input))
                    continue;

                if (haveBatch && targetTick != currentTick)
                {
                    if (!ProcessBatch(
                            game,
                            controller,
                            currentTick,
                            batchDownMask,
                            batchUpMask,
                            frameMask,
                            frameDownMask,
                            frameUpMask))
                    {
                        return false;
                    }

                    batchCount++;
                    batchDownMask = 0UL;
                    batchUpMask = 0UL;
                    currentTick = targetTick;
                }
                else if (!haveBatch)
                {
                    currentTick = targetTick;
                    haveBatch = true;
                }

                Apply(input, ref batchDownMask, ref batchUpMask, ref frameMask, ref frameDownMask, ref frameUpMask);
                ProcessedEvents++;
                LastDispatchAgeMillis = ageNanos / 1_000_000d;
            }

            bool processed;
            if (haveBatch)
            {
                processed = ProcessBatch(
                    game,
                    controller,
                    currentTick,
                    batchDownMask,
                    batchUpMask,
                    frameMask,
                    frameDownMask,
                    frameUpMask);
                if (processed)
                    batchCount++;
            }
            else
            {
                processed = ProcessBatch(
                    game,
                    controller,
                    frameTick,
                    0UL,
                    0UL,
                    _heldMask,
                    0UL,
                    0UL);
                if (processed)
                    batchCount = 1;
            }

            if (!processed)
                return false;

            LastBatchCount = batchCount;
            ProcessedFrames++;
            return true;
        }
        finally
        {
            game.ClearMobileAsyncInputMasks();
            game.SetMobileAsyncInputTypes(false);
        }
    }

    private bool ProcessBatch(
        GameApi game,
        nint controller,
        ulong targetTick,
        ulong downMask,
        ulong upMask,
        ulong frameMask,
        ulong frameDownMask,
        ulong frameUpMask)
    {
        if (!game.ApplyMobileAsyncInputMasks(
                _heldMask,
                downMask,
                upMask,
                frameMask,
                frameDownMask,
                frameUpMask))
        {
            ProcessFailures++;
            return false;
        }

        GameHooks.SetMobileAsyncInputDispatching(true, CountBits(downMask));
        try
        {
            if (game.ProcessMobileAsyncInput(controller, targetTick))
                return true;
            ProcessFailures++;
            return false;
        }
        finally
        {
            GameHooks.SetMobileAsyncInputDispatching(false);
        }
    }

    private static bool TryGetTargetTick(
        ClockSync clock,
        long eventNanos,
        ulong offsetTick,
        float offsetMs,
        out ulong targetTick)
    {
        targetTick = 0UL;
        double eventDsp = clock.ToDspTime(eventNanos) - offsetMs / 1000d;
        double eventDspTicks = eventDsp * DspTicksPerSecond;
        if (!double.IsFinite(eventDspTicks)
            || eventDspTicks <= 0d
            || eventDspTicks >= ulong.MaxValue)
        {
            return false;
        }

        // AsyncInputUtils.UpdateOffsetTime converts DSP seconds to ticks with
        // IL conv.u8, which truncates toward zero for the positive timeline.
        // Match it exactly instead of rounding this producer's event tick.
        ulong songTick = (ulong)eventDspTicks;
        if (songTick > ulong.MaxValue - offsetTick)
            return false;

        targetTick = offsetTick + songTick;
        return true;
    }

    private static int CountBits(ulong value)
    {
        int count = 0;
        while (value != 0UL)
        {
            value &= value - 1UL;
            count++;
        }
        return count;
    }

    private static bool IsPress(MobileTouchEdge input)
    {
        return input.Action is AndroidInput.MotionAction.Down
            or AndroidInput.MotionAction.PointerDown;
    }

    private bool CanApply(MobileTouchEdge input)
    {
        return input.Action switch
        {
            AndroidInput.MotionAction.Down or AndroidInput.MotionAction.PointerDown =>
                input.PointerId >= 0 && FindPointerSlot(input.PointerId) < 0,
            AndroidInput.MotionAction.Up => _pointerActiveMask != 0UL,
            AndroidInput.MotionAction.PointerUp =>
                input.PointerId >= 0 && FindPointerSlot(input.PointerId) >= 0,
            AndroidInput.MotionAction.Cancel => _pointerActiveMask != 0UL || _heldMask != 0UL,
            _ => false,
        };
    }

    private void Apply(
        MobileTouchEdge input,
        ref ulong batchDownMask,
        ref ulong batchUpMask,
        ref ulong frameMask,
        ref ulong frameDownMask,
        ref ulong frameUpMask)
    {
        switch (input.Action)
        {
            case AndroidInput.MotionAction.Down:
            case AndroidInput.MotionAction.PointerDown:
                if (_pointerActiveMask == 0UL && input.Action == AndroidInput.MotionAction.Down)
                    ResetPointerState();

                int downSlot = AllocatePointerSlot(input.PointerId);
                if (downSlot < 0)
                    return;

                ulong downBit = 1UL << downSlot;
                _pointerActiveMask |= downBit;
                _heldMask |= downBit;
                batchDownMask |= downBit;
                frameMask |= downBit;
                frameDownMask |= downBit;
                return;

            case AndroidInput.MotionAction.Up:
                int upSlot = input.PointerId < 0 ? -1 : FindPointerSlot(input.PointerId);
                if (upSlot < 0)
                {
                    ReleaseAll(ref batchUpMask, ref frameMask, ref frameUpMask);
                    return;
                }
                ReleaseSlot(upSlot, ref batchUpMask, ref frameMask, ref frameUpMask);
                return;

            case AndroidInput.MotionAction.PointerUp:
                ReleaseSlot(
                    input.PointerId < 0 ? -1 : FindPointerSlot(input.PointerId),
                    ref batchUpMask,
                    ref frameMask,
                    ref frameUpMask);
                return;

            case AndroidInput.MotionAction.Cancel:
                ReleaseAll(ref batchUpMask, ref frameMask, ref frameUpMask);
                return;
        }
    }

    private void ReleaseSlot(int slot, ref ulong batchUpMask, ref ulong frameMask, ref ulong frameUpMask)
    {
        if (slot < 0 || slot >= PointerSlotCount)
            return;

        ulong bit = 1UL << slot;
        _pointerActiveMask &= ~bit;
        _pointerIds[slot] = -1;
        if ((_heldMask & bit) == 0UL)
            return;

        _heldMask &= ~bit;
        batchUpMask |= bit;
        frameMask &= ~bit;
        frameUpMask |= bit;
    }

    private void ReleaseAll(ref ulong batchUpMask, ref ulong frameMask, ref ulong frameUpMask)
    {
        ulong held = _heldMask;
        ResetPointerState();
        if (held == 0UL)
            return;

        batchUpMask |= held;
        frameMask &= ~held;
        frameUpMask |= held;
    }

    private int FindPointerSlot(int pointerId)
    {
        for (int i = 0; i < PointerSlotCount; i++)
        {
            ulong bit = 1UL << i;
            if ((_pointerActiveMask & bit) != 0UL && _pointerIds[i] == pointerId)
                return i;
        }
        return -1;
    }

    private int AllocatePointerSlot(int pointerId)
    {
        for (int i = 0; i < PointerSlotCount; i++)
        {
            ulong bit = 1UL << i;
            if ((_pointerActiveMask & bit) != 0UL)
                continue;

            _pointerIds[i] = pointerId;
            return i;
        }
        return -1;
    }

    private void ResetPointerState()
    {
        _heldMask = 0UL;
        _pointerActiveMask = 0UL;
        Array.Fill(_pointerIds, -1);
    }
}
