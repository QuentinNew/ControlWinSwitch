using ControlWin.Actions;
using ControlWin.Config;
using ControlWin.Input;

namespace ControlWin.Tests;

public class ControllerProfileTests
{
    private static byte[] Full(byte battery)
    {
        var r = new byte[12];
        r[0] = 0x30;
        r[2] = battery;
        r[7] = 0x80; // sticks centered (2048)
        r[8] = 0x80;
        r[10] = 0x80;
        r[11] = 0x80;
        return r;
    }

    [Theory]
    [InlineData(0x90, 4, true)]
    [InlineData(0x80, 4, false)]
    [InlineData(0x60, 3, false)]
    [InlineData(0x41, 2, false)]
    [InlineData(0x20, 1, false)]
    [InlineData(0x00, 0, false)]
    public void Full_report_carries_the_battery_level_and_charging_flag(int byte2, int level, bool charging)
    {
        Assert.True(ProReportParser.TryParse(Full((byte)byte2), out var report));

        Assert.Equal(level, report.Battery);
        Assert.Equal(charging, report.Charging);
    }

    [Fact]
    public void Simple_report_has_no_battery()
    {
        byte[] simple = [0x3F, 0, 0, 8, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0x80];

        Assert.True(ProReportParser.TryParse(simple, out var report));
        Assert.Equal(-1, report.Battery);
    }

    [Fact]
    public void State_keeps_the_last_battery_and_forgets_it_on_reset()
    {
        var state = new ControllerState();
        Assert.Equal(-1, state.Battery);

        ProReportParser.TryParse(Full(0x70), out var full);
        state.Update(full);
        Assert.Equal(3, state.Battery);
        Assert.True(state.Charging);

        byte[] simple = [0x3F, 0, 0, 8, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0x80];
        ProReportParser.TryParse(simple, out var plain);
        state.Update(plain);
        Assert.Equal(3, state.Battery); // a simple report does not carry it: keep the last value

        state.Reset();
        Assert.Equal(-1, state.Battery);
        Assert.False(state.Charging);
    }

    [Fact]
    public void Device_info_reply_yields_the_mac_address()
    {
        var reply = new byte[49];
        reply[0] = 0x21;
        reply[13] = 0x82;
        reply[14] = 0x02;
        new byte[] { 0xA4, 0x5C, 0x01, 0x02, 0x03, 0xFE }.CopyTo(reply, 19);

        Assert.True(ProCommands.TryParseDeviceInfo(reply, out var mac));
        Assert.Equal("A4:5C:01:02:03:FE", mac);
    }

    [Fact]
    public void Other_reports_and_an_empty_mac_are_not_device_info()
    {
        Assert.False(ProCommands.TryParseDeviceInfo(Full(0x80), out _));

        var reply = new byte[49];
        reply[0] = 0x21;
        reply[13] = 0x82;
        reply[14] = 0x02; // MAC bytes all zero
        Assert.False(ProCommands.TryParseDeviceInfo(reply, out _));

        reply[14] = 0x03; // another subcommand reply
        reply[19] = 1;
        Assert.False(ProCommands.TryParseDeviceInfo(reply, out _));
    }

    [Fact]
    public void Request_device_info_is_subcommand_02_in_report_01()
    {
        var report = ProCommands.RequestDeviceInfo(3, 49);

        Assert.Equal(0x01, report[0]);
        Assert.Equal(0x02, report[10]);
        Assert.Equal(49, report.Length);
    }

    [Fact]
    public void Controller_profiles_round_trip_and_drop_nulls()
    {
        var c = ConfigStore.Parse("""{ "controllers": { "A4:5C:01:02:03:FE": { "name": "  Quentin " }, "bad": null } }""");

        Assert.Equal("Quentin", c.Controllers["A4:5C:01:02:03:FE"].Name);
        Assert.False(c.Controllers.ContainsKey("bad"));

        var back = ConfigStore.Parse(ConfigStore.Serialize(c));
        Assert.Equal("Quentin", back.Controllers["A4:5C:01:02:03:FE"].Name);
    }

    [Fact]
    public void Tile_battery_text_names_the_level_and_charging()
    {
        Assert.Equal("Full", new ControlWin.UI.SlotTile(true, "x", 4, false, 0).BatteryText);
        Assert.Equal("Low, charging", new ControlWin.UI.SlotTile(true, "x", 2, true, 0).BatteryText);
        Assert.Equal("Unknown", new ControlWin.UI.SlotTile(true, "x", -1, false, 0).BatteryText);
    }

    [Fact]
    public void Subcommand_replies_count_as_input_so_battery_is_always_fresh()
    {
        var reply = Full(0x80);
        reply[0] = 0x21;

        Assert.True(ProReportParser.TryParse(reply, out var report));
        Assert.Equal(4, report.Battery);
    }

    [Fact]
    public void Controllers_default_to_none_and_survive_null()
    {
        Assert.Empty(ConfigStore.Parse("{}").Controllers);
        Assert.Empty(ConfigStore.Parse("""{ "controllers": null }""").Controllers);
    }

    [Fact]
    public void Picture_file_names_are_safe_for_mac_and_instance_ids()
    {
        Assert.Equal("A4_5C_01_02_03_FE.png", ControllerPictures.FileName("A4:5C:01:02:03:FE"));

        var name = ControllerPictures.FileName("dev:HID" + (char)92 + "{0000}_VID&0002057e" + (char)92 + "b&14c922c8");
        Assert.DoesNotContain(name, c => c is ':' or '&' or '{' or '}' || c == (char)92);
        Assert.EndsWith(".png", name);
    }

    [Fact]
    public void A_controller_without_a_picture_reports_none()
    {
        var old = ControllerPictures.Folder;
        ControllerPictures.Folder = Path.Combine(Path.GetTempPath(), "cw-pictures-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(ControllerPictures.Has("A4:5C:01:02:03:FE"));
            Assert.Equal(0, ControllerPictures.Get("A4:5C:01:02:03:FE"));
        }
        finally
        {
            ControllerPictures.Folder = old;
        }
    }
}
