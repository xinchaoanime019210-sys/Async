using ImGuiNET;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace AsyncInput.Mobile;

/// <summary>
/// ADOFAI 手机版异步输入。
/// </summary>
/// <remarks>
/// <para>
/// 首选路径复用游戏自己的 <c>ProcessKeyInputs</c> 和
/// <c>AsyncRefreshAngles</c>：Android 输入线程只记录内核时间戳，Unity 主线程在
/// <c>PlayerControl_Update</c> 前按事件时间更新 AsyncInput mask，并逐批把事件送进游戏。
/// 这样一次渲染帧内的多次输入不会被合并成一次，也不会被当前帧采样时间替代。
/// </para>
/// <para>
/// 目标版本缺少官方 AsyncInput 字段时，才回退到 <c>scrPlayer.Hit</c> 或
/// <c>UpdateHoldKeys</c> 前的角度重算路径。
/// </para>
/// </remarks>
public sealed class AsyncInputPlugin : IModPlugin, IModSettings
{
    private const string LogTag = "AsyncInput";

    /// <summary>回退角度修正允许的最大时间跨度。</summary>
    private const double MaxCorrectionSeconds = 0.1d;

    /// <summary>回退路径和校准样本允许的最大延迟。</summary>
    private const double MaxEventAgeSeconds = 0.25d;

    private const double FutureEventToleranceSeconds = 0.005d;
    private const long FutureEventToleranceNanos = 5_000_000L;
    private const long WallTicksPerMillisecond = 10_000L;

    // AsyncKeyCode 的六组 mask 使用同一组 slot。实际游戏最多只会取前几根有效输入，
    // 16 个 slot 足够覆盖四押和 Android 的常见多指输入，同时让每帧 mask 构建保持固定大小。
    private const int PointerSlotCount = 16;
    private const int MaxOfficialEventsPerFrame = 256;

    private readonly ClockSync _clock = new();
    private readonly object _stateLock = new();
    private readonly int[] _pointerIds = new int[PointerSlotCount];

    private GameApi? _game;
    private bool _active;
    // 官方路径的跨帧状态。keyMask 和 frameDependentKeyMask 都是持有状态；
    // 四组 edge mask 在一次 PlayerControl_Update 内保留本帧已发生的边沿。
    private ulong _heldMask;
    private ulong _pointerActiveMask;
    private ulong _previousOfficialFrameTick;
    private long _lastObservedDropCount;
    private bool _officialInputTypesEnabled;
    private bool _officialClockReady;
    // 避免暂停、菜单或禁用状态下每帧重复清空六组官方 HashSet。
    private bool _officialInputGateClosed;

    // ── 调试统计（仅供 HUD 显示） ──
    private double _lastCorrectionMillis;
    private double _avgCorrectionMillis;
    private double _maxCorrectionMillis;
    private int _adjustedHits;
    private int _missedHits;
    private int _rejectedHits;
    private int _clockResets;
    private int _adjustedCalibrationSamples;
    private int _missedCalibrationSamples;
    private int _rejectedCalibrationSamples;
    private long _officialFrames;
    private long _officialEvents;
    private long _officialProcessFailures;
    private int _lastOfficialBatchSize;

    /// <summary>是否启用异步输入。关闭后判定完全走游戏原版路径。</summary>
    public bool Enabled = true;

    /// <summary>
    /// 附加偏移，单位毫秒。正值把事件解释为更早发生，负值把事件解释为更晚发生。
    /// 触摸屏上报延迟差异很大，这里留给玩家手动校准。
    /// </summary>
    public float OffsetMs;

    /// <summary>在游戏延迟校准页使用硬件时间戳，默认启用。</summary>
    public bool ImproveCalibration = true;

    /// <summary>显示调试信息，用于确认官方事件管线是否生效。</summary>
    public bool ShowDebugHud;

    public string Id => "AsyncInput";
    public string Name => "Async Input";
    public string Version => "1.0.0";
    public string Author => "iidamie";
    public string Description => "ADOFAI mobile async input — replays timestamped touch events through the game's native async path";
    public IReadOnlyList<string> Dependencies => Array.Empty<string>();

    internal bool CanImproveCalibration => _game?.CanAdjustCalibration == true;

    internal bool CanUseOfficialAsyncReplay => _game?.CanUseOfficialAsyncReplay == true;

    public void OnLoad()
    {
        _game = GameApi.Create();
        if (_game == null)
            throw new InvalidOperationException("ADOFAI IL2CPP runtime or Assembly-CSharp was not found");

        if (!_game.IsUsable)
        {
            string missing = _game.DescribeMissing();
            _game = null;
            throw new InvalidOperationException($"Required game fields were not found: {missing}");
        }

        ResetPointerState();
        try
        {
            if (!GameHooks.Install(this))
                throw new InvalidOperationException("AsyncInput game hooks could not be installed");

            TouchQueue.Subscribe();
            _active = true;
            // 校准页没有统一的进入 Hook，因此 Mod 活跃时持续接收轻量时间戳事件；
            // 进入关卡、暂停和恢复边界都会清空队列，菜单触摸不会进入判定链路。
            TouchQueue.SetCaptureEnabled(true);
            _lastObservedDropCount = TouchQueue.DroppedCount;
        }
        catch
        {
            TouchQueue.Unsubscribe();
            GameHooks.Uninstall();
            _game = null;
            throw;
        }

        Logger.Info(
            LogTag,
            $"Loaded; input path: {GameHooks.InputHookName}, official API: {CanUseOfficialAsyncReplay}");
    }

    public void OnUnload()
    {
        _active = false;
        TouchQueue.Unsubscribe();
        GameHooks.Uninstall();

        GameApi? game = _game;
        if (game?.CanUseOfficialAsyncReplay == true)
        {
            game.ResetAsyncInputState();
            game.SetAsyncInputTypes(false);
        }

        lock (_stateLock)
        {
            _clock.Reset();
        }
        ResetPointerState();
        _game = null;
        Logger.Info(LogTag, "Unloaded");
    }

    /// <summary>
    /// 更新 monotonic 到 wall tick 的锚点。官方游戏使用 DateTime tick，Android
    /// 触摸事件使用 CLOCK_MONOTONIC；夹心采样让两者之间只剩下系统调用窗口误差。
    /// </summary>
    private bool UpdateWallAnchor(
        out long monotonicNowNanos,
        out long wallNowTicks,
        out bool jumped)
    {
        monotonicNowNanos = 0L;
        wallNowTicks = 0L;
        jumped = false;

        long before = ClockSync.GetMonotonicNanos();
        long realtimeTicks = ClockSync.GetRealtimeTicks();
        long after = ClockSync.GetMonotonicNanos();
        if (before <= 0L || after < before || realtimeTicks <= 0L)
            return false;

        lock (_stateLock)
        {
            jumped = _clock.UpdateWallAnchor(realtimeTicks, before, after);
            wallNowTicks = _clock.GetWallTicks(after);
        }

        monotonicNowNanos = after;
        return wallNowTicks > 0L;
    }

    /// <summary>
    /// 判定回退路径的 DSP 锚点。官方路径不依赖这个换算，而是直接使用游戏的
    /// wall-to-DSP offset；保留该路径是为了兼容没有 ProcessKeyInputs 的版本。
    /// </summary>
    private bool UpdateClockAnchor(GameApi game, nint conductor, out long monotonicNowNanos)
    {
        monotonicNowNanos = 0L;
        if (!_active || conductor == 0)
            return false;

        double dspTimeBefore = game.GetCurrentDspTime(conductor);
        long before = ClockSync.GetMonotonicNanos();
        double dspTimeAfter = game.GetCurrentDspTime(conductor);
        long after = ClockSync.GetMonotonicNanos();
        monotonicNowNanos = after;
        double dspTime = (dspTimeBefore + dspTimeAfter) / 2d;

        if (before <= 0L || after < before || dspTime <= 0d)
            return false;

        lock (_stateLock)
        {
            if (_clock.Update(dspTime, before, after))
                _clockResets++;
            return _clock.IsReady;
        }
    }

    private static bool IsEventFresh(long eventNanos, long monotonicNowNanos)
    {
        double ageSeconds = (monotonicNowNanos - eventNanos) / 1_000_000_000d;
        return ageSeconds >= -FutureEventToleranceSeconds && ageSeconds <= MaxEventAgeSeconds;
    }

    /// <summary>
    /// 官方 AsyncInput 路径的帧入口。事件按时间分组，顺序与 PC 版 UpdateInput 相同：
    /// 先处理上一时间组，再清理普通 edge mask，最后更新下一组事件。
    /// </summary>
    internal bool BeginOfficialAsyncFrame(nint controller)
    {
        GameApi? game = _game;
        if (!_active || !Enabled || game == null || controller == 0)
        {
            if (!Enabled)
                CloseOfficialInput(game, clearQueue: true);
            return false;
        }

        if (game.IsPaused(controller) || !game.IsGameplayController(controller))
        {
            CloseOfficialInput(game, clearQueue: true);
            return false;
        }

        if (_officialInputGateClosed)
        {
            // 不允许菜单/暂停期间产生的触摸穿过状态边界进入歌曲时间轴。
            TouchQueue.Clear();
            _lastObservedDropCount = TouchQueue.DroppedCount;
        }
        TouchQueue.SetCaptureEnabled(true);
        _officialInputGateClosed = false;

        long dropped = TouchQueue.DroppedCount;
        if (dropped != _lastObservedDropCount)
        {
            // 丢失 Down 或 Up 都可能让 held 状态失真。宁可丢掉这段不完整序列，
            // 也不能把一个已经抬起的触点永久当成按住。
            _lastObservedDropCount = dropped;
            ResetOfficialInputState(clearQueue: false);
            game.ResetAsyncInputState();
        }

        if (!UpdateWallAnchor(
                out long monotonicNowNanos,
                out long wallNowTicks,
                out bool wallClockJumped))
        {
            return false;
        }

        if (wallClockJumped)
        {
            _clockResets++;
            TouchQueue.Clear();
            ResetOfficialInputState(clearQueue: false);
            game.ResetAsyncInputState();
            return false;
        }

        if (!game.PrepareAsyncFrame(
                (ulong)wallNowTicks,
                _previousOfficialFrameTick == 0UL
                    ? 0UL
                    : _previousOfficialFrameTick,
                out ulong offsetTick))
        {
            return false;
        }

        _officialClockReady = true;
        _previousOfficialFrameTick = (ulong)wallNowTicks;
        game.ClearAsyncInputEdges();
        if (!game.SetAsyncInputTypes(true))
        {
            game.ResetAsyncInputState();
            game.SetAsyncInputTypes(false);
            return false;
        }

        _officialInputTypesEnabled = true;

        ulong batchDownMask = 0UL;
        ulong batchUpMask = 0UL;
        ulong frameMask = _heldMask;
        ulong frameDownMask = 0UL;
        ulong frameUpMask = 0UL;
        long currentEventTick = 0L;
        bool haveEvent = false;
        int consumed = 0;
        int processedEvents = 0;

        while (consumed < MaxOfficialEventsPerFrame
               && TouchQueue.TryDequeueDue(
                   monotonicNowNanos,
                   FutureEventToleranceNanos,
                   out TouchTimestampInfo eventInfo))
        {
            consumed++;
            long eventTick = ToAdjustedWallTicks(eventInfo.EventTimeNanos);
            if (eventTick <= 0L)
                continue;

            if (!IsStateChanging(eventInfo))
                continue;

            if (haveEvent && eventTick < currentEventTick)
                eventTick = currentEventTick;

            if (haveEvent && eventTick != currentEventTick)
            {
                if (!ProcessOfficialBatch(
                        game,
                        controller,
                        (ulong)currentEventTick,
                        batchDownMask,
                        batchUpMask,
                        frameMask,
                        frameDownMask,
                        frameUpMask))
                {
                    AbortOfficialFrame(game);
                    return false;
                }

                batchDownMask = 0UL;
                batchUpMask = 0UL;
                currentEventTick = eventTick;
                processedEvents++;
            }
            else if (!haveEvent)
            {
                currentEventTick = eventTick;
                haveEvent = true;
            }

            ApplyTouchEvent(
                eventInfo,
                ref batchDownMask,
                ref batchUpMask,
                ref frameMask,
                ref frameDownMask,
                ref frameUpMask);
        }

        if (haveEvent)
        {
            if (!ProcessOfficialBatch(
                    game,
                    controller,
                    (ulong)currentEventTick,
                    batchDownMask,
                    batchUpMask,
                    frameMask,
                    frameDownMask,
                    frameUpMask))
            {
                AbortOfficialFrame(game);
                return false;
            }
            processedEvents++;
        }
        else if (!ProcessOfficialBatch(
                     game,
                     controller,
                     (ulong)wallNowTicks,
                     0UL,
                     0UL,
                     _heldMask,
                     0UL,
                     0UL))
        {
            AbortOfficialFrame(game);
            return false;
        }

        if (haveEvent)
            game.RestoreAsyncAngleToTick(controller, (ulong)wallNowTicks, offsetTick);

        _officialFrames++;
        _officialEvents += consumed;
        _lastOfficialBatchSize = processedEvents;
        return true;
    }

    internal void EndOfficialAsyncFrame()
    {
        if (!_officialInputTypesEnabled)
            return;

        try
        {
            GameApi? game = _game;
            game?.ClearAsyncInputEdges();
            game?.SetAsyncInputTypes(false);
        }
        finally
        {
            _officialInputTypesEnabled = false;
        }
    }

    private bool ProcessOfficialBatch(
        GameApi game,
        nint controller,
        ulong eventTick,
        ulong batchDownMask,
        ulong batchUpMask,
        ulong frameMask,
        ulong frameDownMask,
        ulong frameUpMask)
    {
        if (!game.ApplyAsyncInputMasks(
                _heldMask,
                batchDownMask,
                batchUpMask,
                frameMask,
                frameDownMask,
                frameUpMask))
        {
            _officialProcessFailures++;
            return false;
        }

        if (!game.ProcessAsyncInput(controller, eventTick))
        {
            _officialProcessFailures++;
            return false;
        }

        return true;
    }

    private void AbortOfficialFrame(GameApi game)
    {
        game.ResetAsyncInputState();
        ResetOfficialInputState(clearQueue: false);
        EndOfficialAsyncFrame();
    }

    /// <summary>
    /// 关闭官方输入窗口。该操作包含多次 IL2CPP 容器调用，只在状态边沿执行一次。
    /// </summary>
    private void CloseOfficialInput(GameApi? game, bool clearQueue)
    {
        if (_officialInputGateClosed)
            return;

        _officialInputGateClosed = true;
        TouchQueue.SetCaptureEnabled(false);
        ResetOfficialInputState(clearQueue);
        game?.ResetAsyncInputState();
        game?.SetAsyncInputTypes(false);
        _officialInputTypesEnabled = false;
    }

    private long ToAdjustedWallTicks(long eventTimeNanos)
    {
        long wallTicks;
        lock (_stateLock)
        {
            wallTicks = _clock.ToWallTicks(eventTimeNanos);
        }

        if (wallTicks <= 0L)
            return 0L;

        long offsetTicks = (long)Math.Round(OffsetMs * WallTicksPerMillisecond);
        if (offsetTicks > 0L && wallTicks <= offsetTicks)
            return 1L;
        return offsetTicks >= 0L
            ? wallTicks - offsetTicks
            : wallTicks + Math.Min(long.MaxValue + offsetTicks, -offsetTicks);
    }

    private bool IsStateChanging(TouchTimestampInfo eventInfo)
    {
        switch (eventInfo.Action)
        {
            case AndroidInput.MotionAction.Down:
            case AndroidInput.MotionAction.PointerDown:
                return eventInfo.PointerId >= 0 && FindPointerSlot(eventInfo.PointerId) < 0;

            case AndroidInput.MotionAction.Up:
                return _pointerActiveMask != 0UL;

            case AndroidInput.MotionAction.PointerUp:
                return eventInfo.PointerId >= 0
                    && FindPointerSlot(eventInfo.PointerId) >= 0;

            case AndroidInput.MotionAction.Cancel:
                return _pointerActiveMask != 0UL || _heldMask != 0UL;

            default:
                return false;
        }
    }

    private void ApplyTouchEvent(
        TouchTimestampInfo eventInfo,
        ref ulong batchDownMask,
        ref ulong batchUpMask,
        ref ulong frameMask,
        ref ulong frameDownMask,
        ref ulong frameUpMask)
    {
        switch (eventInfo.Action)
        {
            case AndroidInput.MotionAction.Down:
            case AndroidInput.MotionAction.PointerDown:
                if (_pointerActiveMask == 0UL && eventInfo.Action == AndroidInput.MotionAction.Down)
                    ResetPointerState();

                if (eventInfo.PointerId < 0)
                    return;

                int downSlot = FindPointerSlot(eventInfo.PointerId);
                if (downSlot < 0)
                    downSlot = AllocatePointerSlot(eventInfo.PointerId);
                if (downSlot < 0)
                    return;

                ulong downBit = 1UL << downSlot;
                if ((_heldMask & downBit) != 0UL)
                    return;

                _pointerActiveMask |= downBit;
                _heldMask |= downBit;
                batchDownMask |= downBit;
                frameMask |= downBit;
                frameDownMask |= downBit;
                return;

            case AndroidInput.MotionAction.Up:
                int upSlot = eventInfo.PointerId < 0
                    ? -1
                    : FindPointerSlot(eventInfo.PointerId);
                if (upSlot < 0 && eventInfo.Action == AndroidInput.MotionAction.Up)
                {
                    ReleaseAllPointers(
                        ref batchUpMask,
                        ref frameMask,
                        ref frameUpMask);
                    return;
                }
                ReleasePointerSlot(
                    upSlot,
                    ref batchUpMask,
                    ref frameMask,
                    ref frameUpMask);
                return;

            case AndroidInput.MotionAction.PointerUp:
                ReleasePointerSlot(
                    eventInfo.PointerId < 0 ? -1 : FindPointerSlot(eventInfo.PointerId),
                    ref batchUpMask,
                    ref frameMask,
                    ref frameUpMask);
                return;

            case AndroidInput.MotionAction.Cancel:
                ReleaseAllPointers(
                    ref batchUpMask,
                    ref frameMask,
                    ref frameUpMask);
                return;
        }
    }

    private void ReleasePointerSlot(
        int slot,
        ref ulong batchUpMask,
        ref ulong frameMask,
        ref ulong frameUpMask)
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

    private void ReleaseAllPointers(
        ref ulong batchUpMask,
        ref ulong frameMask,
        ref ulong frameUpMask)
    {
        ulong held = _heldMask;
        _heldMask = 0UL;
        _pointerActiveMask = 0UL;
        for (int i = 0; i < _pointerIds.Length; i++)
            _pointerIds[i] = -1;

        if (held == 0UL)
            return;

        batchUpMask |= held;
        frameMask &= ~held;
        frameUpMask |= held;
    }

    private int FindPointerSlot(int pointerId)
    {
        if (pointerId < 0)
            return -1;

        ulong active = _pointerActiveMask;
        for (int i = 0; i < PointerSlotCount; i++)
        {
            ulong bit = 1UL << i;
            if ((active & bit) != 0UL && _pointerIds[i] == pointerId)
                return i;
        }
        return -1;
    }

    private int AllocatePointerSlot(int pointerId)
    {
        for (int i = 0; i < PointerSlotCount; i++)
        {
            ulong bit = 1UL << i;
            if ((_pointerActiveMask & bit) == 0UL)
            {
                _pointerIds[i] = pointerId;
                return i;
            }
        }
        return -1;
    }

    private void ResetPointerState()
    {
        _heldMask = 0UL;
        _pointerActiveMask = 0UL;
        for (int i = 0; i < _pointerIds.Length; i++)
            _pointerIds[i] = -1;
    }

    private void ResetOfficialInputState(bool clearQueue)
    {
        ResetPointerState();
        _previousOfficialFrameTick = 0UL;
        if (clearQueue)
        {
            TouchQueue.Clear();
            _lastObservedDropCount = TouchQueue.DroppedCount;
        }
    }

    /// <summary>场景切换、重开、暂停恢复后音频时钟和事件序列都必须重建。</summary>
    internal void ResetClock(bool capture)
    {
        if (!capture)
            TouchQueue.SetCaptureEnabled(false);
        else
        {
            TouchQueue.Clear();
            TouchQueue.SetCaptureEnabled(true);
        }

        GameApi? game = _game;
        if (game?.CanUseOfficialAsyncReplay == true)
        {
            game.ResetAsyncInputState();
            game.SetAsyncInputTypes(false);
        }
        _officialInputTypesEnabled = false;
        _officialInputGateClosed = !capture;
        _officialClockReady = false;

        lock (_stateLock)
        {
            _clock.Reset();
        }
        ResetOfficialInputState(clearQueue: false);
        _lastObservedDropCount = TouchQueue.DroppedCount;
    }

    /// <summary>
    /// 旧版本兼容的角度回退。官方路径不安装这两个 Hook，因此不会重复消费事件。
    /// </summary>
    internal void AdjustAngleForHit(nint player, bool requirePendingKey)
    {
        if (!Enabled || !_active || player == 0)
            return;

        GameApi? game = _game;
        if (game == null)
            return;

        if (requirePendingKey && game.GetPendingKeyCount(player) <= 0)
            return;

        if (!TouchQueue.TryDequeueDown(out TouchTimestampInfo eventInfo))
        {
            _missedHits++;
            return;
        }

        nint planet = GetChosenPlanet(game, player);
        nint conductor = game.GetPlanetConductor(planet);
        if (!UpdateClockAnchor(game, conductor, out long monotonicNowNanos))
        {
            _missedHits++;
            return;
        }

        if (!IsEventFresh(eventInfo.EventTimeNanos, monotonicNowNanos))
        {
            _missedHits++;
            _rejectedHits++;
            return;
        }

        bool ready;
        double eventDspTime;
        lock (_stateLock)
        {
            ready = _clock.IsReady;
            eventDspTime = ready
                ? _clock.ToDspTime(eventInfo.EventTimeNanos)
                : 0d;
        }

        if (!ready || planet == 0)
        {
            _missedHits++;
            return;
        }

        // 与官方路径一致：正 OffsetMs 将事件推向更早的歌曲时刻。
        eventDspTime -= OffsetMs / 1000d;
        double? adjusted = game.ComputeAngle(planet, eventDspTime);
        if (adjusted == null)
        {
            _missedHits++;
            return;
        }

        double current = game.GetPlanetAngle(planet);
        double correction = adjusted.Value - current;
        double correctionSeconds = Math.Abs(AngleToSeconds(game, planet, correction));
        if (correctionSeconds > MaxCorrectionSeconds)
        {
            _missedHits++;
            _rejectedHits++;
            return;
        }

        game.SetPlanetAngle(planet, adjusted.Value);
        _adjustedHits++;
        _lastCorrectionMillis = AngleToMillis(game, planet, correction);

        double magnitude = Math.Abs(_lastCorrectionMillis);
        _avgCorrectionMillis += (magnitude - _avgCorrectionMillis) / Math.Min(_adjustedHits, 64);
        if (magnitude > _maxCorrectionMillis)
            _maxCorrectionMillis = magnitude;
    }

    /// <summary>
    /// 在延迟校准页记录样本前，把帧采样角度替换为触摸真实发生时刻的角度。
    /// </summary>
    internal void AdjustCalibrationForInput(nint calibration)
    {
        if (!Enabled || !ImproveCalibration || !_active || calibration == 0)
            return;

        GameApi? game = _game;
        if (game == null || !game.CanAdjustCalibration)
            return;

        if (!TouchQueue.TryDequeueLatestDown(out TouchTimestampInfo eventInfo))
        {
            _missedCalibrationSamples++;
            return;
        }

        nint calibrationConductor = game.GetCalibrationConductor(calibration);
        if (!UpdateClockAnchor(game, calibrationConductor, out long monotonicNowNanos))
        {
            _missedCalibrationSamples++;
            return;
        }

        if (!IsEventFresh(eventInfo.EventTimeNanos, monotonicNowNanos))
        {
            _missedCalibrationSamples++;
            _rejectedCalibrationSamples++;
            return;
        }

        double eventDspTime;
        lock (_stateLock)
        {
            eventDspTime = _clock.ToDspTime(eventInfo.EventTimeNanos);
        }

        // 校准页测量原始输入/音频差值，不叠加 Mod 的手动偏移。
        double? angle = game.ComputeCalibrationAngle(calibration, eventDspTime);
        if (angle == null)
        {
            _missedCalibrationSamples++;
            return;
        }

        game.SetCalibrationAngle(calibration, angle.Value);
        _adjustedCalibrationSamples++;
    }

    /// <summary>把角度差换算成秒。</summary>
    private static double AngleToSeconds(GameApi game, nint planet, double angleDelta)
    {
        nint conductor = game.GetPlanetConductor(planet);
        if (conductor == 0)
            return 0d;

        double crotchet = game.GetCrotchetAtStart(conductor);
        nint system = game.GetPlanetSystem(planet);
        double speed = system == 0 ? 1d : game.GetSystemSpeed(system);
        if (Math.Abs(speed) < 1e-9d)
            return 0d;

        return angleDelta / Math.PI * crotchet / speed;
    }

    private static double AngleToMillis(GameApi game, nint planet, double angleDelta)
        => AngleToSeconds(game, planet, angleDelta) * 1000d;

    private static nint GetChosenPlanet(GameApi game, nint player)
    {
        nint system = game.GetPlayerSystem(player);
        return system == 0 ? 0 : game.GetChosenPlanet(system);
    }

    public void OnGui()
    {
        bool enabledBefore = Enabled;
        ImGui.Checkbox("启用异步输入", ref Enabled);
        if (enabledBefore != Enabled)
        {
            if (Enabled)
                ResetClock(capture: true);
            else
            {
                TouchQueue.SetCaptureEnabled(false);
                ResetOfficialInputState(clearQueue: true);
                _game?.ResetAsyncInputState();
                _game?.SetAsyncInputTypes(false);
                _officialInputTypesEnabled = false;
                _officialInputGateClosed = true;
            }
        }
        else
        {
        }

        ImGui.TextWrapped(
            "优先把带硬件时间戳的触摸事件送入游戏官方 ProcessKeyInputs；"
            + "低帧率和同帧多次输入仍按真实事件时间处理。没有官方 API 时自动使用角度回退。\n"
            + $"当前入口: {GameHooks.InputHookName}");

        ImGui.Separator();

        ImGui.SliderFloat("附加偏移 (ms)", ref OffsetMs, -100f, 100f);
        ImGui.TextWrapped("正值把事件解释为更早发生，负值把事件解释为更晚发生。校准页不会使用此项。");

        ImGui.Checkbox("提升延迟校准精度", ref ImproveCalibration);
        ImGui.TextWrapped("校准样本使用触摸硬件时间戳；手动附加偏移不会写入校准结果。");

        ImGui.Separator();
        ImGui.Checkbox("显示调试信息", ref ShowDebugHud);
        if (!ShowDebugHud)
            return;

        double anchorMillis;
        double sampleSpanMicros;
        double wallSampleSpanMicros;
        bool ready;
        bool wallReady;
        bool officialClockReady;
        lock (_stateLock)
        {
            anchorMillis = _clock.AnchorMillis;
            sampleSpanMicros = _clock.SampleSpanMicros;
            wallSampleSpanMicros = _clock.WallSampleSpanMicros;
            ready = _clock.IsReady;
            wallReady = _clock.IsWallReady;
            officialClockReady = _officialClockReady;
        }

        bool officialPath = CanUseOfficialAsyncReplay;
        ImGui.Separator();
        ImGui.TextUnformatted($"输入入口: {GameHooks.InputHookName}");
        ImGui.TextUnformatted($"官方 API: {(CanUseOfficialAsyncReplay ? "可用" : "不可用")}");
        ImGui.TextUnformatted(
            officialPath
                ? $"时钟基准: {(officialClockReady ? "已建立（官方 wall-to-DSP）" : "等待官方帧")}" 
                : $"时钟基准: {(ready ? "已建立（DSP 回退）" : "未建立")}");
        ImGui.TextUnformatted($"Wall 基准: {(wallReady ? "已建立" : "未建立")}");
        ImGui.TextUnformatted($"事件时钟: {ClockSync.MonotonicSourceName}");
        ImGui.TextUnformatted(
            officialPath
                ? "DSP 锚点: 官方路径不使用独立 DSP 锚点"
                : $"DSP 锚点: {anchorMillis:F3} ms");
        ImGui.TextUnformatted($"DSP 采样跨度: {sampleSpanMicros:F1} us");
        ImGui.TextUnformatted($"Wall 采样跨度: {wallSampleSpanMicros:F1} us");
        ImGui.Separator();
        ImGui.TextUnformatted($"官方处理帧: {_officialFrames}");
        ImGui.TextUnformatted($"官方消费事件: {_officialEvents}");
        ImGui.TextUnformatted($"最近帧事件批次: {_lastOfficialBatchSize}");
        ImGui.TextUnformatted($"官方处理失败: {_officialProcessFailures}");
        ImGui.Separator();
        ImGui.TextUnformatted($"回退已修正判定: {_adjustedHits}");
        ImGui.TextUnformatted($"回退未修正判定: {_missedHits}");
        ImGui.TextUnformatted($"回退超限丢弃: {_rejectedHits}");
        ImGui.TextUnformatted($"校准已修正: {_adjustedCalibrationSamples}");
        ImGui.TextUnformatted($"校准未修正: {_missedCalibrationSamples}");
        ImGui.TextUnformatted($"校准过期丢弃: {_rejectedCalibrationSamples}");
        ImGui.TextUnformatted($"时钟复位次数: {_clockResets}");
        ImGui.TextUnformatted($"收到触摸事件: {TouchQueue.ReceivedCount}");
        ImGui.TextUnformatted($"队列溢出丢弃: {TouchQueue.DroppedCount}");
        ImGui.TextUnformatted($"过期/无效事件: {TouchQueue.StaleCount}");
        ImGui.TextUnformatted($"待处理触摸事件: {TouchQueue.Count}");

        if (ImGui.Button("重置统计"))
        {
            _adjustedHits = 0;
            _missedHits = 0;
            _rejectedHits = 0;
            _clockResets = 0;
            _adjustedCalibrationSamples = 0;
            _missedCalibrationSamples = 0;
            _rejectedCalibrationSamples = 0;
            _officialFrames = 0;
            _officialEvents = 0;
            _officialProcessFailures = 0;
            _lastOfficialBatchSize = 0;
            _lastCorrectionMillis = 0d;
            _avgCorrectionMillis = 0d;
            _maxCorrectionMillis = 0d;
            TouchQueue.ResetStatistics();
            _lastObservedDropCount = TouchQueue.DroppedCount;
        }
    }
}
