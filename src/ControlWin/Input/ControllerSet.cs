namespace ControlWin.Input;

/// <summary>
/// Up to four controllers. Each slot is driven either by a Pro Controller (its <see cref="HidControllerReader"/>) or by
/// Joy-Con 2s (its <see cref="JoyConSlot"/>, fed by the <see cref="Hub"/>). The events carry the slot index.
/// </summary>
internal sealed class ControllerSet : IDisposable
{
    public const int Slots = 4;

    private readonly HidControllerReader[] _hid = new HidControllerReader[Slots];
    private readonly JoyConSlot[] _joy = new JoyConSlot[Slots];

    public JoyCon2Hub Hub { get; } = new();

    /// <summary>A report of the slot's controller.</summary>
    public event Action<int, ProReport>? Report;

    /// <summary>The stick ranges of the slot's controller are known (or changed with its Joy-Con layout).</summary>
    public event Action<int, StickCalibrations>? CalibrationRead;

    /// <summary>The slot's controller went away.</summary>
    public event Action<int>? ControllerRemoved;

    /// <summary>Joy-Cons joined a slot (a new slot, or a partner for a lone one).</summary>
    public event Action<int>? JoyConsChanged;

    public ControllerSet()
    {
        for (int i = 0; i < Slots; i++)
        {
            int slot = i;
            var hid = _hid[slot] = new HidControllerReader(slot);
            hid.Report += report => Report?.Invoke(slot, report);
            hid.CalibrationRead += cal => CalibrationRead?.Invoke(slot, cal);
            hid.ControllerRemoved += () =>
            {
                ControllerRemoved?.Invoke(slot);
                UpdateScanning();
            };

            var joy = _joy[slot] = new JoyConSlot(slot);
            joy.Report += report => Report?.Invoke(slot, report);
            joy.CalibrationRead += cal => CalibrationRead?.Invoke(slot, cal);
            joy.ControllerRemoved += () =>
            {
                ControllerRemoved?.Invoke(slot);
                UpdateScanning();
            };
        }
        Hub.DeviceReady += OnJoyConReady;
    }

    /// <summary>What drives the slot (a closed source when the slot is free).</summary>
    public IControllerSource Source(int slot) => _hid[slot].IsOpen ? _hid[slot] : _joy[slot];

    /// <summary>The physical controllers in the slot whose MAC is known, with their kind (one of <see cref="Config.ControllerKinds"/>).</summary>
    public IEnumerable<(string Mac, string Kind)> Devices(int slot)
    {
        if (_hid[slot].IsOpen)
        {
            if (_hid[slot].HasMac) yield return (_hid[slot].Id!, Config.ControllerKinds.Pro);
            yield break;
        }
        foreach (var device in _joy[slot].Devices)
            yield return (device.Mac, device.Side == JoyConSide.Left ? Config.ControllerKinds.JoyConLeft : Config.ControllerKinds.JoyConRight);
    }

    public bool AnyOpen => Enumerable.Range(0, Slots).Any(i => Source(i).IsOpen);

    public int OpenCount => Enumerable.Range(0, Slots).Count(i => Source(i).IsOpen);

    /// <summary>Signaled when the slot's Pro Controller read completed (wait on it, then call <see cref="OnHidSignaled"/>).</summary>
    public nint HidEventHandle(int slot) => _hid[slot].EventHandle;

    public void OnHidSignaled(int slot) => _hid[slot].OnSignaled();

    /// <summary>Opens every Pro Controller that is present and not open yet, each in the first free slot.</summary>
    public void TryOpen()
    {
        var taken = _hid.Where(r => r.Path is not null).Select(r => r.Path!).ToList();
        for (int i = 0; i < Slots; i++)
        {
            var reader = _hid[i];
            if (reader.IsOpen || _joy[i].IsOpen) continue;
            reader.TryOpen(taken);
            if (reader.Path is not null) taken.Add(reader.Path);
        }
        UpdateScanning();
    }

    public void OnInterfaceRemoved(string path)
    {
        foreach (var reader in _hid) reader.OnInterfaceRemoved(path);
    }

    /// <summary>
    /// A Joy-Con is ready: it joins a lone Joy-Con of the other side (unless SL / SR is held: it stays alone, sideways),
    /// otherwise it takes the first free slot.
    /// </summary>
    private void OnJoyConReady(JoyCon2Device device)
    {
        int slot = device.SideButtonsHeld ? -1 : Array.FindIndex(_joy, j => j.CanTake(device.Side));
        if (slot < 0)
            slot = Enumerable.Range(0, Slots).FirstOrDefault(i => !Source(i).IsOpen, -1);
        if (slot < 0)
        {
            Log.Info($"{device.Name} {device.Mac}: every controller slot is taken");
            device.Disconnect();
            return;
        }
        _joy[slot].Add(device);
        JoyConsChanged?.Invoke(slot);
        UpdateScanning();
    }

    /// <summary>Scans for Joy-Cons while one could still find a slot (a free one, or a lone Joy-Con waiting for a partner).</summary>
    private void UpdateScanning()
    {
        bool room = false;
        for (int i = 0; i < Slots && !room; i++)
            room = !Source(i).IsOpen || (_joy[i].IsOpen && (_joy[i].CanTake(JoyConSide.Left) || _joy[i].CanTake(JoyConSide.Right)));
        Hub.SetScanning(room);
    }

    public void Dispose()
    {
        foreach (var joy in _joy) joy.DisconnectAll();
        Hub.Dispose();
        foreach (var reader in _hid) reader.Dispose();
    }
}
