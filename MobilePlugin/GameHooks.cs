using System.Runtime.InteropServices;
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
    private static int _officialFrameActive;
    private static InputHookMode _inputHookMode;

    private enum InputHookMode
    {
        None,
        OfficialProcessKeyInputs,
        HitFallback,
        UpdateHoldKeysFallback,
    }

    /// <summary>当前实际使用的输入注入入口，供调试 HUD 和日志使用。</summary>
    internal static string InputHookName => _inputHookMode switch
    {
        InputHookMode.OfficialProcessKeyInputs =>
            "scrController.ProcessKeyInputs (PlayerControl_Update driver)",
        InputHookMode.HitFallback => "scrPlayer.Hit",
        InputHookMode.UpdateHoldKeysFallback => "scrPlayer.UpdateHoldKeys",
        _ => "none",
    };

    /// <summary>本 Mod 真正安装成功的 Hook 的卸载动作。</summary>
    /// <remarks>
    /// Source Generator 会在调用 HookHelper.Hook 之前写入 origPtr。若地址已被其他 Mod
    /// 占用，失败的 Install 仍可能留下目标地址；因此卸载列表只登记成功项，失败项必须
    /// 只清空生成器状态，绝不能调用 Unhook 拆掉其他 Mod 的 Hook。
    /// </remarks>
    private static readonly List<Action> InstalledHooks = new();

    /// <summary>核心输入注入点是否就绪。</summary>
    internal static bool InputHookAvailable { get; private set; }

    internal static bool Install(AsyncInputPlugin plugin)
    {
        try
        {
            Uninstall();
            _plugin = plugin;

            // 首选官方管线：目标 Android 版本的 UpdateInput 是短 ret stub，不能
            // 做 inline hook。官方稳定路线是拦截 UpdateOffsetTime 建立帧时钟，
            // 再从 PlayerControl_Update 驱动 ProcessKeyInputs。
            if (plugin.CanUseOfficialAsyncReplay)
            {
                bool clockInstalled = TryInstall(
                    "AsyncInputUtils.UpdateOffsetTime",
                    Install_AsyncInputUtilsUpdateOffsetTime,
                    Uninstall_AsyncInputUtilsUpdateOffsetTime,
                    Abandon_AsyncInputUtilsUpdateOffsetTime);
                bool playerControlInstalled = clockInstalled && TryInstall(
                    "scrController.PlayerControl_Update",
                    Install_PlayerControlUpdate,
                    Uninstall_PlayerControlUpdate,
                    Abandon_PlayerControlUpdate);
                bool activeInstalled = playerControlInstalled && TryInstall(
                    "AsyncInputManager.get_isActive",
                    Install_AsyncInputIsActive,
                    Uninstall_AsyncInputIsActive,
                    Abandon_AsyncInputIsActive);

                if (clockInstalled && playerControlInstalled && activeInstalled)
                {
                    _inputHookMode = InputHookMode.OfficialProcessKeyInputs;
                }
                else
                {
                    Uninstall();
                    _plugin = plugin;
                }
            }

            // 兼容旧版本或官方入口被占用时，优先选择真实 Hit 入口。
            if (_inputHookMode == InputHookMode.None)
            {
                bool hitInstalled = TryInstall(
                    "scrPlayer.Hit",
                    Install_PlayerHit,
                    Uninstall_PlayerHit,
                    Abandon_PlayerHit);
                if (hitInstalled)
                {
                    _inputHookMode = InputHookMode.HitFallback;
                }
                else
                {
                    bool updateHoldKeysInstalled = TryInstall(
                        "scrPlayer.UpdateHoldKeys",
                        Install_UpdateHoldKeys,
                        Uninstall_UpdateHoldKeys,
                        Abandon_UpdateHoldKeys);
                    if (updateHoldKeysInstalled)
                        _inputHookMode = InputHookMode.UpdateHoldKeysFallback;
                }
            }

            if (_inputHookMode == InputHookMode.None)
            {
                Logger.Error(LogTag, "No usable input hook could be installed");
                Uninstall();
                return false;
            }

            InputHookAvailable = true;

            // 进入可操作状态、暂停/恢复都需要清空旧事件并重建时间轴。
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

            Logger.Info(
                LogTag,
                $"Installed {_installed} IL2CPP hooks ({_failed} unavailable), input hook: {InputHookName}");
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Hook installation failed: {exception}");
            Uninstall();
            return false;
        }
    }

    private static bool TryInstall(
        string name,
        Func<bool> install,
        Action uninstall,
        Action? abandon)
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
            abandon?.Invoke();
            _failed++;
            Logger.Warn(LogTag, $"Hook {name} could not be installed (likely already hooked by another mod)");
        }
        return ok;
    }

    internal static void Uninstall()
    {
        Volatile.Write(ref _officialFrameActive, 0);
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
        _inputHookMode = InputHookMode.None;
        InputHookAvailable = false;
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

    private static void Uninstall_PlayerControlUpdate()
    {
        if (_PlayerControlUpdate_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_PlayerControlUpdate_origPtr);
        _PlayerControlUpdate_origPtr = nint.Zero;
        _PlayerControlUpdate_orig = null;
        _PlayerControlUpdate_wrap = null;
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

    private static void Uninstall_PlayerHit()
    {
        if (_PlayerHit_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_PlayerHit_origPtr);
        _PlayerHit_origPtr = nint.Zero;
        _PlayerHit_orig = null;
        _PlayerHit_wrap = null;
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

    private static void Uninstall_PlayerControlEnter()
    {
        if (_PlayerControlEnter_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_PlayerControlEnter_origPtr);
        _PlayerControlEnter_origPtr = nint.Zero;
        _PlayerControlEnter_orig = null;
        _PlayerControlEnter_wrap = null;
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

    private static void Uninstall_CalibrationPutDataPoint()
    {
        if (_CalibrationPutDataPoint_origPtr == nint.Zero)
            return;
        HookHelper.Unhook(_CalibrationPutDataPoint_origPtr);
        _CalibrationPutDataPoint_origPtr = nint.Zero;
        _CalibrationPutDataPoint_orig = null;
        _CalibrationPutDataPoint_wrap = null;
    }

    // 失败安装只清空 Source Generator 的持久字段，不调用 Unhook。
    private static void Abandon_AsyncInputUtilsUpdateOffsetTime()
    {
        _AsyncInputUtilsUpdateOffsetTime_origPtr = nint.Zero;
        _AsyncInputUtilsUpdateOffsetTime_orig = null;
        _AsyncInputUtilsUpdateOffsetTime_wrap = null;
    }

    private static void Abandon_PlayerControlUpdate()
    {
        _PlayerControlUpdate_origPtr = nint.Zero;
        _PlayerControlUpdate_orig = null;
        _PlayerControlUpdate_wrap = null;
    }

    private static void Abandon_AsyncInputIsActive()
    {
        _AsyncInputIsActive_origPtr = nint.Zero;
        _AsyncInputIsActive_orig = null;
        _AsyncInputIsActive_wrap = null;
    }

    private static void Abandon_PlayerHit()
    {
        _PlayerHit_origPtr = nint.Zero;
        _PlayerHit_orig = null;
        _PlayerHit_wrap = null;
    }

    private static void Abandon_UpdateHoldKeys()
    {
        _UpdateHoldKeys_origPtr = nint.Zero;
        _UpdateHoldKeys_orig = null;
        _UpdateHoldKeys_wrap = null;
    }

    private static void Abandon_PlayerControlEnter()
    {
        _PlayerControlEnter_origPtr = nint.Zero;
        _PlayerControlEnter_orig = null;
        _PlayerControlEnter_wrap = null;
    }

    private static void Abandon_SetPaused()
    {
        _SetPaused_origPtr = nint.Zero;
        _SetPaused_orig = null;
        _SetPaused_wrap = null;
    }

    private static void Abandon_CalibrationPutDataPoint()
    {
        _CalibrationPutDataPoint_origPtr = nint.Zero;
        _CalibrationPutDataPoint_orig = null;
        _CalibrationPutDataPoint_wrap = null;
    }

    /// <summary>
    /// 官方时钟入口。UpdateOffsetTime 的原始实现会按普通帧路径刷新 async offset；
    /// gameplay 捕获开启后由 Mod 用 realtime/DSP 同步结果替代，避免依赖 UpdateInput
    /// 这个短 stub。
    /// </summary>
    [UnmanagedHook("Assembly-CSharp.dll", "AsyncInputUtils", "UpdateOffsetTime", ParameterCount = 1)]
    private static void AsyncInputUtilsUpdateOffsetTime(long fixDivider, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        bool handled = false;
        if (_inputHookMode == InputHookMode.OfficialProcessKeyInputs && plugin != null)
        {
            try
            {
                handled = plugin.UpdateOfficialClock(fixDivider);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Official offset update failed: {exception}");
            }
        }

        if (!handled)
            AsyncInputUtilsUpdateOffsetTimeOriginal(fixDivider, methodInfo);
    }

    /// <summary>
    /// 稳定的官方消费驱动。目标 Android 版本的 UpdateInput 是 4 字节 ret，不能
    /// inline hook；PlayerControl_Update 是完整方法，并且官方原始状态机仍在其后运行。
    /// </summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrController", "PlayerControl_Update", ParameterCount = 0)]
    private static void PlayerControlUpdate(nint instance, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        bool officialFrame = false;
        if (_inputHookMode == InputHookMode.OfficialProcessKeyInputs && plugin != null)
        {
            try
            {
                officialFrame = plugin.BeginOfficialPlayerControlFrame(instance);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Official PlayerControl frame failed: {exception}");
            }
        }

        Volatile.Write(ref _officialFrameActive, officialFrame ? 1 : 0);
        try
        {
            PlayerControlUpdateOriginal(instance, methodInfo);
        }
        finally
        {
            if (officialFrame)
            {
                try
                {
                    plugin?.RestoreOfficialFrameAngle(instance);
                }
                catch (Exception exception)
                {
                    Logger.Error(LogTag, $"Official frame angle restore failed: {exception}");
                }

                try
                {
                    plugin?.EndOfficialPlayerControlFrame();
                }
                catch (Exception exception)
                {
                    Logger.Error(LogTag, $"Official PlayerControl cleanup failed: {exception}");
                }
            }
            Volatile.Write(ref _officialFrameActive, 0);
        }
    }

    /// <summary>在官方 PlayerControl_Update 调用期间让游戏读取 AsyncInput mask。</summary>
    [UnmanagedHook("Assembly-CSharp.dll", "AsyncInputManager", "get_isActive", ParameterCount = 0)]
    private static byte AsyncInputIsActive(nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        if (!ReplayCompatibility.IsPlaybackActive
            && (Volatile.Read(ref _officialFrameActive) != 0
                || plugin?.ShouldReportOfficialActive == true))
            return 1;
        return AsyncInputIsActiveOriginal(methodInfo);
    }

    /// <summary>旧版本兼容：真实 Hit 入口是比逐帧采样更精确的回退点。</summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "Hit", ParameterCount = 1)]
    private static byte PlayerHit(nint instance, byte autoHit, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        if (autoHit == 0 && plugin != null)
        {
            try
            {
                plugin.AdjustAngleForHit(instance, requirePendingKey: false);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Hit angle adjustment failed: {exception}");
            }
        }
        return PlayerHitOriginal(instance, autoHit, methodInfo);
    }

    /// <summary>最终兼容回退：在原版逐帧 UpdateHoldKeys 前修正当前一笔输入角度。</summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "UpdateHoldKeys", ParameterCount = 1)]
    private static void UpdateHoldKeys(nint instance, NullableTick targetTick, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        if (plugin != null)
        {
            try
            {
                plugin.AdjustAngleForHit(instance, requirePendingKey: true);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"UpdateHoldKeys angle adjustment failed: {exception}");
            }
        }
        UpdateHoldKeysOriginal(instance, targetTick, methodInfo);
    }

    /// <summary><c>System.Nullable&lt;ulong&gt;</c> 的原生布局，手机端通常为空值。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NullableTick
    {
        public ulong Value;
        public byte HasValue;
    }

    /// <summary>进入可操作状态后重建时钟并打开触摸捕获。</summary>
    [UnmanagedHook("Assembly-CSharp.dll", "scrController", "PlayerControl_Enter", ParameterCount = 0)]
    private static void PlayerControlEnter(nint instance, nint methodInfo)
    {
        PlayerControlEnterOriginal(instance, methodInfo);

        try
        {
            _plugin?.ResetClock(capture: true);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Clock reset failed: {exception}");
        }
    }

    /// <summary>暂停前关闭输入捕获，恢复后重新建立 wall/DSP 时间基准。</summary>
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
            try { plugin?.ResetClock(capture: true); }
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
