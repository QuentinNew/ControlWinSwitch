using ControlWinSwitch.Config;

namespace ControlWinSwitch.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+M", Hotkey.Ctrl | Hotkey.Alt, (uint)'M', "Ctrl+Alt+M")]
    [InlineData(" alt + control + m ", Hotkey.Ctrl | Hotkey.Alt, (uint)'M', "Ctrl+Alt+M")]
    [InlineData("Win+Shift+Space", Hotkey.Win | Hotkey.Shift, 0x20u, "Shift+Win+Space")]
    [InlineData("Ctrl+F5", Hotkey.Ctrl, 0x74u, "Ctrl+F5")]
    [InlineData("F13", 0u, 0x7Cu, "F13")]
    [InlineData("Ctrl+Escape", Hotkey.Ctrl, 0x1Bu, "Ctrl+Esc")]
    [InlineData("Alt+1", Hotkey.Alt, (uint)'1', "Alt+1")]
    public void Parses_and_formats(string text, uint modifiers, uint key, string canonical)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
        Assert.Equal(canonical, hotkey.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("M")]          // no modifier: would steal the key everywhere
    [InlineData("Ctrl+Alt")]   // no key
    [InlineData("Ctrl+M+Alt")] // the key must come last
    [InlineData("Ctrl+Ctrl+M")]
    [InlineData("Ctrl++M")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+F25")]
    public void Rejects_what_is_not_a_shortcut(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Config_defaults_to_ctrl_alt_m_and_clears_an_unreadable_shortcut()
    {
        Assert.Equal("Ctrl+Alt+M", new AppConfig().Sanitize().MenuHotkey);
        Assert.Equal("Ctrl+Shift+K", ConfigStore.Parse("""{ "menuHotkey": "shift+ctrl+k" }""").MenuHotkey);
        Assert.Equal("", ConfigStore.Parse("""{ "menuHotkey": "nonsense" }""").MenuHotkey);
    }
}
