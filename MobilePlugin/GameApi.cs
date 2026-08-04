using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace AsyncInput.Mobile;

/// <summary>
/// 异步输入所需的游戏字段访问 —— 只覆盖角度重算这一条链路，不做多余封装。
/// </summary>
internal unsafe sealed class GameApi
{
    /// <summary>与游戏内 <c>AsyncInputUtils.GetAngle</c> 使用的常量保持一致。</summary>
    private const double Pi = 3.141592653598793d;

    private readonly IRuntimeAssembly _assembly;

    private readonly IRuntimeClass _planetClass;
    private readonly IRuntimeClass _playerClass;
    private readonly IRuntimeClass _conductorClass;
    private readonly IRuntimeClass _systemClass;
    private readonly IRuntimeClass? _calibrationClass;
    private readonly IRuntimeClass? _controllerClass;
    private readonly IRuntimeClass? _adoBaseClass;
    private readonly IRuntimeClass? _asyncInputManagerClass;
    private readonly IRuntimeClass? _rdInputClass;
    private readonly IRuntimeClass? _rdInputTypeClass;
    private readonly IAppDomain _domain;

    private readonly IRuntimeField? _planetAngle;
    private readonly IRuntimeField? _planetCachedAngle;
    private readonly IRuntimeField? _planetSnappedLastAngle;
    private readonly IRuntimeField? _planetSystem;
    private readonly IRuntimeField? _planetPlayer;

    private readonly IRuntimeField? _playerLastHit;
    private readonly IRuntimeField? _playerSystem;
    private readonly IRuntimeField? _playerKeyTimes;

    private readonly IRuntimeField? _conductorInstance;
    private readonly IRuntimeField? _conductorDspTimeSong;
    private readonly IRuntimeField? _conductorCrotchetAtStart;
    private readonly IRuntimeField? _conductorAddOffset;
    private readonly IRuntimeField? _conductorSong;
    private readonly IRuntimeField? _conductorDspTime;
    private readonly IRuntimeField? _conductorPrevFrame;
    private readonly IRuntimeField? _conductorSongPos;

    private readonly IRuntimeField? _systemSpeed;
    private readonly IRuntimeField? _systemIsCW;
    private readonly IRuntimeField? _systemChosenPlanet;

    private readonly IRuntimeField? _controllerPaused;
    private readonly IRuntimeField? _controllerGameWorld;

    private readonly IRuntimeField? _calibrationAngleRadians;
    private readonly IRuntimeField? _calibrationConductor;

    private readonly IRuntimeField? _asyncCurrFrameTick;
    private readonly IRuntimeField? _asyncPrevFrameTick;
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
    private readonly IRuntimeMethod? _getPlanetConductor;
    private readonly IRuntimeMethod? _getCalibrationInput;
    private readonly IRuntimeMethod? _getAudioDspTime;
    private readonly IRuntimeMethod? _getUnscaledTime;
    private readonly IRuntimeMethod? _processKeyInputs;
    private readonly IRuntimeMethod? _getChosenPlanet;
    private readonly IRuntimeMethod? _asyncRefreshAngles;

    private IRuntimeMethod? _hashSetAdd;
    private IRuntimeMethod? _hashSetClear;
    private nint _asyncKeyMaskObject;
    private nint _asyncKeyDownMaskObject;
    private nint _asyncKeyUpMaskObject;
    private nint _asyncFrameKeyMaskObject;
    private nint _asyncFrameKeyDownMaskObject;
    private nint _asyncFrameKeyUpMaskObject;
    private readonly nint[] _hashSetKeyArgs = new nint[1];
    private readonly nint[] _processTickArgs = new nint[1];

    /// <summary>与游戏 AsyncKeyCode 的 IL2CPP 值类型布局一致，大小为 8 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AsyncKeyCodeValue
    {
        public ushort Key;
        public ushort Padding;
        public int Label;
    }

    private static readonly int[] AsyncKeyLabels =
    {
        81, 65, 113, 26, 27, 28, 29, 30,
        31, 32, 33, 34, 35, 41, 42, 44,
    };

    private const ushort AsyncKeyRawBase = 0xff00;

    /// <summary>旧版本角度回退所需的全部句柄是否齐备。</summary>
    private bool CanUseAngleFallback =>
        _planetAngle != null
        && _planetSnappedLastAngle != null
        && _planetSystem != null
        && _planetPlayer != null
        && _playerLastHit != null
        && _playerSystem != null
        && _systemChosenPlanet != null
        && _conductorDspTimeSong != null
        && _conductorCrotchetAtStart != null
        && _systemSpeed != null
        && _systemIsCW != null
        && _conductorDspTime != null
        && _conductorPrevFrame != null
        && _getUnscaledTime != null;

    /// <summary>
    /// 至少有一条完整输入路径时插件才可加载。官方 replay 不依赖角度字段，
    /// 这样字段布局有小幅变化时仍可优先使用游戏自己的判定状态机。
    /// </summary>
    internal bool IsUsable => CanUseOfficialAsyncReplay || CanUseAngleFallback;

    /// <summary>
    /// 官方异步输入管线所需句柄是否存在。
    /// 该路径把事件直接送入 ProcessKeyInputs；缺失时 GameHooks 会选择角度回退路径。
    /// </summary>
    internal bool CanUseOfficialAsyncReplay =>
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
        && _getChosenPlanet != null
        && _asyncRefreshAngles != null;

    /// <summary>延迟校准页能否按硬件时间戳重算采样角度。</summary>
    internal bool CanAdjustCalibration =>
        _calibrationAngleRadians != null
        && _calibrationConductor != null
        && _conductorDspTimeSong != null
        && _conductorCrotchetAtStart != null
        && _conductorDspTime != null
        && _conductorPrevFrame != null
        && _getUnscaledTime != null;

    private GameApi(IAppDomain domain, IRuntimeAssembly assembly)
    {
        _domain = domain;
        _assembly = assembly;

        _planetClass = RequireClass("scrPlanet");
        _playerClass = RequireClass("scrPlayer");
        _conductorClass = RequireClass("scrConductor");
        _systemClass = RequireClass("PlanetarySystem");
        _calibrationClass = FindClass("scnCalibration");
        _controllerClass = FindClass("scrController");
        _adoBaseClass = FindClass("ADOBase");
        _asyncInputManagerClass = FindClass("AsyncInputManager");
        _rdInputClass = FindClass("RDInput");
        _rdInputTypeClass = FindClass("RDInputType");

        _planetAngle = FindField(_planetClass, "angle");
        _planetCachedAngle = FindField(_planetClass, "cachedAngle");
        _planetSnappedLastAngle = FindField(_planetClass, "<snappedLastAngle>k__BackingField", "snappedLastAngle");
        _planetSystem = FindField(_planetClass, "planetarySystem");
        _planetPlayer = FindField(_planetClass, "player");

        _playerLastHit = FindField(_playerClass, "lastHit");
        _playerSystem = FindField(_playerClass, "planetarySystem");
        _playerKeyTimes = FindField(_playerClass, "keyTimes");

        _conductorInstance = FindField(_conductorClass, "_instance", "instance");
        _conductorDspTimeSong = FindField(_conductorClass, "dspTimeSong");
        _conductorCrotchetAtStart = FindField(_conductorClass, "crotchetAtStart");
        _conductorAddOffset = FindField(_conductorClass, "addoffset");
        _conductorSong = FindField(_conductorClass, "song");
        _conductorDspTime = FindField(_conductorClass, "dspTime");
        _conductorPrevFrame = FindField(_conductorClass, "previousFrameTime");
        _conductorSongPos = FindField(_conductorClass, "_songposition_minusi", "songposition_minusi");

        _systemSpeed = FindField(_systemClass, "speed");
        _systemIsCW = FindField(_systemClass, "isCW");
        _systemChosenPlanet = FindField(_systemClass, "chosenPlanet");

        _controllerPaused = FindField(_controllerClass, "paused");
        _controllerGameWorld = FindField(_controllerClass, "gameworld", "isGameWorld", "isgameworld");

        _calibrationAngleRadians = FindField(_calibrationClass, "angleRadians");
        _calibrationConductor = FindField(_calibrationClass, "conductor");

        _asyncCurrFrameTick = FindField(_asyncInputManagerClass, "currFrameTick");
        _asyncPrevFrameTick = FindField(_asyncInputManagerClass, "prevFrameTick");
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
        _getPlanetConductor = _planetClass.GetMethod("get_conductor", 0);
        _getCalibrationInput = _conductorClass.GetMethod("get_calibration_i", 0);
        _getAudioDspTime = FindClassInDomain("UnityEngine", "AudioSettings")
            ?.GetMethod("get_dspTime", 0);
        _getUnscaledTime = FindClassInDomain("UnityEngine", "Time")
            ?.GetMethod("get_unscaledTimeAsDouble", 0);
        _processKeyInputs = _controllerClass?.GetMethod("ProcessKeyInputs", 1);
        _getChosenPlanet = _controllerClass?.GetMethod("get_chosenPlanet", 0);
        _asyncRefreshAngles = _planetClass.GetMethod("AsyncRefreshAngles", 0);
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
    /// 获取当前游戏有效的 DSP 时间。
    /// </summary>
    /// <remarks>
    /// 优先读取 Unity AudioSettings.dspTime。只有在目标版本没有暴露该 API 时，
    /// 才回退到 scrConductor.dspTime + unscaledTime 增量；后者会带有帧缓存延迟。
    /// </remarks>
    internal double GetCurrentDspTime(nint conductor)
    {
        double audioDspTime = GetAudioDspTime();
        if (audioDspTime > 0d)
            return audioDspTime;

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
        if (_systemChosenPlanet == null) missing.Add("PlanetarySystem.chosenPlanet");
        if (_conductorDspTimeSong == null) missing.Add("scrConductor.dspTimeSong");
        if (_conductorCrotchetAtStart == null) missing.Add("scrConductor.crotchetAtStart");
        if (_systemSpeed == null) missing.Add("PlanetarySystem.speed");
        if (_systemIsCW == null) missing.Add("PlanetarySystem.isCW");
        if (_getAudioDspTime == null) missing.Add("UnityEngine.AudioSettings.dspTime");
        if (_conductorDspTime == null) missing.Add("scrConductor.dspTime");
        if (_conductorPrevFrame == null) missing.Add("scrConductor.previousFrameTime");
        if (_getUnscaledTime == null) missing.Add("UnityEngine.Time.unscaledTimeAsDouble");
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
    /// 读取当前待处理输入的第一个 Unity 时间。
    /// 用于在角度回退路径中把硬件事件与游戏这一笔 keyTimes 对齐。
    /// </summary>
    internal bool TryGetFirstPendingKeyTime(nint player, out double keyTime)
    {
        keyTime = 0d;
        nint keyTimes = Read(_playerKeyTimes, player, nint.Zero);
        if (keyTimes == 0)
            return false;

        try
        {
            int count = new RuntimeObject(keyTimes).InvokeUnbox<int>("get_Count", 0);
            if (count <= 0)
                return false;

            int index = 0;
            nint[] args = { (nint)(&index) };
            keyTime = new RuntimeObject(keyTimes).InvokeUnbox<double>("get_Item", 1, args);
            return double.IsFinite(keyTime);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 当前歌曲位置（已扣除校准偏移），对应 <c>scrConductor.songposition_minusi</c>。
    /// </summary>
    internal double GetSongPositionMinusI(nint conductor)
    {
        double value = Read(_conductorSongPos, conductor, 0d);
        if (value > 0d)
            return value;
        // private 字段可能读不到，尝试属性 getter
        try
        {
            nint obj = conductor;
            if (obj != 0)
                return new RuntimeObject(obj).InvokeUnbox<double>("get_songposition_minusi", 0);
        }
        catch { }
        return 0d;
    }

    /// <summary>
    /// 向 <c>scrPlayer.keyTimes</c> 推入一个按键时刻，等价于
    /// <c>keyTimes.Add(time)</c>——即 <c>HitAutoFloors</c> 内部做的事。
    /// </summary>
    internal void AddKeyTime(nint player, double time)
    {
        nint keyTimes = Read(_playerKeyTimes, player, nint.Zero);
        if (keyTimes == 0)
            return;
        try
        {
            // List<double>.Add(double) —— 参数是装箱的 double。
            // il2cpp_runtime_invoke 要求参数为指向已装箱值的指针。
            // double 是 8 字节，先把它放进一个 long 槽里再传地址。
            long boxed = BitConverter.DoubleToInt64Bits(time);
            nint[] args = { (nint)boxed };
            new RuntimeObject(keyTimes).InvokeVoid("Add", 1, args);
        }
        catch (Exception ex)
        {
            Logger.Warn("AsyncInput", $"AddKeyTime failed: {ex.Message}");
        }
    }

    internal nint GetChosenPlanet(nint system) => Read(_systemChosenPlanet, system, nint.Zero);

    internal double GetSystemSpeed(nint system) => Read(_systemSpeed, system, 1d);

    internal bool IsClockwise(nint system) => Read(_systemIsCW, system, (byte)1) != 0;

    internal bool IsPaused(nint controller) => Read(_controllerPaused, controller, (byte)0) != 0;

    /// <summary>
    /// 判断控制器是否仍处于游戏世界。字段不存在时保守地交给上层 capture gate 判断。
    /// </summary>
    internal bool IsGameplayController(nint controller)
    {
        if (controller == 0)
            return false;
        return _controllerGameWorld == null
            || Read(_controllerGameWorld, controller, (byte)0) != 0;
    }

    // ── 官方 AsyncInput 管线 ───────────────────────────────────

    /// <summary>
    /// 写入官方异步输入的帧时钟和 wall-to-DSP 偏移。
    /// </summary>
    internal bool PrepareAsyncFrame(ulong frameTick, ulong previousFrameTick, out ulong offsetTick)
    {
        offsetTick = 0UL;
        if (!CanUseOfficialAsyncReplay || frameTick == 0UL)
            return false;

        // AudioSettings.dspTime 是首选；旧版本没有该属性时，用 conductor 的
        // 当前值加上 unscaledTime 增量，仍能建立官方 wall-to-DSP 偏移。
        double dspTime = GetAudioDspTime();
        if (dspTime <= 0d)
            dspTime = GetCurrentDspTime(GetConductor());
        if (dspTime <= 0d)
            return false;

        double dspTicksDouble = dspTime * 10_000_000d;
        if (!double.IsFinite(dspTicksDouble) || dspTicksDouble <= 0d || dspTicksDouble >= ulong.MaxValue)
            return false;

        ulong dspTicks = (ulong)dspTicksDouble;
        if (frameTick <= dspTicks)
            return false;

        offsetTick = frameTick - dspTicks;
        if (previousFrameTick == 0UL)
            previousFrameTick = frameTick;

        Write(_asyncPrevFrameTick, 0, previousFrameTick);
        Write(_asyncCurrFrameTick, 0, frameTick);
        Write(_asyncOffsetTick, 0, offsetTick);
        Write(_asyncOffsetTickUpdated, 0, (byte)1);
        return true;
    }

    internal void SetAsyncTargetTick(ulong targetTick, ulong offsetTick)
    {
        if (!CanUseOfficialAsyncReplay)
            return;
        ulong songTick = targetTick >= offsetTick ? targetTick - offsetTick : 0UL;
        Write(_asyncTargetSongTick, 0, songTick);
    }

    internal void SetAsyncLastReportedTargetTick(ulong targetTick)
    {
        if (CanUseOfficialAsyncReplay)
            Write(_asyncLastReportedTargetTick, 0, targetTick);
    }

    /// <summary>调用游戏自己的官方异步输入入口。</summary>
    internal bool ProcessAsyncInput(nint controller, ulong targetTick)
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
    /// 官方事件可能早于当前渲染帧。事件批处理完成后把选中行星恢复到当前帧，
    /// 避免低帧率设备在下一次触摸前一直显示在上一个事件时刻。
    /// </summary>
    internal bool RestoreAsyncAngleToTick(nint controller, ulong targetTick, ulong offsetTick)
    {
        if (!CanUseOfficialAsyncReplay
            || controller == 0
            || targetTick == 0UL
            || _getChosenPlanet == null
            || _asyncRefreshAngles == null)
        {
            return false;
        }

        SetAsyncTargetTick(targetTick, offsetTick);
        try
        {
            nint planet = _getChosenPlanet.Invoke(controller);
            if (planet == 0)
                return false;

            _asyncRefreshAngles.Invoke(planet);
            double angle = GetPlanetAngle(planet);
            Write(_planetCachedAngle, planet, angle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 以稳定的 AsyncKeyCode slot 设置官方六组 HashSet。
    /// keyMask 和 frameDependentKeyMask 都保存当前持有状态；四个 edge mask
    /// 描述本帧（以及同一帧内更早事件组）已经出现的 Down/Up。
    /// </summary>
    internal bool ApplyAsyncInputMasks(
        ulong heldMask,
        ulong downMask,
        ulong upMask,
        ulong frameMask,
        ulong frameDownMask,
        ulong frameUpMask)
    {
        if (!EnsureAsyncMaskApi())
            return false;

        bool ok = true;
        ok &= ClearHashSet(_asyncKeyMaskObject);
        ok &= ClearHashSet(_asyncKeyDownMaskObject);
        ok &= ClearHashSet(_asyncKeyUpMaskObject);
        ok &= ClearHashSet(_asyncFrameKeyMaskObject);
        ok &= ClearHashSet(_asyncFrameKeyDownMaskObject);
        ok &= ClearHashSet(_asyncFrameKeyUpMaskObject);

        for (int slot = 0; slot < AsyncKeyLabels.Length; slot++)
        {
            ulong bit = 1UL << slot;
            AsyncKeyCodeValue key = new()
            {
                Key = (ushort)(AsyncKeyRawBase + slot),
                Label = AsyncKeyLabels[slot],
            };

            if ((heldMask & bit) != 0UL)
                ok &= AddHashSet(_asyncKeyMaskObject, key);
            if ((frameMask & bit) != 0UL)
                ok &= AddHashSet(_asyncFrameKeyMaskObject, key);
            if ((downMask & bit) != 0UL)
                ok &= AddHashSet(_asyncKeyDownMaskObject, key);
            if ((frameDownMask & bit) != 0UL)
                ok &= AddHashSet(_asyncFrameKeyDownMaskObject, key);
            if ((upMask & bit) != 0UL)
                ok &= AddHashSet(_asyncKeyUpMaskObject, key);
            if ((frameUpMask & bit) != 0UL)
                ok &= AddHashSet(_asyncFrameKeyUpMaskObject, key);
        }

        return ok;
    }

    internal void ClearAsyncInputEdges()
    {
        if (!EnsureAsyncMaskApi())
            return;
        ClearHashSet(_asyncKeyDownMaskObject);
        ClearHashSet(_asyncKeyUpMaskObject);
        ClearHashSet(_asyncFrameKeyDownMaskObject);
        ClearHashSet(_asyncFrameKeyUpMaskObject);
    }

    internal void ClearAsyncInputMasks()
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

    /// <summary>
    /// 清理官方静态状态。暂停、重开、场景切换和 Mod 卸载都必须调用，避免旧的
    /// keyMask 或 offsetTick 影响下一段歌曲。
    /// </summary>
    internal void ResetAsyncInputState()
    {
        if (!CanUseOfficialAsyncReplay)
            return;

        ClearAsyncInputMasks();
        Write(_asyncCurrFrameTick, 0, 0UL);
        Write(_asyncPrevFrameTick, 0, 0UL);
        Write(_asyncTargetSongTick, 0, 0UL);
        Write(_asyncOffsetTick, 0, 0UL);
        Write(_asyncOffsetTickUpdated, 0, (byte)0);
        Write(_asyncLastReportedTargetTick, 0, 0UL);
    }

    /// <summary>
    /// 切换官方 RDInput 的 regular/async 输入类型。只在主线程短暂开启，避免污染菜单输入。
    /// </summary>
    internal bool SetAsyncInputTypes(bool enabled)
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
        ok &= SetInputTypeActive(_rdInputKeyboardInput, !enabled);
        ok &= SetInputTypeActive(_rdInputKeyboardLeft, !enabled);
        ok &= SetInputTypeActive(_rdInputKeyboardRight, !enabled);
        ok &= SetInputTypeActive(_rdInputAsyncKeyboard, enabled);
        ok &= SetInputTypeActive(_rdInputAsyncKeyboardLeft, enabled);
        ok &= SetInputTypeActive(_rdInputAsyncKeyboardRight, enabled);
        return ok;
    }

    private bool EnsureAsyncMaskApi()
    {
        if (!CanUseOfficialAsyncReplay)
            return false;

        // AsyncInputManager 的六个 HashSet 是静态只读实例。它们在 IL2CPP
        // 域生命周期内不会改变，缓存后每个事件批次无需重复读取字段/解析类型。
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

        if (_hashSetAdd == null || _hashSetClear == null)
        {
            IRuntimeClass? hashSetClass = RuntimeManager.GetObjectClass(_asyncKeyMaskObject);
            _hashSetAdd = hashSetClass?.GetMethod("Add", 1);
            _hashSetClear = hashSetClass?.GetMethod("Clear", 0);
        }

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

    /// <summary>
    /// 写回行星角度。
    /// <c>cachedAngle</c> 必须与 <c>angle</c> 同步写入 —— 游戏在多处用它判断角度是否被外部改动，
    /// 只写 <c>angle</c> 会导致位置在下一帧被回滚。
    /// </summary>
    internal void SetPlanetAngle(nint planet, double angle)
    {
        Write(_planetAngle, planet, angle);
        Write(_planetCachedAngle, planet, angle);
    }

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
