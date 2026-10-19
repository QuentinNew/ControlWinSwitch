using ControlWin.App;
using ControlWin.Config;

namespace ControlWin.Tests;

public class SettingsModelTests
{
    private sealed class FakeHost : ISettingsHost
    {
        public int Changes, Edits, WatchChanges;
        public bool Pad = true;
        public bool PadEnabled => Pad;
        public void SetPad(bool enabled) => Pad = enabled;
        public int Mode = 1;
        public int PadMode => Pad ? Mode : 0;
        public void SetPadMode(int mode) { Pad = mode > 0; if (mode > 0) Mode = mode; }
        public void WatchListChanged() => WatchChanges++;
        public string ConfigPath => "x";
        public string HeadsetStatus => "Connected";
        public List<string> Devices = [];
        public IReadOnlyList<string> HeadsetDevices => Devices;
        public void ConfigChanged() => Changes++;
        public void EditConfig() => Edits++;
        public void ReloadConfig() { }
        public void OpenLog() { }
        public void ToggleHeadset() { }
        public void OpenBluetoothSettings() { }
        public void EditMenu() { }
        public string? Hotkey;
        public void SetMenuHotkey(string text) => Hotkey = text;
        public HashSet<int> Connected = [0];
        public bool ControllerConnected(int slot) => Connected.Contains(slot);
        public List<int> Rumbled = [];
        public void RumbleController(int slot) => Rumbled.Add(slot);
        public float[] Deadzones = new float[2];
        public float ControllerDeadzone(int slot, bool right) => Deadzones[right ? 1 : 0];
        public void SetControllerDeadzone(int slot, bool right, float value) => Deadzones[right ? 1 : 0] = value;
        public int CalibrationOpened;
        public void OpenDeadzoneCalibration() => CalibrationOpened++;
        public (ControlWin.Input.Stick Left, ControlWin.Input.Stick Right) Sticks;
        public (ControlWin.Input.Stick Left, ControlWin.Input.Stick Right) ControllerSticks(int slot) => Sticks;
        public int Added;
        public void AddProController() => Added++;
        public string PairingStatus => "";
        public List<int> Pages = [];
        public int PageClosed;
        public string Name = "";
        public void OpenControllerPage(int slot) => Pages.Add(slot);
        public void CloseControllerPage() => PageClosed++;
        public string ControllerName(int slot) => Name;
        public int NamedSlot = -1;
        public void SetControllerName(int slot, string name) { Name = name; NamedSlot = slot; }
        public int Chosen, Removed;
        public void ChooseControllerPicture(int slot) => Chosen++;
        public void RemoveControllerPicture(int slot) => Removed++;
        public bool StartOnLogon { get; private set; }
        public void SetStartOnLogon(bool enabled) => StartOnLogon = enabled;
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
    public void Activating_a_controller_tile_opens_its_page()
    {
        var (m, _, host) = Create();
        m.SetTab(m.Tabs.ToList().FindIndex(t => t.ControllerStrip));
        m.Tab.Items[0].Slot = 2;

        m.Activate();

        Assert.Equal([2], host.Pages);
    }

    [Fact]
    public void Going_back_from_a_controller_page_leaves_no_controller_selected()
    {
        var (m, _, host) = Create();
        m.SetTab(m.Tabs.ToList().FindIndex(t => t.ControllerStrip));
        Assert.Equal(SettingKind.Strip, m.CurrentItem!.Kind); // the tab opens on the strip
        m.Activate(); // opens the page of the focused controller
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));

        m.Back();

        Assert.False(m.InSubPage);
        Assert.NotEqual(SettingKind.Strip, m.CurrentItem!.Kind);
        Assert.Equal("Add new Pro Controller", m.CurrentItem.Label);
    }

    [Fact]
    public void Back_keeps_the_selection_of_tabs_without_a_strip()
    {
        var (m, _, host) = Create();
        m.SetTab(1); // Layout
        int before = m.Selected;
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));

        m.Back();

        Assert.Equal(before, m.Selected);
    }

    [Fact]
    public void A_sub_page_replaces_the_tab_until_back_and_tab_changes_leave_it()
    {
        var (m, _, host) = Create();
        var tab = m.Tab;
        m.OpenSub(SettingsPages.ControllerPage(host, 1, out _));

        Assert.True(m.InSubPage);
        Assert.Equal("Controller 2", m.SubTitle);
        Assert.NotSame(tab, m.Tab);

        m.Back();
        Assert.False(m.InSubPage);
        Assert.Same(tab, m.Tab);

        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));
        m.MoveTab(1);
        Assert.False(m.InSubPage);
    }

    [Fact]
    public void Controller_page_edits_the_name_tests_rumble_and_goes_back()
    {
        var (m, _, host) = Create();
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));
        var items = m.Tab.Items;
        Assert.True(items.Count <= 5); // the strip (two rows tall) and four settings fill the six rows
        Assert.Equal(SettingKind.Strip, items[0].Kind);

        items.Single(i => i.Label == "Name").Activate!();
        Assert.Equal("", host.Editing);
        host.Commit!("Quentin");
        Assert.Equal("Quentin", host.Name);

        Assert.DoesNotContain(items, i => i.Label.StartsWith("Choose picture")); // the picture is edited on the tiles
        Assert.Equal(0, host.Chosen + host.Removed);

        items.Single(i => i.Label == "Test rumble").Activate!();
        Assert.Equal([0], host.Rumbled);

        items.Single(i => i.Label == "Back").Activate!();
        Assert.Equal(1, host.PageClosed);
    }

    [Fact]
    public void A_controller_page_starts_on_its_first_setting_and_keeps_the_strip_on_top()
    {
        var (m, _, host) = Create();
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));

        Assert.True(m.Tab.ControllerStrip);
        Assert.Equal("Name", m.CurrentItem!.Label);
    }

    [Fact]
    public void Switching_controller_on_the_page_skips_slots_without_a_controller()
    {
        var (m, _, host) = Create();
        host.Connected = [0, 2];
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out var strip));

        strip.Adjust!(1); // what ZR does
        Assert.Equal(2, strip.Slot); // slot 1 is empty: skipped
        Assert.Equal("Controller 3", m.SubTitle);

        strip.Adjust!(1);
        Assert.Equal(0, strip.Slot); // wraps
        strip.Adjust!(-1); // what ZL does
        Assert.Equal(2, strip.Slot);
    }

    [Fact]
    public void The_controller_strip_is_not_a_selectable_row_of_the_page()
    {
        var (m, _, host) = Create();
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));

        for (int i = 0; i < 6; i++)
        {
            m.Move(-1);
            Assert.NotEqual(SettingKind.Strip, m.CurrentItem!.Kind);
        }
    }

    [Fact]
    public void The_strip_stays_put_when_no_other_controller_is_connected()
    {
        var (m, _, host) = Create();
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out var strip));

        strip.Adjust!(1);

        Assert.Equal(0, strip.Slot);
    }

    [Fact]
    public void The_page_edits_the_controller_the_strip_points_at()
    {
        var (m, _, host) = Create();
        host.Connected = [0, 3];
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out var strip));
        strip.Slot = 3; // what a click on the fourth tile does

        m.Tab.Items.Single(i => i.Label == "Name").Activate!();
        host.Commit!("Blue");
        m.Tab.Items.Single(i => i.Label == "Test rumble").Activate!();

        Assert.Equal(3, host.NamedSlot);
        Assert.Equal([3], host.Rumbled);
    }

    [Fact]
    public void Selection_inside_a_sub_page_does_not_move_the_tab_selection()
    {
        var (m, _, host) = Create();
        m.SetTab(1); // Layout
        int before = m.Selected;
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));
        m.Move(1);
        m.Back();

        Assert.Equal(before, m.Selected);
    }

    [Fact]
    public void Other_tab_has_a_launch_on_startup_toggle()
    {
        var (m, _, host) = Create();
        m.SetTab(5);
        var item = m.Tab.Items.Single(i => i.Label == "Launch on startup");

        Assert.False(item.IsOn!());
        item.Activate!();

        Assert.True(host.StartOnLogon);
        Assert.True(item.IsOn!());
    }

    [Fact]
    public void Has_the_six_tabs_in_order()
    {
        var (m, _, _) = Create();
        Assert.Equal(["Controller", "Layout", "Virtual Pad", "Mouse", "Headset", "Other"], m.Tabs.Select(t => t.Title));
    }

    [Fact]
    public void Only_the_controller_tab_shows_the_controller_strip()
    {
        var (m, _, _) = Create();
        Assert.Equal(["Controller"], m.Tabs.Where(t => t.ControllerStrip).Select(t => t.Title));
    }

    [Fact]
    public void Controller_strip_is_driven_with_left_right_and_a()
    {
        var (m, _, host) = Create();
        host.Connected = [0, 1, 2, 3];
        m.SetTab(0);
        Assert.Equal(SettingKind.Strip, m.CurrentItem!.Kind);

        m.Adjust(1);
        m.Adjust(1);
        m.Activate();
        m.Adjust(-1);
        m.Activate();
        m.Adjust(-1);
        m.Adjust(-1); // wraps
        m.Activate();

        Assert.Equal([2, 1, 3], host.Pages); // "A" opens the page of the focused controller
    }

    [Fact]
    public void Controller_strip_skips_slots_without_a_controller()
    {
        var (m, _, host) = Create();
        host.Connected = [1, 3];
        m.SetTab(0);
        var strip = m.CurrentItem!;
        strip.Slot = 1;

        m.Adjust(1);
        Assert.Equal(3, strip.Slot);
        m.Adjust(1);
        Assert.Equal(1, strip.Slot); // wraps over the empty slots
    }

    [Fact]
    public void Controller_tab_adds_a_pro_controller()
    {
        var (m, _, host) = Create();
        m.SetTab(0);
        m.Move(1);
        Assert.Equal("Add new Pro Controller", m.CurrentItem!.Label);

        m.Activate();

        Assert.Equal(1, host.Added);
    }

    [Fact]
    public void Virtual_pad_tab_has_a_rumble_percent_from_0_to_100()
    {
        var (m, store, host) = Create();
        m.SetTab(2);
        m.Move(1);
        m.Move(1);
        m.Move(1);
        Assert.Equal("Rumble", m.CurrentItem!.Label);
        Assert.Equal("100%", m.CurrentItem.Value());

        m.Adjust(1);
        Assert.Equal(100, store.Current.VirtualPad.Rumble);
        for (int i = 0; i < 12; i++) m.Adjust(-1);

        Assert.Equal(0, store.Current.VirtualPad.Rumble);
        Assert.Equal("0%", m.CurrentItem.Value());
        Assert.True(host.Changes > 0);
    }

    [Fact]
    public void Tab_navigation_wraps()
    {
        var (m, _, _) = Create();
        m.MoveTab(-1);
        Assert.Equal("Other", m.Tab.Title);
        m.MoveTab(1);
        Assert.Equal("Controller", m.Tab.Title);
    }

    [Fact]
    public void Selection_skips_info_rows()
    {
        var (m, _, _) = Create();
        m.SetTab(4); // Headset: row 1 is an info row (status)
        Assert.Equal(0, m.Selected);
        m.Move(1);
        Assert.Equal(2, m.Selected);
    }

    [Fact]
    public void Layout_tab_selects_the_main_menu_layout()
    {
        var (m, store, host) = Create();
        m.SetTab(1);
        Assert.Equal("List", m.CurrentItem!.Value());
        Assert.Equal(MenuLayouts.List, store.Current.MenuLayout);

        m.Adjust(1);
        Assert.Equal("Radial", m.CurrentItem!.Value());
        Assert.Equal(MenuLayouts.Radial, store.Current.MenuLayout);
        Assert.Equal(1, host.Changes);

        m.Activate(); // cycles and wraps
        Assert.Equal(MenuLayouts.List, store.Current.MenuLayout);
    }

    [Fact]
    public void Toggle_flips_the_config_and_reports_the_change()
    {
        var (m, store, host) = Create();
        m.SetTab(2); // Virtual Pad
        m.Move(1);   // Swap A / B
        Assert.True(store.Current.VirtualPad.SwapAB);

        m.Activate();
        Assert.False(store.Current.VirtualPad.SwapAB);
        Assert.Equal(1, host.Changes);

        m.Adjust(1); // left / right toggles too
        Assert.True(store.Current.VirtualPad.SwapAB);
    }

    [Fact]
    public void Pad_mode_goes_through_the_host_not_the_config()
    {
        var (m, store, host) = Create();
        m.SetTab(2);
        Assert.Equal(0, m.Selected);

        m.Activate(); // Xbox -> PS4
        Assert.True(host.Pad);
        m.Activate(); // PS4 -> None
        Assert.False(host.Pad);
        Assert.True(store.Current.VirtualPad.Enabled); // the host decides whether it is persisted
    }

    [Fact]
    public void Watch_list_is_edited_as_comma_separated_text()
    {
        var (m, store, host) = Create();
        m.SetTab(2);
        m.Move(-1); // wraps to the last row: "Pad off while running"
        Assert.Equal("steam.exe", host.Editing ?? "steam.exe");
        m.Activate();
        Assert.Equal("steam.exe", host.Editing);

        host.Commit!("steam.exe, EpicGamesLauncher.exe ;steam.exe");
        Assert.Equal(["steam.exe", "EpicGamesLauncher.exe"], store.Current.VirtualPad.DisableWhileRunning);
        Assert.Equal(1, host.WatchChanges);
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
        m.SetTab(5); // Other
        m.Move(1); // Edit config file
        Assert.Equal(1, m.Selected);
        m.Adjust(1);
        Assert.Equal(0, host.Edits);
        m.Activate();
        Assert.Equal(1, host.Edits);
    }

    [Fact]
    public void Select_ignores_info_and_out_of_range_rows()
    {
        var (m, _, _) = Create();
        m.SetTab(4); // Headset
        Assert.False(m.Select(1)); // info row
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
        m.SetTab(1);
        m.SetTab(3);
        Assert.Equal(1, m.Selected);
    }

    [Fact]
    public void Text_row_opens_the_editor_and_commit_updates_the_config()
    {
        var (m, store, host) = Create();
        m.SetTab(m.Tabs.ToList().FindIndex(t => t.Items.Any(i => i.Label == "Pad off while running")));
        int row = m.Tab.Items.ToList().FindIndex(i => i.Label == "Pad off while running");
        while (m.Selected != row) m.Move(1);
        m.Activate();
        Assert.Equal("steam.exe", host.Editing);

        host.Commit!("  game.exe ");
        Assert.Equal(["game.exe"], store.Current.VirtualPad.DisableWhileRunning);
        Assert.Equal(1, host.Changes);
    }

    private static (DeadzoneCalibration Cal, FakeHost Host, Func<long> Clock, Action<long> Advance) Calibration()
    {
        var host = new FakeHost();
        long now = 0;
        var cal = new DeadzoneCalibration(() => 0, host, () => now);
        return (cal, host, () => now, t => now += t);
    }

    [Fact]
    public void Calibration_arrows_change_the_selected_stick_by_steps_and_clamp()
    {
        var (cal, host, _, _) = Calibration();

        cal.MoveStick(1);
        cal.Adjust(1);
        cal.Adjust(1);
        Assert.Equal(0.04f, host.Deadzones[1], 3);
        Assert.Equal(0f, host.Deadzones[0]);

        for (int i = 0; i < 40; i++) cal.Adjust(1);
        Assert.Equal(0.5f, host.Deadzones[1], 3);
        for (int i = 0; i < 40; i++) cal.Adjust(-1);
        Assert.Equal(0f, host.Deadzones[1], 3);
    }

    [Fact]
    public void Auto_sets_each_deadzone_just_above_how_far_the_stick_strayed_at_rest()
    {
        var (cal, host, _, advance) = Calibration();
        cal.StartAuto();

        host.Sticks = (new ControlWin.Input.Stick(0.06f, 0f), new ControlWin.Input.Stick(0f, 0f));
        cal.Feed();
        host.Sticks = (new ControlWin.Input.Stick(0.02f, 0f), new ControlWin.Input.Stick(0.03f, 0.04f)); // right: 0.05
        advance(1000);
        cal.Feed();
        Assert.Equal(AutoDeadzone.Sampling, cal.Auto);
        advance(1000);
        cal.Feed();

        Assert.Equal(AutoDeadzone.Done, cal.Auto);
        Assert.Equal(0.08f, host.Deadzones[0], 3); // 0.06 + 0.02 margin
        Assert.Equal(0.07f, host.Deadzones[1], 3);
    }

    [Fact]
    public void Auto_stops_when_a_stick_is_pushed()
    {
        var (cal, host, _, advance) = Calibration();
        cal.StartAuto();
        host.Sticks = (new ControlWin.Input.Stick(0.9f, 0f), default);
        cal.Feed();
        advance(5000);
        cal.Feed();

        Assert.Equal(AutoDeadzone.Pushed, cal.Auto);
        Assert.Equal(0f, host.Deadzones[0]);
    }

    [Fact]
    public void A_perfectly_still_stick_gets_no_deadzone()
    {
        Assert.Equal(0f, DeadzoneCalibration.Fit(0f));
        Assert.Equal(0.5f, DeadzoneCalibration.Fit(0.49f)); // capped
    }

    [Fact]
    public void The_deadzone_page_replaces_rows_and_back_returns_to_the_controller_page()
    {
        var (m, _, host) = Create();
        var page = SettingsPages.ControllerPage(host, 0, out var strip);
        m.OpenSub(page);
        m.OpenSub(SettingsPages.DeadzonePage(host, strip));

        Assert.NotNull(m.Tab.Calibration);
        Assert.True(m.Tab.ControllerStrip);
        Assert.Equal("Controller 1  >  Deadzone", m.SubTitle);

        m.Back();
        Assert.Same(page, m.Tab);
        Assert.True(m.InSubPage);
        m.Back();
        Assert.False(m.InSubPage);
    }

    [Fact]
    public void Calibrate_deadzone_row_opens_the_screen()
    {
        var (m, _, host) = Create();
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));

        m.Tab.Items.Single(i => i.Label == "Calibrate deadzone").Activate!();

        Assert.Equal(1, host.CalibrationOpened);
    }

    [Fact]
    public void Headset_device_row_cycles_known_devices_and_keeps_a_missing_saved_one()
    {
        var (m, store, host) = Create();
        host.Devices = ["WH-1000XM4", "Buds"];
        store.Current.HeadsetName = "Old";
        var row = m.Tabs.Single(t => t.Title == "Headset").Items[0];

        Assert.Equal("Old (not found)", row.Value());
        row.Adjust!(1); // wraps to (none)
        Assert.Equal("(none)", row.Value());
        Assert.Equal("", store.Current.HeadsetName);
        row.Adjust!(1);
        Assert.Equal("WH-1000XM4", row.Value());
        Assert.Equal("WH-1000XM4", store.Current.HeadsetName);
    }
}
