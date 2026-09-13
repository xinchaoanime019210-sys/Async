using StArray.ModManager.RuntimeAbstractions;
using StArray.ModManager.Manager;
using StArray.ModManager.Il2Cpp;

namespace AsyncInput.Mobile;

/// <summary>
/// Adds the PC async-input setting to the APK's existing settings menu.
/// </summary>
/// <remarks>
/// The APK still contains the complete <c>SettingsMenu.UpdateSetting</c>
/// switch, including the PC <c>useAsynchronousInput</c> case. Its
/// <c>PauseMenuSettings</c> asset marks that item as desktop-only, so the
/// mobile menu never creates the button. This class only clones the already
/// initialized general-settings button and appends it to the original list;
/// activation, persistence, labels, and confirmation behavior remain in the
/// game's own SettingsMenu code.
/// </remarks>
internal sealed class SettingsMenuApi
{
    private const string AsyncSettingName = "useAsynchronousInput";
    private const string AsyncSettingType = "Bool";
    private const string AsyncDescriptionKey = "pauseMenu.settings.info.useAsynchronousInput";
    private const string AsyncSettingLabel = "异步输入（实验性）";
    private const string AsyncSettingDescription =
        "使用 Android 原生触摸时间戳接入游戏原版异步输入链路。\n"
        + "该功能仍处于实验阶段，可能与其他输入相关 Mod 冲突；遇到输入异常时请关闭此选项。";

    private readonly HashSet<nint> _addedMenus = new();

    internal void Reset() => _addedMenus.Clear();

    internal bool IsAsyncSetting(nint setting)
        => setting != 0 && GetUnityObjectName(setting) == AsyncSettingName;

    internal nint GetAsyncDescriptionPointer(GameApi game)
    {
        if (game == null)
            return 0;
        return RuntimeString.New(game.RuntimeDomain, AsyncSettingDescription).Ptr;
    }

    internal void RestoreAsyncDescription(GameApi game, nint settingsMenu, nint setting)
    {
        if (game == null || settingsMenu == 0 || !IsAsyncSetting(setting))
            return;

        IRuntimeClass? settingsClass = game.FindGameClassForMod("SettingsMenu");
        IRuntimeMethod? setDescription = settingsClass?.GetMethod(
            "SetDescription",
            new[] { "System.String" });
        RuntimeString description = RuntimeString.New(game.RuntimeDomain, AsyncSettingDescription);
        if (setDescription != null && description.Ptr != 0)
        {
            try { setDescription.Invoke(settingsMenu, new[] { description.Ptr }); }
            catch { }
        }
    }

    internal bool TryAdd(GameApi game, nint settingsMenu)
    {
        if (game == null || settingsMenu == 0 || _addedMenus.Contains(settingsMenu))
            return false;

        try
        {
            IRuntimeClass? settingsClass = game.FindGameClassForMod("SettingsMenu");
            IRuntimeClass? pauseSettingClass = game.FindGameClassForMod("PauseSettingButton");
            IRuntimeClass? unityObjectClass = game.FindRuntimeClassForMod("UnityEngine", "Object");
            if (settingsClass == null || pauseSettingClass == null || unityObjectClass == null)
                return false;

            IRuntimeField? settingsTabsField = settingsClass.GetField("settingsTabs");
            IRuntimeField? settingsContentField = settingsClass.GetField("settingsScrollRectContent");
            IRuntimeField? pauseTypeField = pauseSettingClass.GetField("type");
            IRuntimeField? pauseDescriptionField = pauseSettingClass.GetField("descriptionKey");
            IRuntimeField? pauseHasDescriptionField = pauseSettingClass.GetField("hasDescription");
            IRuntimeField? pauseDeferUpdateField = pauseSettingClass.GetField("deferUpdate");
            IRuntimeField? pauseLabelField = pauseSettingClass.GetField("label");
            IRuntimeMethod? instantiate = unityObjectClass.GetMethod(
                "Instantiate",
                new[] { "UnityEngine.Object", "UnityEngine.Transform" })
                ?? unityObjectClass.GetMethod("Instantiate", 2);
            IRuntimeMethod? updateSetting = settingsClass.GetMethod("UpdateSetting", 2);
            if (settingsTabsField == null
                || settingsContentField == null
                || pauseTypeField == null
                || pauseDescriptionField == null
                || pauseHasDescriptionField == null
                || pauseDeferUpdateField == null
                || pauseLabelField == null
                || instantiate == null
                || updateSetting == null)
            {
                return false;
            }

            nint settingsTabs = ReadReference(settingsTabsField, settingsMenu);
            nint parent = ReadReference(settingsContentField, settingsMenu);
            if (settingsTabs == 0 || parent == 0)
                return false;

            nint generalSettings = GetListItem(settingsTabs, 0);
            if (generalSettings == 0)
                return false;

            int existingCount = GetListCount(generalSettings);
            for (int i = 0; i < existingCount; i++)
            {
                nint existing = GetListItem(generalSettings, i);
                if (GetUnityObjectName(existing) == AsyncSettingName)
                {
                    _addedMenus.Add(settingsMenu);
                    return true;
                }
            }

            // Clone an already initialized Bool setting instead of the raw
            // prefab. This preserves serialized UI references and the
            // original PauseSettingButton.Awake listeners.
            nint template = GetListItem(generalSettings, 0);
            nint templateGameObject = GetGameObject(template);
            if (templateGameObject == 0)
                return false;

            nint clonedGameObject = instantiate.InvokeStatic(new[] { templateGameObject, parent });
            if (clonedGameObject == 0)
                return false;

            SetUnityObjectName(clonedGameObject, AsyncSettingName);
            nint setting = GetPauseSettingButton(game, clonedGameObject, pauseSettingClass);
            if (setting == 0)
                return false;

            SetRuntimeStringField(game, pauseTypeField, setting, AsyncSettingType);
            SetRuntimeStringField(game, pauseDescriptionField, setting, AsyncDescriptionKey);
            SetBoolField(pauseHasDescriptionField, setting, true);
            SetBoolField(pauseDeferUpdateField, setting, true);
            SetLocalizedLabel(game, pauseLabelField, setting);

            AddListItem(generalSettings, setting);

            int refresh = 0; // SettingsMenu.Interaction.Refresh
            updateSetting.Invoke(settingsMenu, new[] { setting, Arg(ref refresh) });
            _addedMenus.Add(settingsMenu);
            Logger.Info("AsyncInput", "Added native async-input setting to the mobile settings menu");
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warn("AsyncInput", $"Mobile async setting injection unavailable: {exception.Message}");
            return false;
        }
    }

    private static nint GetPauseSettingButton(
        GameApi game,
        nint gameObject,
        IRuntimeClass pauseSettingClass)
    {
        IRuntimeClass? gameObjectClass = RuntimeManager.GetObjectClass(gameObject);
        if (gameObjectClass == null)
            return 0;

        // IL2CPP's safe overload takes System.Type. Passing a managed string
        // to the one-argument overload is unsafe when the resolver selected
        // the Type overload, so prefer the runtime type object explicitly.
        if (pauseSettingClass is Il2CppClass il2CppPauseClass)
        {
            IRuntimeMethod? getComponent = gameObjectClass.GetMethod(
                "GetComponent",
                new[] { "System.Type" })
                ?? gameObjectClass.GetMethod("GetComponent", 1);
            nint typeObject = il2CppPauseClass.GetTypeObject();
            if (getComponent != null && typeObject != 0)
            {
                try
                {
                    nint component = getComponent.Invoke(gameObject, new[] { typeObject });
                    if (component != 0)
                        return component;
                }
                catch
                {
                }
            }
        }

        IRuntimeMethod? getComponentByName = gameObjectClass.GetMethod(
            "GetComponent",
            new[] { "System.String" });
        RuntimeString typeName = RuntimeString.New(game.RuntimeDomain, "PauseSettingButton");
        if (getComponentByName == null || typeName.Ptr == 0)
            return 0;

        try { return getComponentByName.Invoke(gameObject, new[] { typeName.Ptr }); }
        catch { return 0; }
    }

    private static nint GetGameObject(nint component)
    {
        if (component == 0)
            return 0;
        try { return new RuntimeObject(component).Invoke("get_gameObject", 0); }
        catch { return 0; }
    }

    private static nint GetListItem(nint list, int index)
    {
        if (list == 0 || index < 0)
            return 0;
        try
        {
            int value = index;
            return new RuntimeObject(list).Invoke("get_Item", 1, new[] { Arg(ref value) });
        }
        catch
        {
            return 0;
        }
    }

    private static int GetListCount(nint list)
    {
        if (list == 0)
            return 0;
        try { return Math.Max(0, new RuntimeObject(list).InvokeUnbox<int>("get_Count", 0)); }
        catch { return 0; }
    }

    private static void AddListItem(nint list, nint item)
    {
        if (list == 0 || item == 0)
            return;
        new RuntimeObject(list).Invoke("Add", 1, new[] { item });
    }

    private static string GetUnityObjectName(nint component)
    {
        nint gameObject = GetGameObject(component);
        if (gameObject == 0)
            return string.Empty;
        try
        {
            nint value = new RuntimeObject(gameObject).Invoke("get_name", 0);
            return value == 0 ? string.Empty : new RuntimeString(value).ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SetUnityObjectName(nint gameObject, string name)
    {
        if (gameObject == 0)
            return;
        try
        {
            RuntimeString value = RuntimeString.New(name);
            new RuntimeObject(gameObject).InvokeVoid("set_name", 1, new[] { value.Ptr });
        }
        catch
        {
        }
    }

    private static void SetRuntimeStringField(
        GameApi game,
        IRuntimeField field,
        nint instance,
        string value)
    {
        RuntimeString runtimeValue = RuntimeString.New(game.RuntimeDomain, value);
        if (runtimeValue.Ptr != 0)
            field.SetValue(instance, runtimeValue.Ptr);
    }

    private static void SetBoolField(IRuntimeField field, nint instance, bool value)
        => field.SetValue(instance, (byte)(value ? 1 : 0));

    private static void SetLocalizedLabel(GameApi game, IRuntimeField labelField, nint setting)
    {
        nint label = ReadReference(labelField, setting);
        if (label == 0)
            return;

        // This item is intentionally labeled in Chinese, independent of the
        // game's current language: it is the experimental mobile Mod switch,
        // not the desktop localization entry from PauseMenuSettings.
        nint text = RuntimeString.New(game.RuntimeDomain, AsyncSettingLabel).Ptr;

        IRuntimeClass? textClass = RuntimeManager.GetObjectClass(label);
        IRuntimeMethod? setText = textClass?.GetMethod("set_text", new[] { "System.String" });
        setText?.Invoke(label, new[] { text });
    }

    private static nint ReadReference(IRuntimeField field, nint instance)
    {
        try { return field.GetValue<nint>(instance); }
        catch { return 0; }
    }

    private static void SetBoolField(IRuntimeField field, nint instance, byte value)
        => field.SetValue(instance, value);

    private static nint Arg<T>(ref T value) where T : unmanaged
    {
        unsafe
        {
            fixed (T* pointer = &value)
                return (nint)pointer;
        }
    }
}
