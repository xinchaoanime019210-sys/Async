using System.Runtime.InteropServices;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace AsyncInput.Mobile;

public static partial class GameHooks
{
    private const string LogTag = "AsyncInput";

    private static AsyncInputPlugin? _plugin;
    private static int _installed;
    private static int _failed;
    private static int _mobileAsyncFrameActive;
    private static int _mobileAsyncFrameProcessed;
    private static int _mobileAsyncInputDispatching;
    private static int _mobileAsyncPressCount;
    private static bool _mobileAsyncBridgeHooksInstalled;

    /// <summary>
    /// 本 Mod 真正安装成功的 Hook 的卸载动作。
    /// </summary>
    /// <remarks>
    /// 不能直接调用生成的 <c>UninstallHooks()</c>。生成的 <c>Install_xxx</c> 是在
    /// <c>HookHelper.Hook</c> 调用**之前**就写入 <c>_xxx_origPtr</c> 的，安装失败
    /// （地址已被其他 Mod 占用）时该字段依然保存着目标地址；而 <c>UninstallHooks()</c>
    /// 仅凭该字段非零就执行 <c>Unhook</c>，<c>DobbyDestroy(addr)</c> 又只认地址不认归属，
    /// 结果是**把其他 Mod 装在同一地址上的 Hook 拆掉**。
    /// 因此这里只登记确实安装成功的项，卸载时逐个执行。
    /// </remarks>
    private static readonly List<Action> InstalledHooks = new();

    /// <summary>核心注入点是否就绪。装不上就必须彻底禁用重算，绝不能半开着跑。</summary>
    internal static bool InputHookAvailable { get; private set; }

    internal static bool IsMobileAsyncInputDispatching
        => Volatile.Read(ref _mobileAsyncInputDispatching) != 0;

    internal static void SetMobileAsyncInputDispatching(bool active, int pressCount = 0)
    {
        Volatile.Write(ref _mobileAsyncPressCount, active ? Math.Max(0, pressCount) : 0);
        Volatile.Write(ref _mobileAsyncInputDispatching, active ? 1 : 0);
    }

    internal static bool Install(AsyncInputPlugin plugin)
    {
        try
        {
            Uninstall();
            _plugin = plugin;
            plugin.SetHoldReleaseCorrectionEnabled(false);
            plugin.SetMobileAsyncBridgeEnabled(false);

            // The bridge must be considered first. It consumes timestamped
            // Android edges through the game's own ProcessKeyInputs path and
            // does not require the angle-projection fallback hooks.
            bool bridgeInstalled = plugin.CanUseMobileAsyncBridge
                                   && TryInstallMobileAsyncBridge(plugin);
            if (bridgeInstalled)
            {
                InputHookAvailable = true;
                plugin.SetHoldReleaseCorrectionEnabled(false);
            }
            else
            {
                // Fallback for game builds or mod combinations where the
                // complete async bridge cannot be installed. This path still
                // waits for Unity's touch sample, so it is deliberately not a
                // prerequisite for the direct bridge.
                InputHookAvailable = TryInstall(
                    "scrPlayer.UpdateHoldKeys",
                    Install_UpdateHoldKeys,
                    Uninstall_UpdateHoldKeys,
                    Abandon_UpdateHoldKeys);
                if (!InputHookAvailable)
                {
                    Logger.Error(LogTag,
                        "Neither the mobile async bridge nor scrPlayer.UpdateHoldKeys is available");
                    Uninstall();
                    return false;
                }

                bool sourceHookInstalled = TryInstall(
                    "scrPlayer.HitAutoFloors",
                    Install_HitAutoFloors,
                    Uninstall_HitAutoFloors,
                    Abandon_HitAutoFloors);
                if (!sourceHookInstalled)
                {
                    Logger.Warn(LogTag,
                        "scrPlayer.HitAutoFloors is already hooked; async input source mapping is unavailable");
                    Uninstall();
                    return false;
                }

                bool releaseHookInstalled = TryInstall(
                    "scrPlayer.ValidInputWasReleased",
                    Install_ValidInputWasReleased,
                    Uninstall_ValidInputWasReleased,
                    Abandon_ValidInputWasReleased);
                bool frameHookInstalled = TryInstall(
                    "scrPlayer.Simulated_PlayerControl_Update",
                    Install_SimulatedPlayerControlUpdate,
                    Uninstall_SimulatedPlayerControlUpdate,
                    Abandon_SimulatedPlayerControlUpdate);
                plugin.SetHoldReleaseCorrectionEnabled(releaseHookInstalled && frameHookInstalled);
            }

            // 时钟复位点。装不上只是复位不及时（表现为切场景后前若干次判定偏移），不致命，
            // 因为 ClockSync 本身也会在检测到时钟跳变时自行复位。
            TryInstall(
                "scrController.PlayerControl_Enter",
                Install_PlayerControlEnter,
                Uninstall_PlayerControlEnter,
                Abandon_PlayerControlEnter);
            TryInstall(
                "scrController.set_paused",
                Install_SetPaused,
                Uninstall_SetPaused,
                Abandon_SetPaused);

            // 延迟校准页在 PutDataPoint 中读取当前帧缓存的 angleRadians。
            // 在原函数前按硬件时间戳替换该值，即可保留游戏原有的全部统计与保存逻辑。
            if (plugin.CanImproveCalibration)
            {
                TryInstall(
                    "scnCalibration.PutDataPoint",
                    Install_CalibrationPutDataPoint,
                    Uninstall_CalibrationPutDataPoint,
                    Abandon_CalibrationPutDataPoint);
            }
            else
            {
                Logger.Warn(LogTag,
                    "Calibration fields are unavailable; gameplay async input remains enabled");
            }

            Logger.Info(LogTag, $"Installed {_installed} IL2CPP hooks ({_failed} unavailable)");
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Hook installation failed: {exception}");
            Uninstall();
            return false;
        }
    }

    private static bool TryInstallMobileAsyncBridge(AsyncInputPlugin plugin)
    {
        int firstBridgeHook = InstalledHooks.Count;
        bool conductorInstalled = TryInstall(
            "scrConductor.Update",
            Install_ConductorUpdate,
            Uninstall_ConductorUpdate,
            Abandon_ConductorUpdate);
        bool offsetInstalled = conductorInstalled && TryInstall(
            "AsyncInputUtils.UpdateOffsetTime",
            Install_AsyncInputUtilsUpdateOffsetTime,
            Uninstall_AsyncInputUtilsUpdateOffsetTime,
            Abandon_AsyncInputUtilsUpdateOffsetTime);
        bool activeInstalled = offsetInstalled && TryInstall(
            "AsyncInputManager.get_isActive",
            Install_AsyncInputIsActive,
            Uninstall_AsyncInputIsActive,
            Abandon_AsyncInputIsActive);
        bool touchInstalled = activeInstalled && TryInstall(
            "scrPlayer.get_touchEnabled",
            Install_PlayerTouchEnabled,
            Uninstall_PlayerTouchEnabled,
            Abandon_PlayerTouchEnabled);
        bool triggeredInstalled = touchInstalled && TryInstall(
            "scrPlayer.ValidInputWasTriggered",
            Install_ValidInputWasTriggered,
            Uninstall_ValidInputWasTriggered,
            Abandon_ValidInputWasTriggered);

        bool complete = conductorInstalled
                        && offsetInstalled
                        && activeInstalled
                        && touchInstalled
                        && triggeredInstalled;
        if (!complete)
        {
            UninstallInstalledFrom(firstBridgeHook);
            _mobileAsyncBridgeHooksInstalled = false;
            plugin.SetMobileAsyncBridgeEnabled(false);
            Logger.Warn(LogTag,
                "Mobile async bridge hooks are incomplete; using timestamp angle projection fallback");
            return false;
        }

        _mobileAsyncBridgeHooksInstalled = true;
        plugin.SetMobileAsyncBridgeEnabled(true);
        return true;
    }

    private static void UninstallInstalledFrom(int firstIndex)
    {
        for (int i = InstalledHooks.Count - 1; i >= firstIndex; i--)
        {
            try
            {
                InstalledHooks[i]();
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Hook removal failed: {exception}");
            }
            finally
            {
                InstalledHooks.RemoveAt(i);
                _installed = Math.Max(0, _installed - 1);
            }
        }
    }

    private static bool TryInstall(
        string name,
        Func<bool> install,
        Action uninstall,
        Action abandon)
    {
        bool ok;
        try
        {
            ok = install();
        }
        catch (Exception exception)
        {
            Logger.Warn(LogTag, $"Hook {name} threw during installation: {exception.Message}");
            ok = false;
        }

        if (ok)
        {
            _installed++;
            InstalledHooks.Add(uninstall);
        }
        else
        {
            // The generator records the target address before Hook() reports
            // success. Clearing that state prevents a later unload from
            // unhooking another mod that already owns the address.
            abandon();
            _failed++;
            Logger.Warn(LogTag, $"Hook {name} could not be installed (likely already hooked by another mod)");
        }
        return ok;
    }

    internal static void Uninstall()
    {
        _plugin?.SetHoldReleaseCorrectionEnabled(false);
        _plugin?.SetMobileAsyncBridgeEnabled(false);
        Volatile.Write(ref _mobileAsyncFrameActive, 0);
        Volatile.Write(ref _mobileAsyncFrameProcessed, 0);
        Volatile.Write(ref _mobileAsyncInputDispatching, 0);
        Volatile.Write(ref _mobileAsyncPressCount, 0);
        // 逆序卸载，且只动本 Mod 装成功的那些。
        for (int i = InstalledHooks.Count - 1; i >= 0; i--)
        {
            try
            {
                InstalledHooks[i]();
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Hook removal failed: {exception}");
            }
        }
        InstalledHooks.Clear();

        _plugin = null;
        _installed = 0;
        _failed = 0;
        _mobileAsyncBridgeHooksInstalled = false;
        InputHookAvailable = false;
    }

    private static void Uninstall_ConductorUpdate()
    {
        if (_ConductorUpdate_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_ConductorUpdate_origPtr);
        _ConductorUpdate_origPtr = nint.Zero;
        _ConductorUpdate_orig = null;
        _ConductorUpdate_wrap = null;
    }

    private static void Uninstall_AsyncInputUtilsUpdateOffsetTime()
    {
        if (_AsyncInputUtilsUpdateOffsetTime_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_AsyncInputUtilsUpdateOffsetTime_origPtr);
        _AsyncInputUtilsUpdateOffsetTime_origPtr = nint.Zero;
        _AsyncInputUtilsUpdateOffsetTime_orig = null;
        _AsyncInputUtilsUpdateOffsetTime_wrap = null;
    }

    private static void Uninstall_AsyncInputIsActive()
    {
        if (_AsyncInputIsActive_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_AsyncInputIsActive_origPtr);
        _AsyncInputIsActive_origPtr = nint.Zero;
        _AsyncInputIsActive_orig = null;
        _AsyncInputIsActive_wrap = null;
    }

    private static void Uninstall_PlayerTouchEnabled()
    {
        if (_PlayerTouchEnabled_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_PlayerTouchEnabled_origPtr);
        _PlayerTouchEnabled_origPtr = nint.Zero;
        _PlayerTouchEnabled_orig = null;
        _PlayerTouchEnabled_wrap = null;
    }

    private static void Uninstall_ValidInputWasTriggered()
    {
        if (_ValidInputWasTriggered_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_ValidInputWasTriggered_origPtr);
        _ValidInputWasTriggered_origPtr = nint.Zero;
        _ValidInputWasTriggered_orig = null;
        _ValidInputWasTriggered_wrap = null;
    }

    private static void Uninstall_UpdateHoldKeys()
    {
        if (_UpdateHoldKeys_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_UpdateHoldKeys_origPtr);
        _UpdateHoldKeys_origPtr = nint.Zero;
        _UpdateHoldKeys_orig = null;
        _UpdateHoldKeys_wrap = null;
    }

    private static void Uninstall_HitAutoFloors()
    {
        if (_HitAutoFloors_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_HitAutoFloors_origPtr);
        _HitAutoFloors_origPtr = nint.Zero;
        _HitAutoFloors_orig = null;
        _HitAutoFloors_wrap = null;
    }

    private static void Uninstall_PlayerControlEnter()
    {
        if (_PlayerControlEnter_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_PlayerControlEnter_origPtr);
        _PlayerControlEnter_origPtr = nint.Zero;
        _PlayerControlEnter_orig = null;
        _PlayerControlEnter_wrap = null;
    }

    private static void Uninstall_ValidInputWasReleased()
    {
        if (_ValidInputWasReleased_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_ValidInputWasReleased_origPtr);
        _ValidInputWasReleased_origPtr = nint.Zero;
        _ValidInputWasReleased_orig = null;
        _ValidInputWasReleased_wrap = null;
    }

    private static void Uninstall_SetPaused()
    {
        if (_SetPaused_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_SetPaused_origPtr);
        _SetPaused_origPtr = nint.Zero;
        _SetPaused_orig = null;
        _SetPaused_wrap = null;
    }

    private static void Uninstall_SimulatedPlayerControlUpdate()
    {
        if (_SimulatedPlayerControlUpdate_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_SimulatedPlayerControlUpdate_origPtr);
        _SimulatedPlayerControlUpdate_origPtr = nint.Zero;
        _SimulatedPlayerControlUpdate_orig = null;
        _SimulatedPlayerControlUpdate_wrap = null;
    }

    private static void Uninstall_CalibrationPutDataPoint()
    {
        if (_CalibrationPutDataPoint_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_CalibrationPutDataPoint_origPtr);
        _CalibrationPutDataPoint_origPtr = nint.Zero;
        _CalibrationPutDataPoint_orig = null;
        _CalibrationPutDataPoint_wrap = null;
    }

    // Failed installation only clears generator state. It must never call
    // HookHelper.Unhook because the target may belong to another mod.
    private static void Abandon_ConductorUpdate()
    {
        _ConductorUpdate_origPtr = nint.Zero;
        _ConductorUpdate_orig = null;
        _ConductorUpdate_wrap = null;
    }

    private static void Abandon_AsyncInputUtilsUpdateOffsetTime()
    {
        _AsyncInputUtilsUpdateOffsetTime_origPtr = nint.Zero;
        _AsyncInputUtilsUpdateOffsetTime_orig = null;
        _AsyncInputUtilsUpdateOffsetTime_wrap = null;
    }

    private static void Abandon_AsyncInputIsActive()
    {
        _AsyncInputIsActive_origPtr = nint.Zero;
        _AsyncInputIsActive_orig = null;
        _AsyncInputIsActive_wrap = null;
    }

    private static void Abandon_PlayerTouchEnabled()
    {
        _PlayerTouchEnabled_origPtr = nint.Zero;
        _PlayerTouchEnabled_orig = null;
        _PlayerTouchEnabled_wrap = null;
    }

    private static void Abandon_ValidInputWasTriggered()
    {
        _ValidInputWasTriggered_origPtr = nint.Zero;
        _ValidInputWasTriggered_orig = null;
        _ValidInputWasTriggered_wrap = null;
    }

    private static void Abandon_UpdateHoldKeys()
    {
        _UpdateHoldKeys_origPtr = nint.Zero;
        _UpdateHoldKeys_orig = null;
        _UpdateHoldKeys_wrap = null;
    }

    private static void Abandon_HitAutoFloors()
    {
        _HitAutoFloors_origPtr = nint.Zero;
        _HitAutoFloors_orig = null;
        _HitAutoFloors_wrap = null;
    }

    private static void Abandon_PlayerControlEnter()
    {
        _PlayerControlEnter_origPtr = nint.Zero;
        _PlayerControlEnter_orig = null;
        _PlayerControlEnter_wrap = null;
    }

    private static void Abandon_ValidInputWasReleased()
    {
        _ValidInputWasReleased_origPtr = nint.Zero;
        _ValidInputWasReleased_orig = null;
        _ValidInputWasReleased_wrap = null;
    }

    private static void Abandon_SetPaused()
    {
        _SetPaused_origPtr = nint.Zero;
        _SetPaused_orig = null;
        _SetPaused_wrap = null;
    }

    private static void Abandon_SimulatedPlayerControlUpdate()
    {
        _SimulatedPlayerControlUpdate_origPtr = nint.Zero;
        _SimulatedPlayerControlUpdate_orig = null;
        _SimulatedPlayerControlUpdate_wrap = null;
    }

    private static void Abandon_CalibrationPutDataPoint()
    {
        _CalibrationPutDataPoint_origPtr = nint.Zero;
        _CalibrationPutDataPoint_orig = null;
        _CalibrationPutDataPoint_wrap = null;
    }

    /// <summary>
    /// Establishes one mobile async frame before the game samples its own
    /// frame tick. The original conductor still owns all audio bookkeeping.
    /// </summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrConductor", "Update", ParameterCount = 0)]
    private static void ConductorUpdate(nint instance, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        bool bridgeFrame = false;
        if (_mobileAsyncBridgeHooksInstalled && plugin != null)
        {
            try
            {
                bridgeFrame = plugin.BeginMobileAsyncFrame(instance);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Mobile async frame setup failed: {exception}");
            }
        }

        Volatile.Write(ref _mobileAsyncFrameProcessed, 0);
        Volatile.Write(ref _mobileAsyncFrameActive, bridgeFrame ? 1 : 0);
        bool completed = false;
        try
        {
            ConductorUpdateOriginal(instance, methodInfo);
            completed = true;
        }
        finally
        {
            // PlayerControl_Update runs later in the same Unity frame. Keep
            // async active only after UpdateOffsetTime has completed one
            // timestamp-aware ProcessKeyInputs update inside this conductor
            // call, matching the game's desktop async ordering.
            bool keepAsyncFrame = completed
                                  && bridgeFrame
                                  && Volatile.Read(ref _mobileAsyncFrameProcessed) != 0;
            Volatile.Write(ref _mobileAsyncFrameActive, keepAsyncFrame ? 1 : 0);
        }
    }

    /// <summary>
    /// Preserve the game's own offset calculation, then pair its conductor DSP
    /// sample with the same monotonic-clock window for raw event conversion.
    /// </summary>
    [UnmanagedHook("Assembly-CSharp.dll", "AsyncInputUtils", "UpdateOffsetTime", ParameterCount = 1)]
    private static void AsyncInputUtilsUpdateOffsetTime(long fixDivider, nint methodInfo)
    {
        AsyncInputUtilsUpdateOffsetTimeOriginal(fixDivider, methodInfo);
        AsyncInputPlugin? plugin = _plugin;
        try
        {
            plugin?.ObserveMobileAsyncClock();

            // This is the same point at which the game has just established
            // currFrameTick and offsetTick, immediately before its own
            // UpdateInput call. Dispatch here rather than after the complete
            // conductor update so the event never inherits later frame work.
            if (_mobileAsyncBridgeHooksInstalled
                && Volatile.Read(ref _mobileAsyncFrameActive) != 0
                && Volatile.Read(ref _mobileAsyncFrameProcessed) == 0
                && plugin?.ProcessMobileAsyncFrame() == true)
            {
                Volatile.Write(ref _mobileAsyncFrameProcessed, 1);
            }
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Mobile async clock/input processing failed: {exception}");
        }
    }

    /// <summary>
    /// The mobile build leaves AsyncInputManager inactive because it has no
    /// SkyHook producer. It is active only for a bridge-owned conductor frame.
    /// </summary>
    [UnmanagedHook("Assembly-CSharp.dll", "AsyncInputManager", "get_isActive", ParameterCount = 0)]
    private static byte AsyncInputIsActive(nint methodInfo)
    {
        if (Volatile.Read(ref _mobileAsyncFrameActive) != 0)
            return 1;
        return AsyncInputIsActiveOriginal(methodInfo);
    }

    /// <summary>
    /// Mobile player methods normally read Unity Touch directly. During one
    /// controlled ProcessKeyInputs call, make them consume the async masks that
    /// were built from the corresponding Android timestamped edge instead.
    /// </summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "get_touchEnabled", ParameterCount = 0)]
    private static byte PlayerTouchEnabled(nint instance, nint methodInfo)
    {
        if (Volatile.Read(ref _mobileAsyncInputDispatching) != 0)
            return 0;
        return PlayerTouchEnabledOriginal(instance, methodInfo);
    }

    /// <summary>
    /// Mobile's original trigger check bypasses RDInput entirely, even when
    /// touchEnabled is false. The count method itself does use RDInput, so only
    /// supply the missing trigger and let HitAutoFloors retain all of the
    /// game's normal key-count bookkeeping.
    /// </summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "ValidInputWasTriggered", ParameterCount = 0)]
    private static byte ValidInputWasTriggered(nint instance, nint methodInfo)
    {
        if (Volatile.Read(ref _mobileAsyncInputDispatching) != 0)
        {
            return Volatile.Read(ref _mobileAsyncPressCount) > 0 ? (byte)1 : (byte)0;
        }
        return ValidInputWasTriggeredOriginal(instance, methodInfo);
    }

    /// <summary>
    /// 判定前的角度修正 —— 本 Mod 的核心。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在原函数执行前把行星角度改成按硬件时间戳算出的值，随后的判定完全走游戏原有逻辑，
    /// 只是「此刻角度」变得更准确。语义等价于 PC 版判定前调用的
    /// <c>AsyncInputUtils.AdjustAngle</c>。
    /// </para>
    /// <para>
    /// 选 <c>UpdateHoldKeys</c> 而不是 <c>CountValidKeysPressed</c> 或 <c>Hit</c>：
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>CountValidKeysPressed</c> 一次按下会被调用两次（<c>ValidInputWasTriggered</c>
    /// 内部一次、调用方再一次），且它只负责「数」有几个键 —— N 押时它一次返回 N，
    /// 游戏据此往 <c>keyTimes</c> 推 N 个条目，后续会产生 N 次独立判定。
    /// 在这里修正角度，N 押只能改到第一次，其余 N-1 次仍用旧角度。
    /// </item>
    /// <item>
    /// <c>UpdateHoldKeys</c> 普通路径每次从 <c>keyTimes</c> 取走一项并驱动一次
    /// <c>Hit</c>，因此每次消费一个时间戳正好；midspin 的合成边界会保留原版路径。
    /// </item>
    /// <item>
    /// <c>scrPlayer.Hit</c> 语义上最贴切，但已被 Replay 占用，Dobby 不允许同址二次 Hook。
    /// </item>
    /// </list>
    /// </remarks>
    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "UpdateHoldKeys", ParameterCount = 1)]
    private static void UpdateHoldKeys(nint instance, NullableTick targetTick, nint methodInfo)
    {
        if (IsMobileAsyncInputDispatching)
        {
            UpdateHoldKeysOriginal(instance, targetTick, methodInfo);
            return;
        }

        AsyncInputPlugin? plugin = _plugin;
        long pressNanos = 0L;
        TouchTimestampInfo press = default;
        int pendingBefore = 0;
        bool midspinBefore = false;
        bool midspinFloorBefore = false;
        JudgmentAngleProjection projection = default;
        if (plugin != null)
        {
            try
            {
                // ValidInputWasReleased has already projected a matching Up for
                // this frame's hold-only checks. A new Down must start from the
                // ordinary frame angle instead of inheriting that Up projection.
                plugin.EndHoldReleaseJudgmentWindow(instance);
                midspinBefore = plugin.HasMidspinInfiniteMargin(instance);
                midspinFloorBefore = plugin.IsCurrentFloorMidSpin(instance);
                // A non-empty target tick belongs to the game's own async/replay
                // invocation. Keep that timestamp path authoritative.
                plugin.AdjustAngleForHit(
                    instance,
                    targetTick.HasValue != 0 || midspinBefore,
                    out pressNanos,
                    out press,
                    out pendingBefore,
                    out projection);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Angle adjustment failed: {exception}");
            }
        }
        try
        {
            // Preserve the game's exception and return behavior. The async
            // bookkeeping belongs in finally and must never mask a game fault.
            UpdateHoldKeysOriginal(instance, targetTick, methodInfo);
        }
        finally
        {
            try
            {
                plugin?.CompleteUpdateHoldKeys(
                    instance,
                    pressNanos,
                    press,
                    pendingBefore,
                    midspinBefore,
                    midspinFloorBefore);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Hold start tracking failed: {exception}");
            }
            finally
            {
                try
                {
                    plugin?.RestoreJudgmentProjection(projection);
                }
                catch (Exception exception)
                {
                    Logger.Error(LogTag, $"Angle restoration failed: {exception}");
                }
            }
        }
    }

    /// <summary>记录本次原版触摸采样新增的 keyTimes 来源。</summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "HitAutoFloors", ParameterCount = 1)]
    private static void HitAutoFloors(nint instance, NullableTick targetTick, nint methodInfo)
    {
        if (IsMobileAsyncInputDispatching)
        {
            HitAutoFloorsOriginal(instance, targetTick, methodInfo);
            return;
        }

        AsyncInputPlugin? plugin = _plugin;
        int pendingBefore = 0;
        if (plugin != null)
        {
            try { pendingBefore = plugin.GetPendingKeyCount(instance); }
            catch (Exception exception) { Logger.Error(LogTag, $"HitAutoFloors precheck failed: {exception}"); }
        }

        HitAutoFloorsOriginal(instance, targetTick, methodInfo);

        try
        {
            plugin?.ObserveHitAutoFloors(instance, pendingBefore);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"HitAutoFloors source tracking failed: {exception}");
        }
    }

    /// <summary>释放边界。原函数仍负责确认该 Up 是否真的释放了游戏持有键。</summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "ValidInputWasReleased", ParameterCount = 0)]
    private static byte ValidInputWasReleased(nint instance, nint methodInfo)
    {
        if (IsMobileAsyncInputDispatching)
            return ValidInputWasReleasedOriginal(instance, methodInfo);

        AsyncInputPlugin? plugin = _plugin;
        bool wasHolding = false;
        try
        {
            wasHolding = plugin?.IsHolding(instance) == true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Hold release precheck failed: {exception}");
        }

        byte result = ValidInputWasReleasedOriginal(instance, methodInfo);
        try
        {
            plugin?.OnValidInputWasReleased(instance, wasHolding, result != 0);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Hold release timestamp adjustment failed: {exception}");
        }
        return result;
    }

    /// <summary>同一帧所有判定完成后撤销 Up 投影并核对输入来源队列。</summary>
    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "scrPlayer",
        "Simulated_PlayerControl_Update",
        ParameterCount = 1)]
    private static void SimulatedPlayerControlUpdate(
        nint instance,
        NullableTick targetTick,
        nint methodInfo)
    {
        if (IsMobileAsyncInputDispatching)
        {
            SimulatedPlayerControlUpdateOriginal(instance, targetTick, methodInfo);
            return;
        }

        try
        {
            SimulatedPlayerControlUpdateOriginal(instance, targetTick, methodInfo);
        }
        finally
        {
            try
            {
                _plugin?.EndHoldReleaseFrame(instance);
                _plugin?.CompletePlayerControlFrame(instance);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Player-control frame cleanup failed: {exception}");
            }
        }
    }

    /// <summary>
    /// <c>System.Nullable&lt;ulong&gt;</c> 的原生布局。
    /// 这是 PC 异步输入用来传递事件 tick 的参数，手机端恒为空值，
    /// 但必须按值正确接收并原样透传，否则调用约定会错位。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NullableTick
    {
        public ulong Value;
        public byte HasValue;
    }

    /// <summary>进入可操作状态。音频时钟此时刚起步，必须重建基准。</summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrController", "PlayerControl_Enter", ParameterCount = 0)]
    private static void PlayerControlEnter(nint instance, nint methodInfo)
    {
        PlayerControlEnterOriginal(instance, methodInfo);

        AsyncInputPlugin? plugin = _plugin;
        if (plugin == null)
            return;
        try
        {
            plugin.ResetClock();
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Clock reset failed: {exception}");
        }
    }

    /// <summary>暂停期间不能让菜单和系统触摸进入歌曲队列，恢复后重新建立时钟锚点。</summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrController", "set_paused", ParameterCount = 1)]
    private static void SetPaused(nint instance, byte value, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        if (value != 0)
        {
            try { plugin?.ResetClock(capture: false); }
            catch (Exception exception) { Logger.Error(LogTag, $"Pause clock reset failed: {exception}"); }
        }

        SetPausedOriginal(instance, value, methodInfo);

        if (value == 0)
        {
            try { plugin?.ResetClock(); }
            catch (Exception exception) { Logger.Error(LogTag, $"Resume clock reset failed: {exception}"); }
        }
    }

    /// <summary>延迟校准页记录样本前，按触摸硬件时间戳重算采样角度。</summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scnCalibration", "PutDataPoint", ParameterCount = 0)]
    private static void CalibrationPutDataPoint(nint instance, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        if (plugin != null)
        {
            try
            {
                plugin.AdjustCalibrationForInput(instance);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Calibration timestamp adjustment failed: {exception}");
            }
        }

        CalibrationPutDataPointOriginal(instance, methodInfo);
    }
}
