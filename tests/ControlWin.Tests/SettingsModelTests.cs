using ControlWin.App;
using ControlWin.Config;

namespace ControlWin.Tests;

public class SettingsModelTests
{
    private sealed class FakeHost : ISettingsHost
    {
        public int Changes, Edits;
        public string ConfigPath => "x";
        public string HeadsetStatus => "Connected";
        public void ConfigChanged() => Changes++;
        public void EditConfig() => Edits++;
        public void ReloadConfig() { }
        public void OpenLog() { }
        public void ToggleHeadset() { }
        public void OpenBluetoothSettings() { }
        public Action<string>? Commit;
        public string? Editing;
        public void EditText(string current, Action<string> commit) { Editing = current; Commit = commit; }
    }

    private static (SettingsModel Model, ConfigStore Store, FakeHost Host) Create()
    {
        var store = new ConfigStore(Path.Combine(Path.GetTempPath(), "unused-config.json"));
        var host = new FakeHost();
        return (SettingsPages.Create(store, host), store, host);
    }

    [Fact]
    public void Has_the_five_tabs_in_order()
    {
        var (m, _, _) = Create();
        Assert.Equal(["General", "Layout", "Controller", "Mouse", "Headset"], m.Tabs.Select(t => t.Title));
    }

    [Fact]
    public void Tab_navigation_wraps()
    {
        var (m, _, _) = Create();
        m.MoveTab(-1);
        Assert.Equal("Headset", m.Tab.Title);
        m.MoveTab(1);
        Assert.Equal("General", m.Tab.Title);
    }

    [Fact]
    public void Selection_skips_info_rows()
    {
        var (m, _, _) = Create();
        // General: row 0 is an info row (config path), so the first selectable is "Edit config file".
        Assert.Equal(1, m.Selected);
        m.Move(-1); // wraps to the last row
        Assert.Equal(3, m.Selected);
    }

    [Fact]
    public void Layout_tab_has_nothing_selectable()
    {
        var (m, _, _) = Create();
        m.SetTab(1);
        Assert.Equal(-1, m.Selected);
        Assert.Null(m.CurrentItem);
        m.Move(1);
        m.Activate();
        m.Adjust(1); // must not throw
    }

    [Fact]
    public void Toggle_flips_the_config_and_reports_the_change()
    {
        var (m, store, host) = Create();
        m.SetTab(2); // Controller
        Assert.True(store.Current.VirtualPad.Enabled);

        m.Activate();
        Assert.False(store.Current.VirtualPad.Enabled);
        Assert.Equal(1, host.Changes);

        m.Adjust(1); // left / right toggles too
        Assert.True(store.Current.VirtualPad.Enabled);
    }

    [Fact]
    public void Number_steps_and_clamps()
    {
        var (m, store, _) = Create();
        m.SetTab(3); // Mouse, first row = Speed (800, step 100, 100..3000)
        m.Adjust(1);
        Assert.Equal(900f, store.Current.Mouse.Speed);
        m.Adjust(-1);
        m.Adjust(-1);
        Assert.Equal(700f, store.Current.Mouse.Speed);

        store.Current.Mouse.Speed = 100;
        m.Adjust(-1);
        Assert.Equal(100f, store.Current.Mouse.Speed);
        store.Current.Mouse.Speed = 3000;
        m.Adjust(1);
        Assert.Equal(3000f, store.Current.Mouse.Speed);
    }

    [Fact]
    public void Number_does_not_accumulate_float_drift()
    {
        var (m, store, _) = Create();
        m.SetTab(3);
        m.Move(1); // Fast multiplier: 2.5, step 0.5
        for (int i = 0; i < 6; i++) m.Adjust(-1);
        Assert.Equal(1f, store.Current.Mouse.FastMultiplier); // clamped at the minimum
        m.Move(1); // Slow multiplier: 0.3, step 0.1
        for (int i = 0; i < 4; i++) m.Adjust(1);
        Assert.Equal(0.7f, store.Current.Mouse.SlowMultiplier, 4);
    }

    [Fact]
    public void Action_row_runs_on_activate_only()
    {
        var (m, _, host) = Create();
        Assert.Equal(1, m.Selected); // Edit config file
        m.Adjust(1);
        Assert.Equal(0, host.Edits);
        m.Activate();
        Assert.Equal(1, host.Edits);
    }

    [Fact]
    public void Select_ignores_info_and_out_of_range_rows()
    {
        var (m, _, _) = Create();
        Assert.False(m.Select(0)); // info row
        Assert.False(m.Select(99));
        Assert.True(m.Select(2));
        Assert.Equal(2, m.Selected);
    }

    [Fact]
    public void Each_tab_remembers_its_selection()
    {
        var (m, _, _) = Create();
        m.SetTab(3);
        m.Move(1);
        Assert.Equal(1, m.Selected);
        m.SetTab(0);
        m.SetTab(3);
        Assert.Equal(1, m.Selected);
    }

    [Fact]
    public void Text_row_opens_the_editor_and_commit_updates_the_config()
    {
        var (m, store, host) = Create();
        m.SetTab(4); // Headset
        Assert.Equal(0, m.Selected); // device name is the first selectable row
        m.Activate();
        Assert.Equal("WH-CH700N", host.Editing);

        host.Commit!("  My Headset ");
        Assert.Equal("My Headset", store.Current.HeadsetName);
        Assert.Equal(1, host.Changes);
    }
}
