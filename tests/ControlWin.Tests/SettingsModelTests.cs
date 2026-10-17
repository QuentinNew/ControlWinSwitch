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
        public void WatchListChanged() => WatchChanges++;
        public string ConfigPath => "x";
        public string HeadsetStatus => "Connected";
        public void ConfigChanged() => Changes++;
        public void EditConfig() => Edits++;
        public void ReloadConfig() { }
        public void OpenLog() { }
        public void ToggleHeadset() { }
        public void OpenBluetoothSettings() { }
        public void EditRadialMenu() { }
        public HashSet<int> Connected = [0];
        public bool ControllerConnected(int slot) => Connected.Contains(slot);
        public List<int> Rumbled = [];
        public void RumbleController(int slot) => Rumbled.Add(slot);
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
        public string Picture = "";
        public string ControllerPictureName(int slot) => Picture;
        public void ChooseControllerPicture(int slot) => Picture = "me.png";
        public void RemoveControllerPicture(int slot) => Picture = "";
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
    public void Controller_page_edits_name_and_picture_and_goes_back()
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

        var picture = items.Single(i => i.Label == "Choose picture...");
        Assert.False(picture.ShowsClear);
        Assert.Equal("None", picture.Value());
        picture.Activate!();
        Assert.Equal("me.png", host.Picture);
        Assert.Equal("me.png", picture.Value());
        Assert.True(picture.ShowsClear);
        picture.Adjust!(1); // right moves the focus onto the cross: nothing is removed yet
        Assert.True(picture.ClearFocused);
        Assert.Equal("me.png", host.Picture);
        picture.Adjust!(-1); // left goes back to the row
        Assert.False(picture.ClearFocused);
        picture.Adjust!(1);
        picture.Activate!(); // A on the cross removes the picture
        Assert.Equal("", host.Picture);
        Assert.False(picture.ShowsClear);
        Assert.False(picture.ClearFocused);

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
    public void Left_right_on_the_page_strip_switches_between_connected_controllers_only()
    {
        var (m, _, host) = Create();
        host.Connected = [0, 2];
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out var strip));
        m.Move(-1); // up to the strip
        Assert.Equal(SettingKind.Strip, m.CurrentItem!.Kind);

        m.Adjust(1);
        Assert.Equal(2, strip.Slot); // slot 1 is empty: skipped
        Assert.Equal("Controller 3", m.SubTitle);

        m.Adjust(1);
        Assert.Equal(0, strip.Slot); // wraps
        m.Adjust(-1);
        Assert.Equal(2, strip.Slot);
    }

    [Fact]
    public void The_strip_stays_put_when_no_other_controller_is_connected()
    {
        var (m, _, host) = Create();
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out var strip));
        m.Move(-1);

        m.Adjust(1);

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
    public void A_on_the_row_still_chooses_and_leaving_the_row_drops_the_cross_focus()
    {
        var (m, _, host) = Create();
        host.Picture = "me.png";
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));
        var picture = m.Tab.Items.Single(i => i.Label == "Choose picture...");
        while (m.CurrentItem != picture) m.Move(1);

        m.Activate(); // not on the cross: asks for another picture
        Assert.Equal("me.png", host.Picture);
        Assert.False(picture.ClearActive);

        m.Adjust(1);
        Assert.True(picture.ClearActive);
        m.Move(1); // down to the next row
        Assert.False(picture.ClearFocused);
    }

    [Fact]
    public void The_cross_cannot_be_focused_when_there_is_no_picture()
    {
        var (m, _, host) = Create();
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));
        var picture = m.Tab.Items.Single(i => i.Label == "Choose picture...");

        picture.Adjust!(1);

        Assert.False(picture.ClearFocused);
    }

    [Fact]
    public void Selection_inside_a_sub_page_does_not_move_the_tab_selection()
    {
        var (m, _, host) = Create();
        int before = m.Selected;
        m.OpenSub(SettingsPages.ControllerPage(host, 0, out _));
        m.Move(1);
        m.Back();

        Assert.Equal(before, m.Selected);
    }

    [Fact]
    public void General_tab_has_a_launch_on_startup_toggle()
    {
        var (m, _, host) = Create();
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
        Assert.Equal(["General", "Layout", "Controller", "Virtual Pad", "Mouse", "Headset"], m.Tabs.Select(t => t.Title));
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
        m.SetTab(2);
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
        m.SetTab(2);
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
        m.SetTab(2);
        m.Move(1);
        Assert.Equal("Add new Pro Controller", m.CurrentItem!.Label);

        m.Activate();

        Assert.Equal(1, host.Added);
    }

    [Fact]
    public void Virtual_pad_tab_has_a_rumble_percent_from_0_to_100()
    {
        var (m, store, host) = Create();
        m.SetTab(3);
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
        Assert.Equal(4, m.Selected);
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
        m.SetTab(3); // Virtual Pad
        m.Move(1);   // Swap A / B
        Assert.True(store.Current.VirtualPad.SwapAB);

        m.Activate();
        Assert.False(store.Current.VirtualPad.SwapAB);
        Assert.Equal(1, host.Changes);

        m.Adjust(1); // left / right toggles too
        Assert.True(store.Current.VirtualPad.SwapAB);
    }

    [Fact]
    public void Pad_toggle_goes_through_the_host_not_the_config()
    {
        var (m, store, host) = Create();
        m.SetTab(3);
        Assert.Equal(0, m.Selected);

        m.Activate();
        Assert.False(host.Pad);
        Assert.True(store.Current.VirtualPad.Enabled); // the host decides whether it is persisted
    }

    [Fact]
    public void Watch_list_is_edited_as_comma_separated_text()
    {
        var (m, store, host) = Create();
        m.SetTab(3);
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
        m.SetTab(4); // Mouse, first row = Speed (800, step 100, 100..3000)
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
        m.SetTab(4);
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
        m.SetTab(4);
        m.Move(1);
        Assert.Equal(1, m.Selected);
        m.SetTab(0);
        m.SetTab(4);
        Assert.Equal(1, m.Selected);
    }

    [Fact]
    public void Text_row_opens_the_editor_and_commit_updates_the_config()
    {
        var (m, store, host) = Create();
        m.SetTab(5); // Headset
        Assert.Equal(0, m.Selected); // device name is the first selectable row
        m.Activate();
        Assert.Equal("WH-CH700N", host.Editing);

        host.Commit!("  My Headset ");
        Assert.Equal("My Headset", store.Current.HeadsetName);
        Assert.Equal(1, host.Changes);
    }
}
