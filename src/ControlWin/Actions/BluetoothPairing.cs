using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.Actions;

internal enum PairResult { Paired, NotFound, Failed }

/// <summary>
/// Pairs a Pro Controller that Windows does not know yet (put in pairing mode with its sync button), the way
/// "Add a device" does: inquiry, then authenticate and enable the HID service. Blocks for the inquiry
/// (about ten seconds): run it on a worker thread.
/// </summary>
internal static unsafe class BluetoothPairing
{
    private static readonly Guid HidService = new("00001124-0000-1000-8000-00805F9B34FB");

    private const string ControllerName = "Pro Controller";
    private const uint MitmNotRequiredBonding = 2, ServiceEnable = 1;

    public static PairResult PairNewProController()
    {
        try
        {
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
                    ForgetStaleControllers(radio);
                    // The controller stays discoverable for a while after the sync button: search in rounds.
                    for (int round = 0; round < 3 && result == PairResult.NotFound; round++)
                        result = SearchRadio(radio);
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

    /// <summary>
    /// Forgets the Pro Controllers Windows remembers but that are not connected: one that was paired with a
    /// Switch since holds a new link key, so the remembered pairing is stale and blocks pairing it again.
    /// </summary>
    private static void ForgetStaleControllers(nint radio)
    {
        var search = stackalloc byte[40];
        new Span<byte>(search, 40).Clear();
        *(uint*)search = 40;
        *(int*)(search + 4) = 1; // authenticated
        *(int*)(search + 8) = 1; // remembered
        *(nint*)(search + 32) = radio;

        var info = stackalloc byte[560];
        new Span<byte>(info, 560).Clear();
        *(uint*)info = 560;

        var stale = new List<ulong>();
        nint find = BluetoothFindFirstDevice(search, info);
        if (find == 0) return;
        try
        {
            do
            {
                string name = new((char*)(info + 64));
                bool connected = *(int*)(info + 20) != 0;
                if (!connected && name.Contains(ControllerName, StringComparison.OrdinalIgnoreCase))
                    stale.Add(*(ulong*)(info + 8));
            } while (BluetoothFindNextDevice(find, info) != 0);
        }
        finally { BluetoothFindDeviceClose(find); }

        foreach (ulong address in stale)
        {
            ulong a = address;
            uint error = BluetoothRemoveDevice(&a);
            Log.Info($"Pairing: forgot old controller {address:X12} (error {error})");
        }
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

    private static PairResult SearchRadio(nint radio)
    {
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

        // BLUETOOTH_DEVICE_INFO (560 bytes): dwSize, Address @8, fAuthenticated @28, szName @64.
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
                bool authenticated = *(int*)(info + 28) != 0;
                if (authenticated || !name.Contains(ControllerName, StringComparison.OrdinalIgnoreCase)) continue;

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
