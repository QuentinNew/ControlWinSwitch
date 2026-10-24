using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>The standard Windows "Open" dialog.</summary>
internal static unsafe class FilePicker
{
    /// <summary>Filter for <see cref="Pick"/>: pictures the controller picture import understands.</summary>
    public const string PictureFilter = "Pictures\0*.png;*.jpg;*.jpeg;*.bmp\0\0";

    /// <summary>
    /// Blocks until the user picks a file or cancels. <paramref name="filter"/> is the usual double-null list of
    /// description / pattern pairs. Returns the full path, or null.
    /// </summary>
    public static string? Pick(nint owner, string title, string filter)
    {
        // The owner may be a window that never takes the foreground: a synthetic Alt press lets the dialog come to the front.
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);

        const int Max = 1024;
        char* file = stackalloc char[Max];
        file[0] = '\0';
        fixed (char* pFilter = filter)
        fixed (char* pTitle = title)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = owner,
                lpstrFilter = pFilter,
                nFilterIndex = 1,
                lpstrFile = file,
                nMaxFile = Max,
                lpstrTitle = pTitle,
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR,
            };
            return GetOpenFileNameW(&ofn) != 0 ? new string(file) : null;
        }
    }
}
