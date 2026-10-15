using System.Globalization;
using ControlWin.Config;

namespace ControlWin.App;

internal enum SettingKind { Toggle, Number, Action, Info, Text }

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

    public static SettingItem Info(string label, Func<string> value) => new()
    {
        Label = label,
        Kind = SettingKind.Info,
        Value = value,
    };
}

internal sealed class SettingsTab(string title, IReadOnlyList<SettingItem> items)
{
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
        ]);

        var layout = new SettingsTab("Layout",
        [
            SettingItem.Info("Not implemented yet", () => ""),
        ]);

        var controller = new SettingsTab("Controller",
        [
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
