using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.Actions;

internal enum HeadsetState { Unknown, NotFound, Connected, Disconnected, Failed, Connecting }

/// <summary>
/// Connects / disconnects a paired Bluetooth headset the way the "Connect" button of Windows Settings does:
/// the audio endpoint's topology leads to the Bluetooth audio device, whose IKsControl accepts the
/// KSPROPSETID_BtAudio one-shot reconnect / disconnect properties. Raw COM through vtables (AOT friendly).
/// All methods block and must run on a worker thread (they initialize COM themselves).
/// </summary>
internal static unsafe class BluetoothHeadset
{
    private static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E"),
        IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6"),
        IidDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F"),
        IidPart = new("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"),
        IidKsControl = new("28F54685-06FD-11D2-B27A-00A0C9223196"),
        KsPropSetBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D"),
        PkeyFriendlyNameFmtid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    private const uint PkeyFriendlyNamePid = 14;
    private const int ReconnectProperty = 0, DisconnectProperty = 1;
    private const uint KsPropertyTypeGet = 1, KsPropertyTypeSet = 2;
    private const uint DeviceStateActive = 1, StateMaskAll = 0xF;
    private const uint ClsctxAll = 0x17, CoinitMultithreaded = 0;
    private const ushort VtLpwstr = 31;

    private static ulong _address; // Bluetooth address of the headset, learned from its device path

    /// <summary>Current state of the headset whose endpoint name contains <paramref name="name"/>.</summary>
    public static HeadsetState GetState(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return HeadsetState.NotFound; // no name set: would match every endpoint
        if (!Initialize(out bool uninit)) return HeadsetState.Failed;
        try
        {
            Count(name, out int found, out bool anyActive);
            if (found < 0) return HeadsetState.Failed;
            if (found == 0) return HeadsetState.NotFound;
            return anyActive ? HeadsetState.Connected : HeadsetState.Disconnected;
        }
        catch (Exception ex)
        {
            Log.Error("Headset state failed", ex);
            return HeadsetState.Failed;
        }
        finally { if (uninit) CoUninitialize(); }
    }

    /// <summary>
    /// Disconnects the headset when connected, reconnects it otherwise, then waits for the endpoint to reach
    /// the expected state. Returns the resulting state.
    /// </summary>
    public static HeadsetState Toggle(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return HeadsetState.NotFound;
        if (!Initialize(out bool uninit)) return HeadsetState.Failed;
        try
        {
            Count(name, out int found, out bool wasActive);
            if (found < 0) return HeadsetState.Failed;
            if (found == 0) return HeadsetState.NotFound;

            bool connect = !wasActive;
            if (!Send(name, connect ? ReconnectProperty : DisconnectProperty)) return HeadsetState.Failed;

            // The endpoint changes state asynchronously: poll briefly.
            for (int i = 0; i < 30; i++) // the link can take several seconds to come up
            {
                Thread.Sleep(500);
                Count(name, out found, out bool active);
                if (found > 0 && active == connect)
                    return connect ? HeadsetState.Connected : HeadsetState.Disconnected;
            }
            // The command was accepted but Windows is slow to flip the endpoint: not an error.
            return connect ? HeadsetState.Connecting : HeadsetState.Disconnected;
        }
        catch (Exception ex)
        {
            Log.Error("Headset toggle failed", ex);
            return HeadsetState.Failed;
        }
        finally { if (uninit) CoUninitialize(); }
    }

    /// <summary>Opens the Bluetooth page of Windows Settings (fallback when the reconnect fails).</summary>
    public static void OpenBluetoothSettings()
    {
        fixed (char* verb = "open")
        fixed (char* file = "ms-settings:bluetooth")
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = SEE_MASK_FLAG_NO_UI,
                lpVerb = verb,
                lpFile = file,
                nShow = SW_SHOWNORMAL,
            };
            ShellExecuteExW(&info);
        }
    }

    // ---- implementation -------------------------------------------------------------------

    private static bool Initialize(out bool uninit)
    {
        int hr = CoInitializeEx(0, CoinitMultithreaded);
        uninit = hr >= 0; // S_OK / S_FALSE both need a matching CoUninitialize
        if (hr < 0 && hr != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE: COM already usable on this thread
        {
            Log.Error($"CoInitializeEx failed (0x{hr:X8})");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Walks the render endpoints of every state. Counts those whose friendly name contains
    /// <paramref name="name"/> (-1 on enumeration failure) and reports whether one is active.
    /// </summary>
    private static void Count(string name, out int found, out bool anyActive)
    {
        found = 0;
        anyActive = false;
        void* enumerator = null;
        void* collection = null;
        try
        {
            var clsid = ClsidEnumerator;
            var iid = IidEnumerator;
            int hr = CoCreateInstance(&clsid, 0, ClsctxAll, &iid, &enumerator);
            if (hr < 0) { Log.Error($"MMDeviceEnumerator failed (0x{hr:X8})"); found = -1; return; }

            // IMMDeviceEnumerator::EnumAudioEndpoints(eRender = 0, mask, out collection)
            hr = ((delegate* unmanaged<void*, int, uint, void**, int>)Slot(enumerator, 3))(enumerator, 0, StateMaskAll, &collection);
            if (hr < 0) { Log.Error($"EnumAudioEndpoints failed (0x{hr:X8})"); found = -1; return; }

            uint count;
            ((delegate* unmanaged<void*, uint*, int>)Slot(collection, 3))(collection, &count);
            for (uint i = 0; i < count; i++)
            {
                void* device = null;
                if (((delegate* unmanaged<void*, uint, void**, int>)Slot(collection, 4))(collection, i, &device) < 0) continue;
                try
                {
                    if (!NameMatches(device, name)) continue;
                    found++;
                    uint state;
                    ((delegate* unmanaged<void*, uint*, int>)Slot(device, 6))(device, &state);
                    if (state == DeviceStateActive) anyActive = true;
                    if (_address == 0) _address = ParseAddress(PeerId(device));
                }
                finally { Release(device); }
            }
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }

        // The audio endpoint can lag behind the link by many seconds: the Bluetooth link is the faster signal.
        if (found > 0 && !anyActive && LinkConnected(_address)) anyActive = true;
    }

    /// <summary>Id of the device the endpoint's first connector is wired to (contains the Bluetooth address).</summary>
    private static string? PeerId(void* device)
    {
        void* topology = null, connector = null;
        char* id = null;
        try
        {
            var iid = IidDeviceTopology;
            if (((delegate* unmanaged<void*, Guid*, uint, void*, void**, int>)Slot(device, 3))(device, &iid, ClsctxAll, null, &topology) < 0) return null;
            if (((delegate* unmanaged<void*, uint, void**, int>)Slot(topology, 4))(topology, 0, &connector) < 0) return null;
            if (((delegate* unmanaged<void*, char**, int>)Slot(connector, 10))(connector, &id) < 0 || id == null) return null;
            return new string(id);
        }
        finally
        {
            if (id != null) CoTaskMemFree(id);
            Release(connector);
            Release(topology);
        }
    }

    /// <summary>Extracts the 12 hex digits before "_c0000..." from a bthenum device path.</summary>
    private static ulong ParseAddress(string? id)
    {
        if (id is null) return 0;
        int end = id.LastIndexOf("_c", StringComparison.OrdinalIgnoreCase);
        if (end < 12) return 0;
        return ulong.TryParse(id.AsSpan(end - 12, 12), System.Globalization.NumberStyles.HexNumber, null, out var address) ? address : 0;
    }

    private static bool LinkConnected(ulong address)
    {
        if (address == 0) return false;
        var info = stackalloc byte[560];
        new Span<byte>(info, 560).Clear();
        *(uint*)info = 560;
        *(ulong*)(info + 8) = address;
        return BluetoothGetDeviceInfo(0, info) == 0 && *(uint*)(info + 20) != 0;
    }

    private static bool NameMatches(void* device, string name)
    {
        void* store = null;
        // IMMDevice::OpenPropertyStore(STGM_READ = 0, out store)
        if (((delegate* unmanaged<void*, uint, void**, int>)Slot(device, 4))(device, 0, &store) < 0) return false;
        try
        {
            var key = stackalloc byte[20];
            *(Guid*)key = PkeyFriendlyNameFmtid;
            *(uint*)(key + 16) = PkeyFriendlyNamePid;

            var variant = stackalloc byte[24]; // PROPVARIANT: vt @0, pointer @8 (x64)
            new Span<byte>(variant, 24).Clear();
            // IPropertyStore::GetValue(key, out value)
            if (((delegate* unmanaged<void*, void*, void*, int>)Slot(store, 5))(store, key, variant) < 0) return false;
            try
            {
                if (*(ushort*)variant != VtLpwstr) return false;
                var text = new string(*(char**)(variant + 8));
                return text.Contains(name, StringComparison.OrdinalIgnoreCase);
            }
            finally { PropVariantClear(variant); }
        }
        finally { Release(store); }
    }

    /// <summary>Sends the BtAudio property to the Bluetooth device behind every matching endpoint, once each.</summary>
    private static bool Send(string name, int property)
    {
        void* enumerator = null;
        void* collection = null;
        bool any = false, ok = false;
        var sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var clsid = ClsidEnumerator;
            var iid = IidEnumerator;
            if (CoCreateInstance(&clsid, 0, ClsctxAll, &iid, &enumerator) < 0) return false;
            if (((delegate* unmanaged<void*, int, uint, void**, int>)Slot(enumerator, 3))(enumerator, 0, StateMaskAll, &collection) < 0)
                return false;

            uint count;
            ((delegate* unmanaged<void*, uint*, int>)Slot(collection, 3))(collection, &count);
            for (uint i = 0; i < count; i++)
            {
                void* device = null;
                if (((delegate* unmanaged<void*, uint, void**, int>)Slot(collection, 4))(collection, i, &device) < 0) continue;
                try
                {
                    if (!NameMatches(device, name)) continue;
                    any = true;
                    if (SendToEndpoint(device, property, sent)) ok = true;
                }
                finally { Release(device); }
            }
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }
        return any && ok;
    }

    private static bool SendToEndpoint(void* device, int property, HashSet<string> sent)
    {
        void* topology = null, connector = null, peer = null, part = null, ks = null;
        char* peerId = null;
        string? peerPath = null;
        try
        {
            var iidTopology = IidDeviceTopology;
            // IMMDevice::Activate(iid, clsctx, null, out obj)
            int hr = ((delegate* unmanaged<void*, Guid*, uint, void*, void**, int>)Slot(device, 3))(device, &iidTopology, ClsctxAll, null, &topology);
            if (hr < 0) { Log.Info($"Headset: no topology (0x{hr:X8})"); return false; }

            // IDeviceTopology::GetConnector(0, out connector)
            hr = ((delegate* unmanaged<void*, uint, void**, int>)Slot(topology, 4))(topology, 0, &connector);
            if (hr < 0) { Log.Info($"Headset: no connector (0x{hr:X8})"); return false; }

            // IConnector::GetDeviceIdConnectedTo (10): used to send once per underlying device
            if (((delegate* unmanaged<void*, char**, int>)Slot(connector, 10))(connector, &peerId) >= 0 && peerId != null)
            {
                peerPath = new string(peerId);
                if (_address == 0) _address = ParseAddress(peerPath);
                if (!sent.Add(peerPath)) return true;
                if (SendDirect(peerPath, property)) return true; // the path that works on real hardware
            }

            // IConnector::GetConnectedTo (8)
            hr = ((delegate* unmanaged<void*, void**, int>)Slot(connector, 8))(connector, &peer);
            if (hr < 0) { Log.Info($"Headset: not connected to a device (0x{hr:X8})"); return false; }

            var iidPart = IidPart;
            if (QueryInterface(peer, &iidPart, &part) < 0) { Log.Info("Headset: connector has no IPart"); return false; }

            // The control is not always on the connected part itself: also walk its neighbours.
            ks = FindKsControl(part, 11, 6, "out");
            if (ks == null) ks = FindKsControl(part, 10, 6, "in");
            if (ks == null)
            {
                Log.Info($"Headset: no IKsControl on the connected part or its neighbours; trying the device directly ({peerPath})");
                return peerPath != null && SendDirect(peerPath, property);
            }

            // IKsControl::KsProperty(KSPROPERTY, len, data = null, 0, out returned) (3)
            var prop = stackalloc byte[24];
            foreach (uint flags in stackalloc uint[] { KsPropertyTypeGet, KsPropertyTypeSet })
            {
                *(Guid*)prop = KsPropSetBtAudio;
                *(uint*)(prop + 16) = (uint)property;
                *(uint*)(prop + 20) = flags;
                uint returned = 0;
                hr = ((delegate* unmanaged<void*, void*, uint, void*, uint, uint*, int>)Slot(ks, 3))(ks, prop, 24, null, 0, &returned);
                if (hr >= 0)
                {
                    Log.Info($"Headset: BtAudio property {property} sent");
                    return true;
                }
                Log.Info($"Headset: KsProperty flags {flags} failed (0x{hr:X8})");
            }
            return false;
        }
        finally
        {
            if (peerId != null) CoTaskMemFree(peerId);
            Release(ks);
            Release(part);
            Release(peer);
            Release(connector);
            Release(topology);
        }
    }

    /// <summary>
    /// IKsControl::KsProperty is IOCTL_KS_PROPERTY on the KS filter handle: opens the device interface the
    /// endpoint is connected to and sends the property itself.
    /// </summary>
    private static bool SendDirect(string id, int property)
    {
        // Ids may carry a "{flow}." prefix (e.g. "{2}.\?\...") that CreateFile does not understand.
        string path = id;
        int dot = path.IndexOf('.');
        if (path.StartsWith('{') && dot > 0 && dot + 1 < path.Length) path = path[(dot + 1)..];

        nint file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (file == INVALID_HANDLE_VALUE)
        {
            Log.Info($"Headset: cannot open {path} (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
            return false;
        }
        try
        {
            var prop = stackalloc byte[24];
            foreach (uint flags in stackalloc uint[] { KsPropertyTypeGet, KsPropertyTypeSet })
            {
                *(Guid*)prop = KsPropSetBtAudio;
                *(uint*)(prop + 16) = (uint)property;
                *(uint*)(prop + 20) = flags;
                uint returned = 0;
                if (DeviceIoControl(file, 0x2F0003, prop, 24, null, 0, &returned, 0) != 0)
                {
                    Log.Info($"Headset: BtAudio property {property} sent directly");
                    return true;
                }
                Log.Info($"Headset: IOCTL_KS_PROPERTY flags {flags} failed (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
            }
            return false;
        }
        finally { CloseHandle(file); }
    }

    /// <summary>
    /// Tries IPart::Activate(IKsControl) on <paramref name="part"/>, then recursively on the parts reached
    /// through <paramref name="enumSlot"/> (10 = incoming, 11 = outgoing). Returns an owned IKsControl or null.
    /// </summary>
    private static void* FindKsControl(void* part, int enumSlot, int depth, string path)
    {
        var iidKs = IidKsControl;
        void* ks = null;
        // IPart::Activate(clsctx, iid, out obj) (13)
        int hr = ((delegate* unmanaged<void*, uint, Guid*, void**, int>)Slot(part, 13))(part, ClsctxAll, &iidKs, &ks);
        if (hr >= 0 && ks != null)
        {
            Log.Info($"Headset: IKsControl found at {path}");
            return ks;
        }
        if (depth == 0) return null;

        void* list = null;
        if (((delegate* unmanaged<void*, void**, int>)Slot(part, enumSlot))(part, &list) < 0 || list == null) return null;
        try
        {
            uint count;
            ((delegate* unmanaged<void*, uint*, int>)Slot(list, 3))(list, &count);
            for (uint i = 0; i < count; i++)
            {
                void* next = null;
                if (((delegate* unmanaged<void*, uint, void**, int>)Slot(list, 4))(list, i, &next) < 0 || next == null) continue;
                try
                {
                    var found = FindKsControl(next, enumSlot, depth - 1, $"{path}>{i}");
                    if (found != null) return found;
                }
                finally { Release(next); }
            }
        }
        finally { Release(list); }
        return null;
    }

    private static void* Slot(void* obj, int index) => (*(void***)obj)[index];

    private static int QueryInterface(void* obj, Guid* iid, void** result) =>
        ((delegate* unmanaged<void*, Guid*, void**, int>)Slot(obj, 0))(obj, iid, result);

    private static void Release(void* obj)
    {
        if (obj != null) ((delegate* unmanaged<void*, uint>)Slot(obj, 2))(obj);
    }
}
