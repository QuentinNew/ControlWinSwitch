using ControlWinSwitch.Actions;

namespace ControlWinSwitch.Tests;

public class ScreenshotTests
{
    private static readonly DateTime Time = new(2026, 10, 24, 15, 30, 12);

    [Fact]
    public void Name_is_the_window_title_and_the_time()
    {
        Assert.Equal("Elden Ring 2026-10-24 153012", Screenshot.FileName("Elden Ring", Time));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("?*:")]
    public void Untitled_windows_are_called_Screenshot(string title)
    {
        Assert.Equal("Screenshot 2026-10-24 153012", Screenshot.FileName(title, Time));
    }

    [Fact]
    public void Unsafe_characters_become_single_spaces()
    {
        Assert.Equal("a b c d 2026-10-24 153012", Screenshot.FileName("a: b/\\c\t|d.", Time));
    }

    [Fact]
    public void Long_titles_are_cut()
    {
        string name = Screenshot.FileName(new string('x', 300), Time);
        Assert.Equal(80 + " 2026-10-24 153012".Length, name.Length);
    }

    [Fact]
    public void Taken_names_get_a_number()
    {
        var taken = new HashSet<string> { @"C:\s\a.png", @"C:\s\a (2).png" };
        Assert.Equal(@"C:\s\a (3).png", Screenshot.UniquePath(@"C:\s", "a", taken.Contains));
        Assert.Equal(@"C:\s\b.png", Screenshot.UniquePath(@"C:\s", "b", taken.Contains));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("shots", null)]
    [InlineData(@"D:\Shots\", @"D:\Shots")]
    [InlineData("\"D:\\My Shots\"", @"D:\My Shots")]
    public void Custom_folder_must_be_a_full_path(string? configured, string? expected)
    {
        Assert.Equal(expected, Screenshot.CustomFolder(configured));
    }

    [Fact]
    public void Custom_folder_expands_variables()
    {
        string expected = Environment.GetEnvironmentVariable("USERPROFILE") + @"\Shots";
        Assert.Equal(expected, Screenshot.CustomFolder(@"%USERPROFILE%\Shots"));
    }
}
