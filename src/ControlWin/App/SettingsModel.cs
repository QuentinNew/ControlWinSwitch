using System.Globalization;
using ControlWin.Config;

namespace ControlWin.App;

internal enum SettingKind { Toggle, Number, Action, Info, Text, Choice, Strip }

/// <summary>One row of the settings window.</summary>
internal sealed class SettingItem
{
    public required string Label { get; init; }
    public SettingKind Kind { get; init; }

    /// <summary>Text shown on the right (evaluated on every paint).</summary>
    public Func<string> Value { get; init; } = () => "";

    /// <summary>Toggles only: the current state.</summary>
    public Func<bool>? IsOn { get; init; }

    /// <summary>Toggle / Number: -1 or +1 (left / right).</summary>
    public Action<int>? Adjust { get; init; }

    /// <summary>Action rows, and Toggle rows when "A" is pressed.</summary>
    public Action? Activate { get; init; }

    /// <summary>Strip rows: the focused controller slot (left / right move it).</summary>
    public int Slot { get; set; }

    public bool Enabled { get; init; } = true;


    public bool Selectable => Enabled && Kind != SettingKind.Info;

    public static SettingItem Toggle(string label, Func<bool> get, Action<bool> set) => new()
    {
        Label = label,
        Kind = SettingKind.Toggle,
        IsOn = get,
        Value = () => get() ? "On" : "Off",
        Adjust = _ => set(!get()),
        Activate = () => set(!get()),
    };

    public static SettingItem Number(string label, Func<float> get, Action<float> set,
        float min, float max, float step, string format = "0.##", string suffix = "") => new()
    {
        Label = label,
        Kind = SettingKind.Number,
        Value = () => get().ToString(format, CultureInfo.InvariantCulture) + suffix,
        Adjust = delta =>
        {
            // Snap to the step grid first so repeated edits never accumulate float drift.
            float v = MathF.Round(get() / step) * step + delta * step;
            set(MathF.Round(Math.Clamp(v, min, max) / step) * step);
        },
    };

    /// <summary>One value out of a short list; left / right (or "A") cycle through it, wrapping.</summary>
    public static SettingItem Choice(string label, IReadOnlyList<string> options, Func<int> get, Action<int> set)
    {
        void Cycle(int delta) => set(((get() + Math.Sign(delta)) % options.Count + options.Count) % options.Count);
        return new()
        {
            Label = label,
            Kind = SettingKind.Choice,
            Value = () => options[Math.Clamp(get(), 0, options.Count - 1)],
            Adjust = Cycle,
            Activate = () => Cycle(1),
        };
    }

    /// <summary>Like <see cref="Choice"/>, for a list that changes while the window is open: <paramref name="options"/> is read on every use.</summary>
    public static SettingItem DynamicChoice(string label, Func<IReadOnlyList<string>> options, Func<int> get, Action<int> set)
    {
        void Cycle(int delta)
        {
            int n = options().Count;
            set(((get() + Math.Sign(delta)) % n + n) % n);
        }
        return new()
        {
            Label = label,
            Kind = SettingKind.Choice,
            Value = () => { var list = options(); return list[Math.Clamp(get(), 0, list.Count - 1)]; },
            Adjust = Cycle,
            Activate = () => Cycle(1),
        };
    }

    public static SettingItem Action(string label, Action run, Func<string>? value = null) => new()
    {
        Label = label,
        Kind = SettingKind.Action,
        Value = value ?? (() => ""),
        Activate = run,
    };

    /// <summary>A text value; "A" / click opens the inline editor through <paramref name="edit"/>.</summary>
    public static SettingItem Text(string label, Func<string> value, Action edit) => new()
    {
        Label = label,
        Kind = SettingKind.Text,
        Value = value,
        Activate = edit,
    };

    /// <summary>
    /// The row of controller tiles: left / right pick a slot (only those <paramref name="canFocus"/> accepts, when given),
    /// "A" runs <paramref name="run"/> on it.
    /// </summary>
    public static SettingItem Strip(string label, int slots, Action<int> run, Func<int, bool>? canFocus = null, bool selectable = true)
    {
        SettingItem? self = null;
        return self = new()
        {
            Label = label,
            Kind = SettingKind.Strip,
            Enabled = selectable,
            Adjust = d =>
            {
                int step = Math.Sign(d);
                for (int k = 1; k <= slots; k++)
                {
                    int next = ((self!.Slot + step * k) % slots + slots) % slots;
                    if (canFocus?.Invoke(next) == false) continue;
                    self.Slot = next;
                    return;
                }
            },
            Activate = () => run(self!.Slot),
        };
    }

    public static SettingItem Info(string label, Func<string> value) => new()
    {
        Label = label,
        Kind = SettingKind.Info,
        Value = value,
    };
}

internal sealed class SettingsTab(string title, IReadOnlyList<SettingItem> items, bool controllerStrip = false, Func<string>? titleOf = null,
    DeadzoneCalibration? calibration = null)
{
    /// <summary>Set on the deadzone calibration page: the window draws the two stick circles instead of rows.</summary>
    public DeadzoneCalibration? Calibration { get; } = calibration;

    /// <summary>Shows the row of connected-controller tiles above the rows.</summary>
    public bool ControllerStrip { get; } = controllerStrip;

    /// <summary>The tab title, or for a page whose title follows what it edits, the current one.</summary>
    public string Title => titleOf?.Invoke() ?? title;
    public IReadOnlyList<SettingItem> Items { get; } = items;
}

/// <summary>Tabs + selection state of the settings window. Pure: driven by the controller or the mouse.</summary>
internal sealed class SettingsModel
{
    private readonly int[] _selected;
    private SettingsTab? _sub;   // a page opened from a row (a controller's page): shown in place of the tab until closed
    private int _subSelected;
    private SettingsTab? _parent; // the page a sub-page was opened from (the deadzone screen from a controller's page)
    private int _parentSelected;

    public SettingsModel(IReadOnlyList<SettingsTab> tabs)
    {
        Tabs = tabs;
        _selected = new int[tabs.Count];
        for (int i = 0; i < tabs.Count; i++) _selected[i] = FirstSelectable(tabs[i]);
    }

    public IReadOnlyList<SettingsTab> Tabs { get; }

    public int TabIndex { get; private set; }

    public SettingsTab Tab => _sub ?? Tabs[TabIndex];

    /// <summary>A sub-page is open (B / right click goes back to the tab instead of closing the window).</summary>
    public bool InSubPage => _sub is not null;

    /// <summary>Title of the open sub-page, or null.</summary>
    public string? SubTitle => _sub?.Title;

    public void OpenSub(SettingsTab page)
    {
        _parent = _sub;
        _parentSelected = _subSelected;
        _sub = page;
        _subSelected = FirstSelectable(page, skipStrip: true); // a page starts on its first setting, not on the controller strip
    }

    /// <summary>
    /// Closes the open sub-page. When the tab below keeps its controller strip selected, the selection moves to the
    /// first row after it: coming back from a controller's page leaves no controller selected.
    /// </summary>
    public void Back()
    {
        if (_parent is not null)
        {
            _sub = _parent;
            _subSelected = _parentSelected;
            _parent = null;
            return;
        }
        _sub = null;
        if (CurrentItem is not { Kind: SettingKind.Strip }) return;
        int first = FirstSelectable(Tab, skipStrip: true);
        if (first >= 0) _selected[TabIndex] = first;
    }

    /// <summary>Selected row of the current tab, or -1 when nothing can be selected.</summary>
    public int Selected => _sub is not null ? _subSelected : _selected[TabIndex];

    private void SetSelected(int index)
    {
        if (_sub is not null) _subSelected = index;
        else _selected[TabIndex] = index;
    }

    public SettingItem? CurrentItem => Selected >= 0 && Selected < Tab.Items.Count ? Tab.Items[Selected] : null;

    public void SetTab(int index)
    {
        if (index < 0 || index >= Tabs.Count) return;
        TabIndex = index;
        _sub = _parent = null;
    }

    /// <summary>Next / previous tab, wrapping.</summary>
    public void MoveTab(int delta)
    {
        TabIndex = ((TabIndex + Math.Sign(delta)) % Tabs.Count + Tabs.Count) % Tabs.Count;
        _sub = _parent = null;
    }

    /// <summary>Moves the selection among selectable rows, wrapping.</summary>
    public void Move(int delta)
    {
        var items = Tab.Items;
        int count = items.Count;
        if (count == 0 || Selected < 0 || delta == 0) return;

        int step = Math.Sign(delta), index = Selected;
        for (int i = 0; i < count; i++)
        {
            index = ((index + step) % count + count) % count;
            if (items[index].Selectable)
            {
                SetSelected(index);
                return;
            }
        }
    }

    /// <summary>Selects a row of the current tab (mouse hover). Returns true when the selection changed.</summary>
    public bool Select(int index)
    {
        if (index < 0 || index >= Tab.Items.Count || !Tab.Items[index].Selectable || index == Selected) return false;
        SetSelected(index);
        return true;
    }

    /// <summary>Left / right on the selected row.</summary>
    public void Adjust(int delta) => CurrentItem?.Adjust?.Invoke(Math.Sign(delta));

    /// <summary>"A" on the selected row.</summary>
    public void Activate() => CurrentItem?.Activate?.Invoke();

    private static int FirstSelectable(SettingsTab tab, bool skipStrip = false)
    {
        for (int i = 0; i < tab.Items.Count; i++)
            if (tab.Items[i].Selectable && !(skipStrip && tab.Items[i].Kind == SettingKind.Strip)) return i;
        return -1;
    }
}

/// <summary>What the settings rows need from the application.</summary>
internal interface ISettingsHost
{
    string ConfigPath { get; }
    string HeadsetStatus { get; }
    /// <summary>Names of the Bluetooth audio devices Windows knows (refreshed when the settings open).</summary>
    IReadOnlyList<string> HeadsetDevices { get; }
    /// <summary>Config values changed: save, then apply (virtual pad, ...).</summary>
    void ConfigChanged();
    void EditConfig();
    void ReloadConfig();
    void OpenLog();
    void ToggleHeadset();

    /// <summary>Effective state of the virtual pad (off while a watched process runs).</summary>
    bool PadEnabled { get; }
    /// <summary>The user switched the pad from the settings window.</summary>
    void SetPad(bool enabled);
    /// <summary>The pad's kind: 0 = none, 1 = Xbox, 2 = PS4.</summary>
    int PadMode { get; }
    void SetPadMode(int mode);
    /// <summary>The list of watched processes changed.</summary>
    void WatchListChanged();
    /// <summary>Starts inline text editing; <paramref name="commit"/> receives the new text on Enter / A.</summary>
    void EditText(string current, Action<string> commit);
    void OpenBluetoothSettings();
    /// <summary>Opens the editor of the menu layout in use (a mouse window).</summary>
    void EditMenu();
    /// <summary>Sets the keyboard shortcut of the menu from typed text ("Ctrl+Alt+M", empty = none); reports text it cannot read.</summary>
    void SetMenuHotkey(string text);

    /// <summary>ControlWin is registered to start when Windows starts.</summary>
    bool StartOnLogon { get; }
    void SetStartOnLogon(bool enabled);

    /// <summary>Slots shown above the Controller tab rows (the app drives one Pro Controller, in slot 1).</summary>
    const int ControllerSlots = 4;
    bool ControllerConnected(int slot);
    /// <summary>Makes the controller in <paramref name="slot"/> rumble briefly.</summary>
    void RumbleController(int slot);
    /// <summary>Deadzone (0..0.5) of one stick of the controller, applied to its virtual pad.</summary>
    float ControllerDeadzone(int slot, bool right);
    void SetControllerDeadzone(int slot, bool right, float value);
    /// <summary>Opens the deadzone calibration screen of the controller page being edited (circles showing the sticks).</summary>
    void OpenDeadzoneCalibration();
    /// <summary>The controller's sticks as normalized positions (before any deadzone).</summary>
    (ControlWin.Input.Stick Left, ControlWin.Input.Stick Right) ControllerSticks(int slot);
    /// <summary>Pairs a new controller over Bluetooth: a Pro Controller Windows does not know yet, or a Joy-Con 2 in sync mode.</summary>
    void AddController();
    /// <summary>"Searching..." while a pairing runs, otherwise empty.</summary>
    string PairingStatus { get; }

    /// <summary>Opens the page of the controller in <paramref name="slot"/> (name, picture, rumble test).</summary>
    void OpenControllerPage(int slot);
    void CloseControllerPage();
    /// <summary>The name the user gave the controller (empty = none).</summary>
    string ControllerName(int slot);
    void SetControllerName(int slot, string name);
    /// <summary>Asks for a picture file (Windows dialog) and sets it as the controller's picture.</summary>
    /// <summary><paramref name="second"/>: the right Joy-Con's picture of a pair.</summary>
    void ChooseControllerPicture(int slot, bool second = false);
    void RemoveControllerPicture(int slot, bool second = false);
}

internal static class SettingsPages
{
    /// <summary>
    /// The page of a connected controller: the controller strip on top (left / right switch to another connected
    /// controller, a ring marks the one being edited), then its settings. The picture is edited on the strip itself
    /// (hover a tile: a pencil changes it, a cross removes it). <paramref name="strip"/> is the strip row:
    /// its <see cref="SettingItem.Slot"/> is the controller being edited.
    /// </summary>
    public static SettingsTab ControllerPage(ISettingsHost host, int slot, out SettingItem strip)
    {
        // Not a row of the page: the pad switches controller with ZL / ZR (see AppController), the mouse by clicking a tile.
        var tiles = SettingItem.Strip("Connected controllers", ISettingsHost.ControllerSlots, _ => { }, host.ControllerConnected, selectable: false);
        tiles.Slot = slot;
        strip = tiles;
        return new SettingsTab("Controller",
        [
            tiles,
            SettingItem.Text("Name",
                () => host.ControllerName(tiles.Slot) is { Length: > 0 } name ? name : "(default)",
                () => host.EditText(host.ControllerName(tiles.Slot), v => host.SetControllerName(tiles.Slot, v))),
            SettingItem.Action("Test rumble", () => host.RumbleController(tiles.Slot)),
            SettingItem.Action("Calibrate deadzone", host.OpenDeadzoneCalibration),
            SettingItem.Action("Back", host.CloseControllerPage),
        ], controllerStrip: true, titleOf: () => $"Controller {tiles.Slot + 1}");
    }

    /// <summary>
    /// The deadzone calibration screen: the controller strip stays on top (<paramref name="strip"/> is the controller page's,
    /// so ZL / ZR and the tiles still switch controller), the circles take the place of the rows.
    /// </summary>
    public static SettingsTab DeadzonePage(ISettingsHost host, SettingItem strip) =>
        new("Deadzone", [strip], controllerStrip: true, titleOf: () => $"Controller {strip.Slot + 1}  >  Deadzone",
            calibration: new DeadzoneCalibration(() => strip.Slot, host));

    public static SettingsModel Create(ConfigStore store, ISettingsHost host)
    {
        var cfg = () => store.Current;
        void Changed() => host.ConfigChanged();

        var other = new SettingsTab("Other",
        [
            SettingItem.Toggle("Launch on startup", () => host.StartOnLogon, host.SetStartOnLogon),
            SettingItem.Action("Edit config file", host.EditConfig),
            SettingItem.Action("Reload config file", host.ReloadConfig),
            SettingItem.Action("Open log", host.OpenLog),
        ]);

        var layout = new SettingsTab("Layout",
        [
            SettingItem.Choice("Main menu", ["List", "Radial"],
                () => cfg().MenuLayout == MenuLayouts.Radial ? 1 : 0,
                i => { cfg().MenuLayout = i == 1 ? MenuLayouts.Radial : MenuLayouts.List; Changed(); }),
            SettingItem.Action("Edit menu", host.EditMenu),
            SettingItem.Toggle("Show controller in menu", () => cfg().MenuShowControllers, v => { cfg().MenuShowControllers = v; Changed(); }),
            SettingItem.Text("Keyboard shortcut",
                () => cfg().MenuHotkey.Length == 0 ? "(none)" : cfg().MenuHotkey,
                () => host.EditText(cfg().MenuHotkey, host.SetMenuHotkey)),
        ]);

        var controller = new SettingsTab("Controller",
        [
            SettingItem.Strip("Connected controllers", ISettingsHost.ControllerSlots, host.OpenControllerPage, host.ControllerConnected),
            SettingItem.Action("Add new controller", host.AddController, () => host.PairingStatus),
            SettingItem.Info("Single Joy-Con", () => "Hold SL + SR while it connects"),
        ], controllerStrip: true);

        var virtualPad = new SettingsTab("Virtual Pad",
        [
            SettingItem.Choice("Virtual pad", ["None", "Xbox", "PS4"], () => host.PadMode, host.SetPadMode),
            SettingItem.Toggle("Swap A / B", () => cfg().VirtualPad.SwapAB, v => { cfg().VirtualPad.SwapAB = v; Changed(); }),
            SettingItem.Toggle("Swap X / Y", () => cfg().VirtualPad.SwapXY, v => { cfg().VirtualPad.SwapXY = v; Changed(); }),
            SettingItem.Number("Rumble", () => cfg().VirtualPad.Rumble, v => { cfg().VirtualPad.Rumble = (int)v; Changed(); }, 0, 100, 10, "0", "%"),
            SettingItem.Text("Pad off while running",
                () => cfg().VirtualPad.DisableWhileRunning.Count == 0 ? "(off)" : ProcessList.Format(cfg().VirtualPad.DisableWhileRunning),
                () => host.EditText(ProcessList.Format(cfg().VirtualPad.DisableWhileRunning), v =>
                {
                    cfg().VirtualPad.DisableWhileRunning = ProcessList.Parse(v);
                    Changed();
                    host.WatchListChanged();
                })),
        ]);

        var mouse = new SettingsTab("Mouse",
        [
            SettingItem.Number("Speed", () => cfg().Mouse.Speed, v => { cfg().Mouse.Speed = v; Changed(); }, 100, 3000, 100, "0", " px/s"),
            SettingItem.Number("Fast (ZR) multiplier", () => cfg().Mouse.FastMultiplier, v => { cfg().Mouse.FastMultiplier = v; Changed(); }, 1, 5, 0.5f, "0.0", "x"),
            SettingItem.Number("Precise (ZL) multiplier", () => cfg().Mouse.SlowMultiplier, v => { cfg().Mouse.SlowMultiplier = v; Changed(); }, 0.1f, 1, 0.1f, "0.0", "x"),
            SettingItem.Number("Stick deadzone", () => cfg().Mouse.Deadzone, v => { cfg().Mouse.Deadzone = v; Changed(); }, 0.05f, 0.4f, 0.05f, "0.00"),
            SettingItem.Number("Scroll speed", () => cfg().Mouse.ScrollSpeed, v => { cfg().Mouse.ScrollSpeed = v; Changed(); }, 2, 30, 1, "0", " notches/s"),
            SettingItem.Number("D-pad glide speed", () => cfg().Mouse.DpadSpeed, v => { cfg().Mouse.DpadSpeed = v; Changed(); }, 30, 400, 10, "0", " px/s"),
        ]);

        // "(none)", the known devices, then the saved name when Windows no longer lists it.
        const string NotFound = " (not found)";
        IReadOnlyList<string> HeadsetChoices()
        {
            var list = new List<string> { "(none)" };
            list.AddRange(host.HeadsetDevices);
            string saved = cfg().HeadsetName;
            if (saved.Length > 0 && !host.HeadsetDevices.Contains(saved, StringComparer.OrdinalIgnoreCase)) list.Add(saved + NotFound);
            return list;
        }
        int HeadsetChoiceIndex()
        {
            string saved = cfg().HeadsetName;
            if (saved.Length == 0) return 0;
            var devices = host.HeadsetDevices;
            for (int i = 0; i < devices.Count; i++)
                if (string.Equals(devices[i], saved, StringComparison.OrdinalIgnoreCase)) return i + 1;
            return devices.Count + 1;
        }

        var headset = new SettingsTab("Headset",
        [
            SettingItem.DynamicChoice("Device", HeadsetChoices, HeadsetChoiceIndex, i =>
            {
                var choices = HeadsetChoices();
                string choice = i > 0 && i < choices.Count ? choices[i] : "";
                cfg().HeadsetName = choice.EndsWith(NotFound) ? choice[..^NotFound.Length] : choice;
                Changed();
            }),
            SettingItem.Info("Status", () => host.HeadsetStatus),
            SettingItem.Action("Connect / disconnect", host.ToggleHeadset),
            SettingItem.Action("Bluetooth settings", host.OpenBluetoothSettings),
        ]);

        return new SettingsModel([controller, layout, virtualPad, mouse, headset, other]);
    }
}
