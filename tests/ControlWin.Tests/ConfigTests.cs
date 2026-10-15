using ControlWin.Config;

namespace ControlWin.Tests;

public class ConfigTests
{
    [Fact]
    public void Defaults_match_the_plan()
    {
        var c = new AppConfig().Sanitize();
        Assert.Equal("WH-CH700N", c.HeadsetName);
        Assert.True(c.VirtualPad.Enabled);
        Assert.True(c.VirtualPad.SwapAB);
        Assert.True(c.VirtualPad.SwapXY);
        Assert.Equal(800f, c.Mouse.Speed);
        Assert.Equal(["steam://open/bigpicture", "steam://open/main"], c.Shortcuts.Select(s => s.Target));
    }

    [Fact]
    public void Parses_comments_and_trailing_commas()
    {
        var c = ConfigStore.Parse("""
            {
              // my headset
              "headsetName": "Foo",
              "mouse": { "speed": 1000, },
              "shortcuts": [ { "name": "Notepad", "target": "notepad.exe", }, ],
            }
            """);
        Assert.Equal("Foo", c.HeadsetName);
        Assert.Equal(1000f, c.Mouse.Speed);
        Assert.Equal(0.15f, c.Mouse.Deadzone); // missing field -> default
        Assert.Single(c.Shortcuts);
        Assert.Equal("Notepad", c.Shortcuts[0].Name);
    }

    [Fact]
    public void Missing_sections_fall_back_to_defaults()
    {
        var c = ConfigStore.Parse("{}");
        Assert.Equal("WH-CH700N", c.HeadsetName);
        Assert.Equal(2, c.Shortcuts.Count);
    }

    [Fact]
    public void Null_sections_and_bad_numbers_are_repaired()
    {
        var c = ConfigStore.Parse("""{ "mouse": null, "virtualPad": null, "shortcuts": [ null, { "target": "" }, { "target": "a.exe" } ] }""");
        Assert.NotNull(c.Mouse);
        Assert.NotNull(c.VirtualPad);
        Assert.Single(c.Shortcuts);
        Assert.Equal("a.exe", c.Shortcuts[0].Name); // name defaults to the target

        var wild = ConfigStore.Parse("""{ "mouse": { "speed": 99999999, "deadzone": -3 } }""");
        Assert.Equal(10000f, wild.Mouse.Speed);
        Assert.Equal(0f, wild.Mouse.Deadzone);
    }

    [Fact]
    public void Serialization_round_trips()
    {
        var c = new AppConfig();
        c.Mouse.Speed = 1234;
        c.VirtualPad.SwapAB = false;
        var back = ConfigStore.Parse(ConfigStore.Serialize(c));
        Assert.Equal(1234f, back.Mouse.Speed);
        Assert.False(back.VirtualPad.SwapAB);
    }

    [Fact]
    public void Store_creates_the_file_and_keeps_the_old_config_on_invalid_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ControlWinTests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "config.json");
        try
        {
            var store = new ConfigStore(path);
            Assert.Null(store.Load());
            Assert.True(File.Exists(path));

            store.Current.Mouse.Speed = 500;
            Assert.Null(store.Save());

            File.WriteAllText(path, "{ this is not json");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.False(store.ReloadIfChanged(out var error));
            Assert.NotNull(error);
            Assert.Equal(500f, store.Current.Mouse.Speed);

            // The same broken file is reported only once.
            Assert.False(store.ReloadIfChanged(out error));
            Assert.Null(error);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Watch_list_defaults_to_steam_and_is_cleaned()
    {
        Assert.Equal(["steam.exe"], new AppConfig().Sanitize().VirtualPad.DisableWhileRunning);

        var c = ConfigStore.Parse("""{ "virtualPad": { "disableWhileRunning": [ " a.exe ", "", "A.EXE", "b.exe" ] } }""");
        Assert.Equal(["a.exe", "b.exe"], c.VirtualPad.DisableWhileRunning);

        var none = ConfigStore.Parse("""{ "virtualPad": { "disableWhileRunning": null } }""");
        Assert.Empty(none.VirtualPad.DisableWhileRunning);
    }
}
