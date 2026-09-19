using System.Reflection;
using StArray.ModManager.Android.Native;
using ImGuiNET;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace AsyncInput.Mobile;

/// <summary>
/// 一次只在原版判定窗口内有效的角度投影。
/// </summary>
/// <remarks>
/// <c>FrameAngle</c> 是写入前的普通帧角度。原版没有改写 <c>ProjectedAngle</c>
/// 时，Hook 返回前可安全把它恢复；若游戏已切砖或刷新过角度，则保留游戏的新状态。
/// </remarks>
internal readonly struct JudgmentAngleProjection
{
    internal JudgmentAngleProjection(nint planet, double frameAngle, double projectedAngle)
    {
        Planet = planet;
        FrameAngle = frameAngle;
        ProjectedAngle = projectedAngle;
    }

    internal nint Planet { get; }

    internal double FrameAngle { get; }

    internal double ProjectedAngle { get; }

    internal bool IsValid => Planet != 0
                             && double.IsFinite(FrameAngle)
                             && double.IsFinite(ProjectedAngle);
}

/// <summary>
/// ADOFAI 手机版异步输入。
/// </summary>
/// <remarks>
/// <para>
/// 手机版缺少官方异步输入生产者；本 Mod 只提供 Android 边沿采集、时钟换算、指针映射
/// 和兼容 SkyHookEvent 的入队。
/// </para>
/// <para>
/// 游戏内保留的 PC <c>AsyncInputManager</c> 依赖 SkyHook，手机端没有对应原生库，
/// <c>isActive</c> 默认恒为 false。Mod 补上生产层和移植版缺失的 queue 接合后，事件继续
/// 进入 APK 自己的 <c>ProcessKeyInputs</c> 和玩家判定逻辑。
/// </para>
/// </remarks>
public sealed class AsyncInputPlugin : IModPlugin, IModSettings
{
    private const string LogTag = "AsyncInput";

    /// <summary>
    /// 输入事件从内核到 Unity 判定的最大允许时间。超过 250ms 的事件必然是菜单、
    /// 暂停或页面切换遗留项，不能拿来修正当前判定或校准样本。
    /// </summary>
    private const double MaxEventAgeSeconds = 0.25d;

    // An input timestamp must not belong to a future Unity input sample. A
    // very small allowance covers the two native clock reads without letting
    // the next physical press get paired with the current game input.
    private const double FutureEventToleranceSeconds = 0.001d;

    private const long NanosecondsPerSecond = 1_000_000_000L;
    private const long FutureEventToleranceNanos = 1_000_000L;
    private const long MaxEventAgeNanos = 250_000_000L;
    // The raw event should reach Unity's next input sample within a few frames.
    // Keep this much tighter than general event freshness so a UI tap that was
    // rejected by HitAutoFloors cannot be paired with a later gameplay tap.
    private const long MaxSourceAssociationAgeNanos = 80_000_000L;

    private readonly ClockSync _clock = new();
    private readonly MobileAsyncBridge _mobileAsyncBridge = new();
    private readonly OriginalAsyncClock _originalAsyncClock = new();
    private readonly OriginalAsyncProducer _originalAsyncProducer = new();
    private readonly SettingsMenuApi _settingsMenu = new();
    private GitHubUpdateService? _updateService;
    private readonly object _stateLock = new();
    private readonly Dictionary<nint, HoldState> _holdStates = new();
    private readonly Dictionary<nint, Queue<GameInputSource>> _gameInputSources = new();

    private GameApi? _game;
    private bool _active;
    private bool _holdReleaseCorrectionEnabled;
    private bool _mobileAsyncBridgeEnabled;
    private bool _originalAsyncChainEnabled;
    private bool _originalAsyncFailureLogged;
    private bool _originalGameplaySessionActive;

    private sealed class HoldState
    {
        internal long StartNanos;
        internal long PendingReleaseNanos;
        internal int PointerId = -1;
        internal bool WasHolding;
        internal JudgmentAngleProjection ReleaseProjection;
    }

    /// <summary>
    /// 一项 <c>scrPlayer.keyTimes</c> 的物理来源。游戏的队列只保存普通帧时间，
    /// 因此需要在它被创建时另行记录对应的 Android Down。
    /// Android 广播与 Unity 的触摸采样并不保证在同一个调用点完成，因此普通项可在
    /// <c>UpdateHoldKeys</c> 真正判定前短暂等待其 Down 到达。
    /// </summary>
    private sealed class GameInputSource
    {
        internal GameInputSource(long createdNanos)
        {
            CreatedNanos = createdNanos;
        }

        internal long CreatedNanos { get; }

        internal bool HasPress => Press.EventTimeNanos > 0L;

        internal bool AwaitsPress => !HasPress;

        internal TouchTimestampInfo Press { get; private set; }

        internal void SetPress(TouchTimestampInfo press)
        {
            if (press.EventTimeNanos > 0L)
                Press = press;
        }
    }

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
    private int _adjustedHoldReleases;
    private int _missedHoldReleases;
    private int _rejectedHoldReleases;
    private int _mappedGameInputs;
    private int _unmappedGameInputs;
    private int _consumedMappedInputs;
    private int _consumedUnmappedInputs;
    private int _sourceResyncs;

    /// <summary>是否启用异步输入。关闭后判定完全走游戏原版路径。</summary>
    public bool Enabled = true;

    /// <summary>
    /// 附加偏移，单位毫秒。正值判定得更早，负值更晚。
    /// 不同机型的触摸屏上报延迟差异很大，这里留给玩家手动校准。
    /// </summary>
    public float OffsetMs;

    /// <summary>在游戏延迟校准页使用硬件时间戳，默认启用。</summary>
    public bool ImproveCalibration = true;

    /// <summary>显示调试信息，用于确认重算是否真的生效。</summary>
    public bool ShowDebugHud;

    /// <summary>输出输入生产、时钟和会话边界的详细日志。</summary>
    public bool ShowDetailedLogs;

    public string Id => "AsyncInput";
    public string Name => "Async Input";
    public string Version => "1.0.0";
    public string Author => "iidamie";
    public string Description => "ADOFAI mobile async input — feeds timestamped events into the original async chain";
    public IReadOnlyList<string> Dependencies => Array.Empty<string>();

    internal bool CanImproveCalibration => _game?.CanAdjustCalibration == true;

    internal bool CanUseMobileAsyncBridge => _game?.CanUseMobileAsyncBridge == true;

    internal bool IsMobileAsyncBridgeEnabled => _mobileAsyncBridgeEnabled;

    internal bool CanUseOriginalAsyncChain => _game?.CanUseOriginalAsyncChain == true;

    internal bool IsOriginalAsyncChainEnabled => _originalAsyncChainEnabled;

    internal bool ShouldExposeOriginalAsyncInput
    {
        get
        {
            if (!_active || !Enabled || !_originalAsyncChainEnabled
                || !CanUseOriginalAsyncChain || _game == null)
                return false;

            // This is the logical switch exposed by the PC settings menu. It
            // remains readable while the pause/settings menu is open;
            // gameplay/capture gating belongs to IsOriginalGameplayContext.
            return true;
        }
    }

    /// <summary>
    /// Matches the state gate used by the game's AsyncInputManager.Update:
    /// the async source may remain logically available outside gameplay, but
    /// its RDInput types and touch capture are enabled only in PlayerControl.
    /// </summary>
    internal bool IsOriginalGameplayContext
    {
        get
        {
            if (!ShouldExposeOriginalAsyncInput || _game == null)
                return false;

            nint controller = _game.GetController();
            return controller != 0
                   && !_game.IsPaused(controller)
                   && _game.IsPlayerControlState(controller);
        }
    }

    internal bool IsHolding(nint player) => _game?.IsHolding(player) == true;

    internal int GetPendingKeyCount(nint player) => _game?.GetPendingKeyCount(player) ?? 0;

    internal bool HasMidspinInfiniteMargin(nint player)
        => _game?.HasMidspinInfiniteMargin(player) == true;

    internal bool IsCurrentFloorMidSpin(nint player)
        => _game?.IsCurrentFloorMidSpin(player) == true;

    internal bool IsAutoInputPath(nint player)
        => _game?.IsAutoInputPath(player) == true;

    /// <summary>
    /// 长按释放需要同时具备释放确认和帧末清理两个 Hook。任一缺失时只保留普通按下判定，
    /// 不能让释放时刻投影残留到下一帧。
    /// </summary>
    internal void SetHoldReleaseCorrectionEnabled(bool enabled)
    {
        _holdReleaseCorrectionEnabled = enabled;
        if (!enabled)
            ResetHoldTracking();
    }

    public void OnLoad()
    {
        string modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                              ?? AppContext.BaseDirectory;
        _updateService = new GitHubUpdateService(modDirectory, Version);
        _updateService.StartAutomaticCheck();

        _game = GameApi.Create();
        if (_game == null)
            throw new InvalidOperationException("ADOFAI IL2CPP runtime or Assembly-CSharp was not found");

        if (!_game.CanUseOriginalAsyncChain || !_game.InitializeOriginalAsyncQueue())
        {
            string missing = _game.DescribeOriginalAsyncMissing();
            _game = null;
            throw new InvalidOperationException($"Original mobile async chain was not found: {missing}");
        }

        try
        {
            _originalAsyncChainEnabled = true;
            _originalAsyncFailureLogged = false;
            _originalGameplaySessionActive = false;
            _active = true;
            
            // Ép chạy cài đặt Hook nhưng BỎ QUA nếu nó thất bại
            if (!OriginalGameHooks.Install(this))
            {
                Logger.Info(LogTag, "Original hooks failed to install, but ignoring since we use LocalQueue.");
                // ĐÃ XÓA DÒNG THROW ERROR Ở ĐÂY ĐỂ TRÁNH MOD BỊ TẮT
            }

            TouchQueue.Subscribe();
            TouchQueue.SetMobileAsyncCaptureMode(true);
            ResetClock();
        }
        catch
        {
            TouchQueue.Unsubscribe();
            OriginalGameHooks.Uninstall();
            _originalAsyncChainEnabled = false;
            _active = false;
            _updateService?.Dispose();
            _updateService = null;
            _settingsMenu.Reset();
            _game = null;
            throw;
        }

        Logger.Info(LogTag, "Loaded");
    }


    public void OnUnload()
    {
        _active = false;
        _originalAsyncChainEnabled = false;
        _originalGameplaySessionActive = false;
        _mobileAsyncBridgeEnabled = false;
        TouchQueue.SetCaptureEnabled(false);
        TouchQueue.Unsubscribe();
        TouchQueue.SetMobileAsyncCaptureMode(false);
        OriginalGameHooks.Uninstall();
        GameApi? unloadGame = _game;
        if (unloadGame != null)
            unloadGame.ClearOriginalAsyncInputState();
        unloadGame?.SetOriginalAsyncInputTypes(false);
        _originalAsyncProducer.Reset();
        _originalAsyncClock.Reset();
        _clock.Reset();
        _mobileAsyncBridge.Reset(_game);
        ResetHoldTracking();
        _updateService?.Dispose();
        _updateService = null;
        _settingsMenu.Reset();
        _game = null;
        Logger.Info(LogTag, "Unloaded");
    }

    internal void ObserveOriginalAsyncToggle(bool active)
    {
        _originalAsyncChainEnabled = active;
        TouchQueue.SetMobileAsyncCaptureMode(active);

        if (active)
        {
            ResetClock();
            return;
        }

        // The game's logical switch can be toggled by its desktop warning
        // path. It must not call the missing SkyHook library, but stale mobile
        // edges still need to be discarded at that boundary.
        TouchQueue.Clear();
        _originalAsyncProducer.Reset();
        _originalGameplaySessionActive = false;
        DebugLog($"Async toggle: {(active ? "on" : "off")}");
        GameApi? game = _game;
        if (game != null)
        {
            game.ClearOriginalAsyncInputState();
            game.SetOriginalAsyncInputTypes(false);
        }
    }

    internal void AddOriginalAsyncSetting(nint settingsMenu)
    {
        if (_game != null)
            _settingsMenu.TryAdd(_game, settingsMenu);
    }

    internal bool TryGetAsyncSettingDescription(nint setting, out nint description)
    {
        description = 0;
        GameApi? game = _game;
        if (game == null || !_settingsMenu.IsAsyncSetting(setting))
            return false;

        description = _settingsMenu.GetAsyncDescriptionPointer(game);
        return description != 0;
    }

    internal void RestoreAsyncSettingDescription(nint settingsMenu, nint setting)
    {
        if (_game != null)
            _settingsMenu.RestoreAsyncDescription(_game, settingsMenu, setting);
    }

    /// <summary>
    /// Replaces only the bookkeeping part of AsyncInputManager.Update. The
    /// original manager cannot be called on Android because it would consult
    /// the absent SkyHook producer; all queue consumption remains in the game.
    /// </summary>
    internal void MaintainOriginalAsyncManagerState()
    {
        GameApi? game = _game;
        if (game == null)
            return;

        if (!IsOriginalGameplayContext)
        {
            EndOriginalGameplaySession(game);
            game.SetOriginalAsyncInputTypes(false);
            return;
        }

        if (!BeginOriginalGameplaySession(game))
            return;

        if (!game.SetOriginalAsyncInputTypes(true))
            DisableOriginalAsyncChain("RDInput async source activation failed");
    }

    /// <summary>
    /// Supplies the precise frame tick used by the original async offset
    /// calculation. The game's own UpdateOffsetTime still performs the normal
    /// update; this only improves its frame-time sample.
    /// </summary>
    internal void PrepareOriginalAsyncOffset(long fixDivider)
    {
        _ = fixDivider;
        if (!IsOriginalGameplayContext)
            return;

        GameApi? game = _game;
        if (game == null)
            return;

        if (_originalAsyncClock.TryGetCurrentDateTimeTicks(out ulong frameTick))
            game.SetOriginalAsyncCurrentFrameTick(frameTick);
    }

    /// <summary>
    /// Applies the Iridium v3-style XRUN/30-sample correction after the APK's
    /// original UpdateOffsetTime has updated its offset field.
    /// </summary>
    internal void CompleteOriginalAsyncOffset()
    {
        if (!IsOriginalGameplayContext)
            return;

        GameApi? game = _game;
        if (game == null || !game.TryGetOriginalAsyncOffset(out ulong measuredOffset))
            return;

        double audioBufferSeconds = game.GetAudioBufferDuration();
        if (_originalAsyncClock.TryCorrectAsyncOffset(
                measuredOffset,
                game.GetOriginalAsyncOffsetTick(),
                audioBufferSeconds,
                out ulong correctedOffset))
        {
            game.SetOriginalAsyncOffsetTick(correctedOffset);
        }
    }

    /// <summary>
    /// Prepares the game's own UpdateInput call. The return value controls the
    /// temporary touchEnabled override while the APK consumes keyQueue.
    /// </summary>
      internal bool PrepareOriginalInputUpdate(nint controller)
    {
        GameApi? game = _game;
        if (game == null || controller == 0)
            return false;

        if (!IsOriginalGameplayContext)
        {
            EndOriginalGameplaySession(game);
            game.SetOriginalAsyncInputTypes(false);
            return false;
        }

        if (!BeginOriginalGameplaySession(game))
            return false;

        if (!game.SetOriginalAsyncInputTypes(true))
        {
            DisableOriginalAsyncChain("RDInput async source activation failed");
            return false;
        }

        if (!_originalAsyncProducer.Flush(
                game,
                controller,
                _originalAsyncClock,
                OffsetMs))
        {
            DisableOriginalAsyncChain("Android event producer could not feed the original queue");
            return false;
        }

        // Tích hợp đọc hàng đợi giả
        game.ProcessLocalQueue();

        if (_originalAsyncProducer.LastFlushRawCount > 0
            || _originalAsyncProducer.LastFlushProducedCount > 0)
        {
            DebugLog(
                $"UpdateInput flush: raw={_originalAsyncProducer.LastFlushRawCount}, "
                + $"queue={_originalAsyncProducer.LastFlushProducedCount}, "
                + $"ageMs={_originalAsyncProducer.LastDispatchAgeMilliseconds:F3}");
        }

        return true;
    }


    internal void DebugLog(string message)
    {
        if (ShowDetailedLogs)
            Logger.Debug(LogTag, message);
    }

    internal int GetOriginalAsyncPressCount()
        => IsOriginalGameplayContext ? _game?.GetOriginalAsyncPressCount() ?? 0 : 0;

    private bool BeginOriginalGameplaySession(GameApi game)
    {
        if (!_originalGameplaySessionActive)
        {
            _originalGameplaySessionActive = true;
            _originalAsyncProducer.Reset();
            // A state transition can end a gameplay session without passing
            // through PlayerControl_Enter. Do not carry the previous session's
            // Iridium correction window into the new song timeline.
            _originalAsyncClock.ResetAsyncOffsetCorrection();
            game.ClearOriginalAsyncInputState();
        }

        TouchQueue.SetMobileAsyncCaptureMode(true);
        TouchQueue.SetCaptureEnabled(true);
        return true;
    }

    private void EndOriginalGameplaySession(GameApi game)
    {
        if (!_originalGameplaySessionActive
            && TouchQueue.MobileAsyncCount == 0
            && !_originalAsyncProducer.HasActivePointers)
        {
            TouchQueue.SetCaptureEnabled(false);
            _originalAsyncProducer.Reset();
            return;
        }

        TouchQueue.SetCaptureEnabled(false);
        _originalAsyncProducer.Reset();
        game.ClearOriginalAsyncInputState();
        _originalGameplaySessionActive = false;
    }

    private void DisableOriginalAsyncChain(string reason)
    {
        if (!_originalAsyncFailureLogged)
        {
            _originalAsyncFailureLogged = true;
            Logger.Error(LogTag, reason);
        }

        _originalAsyncChainEnabled = false;
        if (_game != null)
        {
            GameApi game = _game;
            EndOriginalGameplaySession(game);
            game.SetOriginalAsyncInputTypes(false);
            game.ClearOriginalAsyncInputState();
        }
        else
        {
            TouchQueue.SetCaptureEnabled(false);
            _originalAsyncProducer.Reset();
        }
    }

    /// <summary>
    /// Kept for compatibility with the older fallback hook set. The active
    /// original-chain path never enables this bridge.
    /// </summary>
    internal void SetMobileAsyncBridgeEnabled(bool enabled)
    {
        _mobileAsyncBridgeEnabled = enabled && CanUseMobileAsyncBridge;
        TouchQueue.SetMobileAsyncCaptureMode(_mobileAsyncBridgeEnabled);
        // Direct async capture starts only after BeginMobileAsyncFrame has
        // confirmed a live PlayerControl session. Never keep a menu/fallback
        // capture window alive while switching producers.
        TouchQueue.SetCaptureEnabled(false);
        if (!_mobileAsyncBridgeEnabled)
            _mobileAsyncBridge.Reset(_game);
    }

    /// <summary>
    /// Fallback clock setup for builds where the mobile bridge is unavailable.
    /// Once the conductor hook has supplied a frame-consistent anchor, retain
    /// that anchor instead of replacing it with a later audio-thread sample.
    /// </summary>
    private bool UpdateClockAnchor(GameApi game, nint conductor, out long monotonicNowNanos)
    {
        monotonicNowNanos = 0L;
        if (!_active || conductor == 0)
            return false;

        monotonicNowNanos = ClockSync.GetMonotonicNanos();
        if (monotonicNowNanos <= 0L)
            return false;

        lock (_stateLock)
        {
            if (_clock.IsReady)
                return true;
        }

        long before = ClockSync.GetMonotonicNanos();
        double dspTime = game.GetAudioDspTime();
        if (!double.IsFinite(dspTime) || dspTime <= 0d)
            dspTime = game.GetCurrentDspTime(conductor);
        long after = ClockSync.GetMonotonicNanos();
        monotonicNowNanos = after;

        if (before <= 0L || after < before || !double.IsFinite(dspTime) || dspTime <= 0d)
            return false;

        lock (_stateLock)
        {
            _clock.Update(dspTime, before, after);
        }
        return true;
    }

    /// <summary>
    /// Samples the realtime audio timeline used to convert Android monotonic
    /// timestamps. The sample is bracketed by the same clock domain as the
    /// touch edge; using the conductor's earlier frame cache here would add
    /// the work already spent in scrConductor.Update to every event tick.
    /// </summary>
    internal void ObserveMobileAsyncClock()
    {
        if (!_active || !_mobileAsyncBridgeEnabled)
            return;

        GameApi? game = _game;
        nint conductor = game?.GetConductor() ?? 0;
        if (game == null || conductor == 0)
            return;

        long monotonicBeforeNanos = ClockSync.GetMonotonicNanos();
        double dspTime = game.GetAudioDspTime();
        // Old exports can omit AudioSettings from runtime metadata. Their
        // current conductor time includes the game's own in-frame
        // extrapolation and remains coherent when sampled in this bracket.
        if (!double.IsFinite(dspTime) || dspTime <= 0d)
            dspTime = game.GetCurrentDspTime(conductor);
        long monotonicAfterNanos = ClockSync.GetMonotonicNanos();

        if (!double.IsFinite(dspTime) || dspTime <= 0d
            || monotonicBeforeNanos <= 0L
            || monotonicAfterNanos < monotonicBeforeNanos)
            return;

        lock (_stateLock)
        {
            _clock.Update(dspTime, monotonicBeforeNanos, monotonicAfterNanos);
        }
    }

    /// <summary>
    /// Enables the game's async frame path before scrConductor.Update creates
    /// its frame tick. The raw events are consumed only after that method has
    /// updated offsetTick and the conductor clock.
    /// </summary>
    internal bool BeginMobileAsyncFrame(nint conductor)
    {
        if (!_active || !Enabled || !_mobileAsyncBridgeEnabled || conductor == 0)
            return false;

        GameApi? game = _game;
        if (game == null || !game.CanUseMobileAsyncBridge)
            return false;

        nint controller = game.GetController();
        if (controller == 0 || game.IsPaused(controller) || !game.IsGameplayController(controller))
        {
            TouchQueue.SetCaptureEnabled(false);
            _mobileAsyncBridge.Reset(game);
            return false;
        }

        TouchQueue.SetCaptureEnabled(true);
        return _mobileAsyncBridge.PrepareFrame(game);
    }

    /// <summary>
    /// Drains raw edges into ProcessKeyInputs after the conductor has generated
    /// the frame's offsetTick. The scoped touch override is set by the bridge
    /// only around each original player update.
    /// </summary>
    internal bool ProcessMobileAsyncFrame()
    {
        if (!_active || !Enabled || !_mobileAsyncBridgeEnabled)
            return false;

        GameApi? game = _game;
        nint conductor = game?.GetConductor() ?? 0;
        if (game == null || conductor == 0 || !game.CanUseMobileAsyncBridge)
            return false;

        nint controller = game.GetController();
        if (controller == 0 || game.IsPaused(controller) || !game.IsGameplayController(controller))
            return false;

        long monotonicNowNanos = ClockSync.GetMonotonicNanos();
        if (monotonicNowNanos <= 0L
            || !game.SynchronizeMobileAsyncFrame(conductor, out ulong frameTick, out ulong offsetTick))
            return false;

        lock (_stateLock)
        {
            if (!_clock.IsReady)
                return false;

            return _mobileAsyncBridge.ProcessFrame(
                game,
                controller,
                monotonicNowNanos,
                frameTick,
                offsetTick,
                _clock,
                OffsetMs);
        }
    }

    private static bool IsEventFresh(long eventNanos, long monotonicNowNanos)
    {
        double ageSeconds = (monotonicNowNanos - eventNanos) / (double)NanosecondsPerSecond;
        return ageSeconds >= -FutureEventToleranceSeconds && ageSeconds <= MaxEventAgeSeconds;
    }

    /// <summary>场景切换、重开、暂停恢复后音频时钟会跳变，必须重建基准。</summary>
    internal void ResetClock(bool capture = true)
    {
        lock (_stateLock)
        {
            if (_clock.IsReady)
                _clockResets++;
            _clock.Reset();
            _originalAsyncClock.Reset();
        }
        TouchQueue.Clear();
        _originalAsyncProducer.Reset();
        _originalGameplaySessionActive = false;
        GameApi? resetGame = _game;
        if (resetGame != null)
            resetGame.ClearOriginalAsyncInputState();
        resetGame?.SetOriginalAsyncInputTypes(false);
        TouchQueue.SetMobileAsyncCaptureMode(_originalAsyncChainEnabled);

        // Capture starts on the next UpdateInput call, after
        // the controller state has been checked. This prevents a pause/menu
        // tap made immediately after a reset from entering the next session.
        bool gameplay = false;
        if (capture && IsOriginalGameplayContext && _game != null)
        {
            nint controller = _game.GetController();
            gameplay = controller != 0
                       && !_game.IsPaused(controller)
                       && _game.IsPlayerControlState(controller);
        }
        TouchQueue.SetCaptureEnabled(gameplay);
        if (_mobileAsyncBridgeEnabled)
            _mobileAsyncBridge.Reset(_game);
        ResetHoldTracking();
        ResetGameInputSources();
        DebugLog("Async clock/input session reset");
    }

    /// <summary>
    /// 在 <c>HitAutoFloors</c> 返回后记录本次新增的游戏输入项。
    /// 游戏在这里确认了 Unity 的 Down，但 Android 时间戳广播可能尚未被主线程读取；
    /// 因此先记为待关联，等 <c>UpdateHoldKeys</c> 真正判定前再绑定最早的对应 Down。
    /// </summary>
    internal void ObserveHitAutoFloors(nint player, int pendingBefore)
    {
        if (!_active || player == 0)
            return;

        GameApi? game = _game;
        if (game == null)
            return;

        int pendingAfter = game.GetPendingKeyCount(player);
        int added = pendingAfter > pendingBefore ? pendingAfter - pendingBefore : 0;
        if (added <= 0)
            return;

        Queue<GameInputSource> sources = GetGameInputSources(player);
        long createdNanos = ClockSync.GetMonotonicNanos();
        for (int i = 0; i < added; i++)
        {
            sources.Enqueue(new GameInputSource(createdNanos));
            if (sources.Count > 256)
            {
                AbandonGameInputSources(sources);
                DiscardAmbiguousPresses();
                _sourceResyncs++;
                return;
            }
        }
    }

    /// <summary>
    /// 判定前的角度修正 —— 本 Mod 的核心。
    /// 取出这次判定对应的按下时刻，按该时刻重算行星角度并写回，
    /// 随后游戏用原有逻辑判定，得到的就是「按下瞬间」而非「渲染帧」的结果。
    /// </summary>
    /// <param name="player">触发判定的 <c>scrPlayer</c></param>
    internal void AdjustAngleForHit(
        nint player,
        bool skipTimestampMapping,
        out long pressNanos,
        out TouchTimestampInfo press,
        out int pendingBefore,
        out JudgmentAngleProjection projection)
    {
        pressNanos = 0L;
        press = default;
        pendingBefore = 0;
        projection = default;
        if (!Enabled || !_active || player == 0)
            return;

        GameApi? game = _game;
        if (game == null)
            return;

        // UpdateHoldKeys 每帧都会被调用，但只有 keyTimes 非空时才会真正驱动一次判定。
        // 空闲帧必须直接返回，否则会白白吃掉队列里的时间戳。
        pendingBefore = game.GetPendingKeyCount(player);
        if (pendingBefore <= 0)
            return;

        // HitAutoFloors 已经把每一项 keyTimes 映射到来源。队列没有映射时，
        // 说明这是旧输入、自动项或映射失效，保持原版角度，不猜测队头触摸。
        Queue<GameInputSource> sources = GetGameInputSources(player);
        if (sources.Count != pendingBefore)
        {
            // keyTimes can be cleared by an automatic/replay/reset path that
            // does not pass through HitAutoFloors. Never reuse an old source.
            AbandonGameInputSources(sources);
            DiscardAmbiguousPresses();
            _unmappedGameInputs += pendingBefore;
            _missedHits++;
            return;
        }

        GameInputSource source = sources.Peek();

        // A midspin continuation can consume a game-created keyTimes entry in
        // the same original call. Its physical origin is ambiguous, so it must
        // never borrow the next Android Down.
        if (skipTimestampMapping || IsAutoInputPath(player))
        {
            _unmappedGameInputs++;
            _missedHits++;
            return;
        }

        nint conductor = game.GetPlanetConductor(GetChosenPlanet(game, player));
        if (!UpdateClockAnchor(game, conductor, out long monotonicNowNanos))
        {
            _missedHits++;
            return;
        }

        if (source.AwaitsPress)
        {
            if (TouchQueue.TryTakePressForGameInput(
                    source.CreatedNanos,
                    monotonicNowNanos,
                    MaxSourceAssociationAgeNanos,
                    FutureEventToleranceNanos,
                    out TouchTimestampInfo arrivingPress))
            {
                source.SetPress(arrivingPress);
                _mappedGameInputs++;
            }
            else
            {
                _unmappedGameInputs++;
                _missedHits++;
                return;
            }
        }

        TouchTimestampInfo mappedPress = source.Press;

        press = mappedPress;
        long eventNanos = mappedPress.EventTimeNanos;

        if (!IsEventFresh(eventNanos, monotonicNowNanos))
        {
            _missedHits++;
            _rejectedHits++;
            return;
        }

        nint planet = GetChosenPlanet(game, player);
        if (planet == 0)
            return;

        if (!TryAdjustAngleAtEvent(
                game,
                player,
                planet,
                eventNanos,
                monotonicNowNanos,
                out double correctionMillis,
                out projection))
        {
            _missedHits++;
            _rejectedHits++;
            return;
        }

        // 即使当前不是长按，调用方也需要知道此次判定真正消费的 Down；
        // 它会在原 UpdateHoldKeys 返回后确认是否刚刚开始了长按。
        pressNanos = eventNanos;
        _adjustedHits++;
        _lastCorrectionMillis = correctionMillis;

        // 滑动平均与峰值 —— 判断修正量是「随机抖动」还是「系统性偏差」的关键依据。
        // 正常情况下平均值应接近半个帧周期且远小于峰值；若平均值本身就很大，
        // 说明时钟基准存在系统性偏移，而不是在消除采样抖动。
        double magnitude = Math.Abs(_lastCorrectionMillis);
        _avgCorrectionMillis += (magnitude - _avgCorrectionMillis) / Math.Min(_adjustedHits, 64);
        if (magnitude > _maxCorrectionMillis)
            _maxCorrectionMillis = magnitude;
    }

    /// <summary>
    /// <c>Simulated_PlayerControl_Update</c> 在每帧最开始调用
    /// <c>ValidInputWasReleased</c>。若此帧原版确认发生了长按释放，随后同一帧的
    /// <c>UpdateHoldBehavior</c>、<c>CheckPreHoldFail</c> 和 <c>UpdateHoldKeys</c>
    /// 都应使用 Up 的硬件时刻，而不是渲染帧时刻。
    /// </summary>
    internal void OnValidInputWasReleased(nint player, bool wasHoldingBefore, bool released)
    {
        if (!Enabled || !_active || !_holdReleaseCorrectionEnabled
            || player == 0 || !wasHoldingBefore || !released)
            return;

        GameApi? game = _game;
        if (game == null)
            return;

        HoldState state = GetHoldState(player);
        long holdStartNanos = state.StartNanos;
        int holdPointerId = state.PointerId;
        state.StartNanos = 0L;
        state.WasHolding = false;
        state.PendingReleaseNanos = 0L;
        state.PointerId = -1;
        if (holdStartNanos <= 0L)
        {
            _missedHoldReleases++;
            return;
        }

        nint planet = GetChosenPlanet(game, player);
        nint conductor = game.GetPlanetConductor(planet);
        if (!UpdateClockAnchor(game, conductor, out long monotonicNowNanos))
        {
            _missedHoldReleases++;
            return;
        }

        if (!TouchQueue.TryPeekLatestReleaseSince(
                holdStartNanos,
                monotonicNowNanos,
                MaxEventAgeNanos,
                FutureEventToleranceNanos,
                holdPointerId,
                out TouchTimestampInfo release))
        {
            _missedHoldReleases++;
            return;
        }

        TouchQueue.RemoveRelease(release);
        if (!IsEventFresh(release.EventTimeNanos, monotonicNowNanos))
        {
            _rejectedHoldReleases++;
            return;
        }

        if (!TryAdjustAngleAtEvent(
                game,
                player,
                planet,
                release.EventTimeNanos,
                monotonicNowNanos,
                out double correctionMillis,
                out JudgmentAngleProjection projection))
        {
            _rejectedHoldReleases++;
            return;
        }

        state.PendingReleaseNanos = release.EventTimeNanos;
        state.ReleaseProjection = projection;
        _lastCorrectionMillis = correctionMillis;
        _adjustedHoldReleases++;
    }

    /// <summary>
    /// 释放相关的原版判断已全部完成，准备进入普通按键判定前撤销 Up 的临时投影。
    /// </summary>
    /// <remarks>
    /// <c>UpdateHoldBehavior</c>、<c>HitHoldFloorsIfStartedAtHold</c> 和
    /// <c>CheckPreHoldFail</c> 都在 <c>UpdateHoldKeys</c> 前执行，应该看到 Up 时刻。
    /// 后者若消费的是另一笔 Down，则必须从普通帧角度重新开始，不能复用这笔 Up。
    /// </summary>
    internal void EndHoldReleaseJudgmentWindow(nint player)
    {
        if (!Enabled || !_active || !_holdReleaseCorrectionEnabled || player == 0)
            return;

        HoldState state = GetHoldState(player);
        if (state.PendingReleaseNanos <= 0L)
            return;

        RestoreJudgmentProjection(state.ReleaseProjection);
        state.ReleaseProjection = default;
        // The Up has now influenced every release-related branch in this
        // Simulated_PlayerControl_Update. Clearing it here prevents the frame
        // cleanup from erasing a new hold that starts later in the same frame.
        state.PendingReleaseNanos = 0L;
    }

    /// <summary>
    /// 在普通逐帧状态更新末尾同步长按生命周期。它使成功开始的长按得到对应 Down 基准，
    /// 也会在自动结束、失败、重开等没有经过释放回调的路径上清理遗留状态。
    /// </summary>
    internal void CompleteUpdateHoldKeys(
        nint player,
        long pressNanos,
        TouchTimestampInfo press,
        int pendingBefore,
        bool midspinBefore,
        bool midspinFloorBefore)
    {
        if (!_active || player == 0)
            return;

        GameApi? game = _game;
        if (game == null)
            return;

        int pendingAfter = game.GetPendingKeyCount(player);
        bool gameConsumed = pendingAfter < pendingBefore;
        bool midspinAfter = game.HasMidspinInfiniteMargin(player);
        Queue<GameInputSource> sources = GetGameInputSources(player);

        // A midspin can append a game-created keyTimes item and consume a
        // second entry before this call returns. With a multipress, that
        // second entry can be a real queued input rather than the appended
        // entry. The game handles that ordering itself, but this Mod cannot
        // recover a one-to-one physical source mapping afterward. Drop the
        // whole affected batch so a synthetic tail can never borrow a later
        // Android Down.
        bool midspinSequence = midspinBefore || midspinFloorBefore || midspinAfter;
        bool midspinQueueAffected = midspinSequence
                                    && (pendingBefore > 0 || pendingAfter > 0 || sources.Count > 0);
        bool keyConsumed = gameConsumed && sources.Count > 0;
        if (midspinQueueAffected)
        {
            if (keyConsumed)
                RecordConsumedGameInput(sources.Dequeue());

            if (sources.Count > 0)
            {
                AbandonGameInputSources(sources);
            }
            DiscardAmbiguousPresses();
            _sourceResyncs++;
        }
        else
        {
            if (keyConsumed)
                RecordConsumedGameInput(sources.Dequeue());

            if (sources.Count != pendingAfter)
            {
                // UpdateHoldKeys can return early through auto/replay/reset paths.
                // Discarding stale associations is safer than shifting every later
                // physical input by one timestamp.
                AbandonGameInputSources(sources);
                DiscardAmbiguousPresses();
                _sourceResyncs++;
            }
        }

        // The game can move to a different chosen planet after Hit, and its
        // final Update_RefreshAngles may run on that new object. A projection
        // whose planet was not touched by that refresh is still restored by
        // the outer Hook finally; this branch only keeps bookkeeping local.

        if (!_holdReleaseCorrectionEnabled)
            return;

        HoldState state = GetHoldState(player);
        bool holding = game.IsHolding(player);
        if (holding)
        {
            state.WasHolding = true;
            if (!midspinQueueAffected && state.StartNanos <= 0L && pressNanos > 0L && gameConsumed)
                BeginHoldTracking(player, pressNanos, press.PointerId);
            return;
        }

        if (state.PendingReleaseNanos == 0L && state.WasHolding)
            ResetHoldTracking(player);
    }

    /// <summary>
    /// 当前帧最后一处相关判定完成后释放 Up 的短生命周期投影。
    /// 同时把已到达但尚未被 Unity 采样到的 Down 留在队列中，供下一帧的
    /// <c>HitAutoFloors</c> 项关联；真正过期的项由 <see cref="TouchQueue"/> 清理。
    /// </summary>
    internal void EndHoldReleaseFrame(nint player)
    {
        if (player == 0 || !_holdStates.TryGetValue(player, out HoldState? state))
            return;

        if (state.PendingReleaseNanos > 0L)
        {
            RestoreJudgmentProjection(state.ReleaseProjection);
            ResetHoldTracking(player);
        }
    }

    /// <summary>
    /// 把临时投影撤回到 Hook 进入时的普通帧状态。
    /// 原版若已改写该行星角度，说明它完成了状态转换或刷新，此时不覆盖它。
    /// </summary>
    internal void RestoreJudgmentProjection(JudgmentAngleProjection projection)
    {
        if (!_active || !projection.IsValid)
            return;

        _game?.RestorePlanetAngleIfUnchanged(
            projection.Planet,
            projection.ProjectedAngle,
            projection.FrameAngle);
    }

    /// <summary>
    /// 在原版本帧完成后统一整理游戏输入来源。
    /// <c>HitAutoFloors</c> 与 <c>UpdateHoldKeys</c> 都发生在
    /// <c>Simulated_PlayerControl_Update</c> 内，这里能够安全确认是否有未消费的
    /// 来源项；任何不一致都放弃关联而不是错配后续真实触摸。
    /// </summary>
    internal void CompletePlayerControlFrame(nint player)
    {
        if (!_active || player == 0)
            return;

        GameApi? game = _game;
        if (game == null)
            return;

        Queue<GameInputSource> sources = GetGameInputSources(player);
        int pending = game.GetPendingKeyCount(player);
        if (sources.Count == pending)
        {
            long now = ClockSync.GetMonotonicNanos();
            if (now > 0L)
                TouchQueue.DiscardStalePresses(now, MaxEventAgeNanos);
            return;
        }

        AbandonGameInputSources(sources);
        DiscardAmbiguousPresses();
        _sourceResyncs++;
    }

    /// <summary>
    /// 在延迟校准页记录样本前，把帧采样角度替换为触摸真实发生时刻的角度。
    /// 游戏原有的偏移计算、异常值剔除、稳定性评级和保存流程保持不变。
    /// </summary>
    internal void AdjustCalibrationForInput(nint calibration)
    {
        if (!Enabled || !ImproveCalibration || !_active || calibration == 0)
            return;

        GameApi? game = _game;
        if (game == null || !game.CanAdjustCalibration)
            return;

        // 校准页每帧至多记录一个样本。取最新项可自动丢弃进入页面、开始音乐
        // 或点击界面留下的旧触摸，保证样本对应本次 PutDataPoint。
        if (!TouchQueue.TryDequeueLatestPress(out TouchTimestampInfo press))
        {
            _missedCalibrationSamples++;
            return;
        }

        long eventNanos = press.EventTimeNanos;

        nint calibrationConductor = game.GetCalibrationConductor(calibration);
        if (!UpdateClockAnchor(game, calibrationConductor, out long monotonicNowNanos))
        {
            _missedCalibrationSamples++;
            return;
        }

        if (!IsEventFresh(eventNanos, monotonicNowNanos))
        {
            _missedCalibrationSamples++;
            _rejectedCalibrationSamples++;
            return;
        }

        double eventDspTime;
        lock (_stateLock)
        {
            eventDspTime = _clock.ToDspTime(eventNanos);
        }

        // 不应用 Mod 的手动 OffsetMs。校准页应测量原始输入/音频差值，
        // 否则保存到游戏的 inputOffset 后会与手动偏移重复计算。
        double? angle = game.ComputeCalibrationAngle(calibration, eventDspTime);
        if (angle == null)
        {
            _missedCalibrationSamples++;
            return;
        }

        game.SetCalibrationAngle(calibration, angle.Value);
        _adjustedCalibrationSamples++;
    }

    private bool TryAdjustAngleAtEvent(
        GameApi game,
        nint player,
        nint planet,
        long eventNanos,
        long monotonicNowNanos,
        out double correctionMillis,
        out JudgmentAngleProjection projection)
    {
        _ = player;
        correctionMillis = 0d;
        projection = default;
        if (planet == 0 || eventNanos <= 0L || !IsEventFresh(eventNanos, monotonicNowNanos))
            return false;

        bool ready;
        double eventDspTime;
        lock (_stateLock)
        {
            ready = _clock.IsReady;
            eventDspTime = ready ? _clock.ToDspTime(eventNanos) : 0d;
        }
        if (!ready)
            return false;

        // 与现有 Mod 的设置语义保持一致：正值让判定使用更早的事件时刻。
        eventDspTime -= OffsetMs / 1000d;
        double? adjusted = game.ComputeAngle(planet, eventDspTime);
        if (!adjusted.HasValue || !double.IsFinite(adjusted.Value))
            return false;

        double frameAngle = game.GetPlanetAngle(planet);
        double correction = adjusted.Value - frameAngle;

        game.SetPlanetAngleForJudgment(planet, adjusted.Value);
        projection = new JudgmentAngleProjection(planet, frameAngle, adjusted.Value);
        correctionMillis = AngleToMillis(game, planet, correction);
        return true;
    }

    private void BeginHoldTracking(nint player, long pressNanos, int pointerId)
    {
        if (pressNanos <= 0L)
            return;

        HoldState state = GetHoldState(player);
        state.StartNanos = pressNanos;
        state.PendingReleaseNanos = 0L;
        state.PointerId = pointerId;
        state.WasHolding = true;
    }

    private HoldState GetHoldState(nint player)
    {
        if (!_holdStates.TryGetValue(player, out HoldState? state))
        {
            state = new HoldState();
            _holdStates.Add(player, state);
        }
        return state;
    }

    private void ResetHoldTracking(nint player)
    {
        if (!_holdStates.TryGetValue(player, out HoldState? state))
            return;

        state.StartNanos = 0L;
        state.PendingReleaseNanos = 0L;
        state.PointerId = -1;
        state.WasHolding = false;
        state.ReleaseProjection = default;
    }

    private void ResetHoldTracking()
    {
        _holdStates.Clear();
    }

    private Queue<GameInputSource> GetGameInputSources(nint player)
    {
        if (!_gameInputSources.TryGetValue(player, out Queue<GameInputSource>? sources))
        {
            sources = new Queue<GameInputSource>();
            _gameInputSources.Add(player, sources);
        }
        return sources;
    }

    private void ResetGameInputSources()
    {
        foreach (Queue<GameInputSource> sources in _gameInputSources.Values)
            AbandonGameInputSources(sources);
        _gameInputSources.Clear();
    }

    /// <summary>
    /// 放弃一批无法再一一对应到 <c>keyTimes</c> 的物理来源。
    /// 对尚未收到 Android 回调的项留下时间水位，避免它们晚到后被下一批游戏输入借用。
    /// </summary>
    private static void AbandonGameInputSources(Queue<GameInputSource> sources)
    {
        long latestUnmappedInputNanos = 0L;
        foreach (GameInputSource source in sources)
        {
            if (source.AwaitsPress && source.CreatedNanos > latestUnmappedInputNanos)
                latestUnmappedInputNanos = source.CreatedNanos;
        }

        sources.Clear();
        if (latestUnmappedInputNanos > 0L)
            TouchQueue.DiscardPressesThrough(AddFutureEventTolerance(latestUnmappedInputNanos));
    }

    /// <summary>
    /// 在来源已失真的边界把尚未归属的 Down 一并作废。
    /// 它可能牺牲紧邻的下一次异步修正，但不会改变原版输入，也不会让旧时间戳污染后续判定。
    /// </summary>
    private static void DiscardAmbiguousPresses()
    {
        long now = ClockSync.GetMonotonicNanos();
        if (now > 0L)
            TouchQueue.DiscardPressesThrough(now);
    }

    private void RecordConsumedGameInput(GameInputSource source)
    {
        if (source.HasPress)
            _consumedMappedInputs++;
        else
            _consumedUnmappedInputs++;

        // If Unity consumed this keyTimes item before the timestamp callback
        // reached the main thread, that late Down belongs to this input, not to
        // the next one. Event time is captured before HitAutoFloors creates the
        // source, so remove only timestamps at or before that boundary.
        if (!source.AwaitsPress)
            return;

        if (source.CreatedNanos <= 0L)
        {
            DiscardAmbiguousPresses();
            return;
        }

        TouchQueue.DiscardPressesThrough(AddFutureEventTolerance(source.CreatedNanos));
    }

    private static long AddFutureEventTolerance(long eventNanos)
        => eventNanos > long.MaxValue - FutureEventToleranceNanos
            ? long.MaxValue
            : eventNanos + FutureEventToleranceNanos;

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

    /// <summary>把角度差换算成毫秒，便于直观判断修正量是否落在一帧之内。</summary>
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
            ResetClock();
        }
        ImGui.TextWrapped(
            "将 Android 原始触摸边沿补入游戏的异步判定通道，按事件 tick 执行原版命中逻辑。"
            + "移动端原本没有这条输入生产路径；低帧率或帧率不稳时提升更明显。");

        ImGui.Separator();

        ImGui.SliderFloat("附加偏移 (ms)", ref OffsetMs, -100f, 100f);
        ImGui.TextWrapped("正值判定更早，负值更晚。用于补偿不同机型的触摸上报延迟。");

        ImGui.TextWrapped("延迟校准继续使用 APK 原版流程；本分支不改写校准样本。");

        _updateService?.DrawGui();

        ImGui.Separator();
        ImGui.Checkbox("显示调试信息", ref ShowDebugHud);
        ImGui.Checkbox("输出详细日志", ref ShowDetailedLogs);

        if (!ShowDebugHud)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted(
            $"原版链路时钟: {(_originalAsyncClock.IsReady ? "已建立" : "未建立")}, "
            + $"wall offset {_originalAsyncClock.WallOffsetMilliseconds:F3} ms, "
            + $"wall anchor {_originalAsyncClock.WallSampleCount}, "
            + $"offset samples {_originalAsyncClock.AsyncSampleCount}/30");
        ImGui.Separator();
        ImGui.TextUnformatted(
            $"原版异步链路: {(IsOriginalGameplayContext ? "已启用" : "未启用")}");
        ImGui.TextUnformatted($"写入原版 queue: {_originalAsyncProducer.ProducedEvents}");
        ImGui.TextUnformatted($"原始边沿拒绝: {_originalAsyncProducer.RejectedEvents}");
        ImGui.TextUnformatted($"UI 点击忽略: {_originalAsyncProducer.IgnoredUiEvents}");
        ImGui.TextUnformatted($"最近 flush: {_originalAsyncProducer.LastFlushRawCount} raw / {_originalAsyncProducer.LastFlushProducedCount} queue");
        ImGui.TextUnformatted($"边沿排队时间（非判定误差）: {_originalAsyncProducer.LastDispatchAgeMilliseconds:F2} ms");
        ImGui.TextUnformatted($"生产失败: {_originalAsyncProducer.ProcessFailures}");
        ImGui.Separator();
        ImGui.TextUnformatted($"会话复位次数: {_clockResets}");
        ImGui.TextUnformatted(
            $"收到触摸边沿: {TouchQueue.ReceivedCount} "
            + $"(Down {TouchQueue.ReceivedPressCount} / Up {TouchQueue.ReceivedReleaseCount})");
        ImGui.TextUnformatted(
            $"已写入原版 queue: {_originalAsyncProducer.ProducedEvents} / {TouchQueue.ReceivedCount}");
        ImGui.TextUnformatted($"队列溢出丢弃: {TouchQueue.DroppedCount}");
        ImGui.TextUnformatted($"过期边沿丢弃: {TouchQueue.StaleCount}");
        ImGui.TextUnformatted(
            $"待 flush 原始边沿: {TouchQueue.MobileAsyncCount}");

        if (ImGui.Button("重置统计"))
        {
            _adjustedHits = 0;
            _missedHits = 0;
            _rejectedHits = 0;
            _adjustedHoldReleases = 0;
            _missedHoldReleases = 0;
            _rejectedHoldReleases = 0;
            _mappedGameInputs = 0;
            _unmappedGameInputs = 0;
            _consumedMappedInputs = 0;
            _consumedUnmappedInputs = 0;
            _sourceResyncs = 0;
            _clockResets = 0;
            _adjustedCalibrationSamples = 0;
            _missedCalibrationSamples = 0;
            _rejectedCalibrationSamples = 0;
            _originalAsyncProducer.ResetStatistics();
            _lastCorrectionMillis = 0d;
            _avgCorrectionMillis = 0d;
            _maxCorrectionMillis = 0d;
            _mobileAsyncBridge.ResetStatistics();
            TouchQueue.ResetStatistics();
        }
    }

    public void OnForegroundGUI(ImDrawListPtr drawList)
    {
        if (_active)
            _updateService?.DrawForegroundNotification();
    }
}
