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
        search[24] = 8;
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
                uint error = BluetoothAuthenticateDeviceEx(0, radio, info, 0, MitmNotRequiredBonding);
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
