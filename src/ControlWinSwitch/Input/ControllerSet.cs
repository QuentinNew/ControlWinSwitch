namespace ControlWinSwitch.Input;

/// <summary>
/// Up to four controllers. Each slot is driven either by a Pro Controller (a <see cref="HidControllerReader"/>) or by
/// Joy-Cons (its <see cref="JoyConSlot"/>): Joy-Con 2s found by the <see cref="Hub"/>, original Joy-Cons read by a
/// <see cref="HidControllerReader"/> each. The readers are a pool: one opens whatever Nintendo HID controller comes. The
/// events carry the slot index.
/// </summary>
internal sealed class ControllerSet : IControllerSlots, IDisposable
{
    public const int Slots = 4;

    /// <summary>HID readers: enough for a pair of original Joy-Cons in every slot.</summary>
    public const int Readers = 2 * Slots;

    private readonly HidControllerReader[] _hid = new HidControllerReader[Readers];
    private readonly HidControllerReader?[] _pro = new HidControllerReader?[Slots];
    private readonly JoyConSlot[] _joy = new JoyConSlot[Slots];

    /// <summary>Original Joy-Cons that found no slot: not opened again until they reconnect or a slot frees up.</summary>
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

    public JoyCon2Hub Hub { get; } = new();

    /// <summary>A report of the slot's controller.</summary>
    public event Action<int, ProReport>? Report;

    /// <summary>The stick ranges of the slot's controller are known (or changed with its Joy-Con layout).</summary>
    public event Action<int, StickCalibrations>? CalibrationRead;

    /// <summary>The slot's controller went away.</summary>
    public event Action<int>? ControllerRemoved;

    /// <summary>The Joy-Cons of a slot changed: some joined (a new slot, or a partner for a lone one), or one of a pair left.</summary>
    public event Action<int>? JoyConsChanged;

    /// <summary>The slot's Pro Controller told its MAC address: its <see cref="IControllerSource.Id"/> is now the MAC.</summary>
    public event Action<int>? ControllerIdentified;

    public ControllerSet()
    {
        for (int i = 0; i < Readers; i++)
        {
            var hid = _hid[i] = new HidControllerReader();
            hid.Report += report =>
            {
                int slot = Array.IndexOf(_pro, hid);
                if (slot >= 0) Report?.Invoke(slot, report);
            };
            hid.CalibrationRead += cal =>
            {
                int slot = Array.IndexOf(_pro, hid);
                if (slot >= 0) CalibrationRead?.Invoke(slot, cal);
            };
            hid.Identified += () =>
            {
                int slot = Array.IndexOf(_pro, hid);
                if (slot >= 0) ControllerIdentified?.Invoke(slot); // a Joy-Con only joins a slot once identified
            };
            hid.ControllerRemoved += () =>
            {
                int slot = Array.IndexOf(_pro, hid);
                if (slot < 0) return; // a Joy-Con: its JoyCon1Device tells its slot
                _pro[slot] = null;
                _refused.Clear();
                ControllerRemoved?.Invoke(slot);
                UpdateScanning();
            };
        }
        for (int i = 0; i < Slots; i++)
        {
            int slot = i;
            var joy = _joy[slot] = new JoyConSlot(slot);
            joy.Report += report => Report?.Invoke(slot, report);
            joy.CalibrationRead += cal => CalibrationRead?.Invoke(slot, cal);
            joy.PartnerLeft += () => JoyConsChanged?.Invoke(slot);
            joy.ControllerRemoved += () =>
            {
                _refused.Clear();
                ControllerRemoved?.Invoke(slot);
                UpdateScanning();
            };
        }
        Hub.DeviceReady += OnJoyConReady;
    }

    /// <summary>What drives the slot (a closed source when the slot is free).</summary>
    public IControllerSource Source(int slot) => _pro[slot] ?? (IControllerSource)_joy[slot];

    /// <summary>The physical controllers in the slot whose MAC is known, with their kind (one of <see cref="Config.ControllerKinds"/>).</summary>
    public IEnumerable<(string Mac, string Kind)> Devices(int slot)
    {
        if (_pro[slot] is { } pro)
        {
            if (pro.HasMac) yield return (pro.Id!, Config.ControllerKinds.Pro);
            yield break;
        }
        foreach (var device in _joy[slot].Devices)
            yield return (device.Mac, device.Side == JoyConSide.Left ? Config.ControllerKinds.JoyConLeft : Config.ControllerKinds.JoyConRight);
    }

    public bool AnyOpen => Enumerable.Range(0, Slots).Any(i => Source(i).IsOpen);

    public int OpenCount => Enumerable.Range(0, Slots).Count(i => Source(i).IsOpen);

    /// <summary>Signaled when the reader's pending read completed (wait on it, then call <see cref="OnHidSignaled"/>).</summary>
    public nint HidEventHandle(int reader) => _hid[reader].EventHandle;

    public void OnHidSignaled(int reader) => _hid[reader].OnSignaled();

    /// <summary>Signaled when the reader's write in flight completed; 0 when none is (then call <see cref="OnHidWriteSignaled"/>).</summary>
    public nint HidWriteHandle(int reader) => _hid[reader].WriteHandle;

    public void OnHidWriteSignaled(int reader) => _hid[reader].OnWriteSignaled();

    private int FreeSlot() => Enumerable.Range(0, Slots).FirstOrDefault(i => !Source(i).IsOpen, -1);

    /// <summary>
    /// Opens every Pro Controller and original Joy-Con that is present and not open yet: a Pro Controller takes the first
    /// free slot right away, a Joy-Con finds its slot once it is set up (<see cref="OnJoyConReady"/>).
    /// </summary>
    public void TryOpen()
    {
        var taken = _hid.Where(r => r.Path is not null).Select(r => r.Path!).Concat(_refused).ToList();
        foreach (var reader in _hid)
        {
            if (reader.IsOpen) continue;
            int free = FreeSlot();
            reader.TryOpen(taken, allowPro: free >= 0);
            if (reader.Path is not { } path) continue;
            taken.Add(path);
            if (reader.Kind == HidKind.Pro)
            {
                _pro[free] = reader;
                reader.Assign(free);
            }
            else new JoyCon1Device(reader).Ready += OnJoyConReady;
        }
        UpdateScanning();
    }

    public void OnInterfaceRemoved(string path)
    {
        _refused.Remove(path);
        foreach (var reader in _hid) reader.OnInterfaceRemoved(path);
    }

    /// <summary>
    /// A Joy-Con is ready: it joins a lone Joy-Con of the other side (unless SL / SR is held: it stays alone, sideways),
    /// otherwise it takes the first free slot.
    /// </summary>
    private void OnJoyConReady(IJoyCon device)
    {
        int slot = device.SideButtonsHeld ? -1 : Array.FindIndex(_joy, j => j.CanTake(device.Side));
        if (slot < 0) slot = FreeSlot();
        if (slot < 0)
        {
            Log.Info($"{device.Name} {device.Mac}: every controller slot is taken");
            if (device.HidPath is { } path) _refused.Add(path);
            device.Disconnect();
            return;
        }
        _joy[slot].Add(device);
        JoyConsChanged?.Invoke(slot);
        UpdateScanning();
    }

    private bool _lookForJoyCons;

    /// <summary>
    /// The Controller tab of the settings is on screen: Joy-Con 2s are looked for meanwhile (a paired one connects when a
    /// button wakes it). Otherwise the Bluetooth LE scan only runs while "Add new controller" searches.
    /// </summary>
    public bool LookForJoyCons
    {
        get => _lookForJoyCons;
        set
        {
            if (value == _lookForJoyCons) return;
            _lookForJoyCons = value;
            UpdateScanning();
        }
    }

    /// <summary>
    /// Scans for Joy-Con 2s while the Controller tab is shown and one could still find a slot (a free one, or a lone
    /// Joy-Con waiting for a partner). The pairing window of "Add new controller" scans on its own.
    /// </summary>
    private void UpdateScanning()
    {
        bool room = false;
        for (int i = 0; i < Slots && !room; i++)
            room = !Source(i).IsOpen || (_joy[i].IsOpen && (_joy[i].CanTake(JoyConSide.Left) || _joy[i].CanTake(JoyConSide.Right)));
        Hub.SetScanning(_lookForJoyCons && room);
    }

    public void Dispose()
    {
        foreach (var joy in _joy) joy.DisconnectAll();
        Hub.Dispose();
        foreach (var reader in _hid) reader.Dispose();
    }
}
