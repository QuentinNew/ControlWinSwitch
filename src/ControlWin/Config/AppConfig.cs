using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlWin.Config;

internal sealed class VirtualPadConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Label-based mapping: Switch A = Xbox A, B = B, X = X, Y = Y.</summary>
    public bool SwapAB { get; set; } = true;
    public bool SwapXY { get; set; } = true;
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

internal sealed class AppConfig
{
    public string HeadsetName { get; set; } = "WH-CH700N";
    public VirtualPadConfig VirtualPad { get; set; } = new();
    public MouseConfig Mouse { get; set; } = new();
    public List<ShortcutConfig> Shortcuts { get; set; } =
    [
        new() { Name = "Steam Big Picture", Target = "steam://open/bigpicture" },
        new() { Name = "Steam", Target = "steam://open/main" },
    ];

    /// <summary>Repairs what a hand-edited file can get wrong: nulls and out-of-range numbers.</summary>
    public AppConfig Sanitize()
    {
        HeadsetName ??= "";
        VirtualPad ??= new();
        Mouse ??= new();
        Shortcuts ??= [];
        Shortcuts.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Target));
        foreach (var s in Shortcuts)
            if (string.IsNullOrWhiteSpace(s.Name)) s.Name = s.Target;

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
