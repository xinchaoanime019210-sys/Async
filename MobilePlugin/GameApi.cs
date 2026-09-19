using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace AsyncInput.Mobile;

/// <summary>
/// 异步输入所需的游戏字段和方法访问。原版异步队列链路与旧版角度回退共用这层反射封装。
/// </summary>
internal unsafe sealed class GameApi
{
    /// <summary>与游戏内 <c>AsyncInputUtils.GetAngle</c> 使用的常量保持一致。</summary>
    private const double Pi = 3.141592653598793d;
    private const double DspTicksPerSecond = 10_000_000d;

    private readonly IRuntimeAssembly _assembly;

    private readonly IRuntimeClass _planetClass;
    private readonly IRuntimeClass _playerClass;
    private readonly IRuntimeClass _conductorClass;
    private readonly IRuntimeClass _systemClass;
    private readonly IRuntimeClass? _floorClass;
    private readonly IRuntimeClass? _calibrationClass;
    private readonly IRuntimeClass? _controllerClass;
    private readonly IRuntimeClass? _adoBaseClass;
    private readonly IRuntimeClass? _asyncInputManagerClass;
    private readonly IRuntimeClass? _rdInputClass;
    private readonly IRuntimeClass? _rdInputTypeClass;
    private readonly IRuntimeClass? _skyHookEventClass;
    private readonly IAppDomain _domain;

    private readonly IRuntimeField? _planetAngle;
    private readonly IRuntimeField? _planetSnappedLastAngle;
    private readonly IRuntimeField? _planetSystem;
    private readonly IRuntimeField? _planetPlayer;

    private readonly IRuntimeField? _playerLastHit;
    private readonly IRuntimeField? _playerSystem;
    private readonly IRuntimeField? _playerKeyTimes;
    private readonly IRuntimeField? _playerHoldKeys;
    private readonly IRuntimeField? _playerMidspinInfiniteMargin;
    private readonly IRuntimeField? _floorMidSpin;

    private readonly IRuntimeField? _conductorInstance;
    private readonly IRuntimeField? _conductorDspTimeSong;
    private readonly IRuntimeField? _conductorCrotchetAtStart;
    private readonly IRuntimeField? _conductorAddOffset;
    private readonly IRuntimeField? _conductorSong;
    private readonly IRuntimeField? _conductorDspTime;
    private readonly IRuntimeField? _conductorPrevFrame;

    private readonly IRuntimeField? _systemSpeed;
    private readonly IRuntimeField? _systemIsCW;
    private readonly IRuntimeField? _systemChosenPlanet;

    private readonly IRuntimeField? _controllerPaused;
    private readonly IRuntimeField? _controllerGameWorld;
    private readonly IRuntimeField? _controllerInstance;
    private readonly IRuntimeField? _controllerCurrentState;

    private readonly IRuntimeField? _calibrationAngleRadians;
    private readonly IRuntimeField? _calibrationConductor;

    // The desktop async data structures remain in the mobile assembly, while
    // the port is missing the producer/consumer connection. These fields let
    // the mod fill that connection without referencing the IL2CPP game types.
    private readonly IRuntimeField? _asyncCurrFrameTick;
    private readonly IRuntimeField? _asyncPrevFrameTick;
    private readonly IRuntimeField? _asyncKeyQueue;
    private readonly IRuntimeField? _asyncTargetSongTick;
    private readonly IRuntimeField? _asyncOffsetTick;
    private readonly IRuntimeField? _asyncOffsetTickUpdated;
    private readonly IRuntimeField? _asyncLastReportedTargetTick;
    private readonly IRuntimeField? _asyncKeyMask;
    private readonly IRuntimeField? _asyncKeyDownMask;
    private readonly IRuntimeField? _asyncKeyUpMask;
    private readonly IRuntimeField? _asyncFrameKeyMask;
    private readonly IRuntimeField? _asyncFrameKeyDownMask;
    private readonly IRuntimeField? _asyncFrameKeyUpMask;

    private readonly IRuntimeField? _rdInputKeyboardInput;
    private readonly IRuntimeField? _rdInputKeyboardLeft;
    private readonly IRuntimeField? _rdInputKeyboardRight;
    private readonly IRuntimeField? _rdInputAsyncKeyboard;
    private readonly IRuntimeField? _rdInputAsyncKeyboardLeft;
    private readonly IRuntimeField? _rdInputAsyncKeyboardRight;
    private readonly IRuntimeField? _rdInputTypeActive;

    private readonly IRuntimeMethod? _getConductor;
    private readonly IRuntimeMethod? _getConductorInstance;
    private readonly IRuntimeMethod? _getController;
    private readonly IRuntimeMethod? _getControllerState;
    private readonly IRuntimeMethod? _getPlanetConductor;
    private readonly IRuntimeMethod? _getPlayerCurrentFloor;
    private readonly IRuntimeMethod? _getNextTileIsAuto;
    private readonly IRuntimeMethod? _getPlayerAuto;
    private readonly IRuntimeMethod? _getCalibrationInput;
    private readonly IRuntimeMethod? _getAudioDspTime;
    private readonly IRuntimeMethod? _getAudioConfiguration;
    private readonly IRuntimeMethod? _getUnscaledTime;
    private readonly IRuntimeMethod? _getScreenHeight;
    private readonly IRuntimeMethod? _processKeyInputs;
    private readonly IRuntimeMethod? _isScreenPointInsideUiElements;
    private readonly IRuntimeMethod? _rdInputGetMain;
    private readonly IRuntimeMethod? _asyncClearKeys;

    private IRuntimeMethod? _hashSetAdd;
    private IRuntimeMethod? _hashSetClear;
    private IRuntimeMethod? _asyncKeyQueueEnqueue;
    private IRuntimeMethod? _asyncKeyQueueClear;
    private readonly nint[] _asyncKeyEventArgs = new nint[1];
    private readonly nint[] _rdInputStateArgs = new nint[1];
    private nint _asyncKeyMaskObject;
    private nint _asyncKeyDownMaskObject;
    private nint _asyncKeyUpMaskObject;
    private nint _asyncFrameKeyMaskObject;
    private nint _asyncFrameKeyDownMaskObject;
    private nint _asyncFrameKeyUpMaskObject;
    private readonly nint[] _hashSetKeyArgs = new nint[1];
    private readonly nint[] _processTickArgs = new nint[1];
    private readonly nint[] _screenPointArgs = new nint[1];
    private readonly IRuntimeField? _skyHookEpochTicks;
    private nint _asyncKeyQueueObject;

    // ButtonState.WentDown / WentUp in the 3.3.x mobile build. Keep these
    // local so the mod does not need to reference the IL2CPP game assembly at
    // compile time.
    private const int ButtonStateWentDown = 0;
    private const int ButtonStateWentUp = 2;

    internal readonly System.Collections.Concurrent.ConcurrentQueue<SkyHookEventValue> LocalQueue = new();


    [StructLayout(LayoutKind.Sequential)]
    private struct AsyncKeyCodeValue
    {
        public ushort Key;
        public ushort Label;
    }

    /// <summary>
    /// The mobile build still contains SkyHookEvent. Its managed layout is
    /// the default sequential layout (24 bytes on arm64), including the
    /// trailing alignment padding after Key.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SkyHookEventValue
    {
        public long TimeSec;
        public uint TimeSubsecNano;
        public int Type;
        public ushort Label;
        public ushort Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vector2Value
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioConfigurationValue
    {
        public int SpeakerMode;
        public int DspBufferSize;
        public int SampleRate;
        public int NumRealVoices;
        public int NumVirtualVoices;
    }

    private static readonly ushort[] AsyncKeyLabels =
    {
        81, 65, 113, 26, 27, 28, 29, 30,
        31, 32, 33, 34, 35, 41, 42, 44,
    };

    private const ushort AsyncTouchRawBase = 0xff00;

    /// <summary>角度重算所需的全部句柄是否齐备。任一缺失都必须禁用功能而不是算出错误结果。</summary>
    internal bool IsUsable =>
        _planetAngle != null
        && _planetSnappedLastAngle != null
        && _planetSystem != null
        && _planetPlayer != null
        && _playerLastHit != null
        && _playerSystem != null
        && _playerKeyTimes != null
        && _playerHoldKeys != null
        && _playerMidspinInfiniteMargin != null
        && _systemChosenPlanet != null
        && _conductorDspTimeSong != null
        && _conductorCrotchetAtStart != null
        && _systemSpeed != null
        && _systemIsCW != null
        && _conductorDspTime != null
        && _conductorPrevFrame != null
        && _getUnscaledTime != null
        && HasDspClockSource;

    /// <summary>
    /// The mobile bridge can drive the game's timestamp-aware player update
    /// only when all of its state containers and input types are available.
    /// This is deliberately separate from the angle-projection fallback.
    /// </summary>
    internal bool CanUseMobileAsyncBridge =>
        _asyncInputManagerClass != null
        && _processKeyInputs != null
        && _asyncCurrFrameTick != null
        && _asyncPrevFrameTick != null
        && _asyncTargetSongTick != null
        && _asyncOffsetTick != null
        && _asyncOffsetTickUpdated != null
        && _asyncLastReportedTargetTick != null
        && _asyncKeyMask != null
        && _asyncKeyDownMask != null
        && _asyncKeyUpMask != null
        && _asyncFrameKeyMask != null
        && _asyncFrameKeyDownMask != null
        && _asyncFrameKeyUpMask != null
        && _rdInputKeyboardInput != null
        && _rdInputKeyboardLeft != null
        && _rdInputKeyboardRight != null
        && _rdInputAsyncKeyboard != null
        && _rdInputAsyncKeyboardLeft != null
        && _rdInputAsyncKeyboardRight != null
        && _rdInputTypeActive != null
        && _controllerGameWorld != null
        && _getController != null
        && _isScreenPointInsideUiElements != null;

    /// <summary>
    /// The minimum runtime surface needed to feed the game's own
    /// AsyncInputManager queue. This intentionally does not require the
    /// custom angle bridge's UI/reflection helpers.
    /// </summary>
    internal bool CanUseOriginalAsyncChain =>
        _asyncInputManagerClass != null
        && _asyncCurrFrameTick != null
        && _asyncPrevFrameTick != null
        && _asyncOffsetTick != null
        && _asyncOffsetTickUpdated != null
        && _rdInputKeyboardInput != null
        && _rdInputKeyboardLeft != null
        && _rdInputKeyboardRight != null
        && _rdInputAsyncKeyboard != null
        && _rdInputAsyncKeyboardLeft != null
        && _rdInputAsyncKeyboardRight != null
        && _rdInputTypeActive != null
        && (_controllerCurrentState != null || _getControllerState != null)
        && _rdInputGetMain != null;

    // Runtime surface used by the optional native settings-menu parity hook.
    // It intentionally stays behind this API so the mod never references
    // Unity or Assembly-CSharp types at compile time.
    internal IAppDomain RuntimeDomain => _domain;

    internal IRuntimeClass? FindGameClassForMod(string name)
        => FindClass(name);

    internal IRuntimeClass? FindRuntimeClassForMod(string namespaze, string name)
        => FindClassInDomain(namespaze, name);

    /// <summary>延迟校准页能否按硬件时间戳重算采样角度。</summary>
    internal bool CanAdjustCalibration =>
        _calibrationAngleRadians != null
        && _calibrationConductor != null
        && _conductorDspTimeSong != null
        && _conductorCrotchetAtStart != null
        && _conductorDspTime != null
        && _conductorPrevFrame != null
        && _getUnscaledTime != null
        && HasDspClockSource;

    /// <summary>
    /// 优先使用 Unity 的音频 DSP 时钟；旧版导出若没有暴露 AudioSettings，
    /// 可用游戏每帧维护的 conductor.dspTime 加 unscaledTime 外推。
    /// </summary>
    private bool HasDspClockSource => _getAudioDspTime != null
                                      || (_conductorDspTime != null
                                          && _conductorPrevFrame != null
                                          && _getUnscaledTime != null);

    private GameApi(IAppDomain domain, IRuntimeAssembly assembly)
    {
        _domain = domain;
        _assembly = assembly;

        _planetClass = RequireClass("scrPlanet");
        _playerClass = RequireClass("scrPlayer");
        _conductorClass = RequireClass("scrConductor");
        _systemClass = RequireClass("PlanetarySystem");
        _floorClass = FindClass("scrFloor");
        _calibrationClass = FindClass("scnCalibration");
        _controllerClass = FindClass("scrController");
        _adoBaseClass = FindClass("ADOBase");
        _asyncInputManagerClass = FindClass("AsyncInputManager");
        _rdInputClass = FindClass("RDInput");
        _rdInputTypeClass = FindClass("RDInputType");
        _skyHookEventClass = FindClassInDomain("SkyHook", "SkyHookEvent");

        _planetAngle = FindField(_planetClass, "angle");
        _planetSnappedLastAngle = FindField(_planetClass, "<snappedLastAngle>k__BackingField", "snappedLastAngle");
        _planetSystem = FindField(_planetClass, "planetarySystem");
        _planetPlayer = FindField(_planetClass, "player");

        _playerLastHit = FindField(_playerClass, "lastHit");
        _playerSystem = FindField(_playerClass, "planetarySystem");
        _playerKeyTimes = FindField(_playerClass, "keyTimes");
        _playerHoldKeys = FindField(_playerClass, "holdKeys");
        _playerMidspinInfiniteMargin = FindField(_playerClass, "midspinInfiniteMargin");
        _floorMidSpin = FindField(_floorClass, "midSpin");

        _conductorInstance = FindField(_conductorClass, "_instance", "instance");
        _conductorDspTimeSong = FindField(_conductorClass, "dspTimeSong");
        _conductorCrotchetAtStart = FindField(_conductorClass, "crotchetAtStart");
        _conductorAddOffset = FindField(_conductorClass, "addoffset");
        _conductorSong = FindField(_conductorClass, "song");
        _conductorDspTime = FindField(_conductorClass, "dspTime");
        _conductorPrevFrame = FindField(_conductorClass, "previousFrameTime");

        _systemSpeed = FindField(_systemClass, "speed");
        _systemIsCW = FindField(_systemClass, "isCW");
        _systemChosenPlanet = FindField(_systemClass, "chosenPlanet");

        _controllerPaused = FindField(_controllerClass, "_paused", "paused");
        _controllerGameWorld = FindField(_controllerClass, "gameworld", "isGameWorld", "isgameworld");
        _controllerInstance = FindField(_controllerClass, "_instance", "instance");
        _controllerCurrentState = FindField(_controllerClass, "currentState");
        _skyHookEpochTicks = FindField(_skyHookEventClass, "EpochTicks");

        _calibrationAngleRadians = FindField(_calibrationClass, "angleRadians");
        _calibrationConductor = FindField(_calibrationClass, "conductor");

        _asyncCurrFrameTick = FindField(_asyncInputManagerClass, "currFrameTick");
        _asyncPrevFrameTick = FindField(_asyncInputManagerClass, "prevFrameTick");
        _asyncKeyQueue = FindField(_asyncInputManagerClass, "keyQueue");
        _asyncTargetSongTick = FindField(_asyncInputManagerClass, "targetSongTick");
        _asyncOffsetTick = FindField(_asyncInputManagerClass, "offsetTick");
        _asyncOffsetTickUpdated = FindField(_asyncInputManagerClass, "offsetTickUpdated");
        _asyncLastReportedTargetTick = FindField(_asyncInputManagerClass, "lastReportedTargetTick");
        _asyncKeyMask = FindField(_asyncInputManagerClass, "keyMask");
        _asyncKeyDownMask = FindField(_asyncInputManagerClass, "keyDownMask");
        _asyncKeyUpMask = FindField(_asyncInputManagerClass, "keyUpMask");
        _asyncFrameKeyMask = FindField(_asyncInputManagerClass, "frameDependentKeyMask");
        _asyncFrameKeyDownMask = FindField(_asyncInputManagerClass, "frameDependentKeyDownMask");
        _asyncFrameKeyUpMask = FindField(_asyncInputManagerClass, "frameDependentKeyUpMask");

        _rdInputKeyboardInput = FindField(_rdInputClass, "keyboardInput");
        _rdInputKeyboardLeft = FindField(_rdInputClass, "keyboardLeft");
        _rdInputKeyboardRight = FindField(_rdInputClass, "keyboardRight");
        _rdInputAsyncKeyboard = FindField(_rdInputClass, "asyncKeyboard");
        _rdInputAsyncKeyboardLeft = FindField(_rdInputClass, "asyncKeyboardLeft");
        _rdInputAsyncKeyboardRight = FindField(_rdInputClass, "asyncKeyboardRight");
        _rdInputTypeActive = FindField(_rdInputTypeClass, "_isActive", "isActive");

        _getConductor = _adoBaseClass?.GetMethod("get_conductor", 0);
        _getConductorInstance = _conductorClass.GetMethod("get_instance", 0);
        _getController = _adoBaseClass?.GetMethod("get_controller", 0)
            ?? _controllerClass?.GetMethod("get_instance", 0);
        _getControllerState = _controllerClass?.GetMethod("get_state", 0);
        _getPlanetConductor = _planetClass.GetMethod("get_conductor", 0);
        _getPlayerCurrentFloor = _playerClass.GetMethod("get_currFloor", 0);
        _getNextTileIsAuto = _playerClass.GetMethod("get__nextTileIsAuto", 0);
        _getPlayerAuto = _playerClass.GetMethod("get_auto", 0);
        _getCalibrationInput = _conductorClass.GetMethod("get_calibration_i", 0);
        _getAudioDspTime = FindClassInDomain("UnityEngine", "AudioSettings")
            ?.GetMethod("get_dspTime", 0);
        _getAudioConfiguration = FindClassInDomain("UnityEngine", "AudioSettings")
            ?.GetMethod("GetConfiguration", 0);
        _getUnscaledTime = FindClassInDomain("UnityEngine", "Time")
            ?.GetMethod("get_unscaledTimeAsDouble", 0);
        _getScreenHeight = FindClassInDomain("UnityEngine", "Screen")
            ?.GetMethod("get_height", 0);
        _processKeyInputs = _controllerClass?.GetMethod("ProcessKeyInputs", 1);
        _rdInputGetMain = _rdInputClass?.GetMethod("GetMain", 1);
        _asyncClearKeys = _asyncInputManagerClass?.GetMethod("ClearKeys", 0);
        _isScreenPointInsideUiElements = _controllerClass?.GetMethod(
            "IsScreenPointInsideUIElements",
            1);
    }

    /// <summary>
    /// 直接读取 Unity 音频系统的高精度 DSP 时钟。
    /// </summary>
    internal double GetAudioDspTime()
    {
        try
        {
            return _getAudioDspTime?.InvokeStaticUnbox<double>() ?? 0d;
        }
        catch
        {
            return 0d;
        }
    }

    /// <summary>
    /// Returns the same audio-buffer duration used by Iridium's
    /// <c>SafeDSPTime.GetAuidoPrecise</c>. Invalid/stripped configurations
    /// return zero so the caller can use its conservative fallback threshold.
    /// </summary>
    internal double GetAudioBufferDuration()
    {
        if (_getAudioConfiguration == null)
            return 0d;

        try
        {
            AudioConfigurationValue configuration =
                _getAudioConfiguration.InvokeStaticUnbox<AudioConfigurationValue>();
            if (configuration.DspBufferSize <= 0 || configuration.SampleRate <= 0)
                return 0d;

            double duration = configuration.DspBufferSize / (double)configuration.SampleRate;
            return double.IsFinite(duration) && duration > 0d && duration < 1d
                ? duration
                : 0d;
        }
        catch
        {
            return 0d;
        }
    }

    /// <summary>
    /// scrConductor.dspTime —— 游戏每帧用 unscaledTime 增量累加的高精度时间。
    /// </summary>
    internal double GetConductorDspTime(nint conductor)
        => Read(_conductorDspTime, conductor, 0d);

    /// <summary>
    /// dspTime 最后一次更新时的 unscaledTime。
    /// </summary>
    internal double GetConductorPrevFrameTime(nint conductor)
        => Read(_conductorPrevFrame, conductor, 0d);

    /// <summary>
    /// Unity Time.unscaledTimeAsDouble —— 当前帧的实时时间。
    /// </summary>
    internal double GetUnscaledTime()
    {
        try
        {
            return _getUnscaledTime?.InvokeStaticUnbox<double>() ?? 0d;
        }
        catch
        {
            return 0d;
        }
    }

    /// <summary>
    /// 获取当前游戏有效的 DSP 时间，补偿帧采样滞后。
    /// </summary>
    /// <remarks>
    /// scrConductor.dspTime 每帧边界更新，判定发生在帧中间，
    /// 此刻 dspTime 已过时 (unscaledTime - previousFrameTime) 秒。
    /// 补回后等效于在判定当刻直接读取游戏时钟，消除半帧系统偏差。
    /// </remarks>
    internal double GetCurrentDspTime(nint conductor)
    {
        double dspTime = GetConductorDspTime(conductor);
        double prevFrame = GetConductorPrevFrameTime(conductor);
        double now = GetUnscaledTime();
        if (dspTime <= 0d || prevFrame <= 0d || now < prevFrame)
            return dspTime;
        return dspTime + (now - prevFrame);
    }


    /// <summary>
    /// 跨程序集查类，用于 <c>UnityEngine</c> 等非 Assembly-CSharp 的类型。
    /// </summary>
    private IRuntimeClass? FindClassInDomain(string namespaze, string name)
    {
        foreach (IRuntimeAssembly assembly in _domain.GetAssemblies())
        {
            try
            {
                IRuntimeClass? type = assembly.GetClass(namespaze, name);
                if (type != null)
                    return type;
            }
            catch
            {
                // 个别程序集查询失败不应中断整轮搜索
            }
        }
        return null;
    }


    internal static GameApi? Create()
    {
        IAppDomain? domain = RuntimeManager.GetDomain();
        if (domain == null)
            return null;

        IRuntimeAssembly? assembly = domain.OpenAssembly("Assembly-CSharp.dll")
            ?? domain.OpenAssembly("Assembly-CSharp");
        return assembly == null ? null : new GameApi(domain, assembly);
    }

    /// <summary>列出缺失的字段，用于在禁用功能时给出可诊断的日志。</summary>
    internal string DescribeMissing()
    {
        List<string> missing = new();
        if (_planetAngle == null) missing.Add("scrPlanet.angle");
        if (_planetSnappedLastAngle == null) missing.Add("scrPlanet.snappedLastAngle");
        if (_planetSystem == null) missing.Add("scrPlanet.planetarySystem");
        if (_planetPlayer == null) missing.Add("scrPlanet.player");
        if (_playerLastHit == null) missing.Add("scrPlayer.lastHit");
        if (_playerSystem == null) missing.Add("scrPlayer.planetarySystem");
        if (_playerKeyTimes == null) missing.Add("scrPlayer.keyTimes");
        if (_playerHoldKeys == null) missing.Add("scrPlayer.holdKeys");
        if (_playerMidspinInfiniteMargin == null) missing.Add("scrPlayer.midspinInfiniteMargin");
        if (_systemChosenPlanet == null) missing.Add("PlanetarySystem.chosenPlanet");
        if (_conductorDspTimeSong == null) missing.Add("scrConductor.dspTimeSong");
        if (_conductorCrotchetAtStart == null) missing.Add("scrConductor.crotchetAtStart");
        if (_systemSpeed == null) missing.Add("PlanetarySystem.speed");
        if (_systemIsCW == null) missing.Add("PlanetarySystem.isCW");
        if (!HasDspClockSource)
            missing.Add("UnityEngine.AudioSettings.dspTime or conductor clock");
        if (_conductorDspTime == null) missing.Add("scrConductor.dspTime");
        if (_conductorPrevFrame == null) missing.Add("scrConductor.previousFrameTime");
        if (_getUnscaledTime == null) missing.Add("UnityEngine.Time.unscaledTimeAsDouble");
        return missing.Count == 0 ? "none" : string.Join(", ", missing);
    }

    /// <summary>列出原版异步消费者链路缺失的运行时对象/字段。</summary>
    internal string DescribeOriginalAsyncMissing()
    {
        List<string> missing = new();
        if (_asyncInputManagerClass == null) missing.Add("AsyncInputManager");
        if (_asyncKeyQueue == null) missing.Add("AsyncInputManager.keyQueue");
        if (_asyncClearKeys == null) missing.Add("AsyncInputManager.ClearKeys");
        if (_asyncKeyQueueObject == 0) missing.Add("keyQueue instance");
        if (_asyncKeyQueueEnqueue == null) missing.Add("keyQueue.Enqueue");
        if (_asyncKeyQueueClear == null) missing.Add("keyQueue.Clear");
        if (_asyncCurrFrameTick == null) missing.Add("AsyncInputManager.currFrameTick");
        if (_asyncOffsetTick == null) missing.Add("AsyncInputManager.offsetTick");
        if (_asyncOffsetTickUpdated == null) missing.Add("AsyncInputManager.offsetTickUpdated");
        if (_controllerCurrentState == null && _getControllerState == null)
            missing.Add("scrController.state/currentState");
        if (_rdInputGetMain == null) missing.Add("RDInput.GetMain");
        if (_rdInputKeyboardInput == null) missing.Add("RDInput.keyboardInput");
        if (_rdInputKeyboardLeft == null) missing.Add("RDInput.keyboardLeft");
        if (_rdInputKeyboardRight == null) missing.Add("RDInput.keyboardRight");
        if (_rdInputAsyncKeyboard == null) missing.Add("RDInput.asyncKeyboard");
        if (_rdInputAsyncKeyboardLeft == null) missing.Add("RDInput.asyncKeyboardLeft");
        if (_rdInputAsyncKeyboardRight == null) missing.Add("RDInput.asyncKeyboardRight");
        if (_rdInputTypeActive == null) missing.Add("RDInputType._isActive");
        if (_skyHookEventClass == null) missing.Add("SkyHook.SkyHookEvent");
        return missing.Count == 0 ? "none" : string.Join(", ", missing);
    }

    // ── conductor ─────────────────────────────────────────────

    internal nint GetConductor()
    {
        nint conductor = InvokeStaticObject(_getConductor);
        if (conductor != 0)
            return conductor;
        conductor = InvokeStaticObject(_getConductorInstance);
        return conductor != 0 ? conductor : Read(_conductorInstance, 0, nint.Zero);
    }

    internal nint GetController()
    {
        nint controller = InvokeStaticObject(_getController);
        return controller != 0 ? controller : Read(_controllerInstance, 0, nint.Zero);
    }

    internal bool IsGameplayController(nint controller)
        => controller != 0 && Read(_controllerGameWorld, controller, (byte)0) != 0;

    internal nint GetPlanetConductor(nint planet)
    {
        if (planet != 0 && _getPlanetConductor != null)
        {
            try
            {
                nint conductor = _getPlanetConductor.Invoke(planet);
                if (conductor != 0)
                    return conductor;
            }
            catch
            {
                // 落到全局 conductor
            }
        }
        return GetConductor();
    }

    internal double GetDspTimeSong(nint conductor) => Read(_conductorDspTimeSong, conductor, 0d);

    internal double GetCrotchetAtStart(nint conductor) => Read(_conductorCrotchetAtStart, conductor, 0d);

    internal double GetAddOffset(nint conductor) => Read(_conductorAddOffset, conductor, 0d);

    /// <summary>输入校准偏移，单位秒（<c>currentPreset.inputOffset / 1000</c>）。</summary>
    internal double GetInputCalibration()
    {
        try
        {
            return _getCalibrationInput?.InvokeStaticUnbox<float>() ?? 0f;
        }
        catch
        {
            return 0d;
        }
    }

    internal double GetSongPitch(nint conductor)
    {
        nint song = Read(_conductorSong, conductor, nint.Zero);
        if (song == 0)
            return 1d;
        try
        {
            float pitch = new RuntimeObject(song).InvokeUnbox<float>("get_pitch", 0);
            return Math.Abs(pitch) > 0.0001f ? pitch : 1d;
        }
        catch
        {
            return 1d;
        }
    }

    // ── planet / player ───────────────────────────────────────

    internal nint GetPlanetSystem(nint planet) => Read(_planetSystem, planet, nint.Zero);

    internal nint GetPlanetPlayer(nint planet) => Read(_planetPlayer, planet, nint.Zero);

    internal double GetPlanetAngle(nint planet) => Read(_planetAngle, planet, 0d);

    internal double GetSnappedLastAngle(nint planet) => Read(_planetSnappedLastAngle, planet, 0d);

    internal double GetLastHit(nint player) => Read(_playerLastHit, player, 0d);

    internal nint GetPlayerSystem(nint player) => Read(_playerSystem, player, nint.Zero);

    /// <summary>
    /// 待处理的按键时刻数量（<c>scrPlayer.keyTimes.Count</c>）。
    /// <c>UpdateHoldKeys</c> 每次取走一项并驱动一次判定，因此它大于 0 就表示本帧会发生判定。
    /// </summary>
    internal int GetPendingKeyCount(nint player)
    {
        nint keyTimes = Read(_playerKeyTimes, player, nint.Zero);
        if (keyTimes == 0)
            return 0;
        try
        {
            return Math.Max(0, new RuntimeObject(keyTimes).InvokeUnbox<int>("get_Count", 0));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 当前是否处于长按状态。游戏用 <c>holdKeys</c> 保存已命中的持有键；它非空时，
    /// <c>ValidInputWasReleased</c> 才会把触摸抬起解释为长按释放。
    /// </summary>
    internal bool IsHolding(nint player)
    {
        nint holdKeys = Read(_playerHoldKeys, player, nint.Zero);
        if (holdKeys == 0)
            return false;
        try
        {
            return new RuntimeObject(holdKeys).InvokeUnbox<int>("get_Count", 0) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 当前帧是否可能触发 midspin 的第二次合成判定。
    /// </summary>
    internal bool HasMidspinInfiniteMargin(nint player)
        => Read(_playerMidspinInfiniteMargin, player, (byte)0) != 0;

    /// <summary>
    /// 当前砖块是否会在一次成功命中后插入 midspin 合成输入。
    /// 该信息只用于防止同帧多次真实输入被合成项重新排序，不参与判定本身。
    /// </summary>
    internal bool IsCurrentFloorMidSpin(nint player)
    {
        if (player == 0 || _getPlayerCurrentFloor == null)
            return false;
        try
        {
            nint floor = _getPlayerCurrentFloor.Invoke(player);
            return floor != 0 && Read(_floorMidSpin, floor, (byte)0) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 自动输入会使用游戏自己的自动分支，不能把手指 Down 当作普通命中去重算角度。
    /// </summary>
    internal bool IsAutoInputPath(nint player)
    {
        try
        {
            return (_getPlayerAuto?.InvokeUnbox<byte>(player) ?? 0) != 0
                || (_getNextTileIsAuto?.InvokeUnbox<byte>(player) ?? 0) != 0;
        }
        catch
        {
            return false;
        }
    }

    internal nint GetChosenPlanet(nint system) => Read(_systemChosenPlanet, system, nint.Zero);

    internal double GetSystemSpeed(nint system) => Read(_systemSpeed, system, 1d);

    internal bool IsClockwise(nint system) => Read(_systemIsCW, system, (byte)1) != 0;

    internal bool IsPaused(nint controller) => Read(_controllerPaused, controller, (byte)0) != 0;

    /// <summary>
    /// 仅在原版判定窗口开始前投影 <c>angle</c>。
    /// </summary>
    /// <remarks>
    /// 官方 <c>scrPlanet.AsyncRefreshAngles</c> 也只写这个字段。随后真正进入
    /// <c>scrPlayer.Hit</c> 时，游戏会自行把 <c>angle</c> 复制给 <c>cachedAngle</c>；
    /// 提前写 cachedAngle 会让未实际调用 Hit 的分支也携带一笔伪判定状态。
    /// </remarks>
    internal void SetPlanetAngleForJudgment(nint planet, double angle)
    {
        Write(_planetAngle, planet, angle);
    }

    /// <summary>
    /// 撤销一笔未被原版刷新覆盖的临时判定投影。
    /// </summary>
    /// <remarks>
    /// 若原版已在命中、切砖或其他状态转移中改写角度，则不能回写旧帧角度。
    /// 因此只在字段仍等于本 Mod 写入的投影值时恢复。cachedAngle 刻意不碰：
    /// 它若被 Hit 写入，就应保留这次真实判定所使用的角度。
    /// </remarks>
    internal bool RestorePlanetAngleIfUnchanged(
        nint planet,
        double projectedAngle,
        double frameAngle)
    {
        if (planet == 0 || !double.IsFinite(projectedAngle) || !double.IsFinite(frameAngle))
            return false;

        double currentAngle = GetPlanetAngle(planet);
        // All values originate from the same formula/field write. A tiny
        // relative tolerance admits only floating-point roundoff, not normal
        // gameplay movement between the projection and this restoration.
        double tolerance = Math.Max(1d, Math.Abs(projectedAngle)) * 1e-10d;
        if (Math.Abs(currentAngle - projectedAngle) > tolerance)
            return false;

        Write(_planetAngle, planet, frameAngle);
        return true;
    }

    // ── Original mobile-to-AsyncInputManager chain ───────────────────────

    private const long UnixEpochTicks = 621355968000000000L;
    private const int PlayerControlState = 4;
    // Keep every synthetic pointer distinguishable to AsyncKeyCode. Its
    // equality operator treats two different raw keys with the same label as
    // equal, so a shared Space label would collapse simultaneous touches.

    /// <summary>
    /// Resolves the actual runtime instance of
    /// <c>ConcurrentQueue&lt;SkyHookEvent&gt;</c> and its closed generic methods.
    /// The queue is a static field, so no managed mirror is needed.
    /// </summary>
internal bool InitializeOriginalAsyncQueue()
{
    return CanUseOriginalAsyncChain; // Bỏ qua EnsureOriginalAsyncQueueApi()
}


    /// <summary>
    /// Enqueues one value into the game's own SkyHook event queue. The caller
    /// supplies the timestamp in the same DateTime tick domain as
    /// <c>AsyncInputManager.currFrameTick</c>; this method only serializes it
    /// into the already loaded <c>SkyHookEvent</c> value type.
    /// </summary>
    internal bool EnqueueOriginalAsyncEvent(
        long dateTimeTicks,
        bool pressed,
        int slot)
    {
        // 1. Đã xóa EnsureOriginalAsyncQueueApi
        if (dateTimeTicks <= 0L || slot < 0 || slot >= 16)
        {
            return false;
        }

        long epochTicks = Read(_skyHookEpochTicks, 0, UnixEpochTicks);
        if (epochTicks <= 0L)
            epochTicks = UnixEpochTicks;

        long relativeTicks;
        try
        {
            relativeTicks = checked(dateTimeTicks - epochTicks);
        }
        catch
        {
            return false;
        }

        long seconds = Math.DivRem(relativeTicks, TimeSpan.TicksPerSecond, out long remainder);
        if (remainder < 0L)
        {
            seconds--;
            remainder += TimeSpan.TicksPerSecond;
        }

        uint subsecNano;
        try
        {
            subsecNano = checked((uint)(remainder * 100L));
        }
        catch
        {
            return false;
        }

        SkyHookEventValue value = new()
        {
            TimeSec = seconds,
            TimeSubsecNano = subsecNano,
            // SkyHook.EventType.KeyPressed = 0, KeyReleased = 1.
            Type = pressed ? 0 : 1,
            // All labels are accepted by the full async keyboard source. The
            // per-slot label/raw-key pair keeps simultaneous pointers distinct.
            Label = AsyncKeyLabels[slot],
            Key = GetTouchRawKey(slot),
        };

        // 2. Đã thay thế khối try...catch bằng LocalQueue
        LocalQueue.Enqueue(value);
        return true;
    }


    /// <summary>清空游戏原生异步输入状态，只用于会话边界或故障复位。</summary>
    internal void ClearOriginalAsyncInputState()
    {
        try
        {
            _asyncClearKeys?.InvokeStatic();
        }
        catch
        {
            // Continue with the individual containers below when available.
        }

        if (EnsureAsyncMaskApi())
        {
            ClearHashSet(_asyncKeyMaskObject);
            ClearHashSet(_asyncKeyDownMaskObject);
            ClearHashSet(_asyncKeyUpMaskObject);
            ClearHashSet(_asyncFrameKeyMaskObject);
            ClearHashSet(_asyncFrameKeyDownMaskObject);
            ClearHashSet(_asyncFrameKeyUpMaskObject);
        }
    }

    /// <summary>
    /// Enables the full async source for gameplay and restores the normal
    /// keyboard source elsewhere. Touch events use Space, which deliberately
    /// keeps the left/right async controller variants from counting them a
    /// second time.
    /// </summary>
    internal bool SetOriginalAsyncInputTypes(bool gameplay)
    {
        if (_rdInputTypeActive == null
            || _rdInputKeyboardInput == null
            || _rdInputKeyboardLeft == null
            || _rdInputKeyboardRight == null
            || _rdInputAsyncKeyboard == null
            || _rdInputAsyncKeyboardLeft == null
            || _rdInputAsyncKeyboardRight == null)
        {
            return false;
        }

        bool ok = true;
        ok &= SetInputTypeActive(_rdInputKeyboardInput, !gameplay);
        ok &= SetInputTypeActive(_rdInputKeyboardLeft, !gameplay);
        ok &= SetInputTypeActive(_rdInputKeyboardRight, !gameplay);
        ok &= SetInputTypeActive(_rdInputAsyncKeyboard, gameplay);
        ok &= SetInputTypeActive(_rdInputAsyncKeyboardLeft, gameplay);
        ok &= SetInputTypeActive(_rdInputAsyncKeyboardRight, gameplay);
        return ok;
    }

    // Kept as a compatibility alias for the older, now-disabled bridge code.
    internal bool SetMobileAsyncInputTypes(bool enabled)
        => SetOriginalAsyncInputTypes(enabled);

    /// <summary>读取原版 RDInput 的 WentDown 计数，不复制其 input mask。</summary>
    internal int GetOriginalAsyncPressCount()
    {
        if (_rdInputGetMain == null)
            return 0;

        try
        {
            int state = ButtonStateWentDown;
            _rdInputStateArgs[0] = (nint)(&state);
            return Math.Max(0, _rdInputGetMain.InvokeStaticUnbox<int>(_rdInputStateArgs));
        }
        catch
        {
            return 0;
        }
    }

    internal int GetControllerState(nint controller)
    {
        if (controller == 0)
            return -1;

        if (_getControllerState != null)
        {
            try
            {
                return _getControllerState.InvokeUnbox<int>(controller);
            }
            catch
            {
                // Fall back to the public mirror field used by this APK.
            }
        }

        return Read(_controllerCurrentState, controller, -1);
    }

    internal bool IsPlayerControlState(nint controller)
        => controller != 0 && GetControllerState(controller) == PlayerControlState;

    internal bool SetOriginalAsyncCurrentFrameTick(ulong tick)
    {
        if (_asyncCurrFrameTick == null || tick == 0UL)
            return false;
        Write(_asyncCurrFrameTick, 0, tick);
        return true;
    }

    internal ulong GetOriginalAsyncCurrentFrameTick()
        => Read(_asyncCurrFrameTick, 0, 0UL);

    internal ulong GetOriginalAsyncOffsetTick()
        => Read(_asyncOffsetTick, 0, 0UL);

    internal void SetOriginalAsyncOffsetTick(ulong offsetTick)
    {
        Write(_asyncOffsetTick, 0, offsetTick);
        Write(_asyncOffsetTickUpdated, 0, (byte)1);
    }

    /// <summary>
    /// Measures the offset using the same conductor DSP field read by the
    /// APK's original AsyncInputUtils.UpdateOffsetTime. Falling back to
    /// AudioSettings is only for an export where that conductor field is not
    /// readable; mixing the two clocks during normal gameplay causes offset
    /// steps and apparent lost inputs at the edge of the judgement window.
    /// </summary>
    internal bool TryGetOriginalAsyncOffset(out ulong measuredOffset)
    {
        measuredOffset = 0UL;
        ulong frameTick = GetOriginalAsyncCurrentFrameTick();
        if (frameTick == 0UL)
            return false;

        nint conductor = GetConductor();
        double dspTime = GetConductorDspTime(conductor);
        if (!double.IsFinite(dspTime) || dspTime <= 0d)
            dspTime = GetAudioDspTime();

        double dspTicks = dspTime * DspTicksPerSecond;
        if (!double.IsFinite(dspTicks)
            || dspTicks <= 0d
            || dspTicks >= ulong.MaxValue)
        {
            return false;
        }

        ulong dspTick = (ulong)dspTicks;
        if (frameTick <= dspTick)
            return false;

        measuredOffset = frameTick - dspTick;
        return true;
    }

    private bool EnsureOriginalAsyncQueueApi()
    {
        if (_asyncKeyQueueObject != 0
            && _asyncKeyQueueEnqueue != null
            && _asyncKeyQueueClear != null)
        {
            return true;
        }

        _asyncKeyQueueObject = Read(_asyncKeyQueue, 0, nint.Zero);
        if (_asyncKeyQueueObject == 0)
            return false;

        IRuntimeClass? queueClass = RuntimeManager.GetObjectClass(_asyncKeyQueueObject);
        _asyncKeyQueueEnqueue = queueClass?.GetMethod("Enqueue", 1);
        _asyncKeyQueueClear = queueClass?.GetMethod("Clear", 0);
        return _asyncKeyQueueEnqueue != null && _asyncKeyQueueClear != null;
    }

    // ── Mobile async bridge ───────────────────────────────────

    /// <summary>
    /// Establishes the bridge's wall-tick to DSP-tick conversion for the
    /// current conductor frame. The stock <c>UpdateOffsetTime(100)</c> eases
    /// toward a newly enabled producer over many frames; that is fine when
    /// SkyHook has been active continuously, but it makes a newly supplied
    /// mobile producer decode ordinary no-edge frames at a wrong song time.
    /// The direct bridge owns this producer window, so use the same current
    /// frame values to establish its offset atomically before judgment.
    /// </summary>
    internal bool SynchronizeMobileAsyncFrame(
        nint conductor,
        out ulong frameTick,
        out ulong offsetTick)
    {
        frameTick = Read(_asyncCurrFrameTick, 0, 0UL);
        offsetTick = 0UL;
        if (conductor == 0 || frameTick == 0UL)
            return false;

        double dspTime = GetConductorDspTime(conductor);
        double dspTicks = dspTime * DspTicksPerSecond;
        if (!double.IsFinite(dspTicks)
            || dspTicks <= 0d
            || dspTicks >= ulong.MaxValue)
        {
            return false;
        }

        // AsyncInputUtils.UpdateOffsetTime uses IL conv.u8 for the positive
        // DSP timeline, so this is truncation rather than rounding.
        ulong dspTick = (ulong)dspTicks;
        if (frameTick <= dspTick)
            return false;

        offsetTick = frameTick - dspTick;
        Write(_asyncOffsetTick, 0, offsetTick);
        Write(_asyncOffsetTickUpdated, 0, (byte)1);
        return true;
    }

    internal bool ApplyMobileAsyncInputMasks(
        ulong heldMask,
        ulong downMask,
        ulong upMask,
        ulong frameMask,
        ulong frameDownMask,
        ulong frameUpMask)
    {
        if (!EnsureAsyncMaskApi())
            return false;

        bool ok = ClearHashSet(_asyncKeyMaskObject)
                  && ClearHashSet(_asyncKeyDownMaskObject)
                  && ClearHashSet(_asyncKeyUpMaskObject)
                  && ClearHashSet(_asyncFrameKeyMaskObject)
                  && ClearHashSet(_asyncFrameKeyDownMaskObject)
                  && ClearHashSet(_asyncFrameKeyUpMaskObject);
        if (!ok)
            return false;

        for (int slot = 0; slot < AsyncKeyLabels.Length; slot++)
        {
            ulong bit = 1UL << slot;
            AsyncKeyCodeValue key = new()
            {
                Key = GetTouchRawKey(slot),
                Label = AsyncKeyLabels[slot],
            };

            if ((heldMask & bit) != 0UL)
                ok &= AddHashSet(_asyncKeyMaskObject, key);
            if ((downMask & bit) != 0UL)
                ok &= AddHashSet(_asyncKeyDownMaskObject, key);
            if ((upMask & bit) != 0UL)
                ok &= AddHashSet(_asyncKeyUpMaskObject, key);
            if ((frameMask & bit) != 0UL)
                ok &= AddHashSet(_asyncFrameKeyMaskObject, key);
            if ((frameDownMask & bit) != 0UL)
                ok &= AddHashSet(_asyncFrameKeyDownMaskObject, key);
            if ((frameUpMask & bit) != 0UL)
                ok &= AddHashSet(_asyncFrameKeyUpMaskObject, key);
        }

        return ok;
    }

    internal bool ProcessMobileAsyncInput(nint controller, ulong targetTick)
    {
        if (_processKeyInputs == null || controller == 0 || targetTick == 0UL)
            return false;

        try
        {
            _processTickArgs[0] = (nint)(&targetTick);
            _processKeyInputs.Invoke(controller, _processTickArgs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Mirrors the UI exclusion performed by the original mobile touch path.
    /// A direct Android edge has no Unity Touch instance, so the bridge must
    /// ask the controller before allowing a Down to become a gameplay key.
    /// Failure is intentionally fail-closed: an uncertain UI tap must not hit
    /// a tile.
    /// </summary>
    internal bool IsScreenPointInsideUi(nint controller, float x, float y)
    {
        if (controller == 0
            || _isScreenPointInsideUiElements == null
            || !float.IsFinite(x)
            || !float.IsFinite(y))
        {
            return true;
        }

        try
        {
            // Android MotionEvent uses a top-left origin; Unity screen-space
            // UI methods use a bottom-left origin. Keep the raw coordinates
            // for input capture, but convert only this UI query.
            int screenHeight = GetScreenHeight();
            if (screenHeight <= 0)
                return true;
            float unityY = screenHeight - y;
            if (!float.IsFinite(unityY))
                return true;

            Vector2Value point = new() { X = x, Y = unityY };
            _screenPointArgs[0] = (nint)(&point);
            return _isScreenPointInsideUiElements.InvokeUnbox<byte>(
                controller,
                _screenPointArgs) != 0;
        }
        catch
        {
            return true;
        }
    }

    private int GetScreenHeight()
    {
        try
        {
            return _getScreenHeight?.InvokeStaticUnbox<int>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    internal void ClearMobileAsyncInputState()
    {
        ClearMobileAsyncInputMasks();
        Write(_asyncTargetSongTick, 0, 0UL);
        Write(_asyncLastReportedTargetTick, 0, 0UL);
    }

    internal void ClearMobileAsyncInputMasks()
    {
        if (!EnsureAsyncMaskApi())
            return;

        ClearHashSet(_asyncKeyMaskObject);
        ClearHashSet(_asyncKeyDownMaskObject);
        ClearHashSet(_asyncKeyUpMaskObject);
        ClearHashSet(_asyncFrameKeyMaskObject);
        ClearHashSet(_asyncFrameKeyDownMaskObject);
        ClearHashSet(_asyncFrameKeyUpMaskObject);
    }

    private bool EnsureAsyncMaskApi()
    {
        if (_asyncKeyMask == null
            || _asyncKeyDownMask == null
            || _asyncKeyUpMask == null
            || _asyncFrameKeyMask == null
            || _asyncFrameKeyDownMask == null
            || _asyncFrameKeyUpMask == null)
            return false;

        if (_asyncKeyMaskObject != 0
            && _asyncKeyDownMaskObject != 0
            && _asyncKeyUpMaskObject != 0
            && _asyncFrameKeyMaskObject != 0
            && _asyncFrameKeyDownMaskObject != 0
            && _asyncFrameKeyUpMaskObject != 0
            && _hashSetAdd != null
            && _hashSetClear != null)
        {
            return true;
        }

        _asyncKeyMaskObject = Read(_asyncKeyMask, 0, nint.Zero);
        _asyncKeyDownMaskObject = Read(_asyncKeyDownMask, 0, nint.Zero);
        _asyncKeyUpMaskObject = Read(_asyncKeyUpMask, 0, nint.Zero);
        _asyncFrameKeyMaskObject = Read(_asyncFrameKeyMask, 0, nint.Zero);
        _asyncFrameKeyDownMaskObject = Read(_asyncFrameKeyDownMask, 0, nint.Zero);
        _asyncFrameKeyUpMaskObject = Read(_asyncFrameKeyUpMask, 0, nint.Zero);
        if (_asyncKeyMaskObject == 0
            || _asyncKeyDownMaskObject == 0
            || _asyncKeyUpMaskObject == 0
            || _asyncFrameKeyMaskObject == 0
            || _asyncFrameKeyDownMaskObject == 0
            || _asyncFrameKeyUpMaskObject == 0)
        {
            return false;
        }

        IRuntimeClass? hashSetClass = RuntimeManager.GetObjectClass(_asyncKeyMaskObject);
        _hashSetAdd = hashSetClass?.GetMethod("Add", 1);
        _hashSetClear = hashSetClass?.GetMethod("Clear", 0);
        return _hashSetAdd != null && _hashSetClear != null;
    }

    private bool AddHashSet(nint set, AsyncKeyCodeValue key)
    {
        if (_hashSetAdd == null || set == 0)
            return false;
        try
        {
            _hashSetKeyArgs[0] = (nint)(&key);
            _hashSetAdd.Invoke(set, _hashSetKeyArgs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool ClearHashSet(nint set)
    {
        if (_hashSetClear == null || set == 0)
            return false;
        try
        {
            _hashSetClear.Invoke(set);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool SetInputTypeActive(IRuntimeField field, bool active)
    {
        nint inputType = Read(field, 0, nint.Zero);
        if (inputType == 0)
            return false;
        Write(_rdInputTypeActive, inputType, (byte)(active ? 1 : 0));
        return true;
    }

    private static ushort GetTouchRawKey(int slot)
        => (ushort)(AsyncTouchRawBase + slot);

    // ── 核心公式 ───────────────────────────────────────────────

    /// <summary>
    /// 把事件时刻换算成歌曲位置，对应 <c>AsyncInputUtils.GetSongPosition</c>。
    /// </summary>
    /// <param name="conductor">当前 conductor</param>
    /// <param name="eventDspTime">由 <see cref="ClockSync.ToDspTime"/> 换算出的事件时刻（秒）</param>

    internal double GetSongPosition(nint conductor, double eventDspTime)
    {
        double pitch = GetSongPitch(conductor);
        return (eventDspTime - GetDspTimeSong(conductor) - GetInputCalibration()) * pitch
               - GetAddOffset(conductor);
    }

    /// <summary>
    /// 按事件时刻推算行星此刻应有的角度，对应 <c>AsyncInputUtils.GetAngle</c>：
    /// <code>
    /// snappedLastAngle
    ///   + (songPosition - player.lastHit) / crotchetAtStart * PI * speed * (isCW ? 1 : -1)
    /// </code>
    /// </summary>
    /// <returns>重算后的角度；无法计算时返回 <c>null</c>。</returns>
    /// <param name="eventDspTime">
    /// 事件发生时刻，已直接换算到 <c>AudioSettings.dspTime</c> 时间轴（秒）。
    /// </param>
    internal double? ComputeAngle(nint planet, double eventDspTime)
    {
        if (planet == 0)
            return null;

        nint system = GetPlanetSystem(planet);
        nint player = GetPlanetPlayer(planet);
        if (system == 0 || player == 0)
            return null;

        nint conductor = GetPlanetConductor(planet);
        if (conductor == 0)
            return null;

        double crotchet = GetCrotchetAtStart(conductor);
        if (Math.Abs(crotchet) < 1e-9d)
            return null;

        if (eventDspTime <= 0d)
            return null;

        double songPosition = GetSongPosition(conductor, eventDspTime);
        double direction = IsClockwise(system) ? 1d : -1d;
        return GetSnappedLastAngle(planet)
               + (songPosition - GetLastHit(player)) / crotchet * Pi * GetSystemSpeed(system) * direction;
    }

    /// <summary>
    /// 按触摸硬件时间戳重算延迟校准页的行星角度。
    /// 原版在 <c>Update</c> 中按当前帧歌曲位置写 <c>angleRadians</c>，随后
    /// <c>PutDataPoint</c> 读取它；这里在采样前替换成输入真实发生时刻的角度。
    /// </summary>
    internal double? ComputeCalibrationAngle(nint calibration, double eventDspTime)
    {
        if (calibration == 0 || eventDspTime <= 0d)
            return null;

        nint conductor = Read(_calibrationConductor, calibration, nint.Zero);
        if (conductor == 0)
            conductor = GetConductor();
        if (conductor == 0)
            return null;

        double crotchet = GetCrotchetAtStart(conductor);
        if (Math.Abs(crotchet) < 1e-9d)
            return null;

        // 与 scnCalibration.Update 一致，但不经过当前帧缓存：
        // angle = PI/2 + ((eventDsp - dspTimeSong) * pitch - addoffset) / crotchet * PI
        // 校准页原公式会把当前 inputOffset 抵消，因此这里也不减 calibration_i。
        double songPosition = (eventDspTime - GetDspTimeSong(conductor)) * GetSongPitch(conductor)
                              - GetAddOffset(conductor);
        return 1.5707963705062866d
               + songPosition / crotchet * 3.1415927410125732d;
    }

    internal void SetCalibrationAngle(nint calibration, double angle)
    {
        Write(_calibrationAngleRadians, calibration, angle);
    }

    internal nint GetCalibrationConductor(nint calibration)
    {
        nint conductor = Read(_calibrationConductor, calibration, nint.Zero);
        return conductor != 0 ? conductor : GetConductor();
    }

    // ── 反射辅助 ───────────────────────────────────────────────

    private IRuntimeClass RequireClass(string name)
    {
        return FindClass(name) ?? throw new InvalidOperationException($"Game class not found: {name}");
    }

    private IRuntimeClass? FindClass(string name)
    {
        return _assembly.GetClass(string.Empty, name);
    }

    private static IRuntimeField? FindField(IRuntimeClass? type, params string[] names)
    {
        if (type == null)
            return null;
        foreach (string name in names)
        {
            IRuntimeField? field = type.GetField(name);
            if (field != null)
                return field;
        }
        return null;
    }

    private static nint InvokeStaticObject(IRuntimeMethod? method)
    {
        try
        {
            return method?.InvokeStatic() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static T Read<T>(IRuntimeField? field, nint instance, T fallback) where T : unmanaged
    {
        if (field == null || (!field.IsStatic && instance == 0))
            return fallback;
        try
        {
            return field.GetValue<T>(instance);
        }
        catch
        {
            return fallback;
        }
    }

    private static void Write<T>(IRuntimeField? field, nint instance, T value) where T : unmanaged
    {
        if (field == null || (!field.IsStatic && instance == 0))
            return;
        try
        {
            field.SetValue(instance, value);
        }
        catch
        {
        }
    }
}
