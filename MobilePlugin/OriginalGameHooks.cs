using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace AsyncInput.Mobile;

/// <summary>
/// Hooks only the switches and entry points needed to make the APK's existing
/// async-input consumer usable. No hook here replaces ProcessKeyInputs or
/// maintains an alternate input mask.
/// </summary>
internal static partial class OriginalGameHooks
{
    private const string LogTag = "AsyncInput";

    private static AsyncInputPlugin? _plugin;
    private static int _dispatching;
    private static int _installed;
    private static int _failed;

    private static readonly List<Action> InstalledHooks = new();

    internal static bool IsOriginalInputDispatching
        => Volatile.Read(ref _dispatching) != 0;

    internal static bool Install(AsyncInputPlugin plugin)
    {
        Uninstall();
        _plugin = plugin;

        bool ok = true;
        ok &= TryInstall(
            "AsyncInputManager.get_isActive",
            Install_OriginalAsyncInputIsActive,
            Uninstall_OriginalAsyncInputIsActive,
            Abandon_OriginalAsyncInputIsActive);
        ok &= TryInstall(
            "AsyncInputManager.ToggleHook",
            Install_OriginalAsyncInputToggleHook,
            Uninstall_OriginalAsyncInputToggleHook,
            Abandon_OriginalAsyncInputToggleHook);
        ok &= TryInstall(
            "AsyncInputManager.Update",
            Install_OriginalAsyncInputManagerUpdate,
            Uninstall_OriginalAsyncInputManagerUpdate,
            Abandon_OriginalAsyncInputManagerUpdate);
        ok &= TryInstall(
            "AsyncInputUtils.UpdateOffsetTime",
            Install_OriginalAsyncInputUtilsUpdateOffsetTime,
            Uninstall_OriginalAsyncInputUtilsUpdateOffsetTime,
            Abandon_OriginalAsyncInputUtilsUpdateOffsetTime);
        ok &= TryInstall(
            "scrController.UpdateInput",
            Install_OriginalUpdateInput,
            Uninstall_OriginalUpdateInput,
            Abandon_OriginalUpdateInput);
        ok &= TryInstall(
            "scrPlayer.get_touchEnabled",
            Install_OriginalPlayerTouchEnabled,
            Uninstall_OriginalPlayerTouchEnabled,
            Abandon_OriginalPlayerTouchEnabled);
        ok &= TryInstall(
            "scrPlayer.ValidInputWasTriggered",
            Install_OriginalValidInputWasTriggered,
            Uninstall_OriginalValidInputWasTriggered,
            Abandon_OriginalValidInputWasTriggered);
        ok &= TryInstall(
            "scrController.PlayerControl_Enter",
            Install_OriginalPlayerControlEnter,
            Uninstall_OriginalPlayerControlEnter,
            Abandon_OriginalPlayerControlEnter);
        ok &= TryInstall(
            "scrController.set_paused",
            Install_OriginalSetPaused,
            Uninstall_OriginalSetPaused,
            Abandon_OriginalSetPaused);

        // The APK keeps the PC UpdateSetting case but filters the actual
        // button out of PauseMenuSettings on Android. This optional hook only
        // appends that native button; failure must not disable gameplay input.
        _ = TryInstall(
            "SettingsMenu.GenerateSettings",
            Install_OriginalSettingsMenuGenerateSettings,
            Uninstall_OriginalSettingsMenuGenerateSettings,
            Abandon_OriginalSettingsMenuGenerateSettings);
        _ = TryInstall(
            "SettingsMenu.UpdateSetting",
            Install_OriginalSettingsMenuUpdateSetting,
            Uninstall_OriginalSettingsMenuUpdateSetting,
            Abandon_OriginalSettingsMenuUpdateSetting);
        _ = TryInstall(
            "PauseSettingButton.GetDescriptionText",
            Install_OriginalPauseSettingGetDescriptionText,
            Uninstall_OriginalPauseSettingGetDescriptionText,
            Abandon_OriginalPauseSettingGetDescriptionText);

        if (!ok)
        {
            Logger.Error(LogTag,
                "The original async consumer hooks are incomplete; leaving the game's normal touch path active");
            Uninstall();
            return false;
        }

        Logger.Info(LogTag, $"Installed {_installed} original async hooks ({_failed} unavailable)");
        return true;
    }

    internal static void Uninstall()
    {
        Volatile.Write(ref _dispatching, 0);
        for (int i = InstalledHooks.Count - 1; i >= 0; i--)
        {
            try
            {
                InstalledHooks[i]();
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Original async hook removal failed: {exception}");
            }
        }

        InstalledHooks.Clear();
        _plugin = null;
        _installed = 0;
        _failed = 0;
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
            // Generated hook state is populated before HookHelper.Hook
            // reports failure. Do not later unhook an address owned by another
            // mod.
            abandon();
            _failed++;
            Logger.Warn(LogTag, $"Hook {name} could not be installed");
        }

        return ok;
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "AsyncInputManager",
        "get_isActive",
        ParameterCount = 0)]
    private static byte OriginalAsyncInputIsActive(nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        if (plugin != null)
            return plugin.ShouldExposeOriginalAsyncInput ? (byte)1 : (byte)0;

        return OriginalAsyncInputIsActiveOriginal(methodInfo);
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "AsyncInputManager",
        "ToggleHook",
        ParameterCount = 1)]
    private static void OriginalAsyncInputToggleHook(byte active, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        if (plugin != null)
        {
            // The APK's SkyHook native library is absent. Let the mod own
            // this logical switch without entering the missing native DLL.
            plugin.ObserveOriginalAsyncToggle(active != 0);
            return;
        }

        OriginalAsyncInputToggleHookOriginal(active, methodInfo);
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "AsyncInputManager",
        "Update",
        ParameterCount = 0)]
    private static void OriginalAsyncInputManagerUpdate(nint instance, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        if (plugin == null)
        {
            OriginalAsyncInputManagerUpdateOriginal(instance, methodInfo);
            return;
        }

        try
        {
            // The stock Update would consult the desktop persistence flag and
            // then touch SkyHook-dependent state. Keep RDInput source
            // activation aligned with the Mod's producer;
            // UpdateInput itself remains the APK's queue consumer.
            plugin.MaintainOriginalAsyncManagerState();
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Original async manager update failed: {exception}");
        }
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "AsyncInputUtils",
        "UpdateOffsetTime",
        ParameterCount = 1)]
    private static void OriginalAsyncInputUtilsUpdateOffsetTime(
        long fixDivider,
        nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        try
        {
            plugin?.PrepareOriginalAsyncOffset(fixDivider);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Original async clock preparation failed: {exception}");
        }

        try
        {
            // Keep the APK's own offset calculation and only correct its
            // precise frame sample after it returns.
            OriginalAsyncInputUtilsUpdateOffsetTimeOriginal(fixDivider, methodInfo);
        }
        finally
        {
            try
            {
                plugin?.CompleteOriginalAsyncOffset();
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Original async offset correction failed: {exception}");
            }
        }
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "scrController",
        "UpdateInput",
        ParameterCount = 0)]
    private static void OriginalUpdateInput(nint instance, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        bool dispatching = false;
        try
        {
            dispatching = plugin?.PrepareOriginalInputUpdate(instance) == true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Original async producer preparation failed: {exception}");
        }

        if (dispatching)
            Volatile.Write(ref _dispatching, 1);

        try
        {
            // The APK's own UpdateInput drains keyQueue, sorts its events,
            // updates the six async masks, and calls ProcessKeyInputs.
            OriginalUpdateInputOriginal(instance, methodInfo);
        }
        finally
        {
            if (dispatching)
                Volatile.Write(ref _dispatching, 0);
        }
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "scrPlayer",
        "get_touchEnabled",
        ParameterCount = 0)]
    private static byte OriginalPlayerTouchEnabled(nint instance, nint methodInfo)
    {
        if (IsOriginalInputDispatching)
            return 0;
        return OriginalPlayerTouchEnabledOriginal(instance, methodInfo);
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "scrPlayer",
        "ValidInputWasTriggered",
        ParameterCount = 0)]
    private static byte OriginalValidInputWasTriggered(nint instance, nint methodInfo)
    {
        byte original = OriginalValidInputWasTriggeredOriginal(instance, methodInfo);
        if (original != 0 || !IsOriginalInputDispatching)
            return original;

        // Mobile's original method gates the final count behind
        // Input.anyKeyDown, which does not see a SkyHook queue event. The
        // count itself is still obtained from the game's RDInput implementation.
        return _plugin?.GetOriginalAsyncPressCount() > 0 ? (byte)1 : (byte)0;
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "scrController",
        "PlayerControl_Enter",
        ParameterCount = 0)]
    private static void OriginalPlayerControlEnter(nint instance, nint methodInfo)
    {
        try
        {
            OriginalPlayerControlEnterOriginal(instance, methodInfo);
        }
        finally
        {
            try { _plugin?.ResetClock(); }
            catch (Exception exception) { Logger.Error(LogTag, $"PlayerControl clock reset failed: {exception}"); }
        }
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "scrController",
        "set_paused",
        ParameterCount = 1)]
    private static void OriginalSetPaused(nint instance, byte value, nint methodInfo)
    {
        AsyncInputPlugin? plugin = _plugin;
        if (value != 0)
        {
            try { plugin?.ResetClock(capture: false); }
            catch (Exception exception) { Logger.Error(LogTag, $"Pause clock reset failed: {exception}"); }
        }

        OriginalSetPausedOriginal(instance, value, methodInfo);

        if (value == 0)
        {
            try { plugin?.ResetClock(); }
            catch (Exception exception) { Logger.Error(LogTag, $"Resume clock reset failed: {exception}"); }
        }
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "SettingsMenu",
        "GenerateSettings",
        ParameterCount = 0)]
    private static void OriginalSettingsMenuGenerateSettings(nint instance, nint methodInfo)
    {
        OriginalSettingsMenuGenerateSettingsOriginal(instance, methodInfo);
        try
        {
            _plugin?.AddOriginalAsyncSetting(instance);
        }
        catch (Exception exception)
        {
            Logger.Warn(LogTag, $"Native async setting injection failed: {exception.Message}");
        }
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "SettingsMenu",
        "UpdateSetting",
        ParameterCount = 2)]
    private static void OriginalSettingsMenuUpdateSetting(
        nint instance,
        nint setting,
        int action,
        nint methodInfo)
    {
        OriginalSettingsMenuUpdateSettingOriginal(instance, setting, action, methodInfo);
        try
        {
            _plugin?.RestoreAsyncSettingDescription(instance, setting);
        }
        catch (Exception exception)
        {
            Logger.Warn(LogTag, $"Async setting description refresh failed: {exception.Message}");
        }
    }

    [UnmanagedHook(
        "Assembly-CSharp.dll",
        "PauseSettingButton",
        "GetDescriptionText",
        ParameterCount = 0)]
    private static nint OriginalPauseSettingGetDescriptionText(nint instance, nint methodInfo)
    {
        try
        {
            if (_plugin?.TryGetAsyncSettingDescription(instance, out nint description) == true)
                return description;
        }
        catch (Exception exception)
        {
            Logger.Warn(LogTag, $"Async setting description lookup failed: {exception.Message}");
        }

        return OriginalPauseSettingGetDescriptionTextOriginal(instance, methodInfo);
    }

    // ── generated hook cleanup ───────────────────────────────────────────

    private static void Uninstall_OriginalAsyncInputIsActive()
    {
        if (_OriginalAsyncInputIsActive_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalAsyncInputIsActive_origPtr);
        _OriginalAsyncInputIsActive_origPtr = nint.Zero;
        _OriginalAsyncInputIsActive_orig = null;
        _OriginalAsyncInputIsActive_wrap = null;
    }

    private static void Uninstall_OriginalAsyncInputToggleHook()
    {
        if (_OriginalAsyncInputToggleHook_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalAsyncInputToggleHook_origPtr);
        _OriginalAsyncInputToggleHook_origPtr = nint.Zero;
        _OriginalAsyncInputToggleHook_orig = null;
        _OriginalAsyncInputToggleHook_wrap = null;
    }

    private static void Uninstall_OriginalAsyncInputManagerUpdate()
    {
        if (_OriginalAsyncInputManagerUpdate_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalAsyncInputManagerUpdate_origPtr);
        _OriginalAsyncInputManagerUpdate_origPtr = nint.Zero;
        _OriginalAsyncInputManagerUpdate_orig = null;
        _OriginalAsyncInputManagerUpdate_wrap = null;
    }

    private static void Uninstall_OriginalAsyncInputUtilsUpdateOffsetTime()
    {
        if (_OriginalAsyncInputUtilsUpdateOffsetTime_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalAsyncInputUtilsUpdateOffsetTime_origPtr);
        _OriginalAsyncInputUtilsUpdateOffsetTime_origPtr = nint.Zero;
        _OriginalAsyncInputUtilsUpdateOffsetTime_orig = null;
        _OriginalAsyncInputUtilsUpdateOffsetTime_wrap = null;
    }

    private static void Uninstall_OriginalUpdateInput()
    {
        if (_OriginalUpdateInput_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalUpdateInput_origPtr);
        _OriginalUpdateInput_origPtr = nint.Zero;
        _OriginalUpdateInput_orig = null;
        _OriginalUpdateInput_wrap = null;
    }

    private static void Uninstall_OriginalPlayerTouchEnabled()
    {
        if (_OriginalPlayerTouchEnabled_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalPlayerTouchEnabled_origPtr);
        _OriginalPlayerTouchEnabled_origPtr = nint.Zero;
        _OriginalPlayerTouchEnabled_orig = null;
        _OriginalPlayerTouchEnabled_wrap = null;
    }

    private static void Uninstall_OriginalValidInputWasTriggered()
    {
        if (_OriginalValidInputWasTriggered_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalValidInputWasTriggered_origPtr);
        _OriginalValidInputWasTriggered_origPtr = nint.Zero;
        _OriginalValidInputWasTriggered_orig = null;
        _OriginalValidInputWasTriggered_wrap = null;
    }

    private static void Uninstall_OriginalPlayerControlEnter()
    {
        if (_OriginalPlayerControlEnter_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalPlayerControlEnter_origPtr);
        _OriginalPlayerControlEnter_origPtr = nint.Zero;
        _OriginalPlayerControlEnter_orig = null;
        _OriginalPlayerControlEnter_wrap = null;
    }

    private static void Uninstall_OriginalSetPaused()
    {
        if (_OriginalSetPaused_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalSetPaused_origPtr);
        _OriginalSetPaused_origPtr = nint.Zero;
        _OriginalSetPaused_orig = null;
        _OriginalSetPaused_wrap = null;
    }

    private static void Uninstall_OriginalSettingsMenuGenerateSettings()
    {
        if (_OriginalSettingsMenuGenerateSettings_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalSettingsMenuGenerateSettings_origPtr);
        _OriginalSettingsMenuGenerateSettings_origPtr = nint.Zero;
        _OriginalSettingsMenuGenerateSettings_orig = null;
        _OriginalSettingsMenuGenerateSettings_wrap = null;
    }

    private static void Uninstall_OriginalSettingsMenuUpdateSetting()
    {
        if (_OriginalSettingsMenuUpdateSetting_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalSettingsMenuUpdateSetting_origPtr);
        _OriginalSettingsMenuUpdateSetting_origPtr = nint.Zero;
        _OriginalSettingsMenuUpdateSetting_orig = null;
        _OriginalSettingsMenuUpdateSetting_wrap = null;
    }

    private static void Uninstall_OriginalPauseSettingGetDescriptionText()
    {
        if (_OriginalPauseSettingGetDescriptionText_origPtr == nint.Zero) return;
        HookHelper.Unhook(_OriginalPauseSettingGetDescriptionText_origPtr);
        _OriginalPauseSettingGetDescriptionText_origPtr = nint.Zero;
        _OriginalPauseSettingGetDescriptionText_orig = null;
        _OriginalPauseSettingGetDescriptionText_wrap = null;
    }

    private static void Abandon_OriginalAsyncInputIsActive()
    {
        _OriginalAsyncInputIsActive_origPtr = nint.Zero;
        _OriginalAsyncInputIsActive_orig = null;
        _OriginalAsyncInputIsActive_wrap = null;
    }

    private static void Abandon_OriginalAsyncInputToggleHook()
    {
        _OriginalAsyncInputToggleHook_origPtr = nint.Zero;
        _OriginalAsyncInputToggleHook_orig = null;
        _OriginalAsyncInputToggleHook_wrap = null;
    }

    private static void Abandon_OriginalAsyncInputManagerUpdate()
    {
        _OriginalAsyncInputManagerUpdate_origPtr = nint.Zero;
        _OriginalAsyncInputManagerUpdate_orig = null;
        _OriginalAsyncInputManagerUpdate_wrap = null;
    }

    private static void Abandon_OriginalAsyncInputUtilsUpdateOffsetTime()
    {
        _OriginalAsyncInputUtilsUpdateOffsetTime_origPtr = nint.Zero;
        _OriginalAsyncInputUtilsUpdateOffsetTime_orig = null;
        _OriginalAsyncInputUtilsUpdateOffsetTime_wrap = null;
    }

    private static void Abandon_OriginalUpdateInput()
    {
        _OriginalUpdateInput_origPtr = nint.Zero;
        _OriginalUpdateInput_orig = null;
        _OriginalUpdateInput_wrap = null;
    }

    private static void Abandon_OriginalPlayerTouchEnabled()
    {
        _OriginalPlayerTouchEnabled_origPtr = nint.Zero;
        _OriginalPlayerTouchEnabled_orig = null;
        _OriginalPlayerTouchEnabled_wrap = null;
    }

    private static void Abandon_OriginalValidInputWasTriggered()
    {
        _OriginalValidInputWasTriggered_origPtr = nint.Zero;
        _OriginalValidInputWasTriggered_orig = null;
        _OriginalValidInputWasTriggered_wrap = null;
    }

    private static void Abandon_OriginalPlayerControlEnter()
    {
        _OriginalPlayerControlEnter_origPtr = nint.Zero;
        _OriginalPlayerControlEnter_orig = null;
        _OriginalPlayerControlEnter_wrap = null;
    }

    private static void Abandon_OriginalSetPaused()
    {
        _OriginalSetPaused_origPtr = nint.Zero;
        _OriginalSetPaused_orig = null;
        _OriginalSetPaused_wrap = null;
    }

    private static void Abandon_OriginalSettingsMenuGenerateSettings()
    {
        _OriginalSettingsMenuGenerateSettings_origPtr = nint.Zero;
        _OriginalSettingsMenuGenerateSettings_orig = null;
        _OriginalSettingsMenuGenerateSettings_wrap = null;
    }

    private static void Abandon_OriginalSettingsMenuUpdateSetting()
    {
        _OriginalSettingsMenuUpdateSetting_origPtr = nint.Zero;
        _OriginalSettingsMenuUpdateSetting_orig = null;
        _OriginalSettingsMenuUpdateSetting_wrap = null;
    }

    private static void Abandon_OriginalPauseSettingGetDescriptionText()
    {
        _OriginalPauseSettingGetDescriptionText_origPtr = nint.Zero;
        _OriginalPauseSettingGetDescriptionText_orig = null;
        _OriginalPauseSettingGetDescriptionText_wrap = null;
    }
}
