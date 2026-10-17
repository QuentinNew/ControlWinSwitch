using System.Runtime.InteropServices;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.Actions;

/// <summary>
/// Hides physical controllers from every other application through the installed HidHide driver
/// (https://github.com/nefarius/HidHide): the controller's device goes on the blacklist, ControlWin's own exe on
/// the application whitelist, and the cloak is switched on. No elevation needed, but the control device is
/// exclusive: it cannot be opened while the HidHide Configuration Client (or the CLI) holds it.
/// </summary>
internal static unsafe class HidHide
{
    private static readonly string ControlDevice = new string((char)92, 2) + "." + (char)92 + "HidHide"; // two backslashes, dot, backslash
    private const uint DeviceType = 32769, FileReadData = 1;
    private static readonly uint IoctlGetWhitelist = Code(2048), IoctlSetWhitelist = Code(2049),
        IoctlSetBlacklist = Code(2051), IoctlSetActive = Code(2053);

    // CTL_CODE(DeviceType, function, METHOD_BUFFERED, FILE_READ_DATA)
    private static uint Code(uint function) => (DeviceType << 16) | (FileReadData << 14) | (function << 2);

    /// <summary>
    /// Makes exactly <paramref name="interfacePaths"/> (HID device interface paths) hidden; an empty set switches the
    /// cloak off and clears the blacklist. Returns null on success, otherwise a short reason for the user.
    /// </summary>
    public static string? Apply(IReadOnlyCollection<string> interfacePaths)
    {
        var device = CreateFileW(ControlDevice, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | 4, 0, OPEN_EXISTING, 0, 0);
        if (device == INVALID_HANDLE_VALUE)
        {
            int error = Marshal.GetLastPInvokeError();
            return error switch
            {
                2 => interfacePaths.Count == 0 ? null : "HidHide is not installed (error 2)",
                ERROR_ACCESS_DENIED => "HidHide is busy: close the HidHide Configuration Client",
                _ => $"HidHide unavailable (error {error})",
            };
        }

        try
        {
            var instances = new List<string>();
            foreach (var path in interfacePaths)
            {
                var id = InstanceId(path);
                if (id is null) return "Cannot resolve the controller's device id";
                instances.Add(id);
            }

            if (instances.Count > 0)
            {
                var whitelist = GetMultiString(device, IoctlGetWhitelist);
                string? self = NtImagePath(Environment.ProcessPath);
                if (self is null) return "Cannot resolve ControlWin's own path";
                if (!whitelist.Contains(self, StringComparer.OrdinalIgnoreCase)) whitelist.Add(self);
                if (!SetMultiString(device, IoctlSetWhitelist, whitelist)) return Fail("whitelist");
            }

            if (!SetMultiString(device, IoctlSetBlacklist, instances)) return Fail("blacklist");

            byte active = (byte)(instances.Count > 0 ? 1 : 0);
            uint returned;
            if (DeviceIoControl(device, IoctlSetActive, &active, 1, null, 0, &returned, 0) == 0) return Fail("cloak");
            return null;
        }
        finally
        {
            CloseHandle(device);
        }
    }

    private static string Fail(string what)
    {
        int error = Marshal.GetLastPInvokeError();
        Log.Error($"HidHide: setting the {what} failed (error {error})");
        return $"HidHide: setting the {what} failed (error {error})";
    }

    /// <summary>Device instance id of a HID interface (HID\...\...): what HidHide's blacklist holds.</summary>
    internal static string? InstanceId(string interfacePath)
    {
        // DEVPKEY_Device_InstanceId = {78c34fc8-104a-4aca-9ea4-524d52996e57}, 256: key = GUID + pid.
        var key = stackalloc byte[20];
        *(Guid*)key = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57");
        *(uint*)(key + 16) = 256;
        uint type = 0, size = 0;
        CM_Get_Device_Interface_PropertyW(interfacePath, (Guid*)key, &type, null, &size, 0); // CR_BUFFER_SMALL: size set
        if (size < 2 || size > 4096) return null;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
            if (CM_Get_Device_Interface_PropertyW(interfacePath, (Guid*)key, &type, p, &size, 0) != 0) return null;
        return new string(MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, (int)size))).TrimEnd('\0');
    }

    /// <summary>"C:\dir\app.exe" -> "\Device\HarddiskVolumeN\dir\app.exe", the form HidHide's whitelist holds.</summary>
    private static string? NtImagePath(string? path)
    {
        if (path is null || path.Length < 3 || path[1] != ':') return null;
        var buffer = stackalloc char[520];
        if (QueryDosDeviceW(path[..2], buffer, 520) == 0) return null;
        return new string(buffer) + path[2..];
    }

    private static List<string> GetMultiString(nint device, uint code)
    {
        var result = new List<string>();
        uint needed = 0;
        if (DeviceIoControl(device, code, null, 0, null, 0, &needed, 0) == 0 || needed < 2) return result;
        var buffer = new char[needed / 2];
        fixed (char* p = buffer)
            if (DeviceIoControl(device, code, null, 0, p, needed, &needed, 0) == 0) return result;
        foreach (var s in new string(buffer, 0, (int)(needed / 2)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            result.Add(s);
        return result;
    }

    private static bool SetMultiString(nint device, uint code, List<string> items)
    {
        var text = items.Count == 0 ? "\0\0" : string.Join('\0', items) + "\0\0";
        uint returned;
        fixed (char* p = text)
            return DeviceIoControl(device, code, p, (uint)(text.Length * 2), null, 0, &returned, 0) != 0;
    }
}
