using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlWin.Config;

internal sealed class VirtualPadConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Label-based mapping: Switch A = Xbox A, B = B, X = X, Y = Y.</summary>
    public bool SwapAB { get; set; } = true;
    public bool SwapXY { get; set; } = true;

    /// <summary>The pad is switched off while one of these processes runs (empty = feature off).</summary>
    public List<string> DisableWhileRunning { get; set; } = ["steam.exe"];
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
}

/// <summary>Values of <see cref="RadialNodeConfig.Type"/>.</summary>
internal static class RadialNodeTypes
{
    public const string Close = "close", Quit = "quit", Sleep = "sleep", Restart = "restart", Shutdown = "shutdown",
        Settings = "settings", Headset = "headset", MouseMode = "mouseMode", VirtualPad = "virtualPad",
        Folder = "folder", Shortcut = "shortcut";

    public static readonly string[] All =
        [Close, Quit, Sleep, Restart, Shutdown, Settings, Headset, MouseMode, VirtualPad, Folder, Shortcut];
}

/// <summary>Icon of a radial node. Only "glyph" is drawn for now; "auto" and "image" are kept as written.</summary>
internal sealed class RadialIconConfig
{
    public string Kind { get; set; } = "glyph";

    /// <summary>"glyph": Segoe Fluent Icons code point in hex (for example "E7E8").</summary>
    public string? Glyph { get; set; }

    /// <summary>"image": path of a .png / .ico file.</summary>
    public string? Path { get; set; }
}

/// <summary>One node of the radial menu tree: a built-in action, a folder or an app shortcut.</summary>
internal sealed class RadialNodeConfig
{
    public string Type { get; set; } = "";

    /// <summary>Position on the ring: 0-7 on the root, 0-6 in a folder (clockwise after the Back node).</summary>
    public int Slot { get; set; } = -1;

    /// <summary>Overrides the default label.</summary>
    public string? Name { get; set; }

    /// <summary>"#RRGGBB".</summary>
    public string? Color { get; set; }

    public RadialIconConfig? Icon { get; set; }

    // Shortcuts.
    public string? Target { get; set; }
    public string? Args { get; set; }
    public string? WorkingDir { get; set; }

    /// <summary>Folders only; folders do not nest.</summary>
    public List<RadialNodeConfig>? Children { get; set; }

    public RadialNodeConfig Clone() => new()
    {
        Type = Type,
        Slot = Slot,
        Name = Name,
        Color = Color,
        Icon = Icon is null ? null : new RadialIconConfig { Kind = Icon.Kind, Glyph = Icon.Glyph, Path = Icon.Path },
        Target = Target,
        Args = Args,
        WorkingDir = WorkingDir,
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
    public string HeadsetName { get; set; } = "WH-CH700N";

    /// <summary>How the main menu is drawn: <see cref="MenuLayouts.List"/> or <see cref="MenuLayouts.Radial"/>.</summary>
    public string MenuLayout { get; set; } = MenuLayouts.List;

    /// <summary>Radial menu: the stick must travel from "pushed" to "centered" within this time to count as a release.</summary>
    public int RadialReleaseMs { get; set; } = 100;

    public VirtualPadConfig VirtualPad { get; set; } = new();
    public MouseConfig Mouse { get; set; } = new();
    /// <summary>
    /// The radial menu tree (root nodes). Missing in the file = seeded from the defaults and <see cref="Shortcuts"/>.
    /// </summary>
    public List<RadialNodeConfig>? RadialMenu { get; set; }

    /// <summary>What <see cref="Sanitize"/> had to repair in <see cref="RadialMenu"/> (logged by the store).</summary>
    [JsonIgnore]
    public List<string> RepairNotes { get; } = [];

    public List<ShortcutConfig> Shortcuts { get; set; } =
    [
        new() { Name = "Steam Big Picture", Target = "steam://open/bigpicture" },
        new() { Name = "Steam", Target = "steam://open/main" },
    ];

    /// <summary>Repairs what a hand-edited file can get wrong: nulls and out-of-range numbers.</summary>
    public AppConfig Sanitize()
    {
        HeadsetName ??= "";
        MenuLayout = string.Equals(MenuLayout?.Trim(), MenuLayouts.Radial, StringComparison.OrdinalIgnoreCase)
            ? MenuLayouts.Radial : MenuLayouts.List;
        RadialReleaseMs = Math.Clamp(RadialReleaseMs, 20, 500);
        VirtualPad ??= new();
        VirtualPad.DisableWhileRunning ??= [];
        VirtualPad.DisableWhileRunning = VirtualPad.DisableWhileRunning
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Mouse ??= new();
        Shortcuts ??= [];
        Shortcuts.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Target));
        foreach (var s in Shortcuts)
            if (string.IsNullOrWhiteSpace(s.Name)) s.Name = s.Target;

        RepairNotes.Clear();
        RadialMenu = RadialMenuRepair.Run(RadialMenu, Shortcuts, RepairNotes);

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
            foreach (var note in parsed.RepairNotes) Log.Info("Radial menu repaired: " + note);
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
