using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.Input;
using ControlWinSwitch.Setup;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.App;

/// <summary>What the settings rows need from the app around them, beyond the services.</summary>
internal interface ISettingsApp
{
    /// <summary>Saves the config and brings the running parts in line with it.</summary>
    void SaveConfig();

    /// <summary>Reads config.json again and applies it.</summary>
    void ReloadConfig();

    /// <summary>Opens config.json in Notepad.</summary>
    void EditConfig();

    /// <summary>Opens log.txt in Notepad.</summary>
    void OpenLog();

    /// <summary>Opens the editor of the menu layout in use.</summary>
    void EditMenu();
}

/// <summary>
/// What the settings rows do (<see cref="ISettingsHost"/>): a façade over the services (pads, headset, pairing,
/// controller registry) and the settings screen's page navigation.
/// </summary>
internal sealed class SettingsHost : ISettingsHost
{
    private const uint RumbleMs = 400;

    private readonly SettingsMode _screen;
    private readonly ISettingsApp _app;
    private readonly ConfigStore _store;
    private readonly ControllerSet _set;
    private readonly Func<int, ControllerState> _stateOf;
    private readonly ControllerRegistry _registry;
    private readonly PadService _pads;
    private readonly HeadsetService _headset;
    private readonly PairingService _pairing;
    private readonly Toast _toast;
    private readonly UiTimer[] _rumbleTimers = new UiTimer[ControllerSet.Slots]; // ends the rumble test

    public SettingsHost(SettingsMode screen, ISettingsApp app, ConfigStore store, ControllerSet set, Func<int, ControllerState> stateOf,
        ControllerRegistry registry, PadService pads, HeadsetService headset, PairingService pairing, TimerService timers, Toast toast)
    {
        _screen = screen;
        _app = app;
        _store = store;
        _set = set;
        _stateOf = stateOf;
        _registry = registry;
        _pads = pads;
        _headset = headset;
        _pairing = pairing;
        _toast = toast;
        for (int i = 0; i < _rumbleTimers.Length; i++)
        {
            int slot = i;
            _rumbleTimers[slot] = timers.Create(() =>
            {
                _rumbleTimers[slot].Stop();
                _set.Source(slot).SetRumble(false);
            });
        }
    }

    private AppConfig Config => _store.Current;

    // ---- config and app ------------------------------------------------------------------------

    public string ConfigPath => _store.Path;

    public void ConfigChanged() => _app.SaveConfig();

    public void ReloadConfig() => _app.ReloadConfig();

    public void EditConfig() => _app.EditConfig();

    public void OpenLog() => _app.OpenLog();

    public void EditMenu() => _app.EditMenu();

    public void EditText(string current, Action<string> commit) => _screen.BeginEdit(current, commit);

    public void SetMenuHotkey(string text)
    {
        if (text.Trim().Length == 0) Config.MenuHotkey = "";
        else if (Hotkey.TryParse(text, out var hotkey)) Config.MenuHotkey = hotkey.ToString();
        else
        {
            _toast.Show(MenuPages.Gear, $"Unknown shortcut \"{text.Trim()}\" - try Ctrl+Alt+M");
            return;
        }
        ConfigChanged();
    }

    public bool StartOnLogon => Startup.IsEnabled;

    public void SetStartOnLogon(bool enabled)
    {
        string exe = Environment.ProcessPath ?? "";
        var mode = Startup.Set(enabled, exe, out var error);
        Log.Info($"Launch on startup: {(enabled ? "on" : "off")} -> {mode}{(error is null ? "" : " (" + error + ")")}");

        string message = error ?? (enabled
            ? mode == Startup.Mode.Task ? "ControlWinSwitch will start with Windows" : "ControlWinSwitch will start with Windows (without administrator rights)"
            : "ControlWinSwitch will no longer start with Windows");
        _toast.Show(MenuPages.Gear, message);
    }

    // ---- headset -------------------------------------------------------------------------------

    public string HeadsetStatus => _headset.Status;

    public IReadOnlyList<string> HeadsetDevices => _headset.Devices;

    public void ToggleHeadset() => _headset.Toggle(MenuPages.Headphone);

    public void OpenBluetoothSettings() => _screen.OpenBluetoothSettings();

    // ---- virtual pad ---------------------------------------------------------------------------

    public bool PadEnabled => _pads.Enabled;

    public void SetPad(bool enabled)
    {
        if (enabled == _pads.Enabled) return;
        _pads.UserSet(enabled);
    }

    public int PadMode => _pads.Mode;

    public void SetPadMode(int mode) => _pads.SetMode(mode);

    public void WatchListChanged() => _pads.WatchListChanged();

    // ---- connected controllers -----------------------------------------------------------------

    public bool ControllerConnected(int slot) => _set.Source(slot).IsOpen;

    public void RumbleController(int slot)
    {
        if (!_set.Source(slot).SetRumble(true)) return;
        _rumbleTimers[slot].Start(RumbleMs);
    }

    public float ControllerDeadzone(int slot, bool right) => _registry.StickDeadzone(slot, right);

    public void SetControllerDeadzone(int slot, bool right, float value) => _registry.SetStickDeadzone(slot, right, value);

    public void OpenDeadzoneCalibration() => _screen.OpenDeadzoneCalibration();

    public (Stick Left, Stick Right) ControllerSticks(int slot) => (_stateOf(slot).Left, _stateOf(slot).Right);

    public void OpenControllerPage(int slot) => _screen.OpenControllerPage(slot);

    public void CloseControllerPage() => _screen.CloseControllerPage();

    public string ControllerName(int slot) => _registry.Key(slot) is { } key ? _registry.ProfileName(key) : "";

    public void SetControllerName(int slot, string name)
    {
        if (_registry.Key(slot) is { } key) _registry.SetProfileName(key, name);
    }

    public void ChooseControllerPicture(int slot, bool second)
    {
        if (_registry.PictureKey(slot, second) is { } key) ChoosePicture(key);
    }

    /// <summary>Asks for a picture file (Windows dialog) and stores it under <paramref name="key"/>.</summary>
    public void ChoosePicture(string key)
    {
        var file = FilePicker.Pick(_screen.WindowHandle, "Choose a picture for this controller", FilePicker.PictureFilter);
        if (file is null) return;
        var error = ControllerPictures.Import(file, key);
        if (error is not null) _toast.Show(MenuPages.Gear, error);
    }

    public void RemoveControllerPicture(int slot, bool second)
    {
        if (_registry.PictureKey(slot, second) is { } key) ControllerPictures.Remove(key);
    }

    // ---- paired controllers --------------------------------------------------------------------

    public void AddController() => _pairing.Start(only: null);

    public string PairingStatus => _pairing.Status;

    public IReadOnlyList<string> PairedControllers => _registry.PairedControllers;

    public void ForgetController(string key)
    {
        if (!_registry.Forget(key)) _toast.Show(MenuPages.Game, "Could not forget the controller");
        _screen.OnForgotten(key); // forgotten from its own page: back to the Controller tab
    }

    public void RepairController(string key)
    {
        _pairing.Start(only: _registry.UsesWindowsPairing(key) ? key : null); // a Joy-Con 2 in sync mode is claimed by the hub
        _screen.Refresh();
    }

    public void OpenPairedPage(string key) => _screen.OpenPairedPage(key);

    public string ProfileName(string key) => _registry.ProfileName(key);

    public void SetProfileName(string key, string name) => _registry.SetProfileName(key, name);

    public string PairedDisplayName(string key) => _registry.PairedDisplayName(key);
}
