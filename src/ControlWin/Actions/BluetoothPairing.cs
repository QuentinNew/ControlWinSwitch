using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.Actions;

internal enum PairResult { Paired, NotFound, Failed }

/// <summary>A Pro Controller Windows remembers: its MAC address, whether it is connected and when it was last used (UTC).</summary>
internal readonly record struct RememberedController(string Mac, bool Connected, DateTime LastUsed);

/// <summary>
/// Pairs a Pro Controller put in pairing mode with its sync button, the way "Add a device" does: inquiry, then
/// authenticate and enable the HID service. One Windows already remembers is paired again only when it answers the
/// inquiry (it is in sync mode): the remembered pairing is stale after the controller was paired with a Switch.
/// Blocks for the search (<see cref="SearchMs"/>): run it on a worker thread.
/// </summary>
internal static unsafe class BluetoothPairing
{
    private static readonly Guid HidService = new("00001124-0000-1000-8000-00805F9B34FB");

    private const string ControllerName = "Pro Controller";
    private const uint MitmNotRequiredBonding = 2, ServiceEnable = 1;

    /// <summary>How long the search lasts (inquiry rounds of about five seconds each).</summary>
    public const int SearchMs = 40_000;

    /// <summary>Pairs a Pro Controller in sync mode; <paramref name="only"/>: that controller only (its MAC), null for any.</summary>
    public static PairResult PairProController(string? only = null)
    {
        try
        {
            ulong target = only is null ? 0 : ParseAddress(only);
            uint findParams = 4; // BLUETOOTH_FIND_RADIO_PARAMS { dwSize }
            nint radio;
            nint find = BluetoothFindFirstRadio(&findParams, &radio);
            if (find == 0)
            {
                Log.Error("Pairing: no Bluetooth radio");
                return PairResult.Failed;
            }

            var result = PairResult.NotFound;
            try
            {
                do
                {
                    // The controller stays discoverable for a while after the sync button: search in rounds.
                    long until = Environment.TickCount64 + SearchMs;
                    while (result == PairResult.NotFound && Environment.TickCount64 < until)
                        result = SearchRadio(radio, target);
                    CloseHandle(radio);
                    if (result != PairResult.NotFound) break;
                } while (BluetoothFindNextRadio(find, &radio) != 0);
            }
            finally { BluetoothFindRadioClose(find); }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Pairing failed", ex);
            return PairResult.Failed;
        }
    }

    /// <summary>The Pro Controllers Windows remembers (paired), connected or not.</summary>
    public static List<RememberedController> RememberedProControllers()
    {
        var list = new List<RememberedController>();
        try
        {
            var search = stackalloc byte[40];
            new Span<byte>(search, 40).Clear();
            *(uint*)search = 40;
            *(int*)(search + 4) = 1;  // authenticated
            *(int*)(search + 8) = 1;  // remembered
            *(int*)(search + 16) = 1; // connected
            // hRadio @32 left at 0: every radio.

            var info = stackalloc byte[560];
            new Span<byte>(info, 560).Clear();
            *(uint*)info = 560;

            nint find = BluetoothFindFirstDevice(search, info);
            if (find == 0) return list;
            try
            {
                do
                {
                    string name = new((char*)(info + 64));
                    if (!name.Contains(ControllerName, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new RememberedController(FormatAddress(*(ulong*)(info + 8)), *(int*)(info + 20) != 0,
                        ReadTime(info + 48) ?? DateTime.MinValue));
                } while (BluetoothFindNextDevice(find, info) != 0);
            }
            finally { BluetoothFindDeviceClose(find); }
        }
        catch (Exception ex)
        {
            Log.Error("Listing the paired controllers failed", ex);
        }
        return list;
    }

    /// <summary>Unpairs a controller from Windows (like "Remove device"). Returns false when Windows refused.</summary>
    public static bool Forget(string mac)
    {
        ulong address = ParseAddress(mac);
        uint error = BluetoothRemoveDevice(&address);
        Log.Info($"Forgot controller {mac} (error {error})");
        return error == 0;
    }

    public static string FormatAddress(ulong address) => Input.JoyCon2Protocol.FormatAddress(address);

    private static ulong ParseAddress(string mac) =>
        ulong.TryParse(mac.Replace(":", ""), System.Globalization.NumberStyles.HexNumber, null, out var address) ? address : 0;

    /// <summary>A SYSTEMTIME (UTC), or null when empty.</summary>
    private static DateTime? ReadTime(byte* t)
    {
        var w = (ushort*)t;
        if (w[0] == 0) return null;
        try { return new DateTime(w[0], w[1], w[3], w[4], w[5], w[6], w[7], DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// The device answered the inquiry that started at <paramref name="since"/>: Windows updated its "last seen" time.
    /// The time is read both as UTC and as local time, since what Windows stores is not documented.
    /// </summary>
    private static bool AnsweredInquiry(byte* info, DateTime since)
    {
        if (ReadTime(info + 32) is not { } seen) return false;
        var now = DateTime.UtcNow;
        bool Within(DateTime t) => t >= since.AddSeconds(-2) && t <= now.AddSeconds(5);
        return Within(seen) || Within(DateTime.SpecifyKind(seen, DateTimeKind.Local).ToUniversalTime());
    }

    private static nint _radio; // radio being paired on, for the pairing handler

    /// <summary>
    /// Pairing handler (BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS: deviceInfo @0 (Address @8), method @560,
    /// numeric value @572). The Pro Controller pairs with "Just Works" numeric comparison: accept it.
    /// </summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int OnAuthentication(nint param, byte* callback)
    {
        try
        {
            const uint NumericComparison = 3;
            uint method = *(uint*)(callback + 560);
            Log.Info($"Pairing: request received (method {method}, io {*(uint*)(callback + 564)}, req {*(uint*)(callback + 568)}, value {*(uint*)(callback + 572)})");
            if (method != NumericComparison)
            {
                Log.Info($"Pairing: unhandled authentication method {method}");
                return 1;
            }

            // BLUETOOTH_AUTHENTICATE_RESPONSE (48 bytes): address @0, method @8, numeric value @12, negative @44.
            // Answer from another thread: the pairing stack waits for this callback to return.
            ulong address = *(ulong*)(callback + 8);
            nint radio = _radio;
            new Thread(() =>
            {
                var response = stackalloc byte[48];
                new Span<byte>(response, 48).Clear();
                *(ulong*)response = address;
                *(uint*)(response + 8) = method;
                *(uint*)(response + 12) = 1; // "yes, the numbers match"
                uint error = BluetoothSendAuthenticationResponseEx(0, response);
                Log.Info($"Pairing: confirmation sent to {address:X12} without radio handle (error {error})");
                if (error == 0) return;
                error = BluetoothSendAuthenticationResponseEx(radio, response);
                Log.Info($"Pairing: confirmation sent with radio handle {radio:X} (error {error})");
            }) { IsBackground = true, Name = "pairing-confirm" }.Start();
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error("Pairing handler failed", ex);
            return 0;
        }
    }

    private static PairResult SearchRadio(nint radio, ulong target)
    {
        var since = DateTime.UtcNow;
        // BLUETOOTH_DEVICE_SEARCH_PARAMS (40 bytes): dwSize, fReturnAuthenticated, fReturnRemembered,
        // fReturnUnknown, fReturnConnected, fIssueInquiry, cTimeoutMultiplier (1.28 s units), hRadio @32.
        var search = stackalloc byte[40];
        new Span<byte>(search, 40).Clear();
        *(uint*)search = 40;
        *(int*)(search + 8) = 1;
        *(int*)(search + 12) = 1;
        *(int*)(search + 20) = 1;
        search[24] = 4;
        *(nint*)(search + 32) = radio;

        // BLUETOOTH_DEVICE_INFO (560 bytes): dwSize, Address @8, fConnected @20, fAuthenticated @28, stLastSeen @32,
        // stLastUsed @48 (SYSTEMTIME), szName @64.
        var info = stackalloc byte[560];
        new Span<byte>(info, 560).Clear();
        *(uint*)info = 560;

        nint find = BluetoothFindFirstDevice(search, info);
        if (find == 0) return PairResult.NotFound;
        try
        {
            do
            {
                string name = new((char*)(info + 64));
                ulong address = *(ulong*)(info + 8);
                if (!name.Contains(ControllerName, StringComparison.OrdinalIgnoreCase) || (target != 0 && address != target)) continue;
                if (*(int*)(info + 28) != 0) // authenticated: Windows already knows it
                {
                    // Connected, or not in sync mode: nothing to do. In sync mode: its pairing is stale, pair it again.
                    if (*(int*)(info + 20) != 0 || !AnsweredInquiry(info, since)) continue;
                    uint removed = BluetoothRemoveDevice(&address);
                    Log.Info($"Pairing: {FormatAddress(address)} is in sync mode, its old pairing was removed (error {removed})");
                }

                Log.Info($"Pairing: found {name}, pairing");

                // Answering the pairing request ourselves keeps Windows from raising its "Add a device"
                // notification and the Bluetooth settings confirmation.
                _radio = radio;
                nint registration = 0;
                uint error = BluetoothRegisterForAuthenticationEx(info, &registration, &OnAuthentication, 0);
                if (error != 0) Log.Error($"Pairing: registering the pairing handler failed (error {error})");
                try { error = BluetoothAuthenticateDeviceEx(0, radio, info, 0, MitmNotRequiredBonding); }
                finally { if (registration != 0) BluetoothUnregisterAuthentication(registration); }
                if (error != 0)
                {
                    Log.Error($"Pairing: authenticate failed (error {error})");
                    return PairResult.Failed;
                }
                var service = HidService;
                error = BluetoothSetServiceState(radio, info, &service, ServiceEnable);
                if (error != 0) Log.Error($"Pairing: enabling the HID service failed (error {error})");
                return PairResult.Paired;
            } while (BluetoothFindNextDevice(find, info) != 0);
        }
        finally { BluetoothFindDeviceClose(find); }
        return PairResult.NotFound;
    }
}
