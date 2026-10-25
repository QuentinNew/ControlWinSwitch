using ControlWinSwitch.Actions;
using ControlWinSwitch.App;
using ControlWinSwitch.Config;
using ControlWinSwitch.Input;

namespace ControlWinSwitch.Tests;

public class ControllerRegistryTests : IDisposable
{
    private sealed class FakeSource : IControllerSource
    {
        public bool IsOpen { get; set; }
        public string? Id { get; set; }
        public string? PartnerId { get; set; }
        public string? FallbackId { get; set; }
        public bool HasMac { get; set; }
        public IReadOnlyList<string> HidPaths { get; set; } = [];
        public (string Id, bool RightField)? StickProfile(bool right) => Id is null ? null : (Id, right);
        public bool SetRumble(bool on) => false;
        public bool SetRumbleMotors(byte large, byte small, int percent) => false;
    }

    private sealed class FakeSlots : IControllerSlots
    {
        public readonly FakeSource[] Sources = Enumerable.Range(0, ControllerSet.Slots).Select(_ => new FakeSource()).ToArray();
        public readonly List<(string Mac, string Kind)>[] DeviceLists =
            Enumerable.Range(0, ControllerSet.Slots).Select(_ => new List<(string, string)>()).ToArray();
        public IControllerSource Source(int slot) => Sources[slot];
        public IEnumerable<(string Mac, string Kind)> Devices(int slot) => DeviceLists[slot];
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cws-registry-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSlots _slots = new();
    private readonly List<RememberedController> _remembered = [];
    private readonly ConfigStore _store;
    private readonly ControllerRegistry _registry;

    public ControllerRegistryTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ConfigStore(Path.Combine(_dir, "config.json"));
        _registry = new ControllerRegistry(_store, _slots, _ => new ControllerState(), () => [.. _remembered]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Connect(int slot, string mac, string kind = ControllerKinds.Pro)
    {
        _slots.Sources[slot].IsOpen = true;
        _slots.Sources[slot].Id = mac;
        _slots.Sources[slot].HasMac = true;
        _slots.DeviceLists[slot].Add((mac, kind));
    }

    [Fact]
    public void Deadzone_is_clamped_saved_and_a_profile_back_to_default_is_pruned()
    {
        Connect(0, "AA");
        _registry.SetStickDeadzone(0, right: true, 0.9f);
        Assert.Equal(0.5f, _registry.StickDeadzone(0, right: true));
        Assert.Equal(0f, _registry.StickDeadzone(0, right: false));

        _registry.SetStickDeadzone(0, right: true, 0f);
        Assert.False(_store.Current.Controllers.ContainsKey("AA"));
    }

    [Fact]
    public void Name_is_trimmed_and_cut_to_24_characters()
    {
        _registry.SetProfileName("AA", "  " + new string('x', 30) + "  ");
        Assert.Equal(new string('x', 24), _registry.ProfileName("AA"));
    }

    [Fact]
    public void A_profile_saved_under_the_temporary_id_moves_to_the_mac()
    {
        _store.Current.Controllers["dev:1"] = new ControllerProfile { Name = "Mine" };
        var source = _slots.Sources[0];
        source.IsOpen = true;
        source.Id = "AA";
        source.HasMac = true;
        source.FallbackId = "dev:1";

        _registry.MigrateProfile(0);
        Assert.Equal("AA", _registry.Key(0));
        Assert.Equal("Mine", _registry.ProfileName("AA"));
        Assert.False(_store.Current.Controllers.ContainsKey("dev:1"));
    }

    [Fact]
    public void SyncKnown_reports_joined_controllers_and_null_when_nothing_changed()
    {
        Connect(0, "AA", ControllerKinds.JoyConLeft);
        Assert.Equal(["AA"], _registry.SyncKnown());
        Assert.Equal(ControllerKinds.JoyConLeft, _store.Current.KnownControllers["AA"].Kind);
        Assert.Null(_registry.SyncKnown());

        _slots.DeviceLists[0].Clear();
        Assert.Empty(_registry.SyncKnown()!); // one went away: changed, nobody joined
    }

    [Fact]
    public void Paired_list_skips_connected_ones_and_pro_controllers_windows_forgot()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _store.Current.KnownControllers["JL"] = new KnownController { Kind = ControllerKinds.JoyConLeft, LastSeen = old };
        _store.Current.KnownControllers["P1"] = new KnownController { Kind = ControllerKinds.Pro, LastSeen = old.AddDays(1) };
        _store.Current.KnownControllers["P2"] = new KnownController { Kind = ControllerKinds.Pro, LastSeen = old.AddDays(2) };
        _remembered.Add(new RememberedController("P1", false, old, ControllerKinds.Pro));
        _registry.RefreshRemembered();
        Assert.Equal(["P1", "JL"], _registry.PairedControllers);

        Connect(0, "JL", ControllerKinds.JoyConLeft);
        _registry.SyncKnown();
        Assert.Equal(["P1"], _registry.PairedControllers);
    }

    [Fact]
    public void A_controller_windows_remembers_joins_the_known_list()
    {
        var used = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        _remembered.Add(new RememberedController("P1", false, used, ControllerKinds.Pro));
        _registry.RefreshRemembered();
        Assert.Equal(used, _store.Current.KnownControllers["P1"].LastSeen);
        Assert.True(_registry.UsesWindowsPairing("P1"));
    }
}
