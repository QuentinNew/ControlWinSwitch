using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlWinSwitch.Config;

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

    /// <summary>
    /// When Joy-Con 2s are looked for (Bluetooth LE scan): one of <see cref="JoyCon2Searches"/>. "Add new controller"
    /// always looks for them while it searches.
    /// </summary>
    public string JoyCon2Search { get; set; } = JoyCon2Searches.ControllerTab;
}

internal static class PadTypes
{
    public const string Xbox = "xbox", Ps4 = "ps4";
}

/// <summary>Values of <see cref="VirtualPadConfig.JoyCon2Search"/>, in the order the settings list them.</summary>
internal static class JoyCon2Searches
{
    public const string Never = "never", ControllerTab = "controllerTab", Always = "always";

    public static readonly string[] All = [Never, ControllerTab, Always];

    /// <summary>A known value whatever its case; anything else is the default.</summary>
    public static string Normalize(string? value) =>
        Array.Find(All, v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? ControllerTab;
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

    /// <summary>Start the program with administrator rights (no UAC prompt while ControlWinSwitch itself runs elevated).</summary>
    public bool? RunAsAdmin { get; set; }
}

/// <summary>Values of <see cref="MenuNodeConfig.Type"/>.</summary>
internal static class MenuNodeTypes
{
    public const string Close = "close", Quit = "quit", Power = "power",
        Settings = "settings", Headset = "headset", MouseMode = "mouseMode", VirtualPad = "virtualPad",
        Folder = "folder", Shortcut = "shortcut", AddController = "addController", CloseWindow = "closeWindow",
        Screenshot = "screenshot", Volume = "volume", Brightness = "brightness", ChangeScreen = "changeScreen";

    public static readonly string[] All =
        [Close, Quit, Power, Settings, Headset, MouseMode, VirtualPad, AddController, CloseWindow, Screenshot,
            Volume, Brightness, ChangeScreen, Folder, Shortcut];
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

    /// <summary>Screenshots: where the pictures go; null = the Windows Screenshots folder.</summary>
    public string? SaveFolder { get; set; }

    /// <summary>Screenshots: also put the picture on the clipboard.</summary>
    public bool? CopyToClipboard { get; set; }

    /// <summary>Change screen: "pcOnly", "duplicate", "extend" or "secondOnly"; null or unknown = extend.</summary>
    public string? ScreenMode { get; set; }

    /// <summary>Power: "sleep", "restart" or "shutdown"; null or unknown = sleep.</summary>
    public string? PowerMode { get; set; }

    /// <summary>Folders only; a folder can hold other folders.</summary>
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
        SaveFolder = SaveFolder,
        CopyToClipboard = CopyToClipboard,
        ScreenMode = ScreenMode,
        PowerMode = PowerMode,
        Children =Children?.Select(c => c.Clone()).ToList(),
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

    /// <summary>
    /// Every connected controller can drive the open menu and the settings, not only the one that opened them: the last
    /// one to press a button or tilt a stick has them.
    /// </summary>
    public bool MenuAnyController { get; set; }

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

    /// <summary>Per-controller name, by controller id. The picture lives in %APPDATA%\ControlWinSwitch\controllers\.</summary>
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
        VirtualPad.JoyCon2Search = JoyCon2Searches.Normalize(VirtualPad.JoyCon2Search);
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

/// <summary>
/// Loads and saves config.json; reloads when the file changed on disk. Saves can be deferred (<see cref="SaveRequested"/>):
/// a burst of changes is then written once, off the UI thread (<see cref="FlushInBackground"/>).
/// </summary>
internal sealed class ConfigStore
{
    private readonly object _fileLock = new(); // one writer at a time; guards _stamp and _writtenVersion
    private DateTime _stamp;
    private bool _dirty;          // changed in memory, not written yet (UI thread)
    private long _queuedVersion;  // version of the last snapshot handed to a writer (UI thread)
    private long _writtenVersion; // version on disk: an older snapshot that comes later is not written
    private int _writesInFlight;  // background writes not finished yet

    public ConfigStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ControlWinSwitch", "config.json");
    }

    public string Path { get; }

    public AppConfig Current { get; private set; } = new AppConfig().Sanitize();

    /// <summary>
    /// When set, <see cref="Save"/> only marks the config changed and calls this (the app starts its save timer, then calls
    /// <see cref="FlushInBackground"/>). When null (startup, tests), <see cref="Save"/> writes at once.
    /// </summary>
    public Action? SaveRequested { get; set; }

    /// <summary>A change is not on disk yet (waiting for the timer, or being written).</summary>
    public bool HasPendingSave => _dirty || Volatile.Read(ref _writesInFlight) > 0;

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
                _dirty = false;
                return Save();
            }

            AppConfig parsed;
            lock (_fileLock)
            {
                var stamp = File.GetLastWriteTimeUtc(Path);
                parsed = Parse(File.ReadAllText(Path));
                _stamp = stamp;
                _writtenVersion = _queuedVersion; // the file wins: a snapshot still on its way to disk is dropped
            }
            Current = parsed;
            _dirty = false;
            foreach (var note in parsed.RepairNotes) Log.Info("Menu repaired: " + note);
            return null;
        }
        catch (Exception ex)
        {
            // Keep the previous config; remember the stamp so the same broken file is reported once.
            try { lock (_fileLock) _stamp = File.GetLastWriteTimeUtc(Path); } catch { }
            Log.Error("Config load failed", ex);
            return ex is JsonException ? "config.json is invalid - keeping the previous settings" : "Cannot read config.json";
        }
    }

    /// <summary>Reloads when the file's timestamp changed. Returns true when it was re-read; <paramref name="error"/> is set on failure.</summary>
    public bool ReloadIfChanged(out string? error)
    {
        error = null;
        // Our own change is on its way to disk: the timestamp moves because of it, not because of an edit.
        if (HasPendingSave) return false;
        try
        {
            DateTime stamp;
            lock (_fileLock) stamp = _stamp;
            if (!File.Exists(Path) || File.GetLastWriteTimeUtc(Path) == stamp) return false;
        }
        catch { return false; }

        error = Load();
        return error is null;
    }

    /// <summary>
    /// Saves the current config: at once, or (with <see cref="SaveRequested"/>) marks it changed for the next flush.
    /// Returns an error message, or null (always null when deferred: the flush reports errors).
    /// </summary>
    public string? Save()
    {
        if (SaveRequested is { } request)
        {
            _dirty = true;
            request();
            return null;
        }
        return Flush(force: true);
    }

    /// <summary>
    /// Writes a pending change now, on this thread (also waits for a background write in flight). <paramref name="force"/>
    /// writes even when nothing changed. Returns an error message, or null.
    /// </summary>
    public string? Flush(bool force = false)
    {
        if (!force && !HasPendingSave) return null;
        _dirty = false;
        return WriteSnapshot(Serialize(Current), ++_queuedVersion);
    }

    /// <summary>
    /// Writes a pending change on a worker thread (the snapshot is taken now, on the UI thread).
    /// <paramref name="onError"/> runs on that worker when the write fails.
    /// </summary>
    public void FlushInBackground(Action<string> onError)
    {
        if (!_dirty) return;
        _dirty = false;
        string json = Serialize(Current);
        long version = ++_queuedVersion;
        Interlocked.Increment(ref _writesInFlight);
        ErrorBoundary.StartWorker("config-save", () =>
        {
            try
            {
                if (WriteSnapshot(json, version) is { } error) onError(error);
            }
            finally { Interlocked.Decrement(ref _writesInFlight); }
        });
    }

    private string? WriteSnapshot(string json, long version)
    {
        lock (_fileLock)
        {
            if (version <= _writtenVersion) return null; // a newer snapshot (or a reload) got there first
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                // Write to a temp file then swap it in, so a crash mid-write never leaves a broken config.json.
                string tmp = Path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(Path)) File.Replace(tmp, Path, Path + ".bak");
                else File.Move(tmp, Path);
                _stamp = File.GetLastWriteTimeUtc(Path);
                _writtenVersion = version;
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Config save failed", ex);
                return "Cannot write config.json";
            }
        }
    }
}
