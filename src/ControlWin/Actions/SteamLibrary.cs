using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ControlWin.Actions;

internal sealed record SteamGame(string AppId, string Name, long LastPlayed, string? CoverPath)
{
    /// <summary>What starts the game: Steam handles it from there.</summary>
    public string LaunchTarget => $"steam://rungameid/{AppId}";
}

/// <summary>
/// Finds the installed Steam games played most recently, from the files Steam keeps on disk:
/// <c>steamapps\appmanifest_*.acf</c> (name, LastPlayed) in every library folder.
/// </summary>
internal static partial class SteamLibrary
{
    // Steam tools that sit in the library next to the games.
    private static readonly string[] NotGames = ["228980", "1493710", "1628350", "1391110", "1070560", "1826330", "1887720"];

    private static readonly string[] NotGamePrefixes = ["Steamworks Common", "Proton ", "Steam Linux Runtime", "Steam Client"];

    [GeneratedRegex("\"(?<key>[^\"]+)\"\\s+\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex Pair();

    /// <summary>Steam's install folder (registry), or null when Steam is not installed.</summary>
    public static string? FindSteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
                return path.Replace('/', '\\');
        }
        catch
        {
            // Registry unavailable: fall through to the default location.
        }

        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>The most recently played games, newest first.</summary>
    public static List<SteamGame> Recent(int max, string? steamRoot = null)
    {
        var games = new List<SteamGame>();
        try
        {
            steamRoot ??= FindSteamRoot();
            if (steamRoot is null) return games;

            foreach (string library in LibraryFolders(steamRoot))
            {
                string apps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(apps)) continue;

                foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    var game = ParseManifest(File.ReadAllText(manifest), steamRoot);
                    if (game is not null && !games.Any(g => g.AppId == game.AppId)) games.Add(game);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Steam library scan failed", ex);
        }

        return games.OrderByDescending(g => g.LastPlayed).Take(max).ToList();
    }

    /// <summary>Library folders: Steam's own folder plus every "path" in libraryfolders.vdf.</summary>
    internal static List<string> LibraryFolders(string steamRoot)
    {
        var folders = new List<string> { steamRoot };
        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return folders;

        foreach (Match m in Pair().Matches(File.ReadAllText(vdf)))
        {
            if (m.Groups["key"].Value != "path") continue;
            string path = Unescape(m.Groups["value"].Value);
            if (!folders.Contains(path, StringComparer.OrdinalIgnoreCase)) folders.Add(path);
        }
        return folders;
    }

    /// <summary>One appmanifest file; null for tools, entries that were never played, or unreadable ones.</summary>
    internal static SteamGame? ParseManifest(string text, string steamRoot)
    {
        string? appId = null, name = null;
        long lastPlayed = 0;
        foreach (Match m in Pair().Matches(text))
        {
            string value = Unescape(m.Groups["value"].Value);
            switch (m.Groups["key"].Value)
            {
                case "appid": appId ??= value; break;
                case "name": name ??= value; break;
                case "LastPlayed":
                    long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out lastPlayed);
                    break;
            }
        }

        if (appId is null || string.IsNullOrWhiteSpace(name) || lastPlayed <= 0) return null;
        if (!appId.All(char.IsAsciiDigit) || NotGames.Contains(appId)) return null;
        if (NotGamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return null;

        return new SteamGame(appId, name.Trim(), lastPlayed, CoverOf(appId, steamRoot));
    }

    /// <summary>The cached portrait cover of a game, when Steam has downloaded one.</summary>
    internal static string? CoverOf(string appId, string steamRoot)
    {
        string cache = Path.Combine(steamRoot, "appcache", "librarycache");
        string[] candidates =
        [
            Path.Combine(cache, appId, "library_600x900.jpg"),
            Path.Combine(cache, appId + "_library_600x900.jpg"),
        ];
        foreach (string file in candidates)
            if (File.Exists(file)) return file;
        return null;
    }

    private static string Unescape(string value) => value.Replace("\\\\", "\\").Replace("\\\"", "\"");
}
