using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlWin.Config;

internal sealed class VirtualPadConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>What the pad presents itself as: <see cref="PadTypes.Xbox"/> or <see cref="PadTypes.Ps4"/>.</summary>
    public string Type { get; set; } = PadTypes.Xbox;

    /// <summary>Label-based mapping: Switch A = Xbox A, B = B, X = X, Y = Y.</summary>
    public bool SwapAB { get; set; } = true;
    public bool SwapXY { get; set; } = true;

    /// <summary>While the pad is on, hides each physical Pro Controller from other apps (needs HidHide installed).</summary>
    public bool HidePhysical { get; set; } = true;

    /// <summary>Strength of the controller rumble when a game vibrates the virtual pad: 0 (none) to 100.</summary>
    public int Rumble { get; set; } = 100;

    /// <summary>The pad is switched off while one of these processes runs (empty = feature off).</summary>
    public List<string> DisableWhileRunning { get; set; } = ["steam.exe"];
}

internal static class PadTypes
{
    public const string Xbox = "xbox", Ps4 = "ps4";
}

/// <summary>What the user set for one physical controller, keyed by its id (MAC address) in <see cref="AppConfig.Controllers"/>.</summary>
internal sealed class ControllerProfile
{
    /// <summary>Shown under the controller's tile; empty = the default "Controller N".</summary>
    public string Name { get; set; } = "";

    /// <summary>Radial deadzone of each stick on the virtual pad (0 = none, 0.5 max): hides a stick that drifts around its center.</summary>
    public float LeftDeadzone { get; set; }
    public float RightDeadzone { get; set; }

    public bool IsDefault => Name.Length == 0 && LeftDeadzone <= 0f && RightDeadzone <= 0f;
}

internal static class ControllerKinds
{
    public const string Pro = "pro", JoyConLeft = "joyConL", JoyConRight = "joyConR";

    /// <summary>The name shown for a controller the user did not name.</summary>
    public static string DefaultName(string kind) => kind switch
    {
        JoyConLeft => "Joy-Con (L)",
        JoyConRight => "Joy-Con (R)",
        _ => "Pro Controller",
    };
}

/// <summary>
/// A controller that connected to this PC, keyed by its MAC address in <see cref="AppConfig.KnownControllers"/>: the
/// Controller tab lists the ones not connected, last connected first.
/// </summary>
internal sealed class KnownController
{
    /// <summary>One of <see cref="ControllerKinds"/>.</summary>
    public string Kind { get; set; } = ControllerKinds.Pro;

    /// <summary>When it was last connected (UTC): the time it disconnected, or for one still connected, when it connected.</summary>
    public DateTime LastSeen { get; set; }
}

internal sealed class MouseConfig
{
    public float Speed { get; set; } = 800f;           // px/s at full deflection
    public float FastMultiplier { get; set; } = 2.5f;  // ZR
    public float SlowMultiplier { get; set; } = 0.3f;  // ZL
    public float Deadzone { get; set; } = 0.15f;
    public float ScrollSpeed { get; set; } = 12f;      // notches/s at full deflection
    public float DpadSpeed { get; set; } = 120f;       // px/s once the D-pad is held
}

internal sealed class ShortcutConfig
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Args { get; set; }
    public string? WorkingDir { get; set; }

    /// <summary>Start the program with administrator rights (no UAC prompt while ControlWin itself runs elevated).</summary>
    public bool? RunAsAdmin { get; set; }
}

/// <summary>Values of <see cref="MenuNodeConfig.Type"/>.</summary>
internal static class MenuNodeTypes
{
    public const string Close = "close", Quit = "quit", Sleep = "sleep", Restart = "restart", Shutdown = "shutdown",
        Settings = "settings", Headset = "headset", MouseMode = "mouseMode", VirtualPad = "virtualPad",
        Folder = "folder", Shortcut = "shortcut", AddController = "addController", CloseWindow = "closeWindow";

    public static readonly string[] All =
        [Close, Quit, Sleep, Restart, Shutdown, Settings, Headset, MouseMode, VirtualPad, AddController, CloseWindow, Folder, Shortcut];
}

/// <summary>Icon of a radial node. Only "glyph" is drawn for now; "auto" and "image" are kept as written.</summary>
internal sealed class MenuIconConfig
{
    public string Kind { get; set; } = "glyph";

    /// <summary>"glyph": Segoe Fluent Icons code point in hex (for example "E7E8").</summary>
    public string? Glyph { get; set; }

    /// <summary>"image": path of a .png / .ico file.</summary>
    public string? Path { get; set; }
}

/// <summary>One node of a menu tree (radial or list): a built-in action, a folder or an app shortcut.</summary>
internal sealed class MenuNodeConfig
{
    public string Type { get; set; } = "";

    /// <summary>Radial: position on the ring, 0-7 on the root, 0-6 in a folder (clockwise after the Back node). List: index in the list.</summary>
    public int Slot { get; set; } = -1;

    /// <summary>Overrides the default label.</summary>
    public string? Name { get; set; }

    /// <summary>"#RRGGBB".</summary>
    public string? Color { get; set; }

    public MenuIconConfig? Icon { get; set; }

    // Shortcuts.
    public string? Target { get; set; }
    public string? Args { get; set; }
    public string? WorkingDir { get; set; }

    /// <summary>Shortcuts: start the program with administrator rights.</summary>
    public bool? RunAsAdmin { get; set; }

    /// <summary>Folders only; folders do not nest.</summary>
    public List<MenuNodeConfig>? Children { get; set; }

    public MenuNodeConfig Clone() => new()
    {
        Type = Type,
        Slot = Slot,
        Name = Name,
        Color = Color,
        Icon = Icon is null ? null : new MenuIconConfig { Kind = Icon.Kind, Glyph = Icon.Glyph, Path = Icon.Path },
        Target = Target,
        Args = Args,
        WorkingDir = WorkingDir,
        RunAsAdmin = RunAsAdmin,
        Children = Children?.Select(c => c.Clone()).ToList(),
    };
}

/// <summary>Values of <see cref="AppConfig.MenuLayout"/>.</summary>
internal static class MenuLayouts
{
    public const string List = "list", Radial = "radial";
}

internal sealed class AppConfig
{
    /// <summary>Part of the headset's audio endpoint name; empty = no headset set up.</summary>
    public string HeadsetName { get; set; } = "";

    /// <summary>How the main menu is drawn: <see cref="MenuLayouts.List"/> or <see cref="MenuLayouts.Radial"/>.</summary>
    public string MenuLayout { get; set; } = MenuLayouts.List;

    /// <summary>Show the connected controllers (picture and battery) in the menu: in the center of the radial ring, above the list's footer.</summary>
    public bool MenuShowControllers { get; set; } = true;

    /// <summary>Global keyboard shortcut that opens / closes the menu, as <see cref="Hotkey"/> text ("Ctrl+Alt+M"); empty = none.</summary>
    public string MenuHotkey { get; set; } = "Ctrl+Alt+M";

    /// <summary>Radial menu: the stick must travel from "pushed" to "centered" within this time to count as a release.</summary>
    public int RadialReleaseMs { get; set; } = 100;

    /// <summary>
    /// Radial menu: degrees by which the straight up / down / left / right nodes are made easier to hit
    /// (diagonals are naturally favoured by an octagonal stick gate).
    /// </summary>
    public int RadialCardinalBias { get; set; } = 12;

    public VirtualPadConfig VirtualPad { get; set; } = new();
    public MouseConfig Mouse { get; set; } = new();
    /// <summary>The radial menu tree (root nodes). Missing in the file = seeded from the defaults.</summary>
    public List<MenuNodeConfig>? RadialMenu { get; set; }

    /// <summary>The list menu tree (root nodes, in display order). Missing in the file = seeded from the defaults.</summary>
    public List<MenuNodeConfig>? ListMenu { get; set; }

    /// <summary>What <see cref="Sanitize"/> had to repair in <see cref="RadialMenu"/> and <see cref="ListMenu"/> (logged by the store).</summary>
    [JsonIgnore]
    public List<string> RepairNotes { get; } = [];

    /// <summary>Per-controller name, by controller id. The picture lives in %APPDATA%\ControlWin\controllers\.</summary>
    public Dictionary<string, ControllerProfile> Controllers { get; set; } = [];

    /// <summary>The controllers that connected to this PC, by MAC address (the paired list of the Controller tab).</summary>
    public Dictionary<string, KnownController> KnownControllers { get; set; } = [];

    /// <summary>Repairs what a hand-edited file can get wrong: nulls and out-of-range numbers.</summary>
    public AppConfig Sanitize()
    {
        HeadsetName ??= "";
        MenuLayout = string.Equals(MenuLayout?.Trim(), MenuLayouts.Radial, StringComparison.OrdinalIgnoreCase)
            ? MenuLayouts.Radial : MenuLayouts.List;
        MenuHotkey = Hotkey.TryParse(MenuHotkey, out var hotkey) ? hotkey.ToString() : "";
        RadialReleaseMs = Math.Clamp(RadialReleaseMs, 20, 500);
        RadialCardinalBias = Math.Clamp(RadialCardinalBias, 0, 25);
        VirtualPad ??= new();
        VirtualPad.Type = string.Equals(VirtualPad.Type?.Trim(), PadTypes.Ps4, StringComparison.OrdinalIgnoreCase) ? PadTypes.Ps4 : PadTypes.Xbox;
        VirtualPad.Rumble = Math.Clamp(VirtualPad.Rumble, 0, 100);
        VirtualPad.DisableWhileRunning ??= [];
        VirtualPad.DisableWhileRunning = VirtualPad.DisableWhileRunning
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Mouse ??= new();
        Controllers ??= [];
        foreach (var key in Controllers.Where(c => c.Value is null).Select(c => c.Key).ToList()) Controllers.Remove(key);
        foreach (var profile in Controllers.Values)
        {
            profile.Name = (profile.Name ?? "").Trim();
            profile.LeftDeadzone = Clamp(profile.LeftDeadzone, 0f, 0.5f, 0f);
            profile.RightDeadzone = Clamp(profile.RightDeadzone, 0f, 0.5f, 0f);
        }
        KnownControllers ??= [];
        foreach (var key in KnownControllers.Where(c => c.Value is null).Select(c => c.Key).ToList()) KnownControllers.Remove(key);
        foreach (var known in KnownControllers.Values)
            if (known.Kind is not (ControllerKinds.JoyConLeft or ControllerKinds.JoyConRight)) known.Kind = ControllerKinds.Pro;
        RepairNotes.Clear();
        RadialMenu = MenuRepair.Run(RadialMenu, MenuLayouts.Radial, RepairNotes, "Radial menu");
        ListMenu = MenuRepair.Run(ListMenu, MenuLayouts.List, RepairNotes, "List menu");

        Mouse.Speed = Clamp(Mouse.Speed, 50f, 10000f, 800f);
        Mouse.FastMultiplier = Clamp(Mouse.FastMultiplier, 1f, 10f, 2.5f);
        Mouse.SlowMultiplier = Clamp(Mouse.SlowMultiplier, 0.05f, 1f, 0.3f);
        Mouse.Deadzone = Clamp(Mouse.Deadzone, 0f, 0.8f, 0.15f);
        Mouse.ScrollSpeed = Clamp(Mouse.ScrollSpeed, 1f, 100f, 12f);
        Mouse.DpadSpeed = Clamp(Mouse.DpadSpeed, 10f, 2000f, 120f);
        return this;
    }

    private static float Clamp(float v, float min, float max, float fallback) =>
        float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>Loads and saves config.json; reloads when the file changed on disk.</summary>
internal sealed class ConfigStore
{
    private DateTime _stamp;

    public ConfigStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ControlWin", "config.json");
    }

    public string Path { get; }

    public AppConfig Current { get; private set; } = new AppConfig().Sanitize();

    public static AppConfig Parse(string json) =>
        (JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig) ?? new AppConfig()).Sanitize();

    public static string Serialize(AppConfig config) =>
        JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);

    /// <summary>Loads the file (creating it with defaults when missing). Returns an error message, or null.</summary>
    public string? Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                Current = new AppConfig().Sanitize();
                return Save();
            }

            var stamp = File.GetLastWriteTimeUtc(Path);
            var parsed = Parse(File.ReadAllText(Path));
            _stamp = stamp;
            Current = parsed;
            foreach (var note in parsed.RepairNotes) Log.Info("Menu repaired: " + note);
            return null;
        }
        catch (Exception ex)
        {
            // Keep the previous config; remember the stamp so the same broken file is reported once.
            try { _stamp = File.GetLastWriteTimeUtc(Path); } catch { }
            Log.Error("Config load failed", ex);
            return ex is JsonException ? "config.json is invalid - keeping the previous settings" : "Cannot read config.json";
        }
    }

    /// <summary>Reloads when the file's timestamp changed. Returns true when it was re-read; <paramref name="error"/> is set on failure.</summary>
    public bool ReloadIfChanged(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(Path) || File.GetLastWriteTimeUtc(Path) == _stamp) return false;
        }
        catch { return false; }

        error = Load();
        return error is null;
    }

    /// <summary>Writes the current config. Returns an error message, or null.</summary>
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, Serialize(Current));
            _stamp = File.GetLastWriteTimeUtc(Path);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Config save failed", ex);
            return "Cannot write config.json";
        }
    }
}
