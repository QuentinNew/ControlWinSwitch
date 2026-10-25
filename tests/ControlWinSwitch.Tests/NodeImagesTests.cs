using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;

namespace ControlWinSwitch.Tests;

public class NodeImagesTests
{
    private static MenuNodeConfig Image(string path, string kind = "image") =>
        new() { Type = MenuNodeTypes.Shortcut, Target = "x", Icon = new MenuIconConfig { Kind = kind, Path = path } };

    [Fact]
    public void Name_is_twelve_hex_characters_of_the_contents_and_stable()
    {
        string name = NodeImages.FileName([1, 2, 3]);
        Assert.Equal(name, NodeImages.FileName([1, 2, 3]));
        Assert.NotEqual(name, NodeImages.FileName([1, 2, 4]));
        Assert.True(NodeImages.IsCacheName(name));
        Assert.Equal(16, name.Length);
    }

    [Theory]
    [InlineData("3f9a1c5e7b20.png", true)]
    [InlineData("3F9A1C5E7B20.png", false)]
    [InlineData("3f9a1c5e7b2.png", false)]
    [InlineData("3f9a1c5e7b20.jpg", false)]
    [InlineData("notes.txt", false)]
    public void Only_hash_named_pngs_are_cache_names(string name, bool expected)
    {
        Assert.Equal(expected, NodeImages.IsCacheName(name));
    }

    [Theory]
    [InlineData(600, 900, 0, 150, 600)]
    [InlineData(900, 600, 150, 0, 600)]
    [InlineData(64, 64, 0, 0, 64)]
    [InlineData(101, 100, 0, 0, 100)]
    public void Crop_is_the_centered_square(int w, int h, int x, int y, int side)
    {
        Assert.Equal((x, y, side), NodeImages.CropSquare(w, h));
    }

    [Fact]
    public void Cached_pictures_are_recognized_with_or_without_env_vars()
    {
        string file = Path.Combine(NodeImages.Folder, "3f9a1c5e7b20.png");
        Assert.True(NodeImages.IsCached(file));
        Assert.True(NodeImages.IsCached(@"%APPDATA%\ControlWinSwitch\nodes\3f9a1c5e7b20.png"));
        Assert.False(NodeImages.IsCached(@"C:\icones\3f9a1c5e7b20.png"));
        Assert.False(NodeImages.IsCached(Path.Combine(NodeImages.Folder, "mine.png")));
        Assert.False(NodeImages.IsCached(""));
    }

    [Fact]
    public void Cached_keeps_a_path_that_is_not_a_picture()
    {
        Assert.Equal("", NodeImages.Cached(""));
        Assert.Equal(@"C:\nowhere\missing.png", NodeImages.Cached(@"C:\nowhere\missing.png"));
        Assert.Equal(@"%APPDATA%\ControlWinSwitch\nodes\3f9a1c5e7b20.png", NodeImages.Cached(@"%APPDATA%\ControlWinSwitch\nodes\3f9a1c5e7b20.png"));
    }

    [Fact]
    public void Referenced_collects_image_icons_of_the_cache_in_folders_too()
    {
        var folder = new MenuNodeConfig
        {
            Type = MenuNodeTypes.Folder,
            Children = [Image(@"%APPDATA%\ControlWinSwitch\nodes\bbbbbbbbbbbb.png")],
        };
        var names = NodeImages.Referenced(
        [
            Image(@"%APPDATA%\ControlWinSwitch\nodes\aaaaaaaaaaaa.png"),
            Image(@"C:\icones\steam.png"),
            Image(@"%APPDATA%\ControlWinSwitch\nodes\cccccccccccc.png", kind: "glyph"),
            folder,
        ]);

        Assert.Equal(["aaaaaaaaaaaa.png", "bbbbbbbbbbbb.png"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Unused_lists_only_unreferenced_cache_pictures()
    {
        var referenced = new HashSet<string> { "aaaaaaaaaaaa.png" };
        var unused = NodeImages.Unused(["aaaaaaaaaaaa.png", "bbbbbbbbbbbb.png", "readme.txt", "mine.png"], referenced);
        Assert.Equal(["bbbbbbbbbbbb.png"], unused);
    }
}
