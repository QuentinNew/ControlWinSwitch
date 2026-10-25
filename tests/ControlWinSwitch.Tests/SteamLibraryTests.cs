using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.Tests;

public class SteamLibraryTests
{
    private static string Game(string id, string name, long played) =>
        $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{name}\"\n\t\"LastPlayed\"\t\t\"{played}\"\n}}\n";

    [Fact]
    public void A_manifest_gives_the_game()
    {
        var game = SteamLibrary.ParseManifest(Game("1003590", "Tetris Effect: Connected", 1792074976), @"C:\Steam");

        Assert.NotNull(game);
        Assert.Equal("1003590", game!.AppId);
        Assert.Equal("Tetris Effect: Connected", game.Name);
        Assert.Equal(1792074976L, game.LastPlayed);
        Assert.Equal("steam://rungameid/1003590", game.LaunchTarget);
    }

    [Fact]
    public void Games_never_played_and_steam_tools_are_skipped()
    {
        Assert.Null(SteamLibrary.ParseManifest(Game("1", "Game", 0), @"C:\Steam"));
        Assert.Null(SteamLibrary.ParseManifest(Game("228980", "Steamworks Common Redistributables", 5), @"C:\Steam"));
        Assert.Null(SteamLibrary.ParseManifest(Game("2", "Proton 9.0", 5), @"C:\Steam"));
    }

    [Fact]
    public void Recent_games_come_from_every_library_newest_first_with_their_cover()
    {
        string root = Path.Combine(Path.GetTempPath(), "cw-steam-" + Guid.NewGuid().ToString("N"));
        string second = Path.Combine(root, "Library2");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            Directory.CreateDirectory(Path.Combine(second, "steamapps"));
            Directory.CreateDirectory(Path.Combine(root, "appcache", "librarycache", "10"));
            File.WriteAllText(Path.Combine(root, "appcache", "librarycache", "10", "library_600x900.jpg"), "x");
            Directory.CreateDirectory(Path.Combine(root, "appcache", "librarycache", "30", "2ba105370a00877459144db0dd68af5f2d338429"));
            File.WriteAllText(Path.Combine(root, "appcache", "librarycache", "30", "2ba105370a00877459144db0dd68af5f2d338429", "library_capsule.jpg"), "x");
            Directory.CreateDirectory(Path.Combine(root, "appcache", "librarycache", "20", "4c811a41d2b3c87481cd5a265b45c7f347b6118e"));
            File.WriteAllText(Path.Combine(root, "appcache", "librarycache", "20", "4c811a41d2b3c87481cd5a265b45c7f347b6118e", "logo.png"), "x");
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                "\"libraryfolders\"\n{\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + second.Replace("\\", "\\\\") + "\"\n\t}\n}\n");
            File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_10.acf"), Game("10", "Old", 100));
            File.WriteAllText(Path.Combine(second, "steamapps", "appmanifest_20.acf"), Game("20", "New", 300));
            File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_30.acf"), Game("30", "Mid", 200));

            var recent = SteamLibrary.Recent(2, root);

            Assert.Equal(["New", "Mid"], recent.Select(g => g.Name));
            var all = SteamLibrary.Recent(10, root);
            Assert.EndsWith("library_600x900.jpg", all.Single(g => g.AppId == "10").CoverPath);
            Assert.EndsWith("library_capsule.jpg", all.Single(g => g.AppId == "30").CoverPath);
            Assert.Null(all.Single(g => g.AppId == "20").CoverPath); // only a logo in the cache
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

[Collection("native windows")]
public class RadialEditorSteamTests
{
    private static (MenuEditorWindow Window, MenuNodeConfig Node) Open(string name)
    {
        var shortcut = new MenuNodeConfig { Type = "shortcut", Slot = 0, Name = name, Target = "" };
        var window = new MenuEditorWindow();
        window.DebugInit(MenuLayouts.Radial, [shortcut, new MenuNodeConfig { Type = "settings", Slot = 1 }], []);
        var draft = window.DebugModel.Nodes[0]; // the editor works on a copy
        window.DebugModel.Select(draft);
        return (window, draft);
    }

    [Fact]
    public void Choosing_a_steam_game_sets_target_name_and_cover()
    {
        var (window, shortcut) = Open("New shortcut");

        window.ApplySteamGame(new SteamGame("1003590", "Tetris Effect", 1, @"C:\cache\cover.jpg"));

        Assert.Equal("steam://rungameid/1003590", shortcut.Target);
        Assert.Equal("Tetris Effect", shortcut.Name);
        Assert.Equal("image", shortcut.Icon!.Kind);
        Assert.Equal(@"C:\cache\cover.jpg", shortcut.Icon.Path);
    }

    [Fact]
    public void Choosing_another_game_updates_the_name_and_target_again()
    {
        var (window, shortcut) = Open("My game");

        window.ApplySteamGame(new SteamGame("5", "First", 1, null));
        window.ApplySteamGame(new SteamGame("6", "Second", 1, null));

        Assert.Equal("Second", shortcut.Name);
        Assert.Equal("steam://rungameid/6", shortcut.Target);
        Assert.Null(shortcut.Icon);
    }
}
