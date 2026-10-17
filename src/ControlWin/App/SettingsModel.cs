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

    /// <summary>The row of controller tiles: left / right pick a slot, "A" runs <paramref name="run"/> on it.</summary>
    public static SettingItem Strip(string label, int slots, Action<int> run)
    {
        SettingItem? self = null;
        return self = new()
        {
            Label = label,
            Kind = SettingKind.Strip,
            Adjust = d => self!.Slot = ((self.Slot + Math.Sign(d)) % slots + slots) % slots,
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

internal sealed class SettingsTab(string title, IReadOnlyList<SettingItem> items, bool controllerStrip = false)
{
    /// <summary>Shows the row of connected-controller tiles above the rows.</summary>
    public bool ControllerStrip { get; } = controllerStrip;

    public string Title { get; } = title;
    public IReadOnlyList<SettingItem> Items { get; } = items;
}

/// <summary>Tabs + selection state of the settings window. Pure: driven by the controller or the mouse.</summary>
internal sealed class SettingsModel
{
    private readonly int[] _selected;

    public SettingsModel(IReadOnlyList<SettingsTab> tabs)
    {
        Tabs = tabs;
        _selected = new int[tabs.Count];
        for (int i = 0; i < tabs.Count; i++) _selected[i] = FirstSelectable(tabs[i]);
    }

    public IReadOnlyList<SettingsTab> Tabs { get; }

    public int TabIndex { get; private set; }

    public SettingsTab Tab => Tabs[TabIndex];

    /// <summary>Selected row of the current tab, or -1 when nothing can be selected.</summary>
    public int Selected => _selected[TabIndex];

    public SettingItem? CurrentItem => Selected >= 0 && Selected < Tab.Items.Count ? Tab.Items[Selected] : null;

    public void SetTab(int index)
    {
        if (index >= 0 && index < Tabs.Count) TabIndex = index;
    }

    /// <summary>Next / previous tab, wrapping.</summary>
    public void MoveTab(int delta) => TabIndex = ((TabIndex + Math.Sign(delta)) % Tabs.Count + Tabs.Count) % Tabs.Count;

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
                _selected[TabIndex] = index;
                return;
            }
        }
    }

    /// <summary>Selects a row of the current tab (mouse hover). Returns true when the selection changed.</summary>
    public bool Select(int index)
    {
        if (index < 0 || index >= Tab.Items.Count || !Tab.Items[index].Selectable || index == Selected) return false;
        _selected[TabIndex] = index;
        return true;
    }

    /// <summary>Left / right on the selected row.</summary>
    public void Adjust(int delta) => CurrentItem?.Adjust?.Invoke(Math.Sign(delta));

    /// <summary>"A" on the selected row.</summary>
    public void Activate() => CurrentItem?.Activate?.Invoke();

    private static int FirstSelectable(SettingsTab tab)
    {
        for (int i = 0; i < tab.Items.Count; i++)
            if (tab.Items[i].Selectable) return i;
        return -1;
    }
}

/// <summary>What the settings rows need from the application.</summary>
internal interface ISettingsHost
{
    string ConfigPath { get; }
    string HeadsetStatus { get; }
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
    /// <summary>The list of watched processes changed.</summary>
    void WatchListChanged();
    /// <summary>Starts inline text editing; <paramref name="commit"/> receives the new text on Enter / A.</summary>
    void EditText(string current, Action<string> commit);
    void OpenBluetoothSettings();
    /// <summary>Opens the radial menu editor (a mouse window).</summary>
    void EditRadialMenu();

    /// <summary>ControlWin is registered to start when Windows starts.</summary>
    bool StartOnLogon { get; }
    void SetStartOnLogon(bool enabled);

    /// <summary>Slots shown above the Controller tab rows (the app drives one Pro Controller, in slot 1).</summary>
    const int ControllerSlots = 4;
    bool ControllerConnected(int slot);
    /// <summary>Makes the controller in <paramref name="slot"/> rumble briefly.</summary>
    void RumbleController(int slot);
}

internal static class SettingsPages
{
    public static SettingsModel Create(ConfigStore store, ISettingsHost host)
    {
        var cfg = () => store.Current;
        void Changed() => host.ConfigChanged();

        var general = new SettingsTab("General",
        [
            SettingItem.Info("Config file", () => host.ConfigPath),
            SettingItem.Action("Edit config file", host.EditConfig),
            SettingItem.Action("Reload config file", host.ReloadConfig),
            SettingItem.Action("Open log", host.OpenLog),
            SettingItem.Toggle("Launch on startup", () => host.StartOnLogon, host.SetStartOnLogon),
        ]);

        var layout = new SettingsTab("Layout",
        [
            SettingItem.Choice("Main menu", ["List", "Radial"],
                () => cfg().MenuLayout == MenuLayouts.Radial ? 1 : 0,
                i => { cfg().MenuLayout = i == 1 ? MenuLayouts.Radial : MenuLayouts.List; Changed(); }),
            SettingItem.Action("Edit radial menu", host.EditRadialMenu),
        ]);

        var controller = new SettingsTab("Controller",
        [
            SettingItem.Strip("Connected controllers", ISettingsHost.ControllerSlots, host.RumbleController),
            SettingItem.Toggle("Virtual Xbox pad", () => host.PadEnabled, host.SetPad),
            SettingItem.Toggle("Swap A / B", () => cfg().VirtualPad.SwapAB, v => { cfg().VirtualPad.SwapAB = v; Changed(); }),
            SettingItem.Toggle("Swap X / Y", () => cfg().VirtualPad.SwapXY, v => { cfg().VirtualPad.SwapXY = v; Changed(); }),
            SettingItem.Text("Pad off while running",
                () => cfg().VirtualPad.DisableWhileRunning.Count == 0 ? "(off)" : ProcessList.Format(cfg().VirtualPad.DisableWhileRunning),
                () => host.EditText(ProcessList.Format(cfg().VirtualPad.DisableWhileRunning), v =>
                {
                    cfg().VirtualPad.DisableWhileRunning = ProcessList.Parse(v);
                    Changed();
                    host.WatchListChanged();
                })),
        ], controllerStrip: true);

        var mouse = new SettingsTab("Mouse",
        [
            SettingItem.Number("Speed", () => cfg().Mouse.Speed, v => { cfg().Mouse.Speed = v; Changed(); }, 100, 3000, 100, "0", " px/s"),
            SettingItem.Number("Fast (ZR) multiplier", () => cfg().Mouse.FastMultiplier, v => { cfg().Mouse.FastMultiplier = v; Changed(); }, 1, 5, 0.5f, "0.0", "x"),
            SettingItem.Number("Precise (ZL) multiplier", () => cfg().Mouse.SlowMultiplier, v => { cfg().Mouse.SlowMultiplier = v; Changed(); }, 0.1f, 1, 0.1f, "0.0", "x"),
            SettingItem.Number("Stick deadzone", () => cfg().Mouse.Deadzone, v => { cfg().Mouse.Deadzone = v; Changed(); }, 0.05f, 0.4f, 0.05f, "0.00"),
            SettingItem.Number("Scroll speed", () => cfg().Mouse.ScrollSpeed, v => { cfg().Mouse.ScrollSpeed = v; Changed(); }, 2, 30, 1, "0", " notches/s"),
            SettingItem.Number("D-pad glide speed", () => cfg().Mouse.DpadSpeed, v => { cfg().Mouse.DpadSpeed = v; Changed(); }, 30, 400, 10, "0", " px/s"),
        ]);

        var headset = new SettingsTab("Headset",
        [
            SettingItem.Text("Device name", () => string.IsNullOrEmpty(cfg().HeadsetName) ? "(not set)" : cfg().HeadsetName,
                () => host.EditText(cfg().HeadsetName, v => { cfg().HeadsetName = v.Trim(); Changed(); })),
            SettingItem.Info("Status", () => host.HeadsetStatus),
            SettingItem.Action("Connect / disconnect", host.ToggleHeadset),
            SettingItem.Action("Bluetooth settings", host.OpenBluetoothSettings),
        ]);

        return new SettingsModel([general, layout, controller, mouse, headset]);
    }
}
