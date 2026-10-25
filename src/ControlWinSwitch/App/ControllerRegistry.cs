using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.Input;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.App;

/// <summary>
/// What the app remembers about controllers: each one's profile (name, deadzones, picture) and the known list of the
/// Controller tab (controllers seen before, with when they were last connected). No UI: the caller shows the results.
/// </summary>
internal sealed class ControllerRegistry
{
    private const long KnownSyncMs = 1000;

    private readonly ConfigStore _store;
    private readonly IControllerSlots _slots;
    private readonly Func<int, ControllerState> _stateOf;
    private readonly Func<List<RememberedController>> _readRemembered;

    private long _lastKnownSync;
    private readonly HashSet<string> _connectedMacs = [];
    private List<RememberedController> _remembered = [];

    public ControllerRegistry(ConfigStore store, IControllerSlots slots, Func<int, ControllerState> stateOf,
        Func<List<RememberedController>>? readRemembered = null)
    {
        _store = store;
        _slots = slots;
        _stateOf = stateOf;
        _readRemembered = readRemembered ?? BluetoothPairing.RememberedControllers;
    }

    private AppConfig Config => _store.Current;

    // ---- profiles ----------------------------------------------------------------------------

    /// <summary>
    /// The id a controller's profile is stored under: its MAC address, or the temporary instance id until the MAC is
    /// known. A profile (and picture) saved under the temporary id moves to the MAC once that is known.
    /// </summary>
    public string? Key(int slot)
    {
        var source = _slots.Source(slot);
        var id = source.Id;
        if (id is null) return null;
        if (source.HasMac && source.FallbackId is { } old && old != id)
        {
            bool moved = false;
            if (Config.Controllers.Remove(old, out var profile) && !Config.Controllers.ContainsKey(id))
            {
                Config.Controllers[id] = profile;
                moved = true;
            }
            if (ControllerPictures.Has(old) && !ControllerPictures.Has(id)) ControllerPictures.Move(old, id);
            if (moved) _store.Save();
        }
        return id;
    }

    public SlotTile TileOf(int slot)
    {
        if (!_slots.Source(slot).IsOpen) return default;
        var key = Key(slot);
        string name = key is not null && Config.Controllers.TryGetValue(key, out var profile) ? profile.Name : "";
        var state = _stateOf(slot);
        // A Joy-Con pair: each Joy-Con shows its own picture, turned upright (pictured sideways, the left one is turned a
        // quarter clockwise, the right one a quarter counterclockwise).
        var partner = _slots.Source(slot).PartnerId;
        nint image = key is null ? 0 : partner is null ? ControllerPictures.Get(key) : ControllerPictures.GetRotated(key, clockwise: true);
        nint image2 = partner is null ? 0 : ControllerPictures.GetRotated(partner, clockwise: false);
        return new SlotTile(true, name.Length > 0 ? name : $"Controller {slot + 1}", state.Battery, state.Charging,
            image, partner is not null, image2);
    }

    /// <summary>The tiles of the connected controllers, in slot order.</summary>
    public List<SlotTile> ConnectedTiles()
    {
        var tiles = new List<SlotTile>();
        for (int slot = 0; slot < ControllerSet.Slots; slot++)
            if (TileOf(slot) is { Connected: true } tile) tiles.Add(tile);
        return tiles;
    }

    /// <summary>The profile of a controller (created when missing).</summary>
    private ControllerProfile ProfileOf(string key)
    {
        if (!Config.Controllers.TryGetValue(key, out var profile)) Config.Controllers[key] = profile = new ControllerProfile();
        return profile;
    }

    /// <summary>Forgets a profile that holds nothing.</summary>
    private void PruneProfile(string key)
    {
        if (Config.Controllers.TryGetValue(key, out var profile) && profile.IsDefault)
            Config.Controllers.Remove(key);
    }

    /// <summary>
    /// The deadzone of a slot's stick. A Joy-Con keeps its own stick's deadzone (the left one in its left field, the right
    /// one in its right field), whichever Joy-Con it is paired with.
    /// </summary>
    public float StickDeadzone(int slot, bool right)
    {
        Key(slot); // moves a profile saved under the temporary id
        return _slots.Source(slot).StickProfile(right) is ({ } key, var rightField) && Config.Controllers.TryGetValue(key, out var profile)
            ? (rightField ? profile.RightDeadzone : profile.LeftDeadzone) : 0f;
    }

    public void SetStickDeadzone(int slot, bool right, float value)
    {
        Key(slot);
        if (_slots.Source(slot).StickProfile(right) is not ({ } key, var rightField)) return;
        var profile = ProfileOf(key);
        value = Math.Clamp(value, 0f, 0.5f);
        if (rightField) profile.RightDeadzone = value; else profile.LeftDeadzone = value;
        PruneProfile(key);
        _store.Save();
    }

    public string ProfileName(string key) => Config.Controllers.TryGetValue(key, out var profile) ? profile.Name : "";

    public void SetProfileName(string key, string name)
    {
        name = name.Trim();
        if (name.Length > 24) name = name[..24];
        ProfileOf(key).Name = name;
        PruneProfile(key);
        _store.Save();
    }

    /// <summary>The id a picture is stored under: the slot's, or the right Joy-Con's of a pair.</summary>
    public string? PictureKey(int slot, bool second) => second ? _slots.Source(slot).PartnerId : Key(slot);

    // ---- known controllers (the paired list of the Controller tab) ----------------------------

    /// <summary>A second has passed since the last <see cref="SyncKnown"/> (a MAC read late, half of a Joy-Con pair gone).</summary>
    public bool KnownSyncDue => Environment.TickCount64 - _lastKnownSync >= KnownSyncMs;

    /// <summary>
    /// Records the controllers that connected or went away since the last call: a new one joins the known list, and
    /// each one's last-seen time is set when it connects and when it goes. Returns the MACs that joined, or null when
    /// nothing changed.
    /// </summary>
    public List<string>? SyncKnown()
    {
        _lastKnownSync = Environment.TickCount64;
        var current = new Dictionary<string, string>();
        for (int slot = 0; slot < ControllerSet.Slots; slot++)
            foreach (var (mac, kind) in _slots.Devices(slot)) current[mac] = kind;

        var now = DateTime.UtcNow;
        var joined = new List<string>();
        foreach (var (mac, kind) in current)
        {
            if (!_connectedMacs.Add(mac)) continue;
            if (!Config.KnownControllers.TryGetValue(mac, out var known)) Config.KnownControllers[mac] = known = new KnownController();
            known.Kind = kind;
            known.LastSeen = now;
            joined.Add(mac);
        }
        var left = _connectedMacs.Where(mac => !current.ContainsKey(mac)).ToList();
        foreach (var mac in left)
        {
            _connectedMacs.Remove(mac);
            if (Config.KnownControllers.TryGetValue(mac, out var known)) known.LastSeen = now;
        }
        if (joined.Count == 0 && left.Count == 0) return null;
        _store.Save();
        return joined;
    }

    /// <summary>The slot a controller with this MAC is connected to, or -1.</summary>
    public int SlotOf(string mac) =>
        Enumerable.Range(0, ControllerSet.Slots).FirstOrDefault(i => _slots.Devices(i).Any(d => d.Mac == mac), -1);

    /// <summary>Shutdown: the connected controllers were last seen now.</summary>
    public void SaveLastSeen()
    {
        foreach (var mac in _connectedMacs)
            if (Config.KnownControllers.TryGetValue(mac, out var known)) known.LastSeen = DateTime.UtcNow;
        if (_connectedMacs.Count > 0) _store.Save();
    }

    /// <summary>Reads the Pro Controllers and original Joy-Cons Windows remembers; one the app has not seen yet joins the known list.</summary>
    public void RefreshRemembered()
    {
        _remembered = _readRemembered();
        bool added = false;
        foreach (var controller in _remembered)
        {
            if (Config.KnownControllers.ContainsKey(controller.Mac)) continue;
            Config.KnownControllers[controller.Mac] = new KnownController
            {
                Kind = controller.Kind,
                LastSeen = controller.LastUsed == DateTime.MinValue ? DateTime.UtcNow : controller.LastUsed,
            };
            added = true;
        }
        if (added) _store.Save();
    }

    /// <summary>Windows remembers the controller: a Pro Controller or an original Joy-Con (a Joy-Con 2 never is).</summary>
    private bool PairedInWindows(string key) => _remembered.Exists(c => c.Mac == key);

    /// <summary>Pairing and forgetting this controller go through Windows' Bluetooth pairing (not the Joy-Con 2 hub).</summary>
    public bool UsesWindowsPairing(string key) => KindOf(key) == ControllerKinds.Pro || PairedInWindows(key);

    /// <summary>The known controllers that are not connected (a Pro Controller only while Windows remembers it), last seen first.</summary>
    public IReadOnlyList<string> PairedControllers
    {
        get
        {
            var list = new List<(string Mac, DateTime Seen)>();
            foreach (var (mac, known) in Config.KnownControllers)
            {
                if (_connectedMacs.Contains(mac)) continue;
                if (known.Kind == ControllerKinds.Pro && !PairedInWindows(mac)) continue;
                list.Add((mac, known.LastSeen));
            }
            list.Sort((a, b) => b.Seen.CompareTo(a.Seen));
            return list.ConvertAll(e => e.Mac);
        }
    }

    private string KindOf(string key) => Config.KnownControllers.TryGetValue(key, out var known) ? known.Kind : ControllerKinds.Pro;

    public string PairedDisplayName(string key) =>
        ProfileName(key) is { Length: > 0 } name ? name : ControllerKinds.DefaultName(KindOf(key));

    public SlotTile PairedTileOf(string key)
    {
        var seen = Config.KnownControllers.TryGetValue(key, out var known) ? known.LastSeen : DateTime.MinValue;
        return new SlotTile(true, PairedDisplayName(key), -1, false, ControllerPictures.Get(key), Ago: Ago(seen));
    }

    /// <summary>"now", "5m", "3h", "2d", "4mo", "1y": how long ago a controller was last connected (empty when unknown).</summary>
    internal static string Ago(DateTime seen, DateTime? now = null)
    {
        if (seen == DateTime.MinValue) return "";
        var span = (now ?? DateTime.UtcNow) - seen;
        return span.TotalMinutes < 1 ? "now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h"
            : span.TotalDays < 30 ? $"{(int)span.TotalDays}d"
            : span.TotalDays < 365 ? $"{(int)(span.TotalDays / 30)}mo"
            : $"{(int)(span.TotalDays / 365)}y";
    }

    /// <summary>
    /// Forgets a known controller: Windows forgets a Pro Controller or original Joy-Con, and a Joy-Con leaves the known
    /// list (it comes back when it connects). Returns false when Windows could not forget it.
    /// </summary>
    public bool Forget(string key)
    {
        bool forgotten = true;
        if (UsesWindowsPairing(key))
        {
            forgotten = BluetoothPairing.Forget(key);
            RefreshRemembered();
        }
        if (KindOf(key) != ControllerKinds.Pro)
        {
            Config.KnownControllers.Remove(key); // a Joy-Con comes back when it connects (an original one once paired again)
            _store.Save();
        }
        return forgotten;
    }
}
