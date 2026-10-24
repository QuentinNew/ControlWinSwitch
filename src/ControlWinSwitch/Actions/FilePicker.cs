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

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IidFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, SIGDN_FILESYSPATH = 0x80058000;
    private const uint ClsctxInprocServer = 0x1, CoinitApartmentThreaded = 0x2;

    /// <summary>
    /// The Explorer-style "Select folder" dialog (IFileOpenDialog), opened on <paramref name="start"/> when it exists.
    /// Blocks until the user picks a folder or cancels. Returns the full path, or null.
    /// </summary>
    public static string? PickFolder(nint owner, string title, string? start)
    {
        keybd_event(VK_MENU, 0, 0, 0); // same reason as in Pick
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);

        int init = CoInitializeEx(0, CoinitApartmentThreaded);
        void* dialog = null, folder = null, result = null;
        char* path = null;
        try
        {
            var clsid = ClsidFileOpenDialog;
            var iid = IidFileOpenDialog;
            if (CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, &dialog) < 0) return null;

            // IFileDialog: GetOptions 10, SetOptions 9.
            uint options;
            ((delegate* unmanaged<void*, uint*, int>)Slot(dialog, 10))(dialog, &options);
            ((delegate* unmanaged<void*, uint, int>)Slot(dialog, 9))(dialog, options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
            fixed (char* pTitle = title) ((delegate* unmanaged<void*, char*, int>)Slot(dialog, 17))(dialog, pTitle); // SetTitle

            if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
            {
                var iidItem = IidShellItem;
                fixed (char* pStart = start)
                    if (SHCreateItemFromParsingName(pStart, 0, &iidItem, &folder) >= 0)
                        ((delegate* unmanaged<void*, void*, int>)Slot(dialog, 12))(dialog, folder); // SetFolder
            }

            // IModalWindow::Show 3, IFileDialog::GetResult 20, IShellItem::GetDisplayName 5.
            if (((delegate* unmanaged<void*, nint, int>)Slot(dialog, 3))(dialog, owner) < 0) return null; // cancelled
            if (((delegate* unmanaged<void*, void**, int>)Slot(dialog, 20))(dialog, &result) < 0) return null;
            if (((delegate* unmanaged<void*, uint, char**, int>)Slot(result, 5))(result, SIGDN_FILESYSPATH, &path) < 0 || path == null) return null;
            return new string(path);
        }
        finally
        {
            if (path != null) CoTaskMemFree(path);
            Release(result);
            Release(folder);
            Release(dialog);
            if (init >= 0) CoUninitialize();
        }
    }

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com)
    {
        if (com != null) ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);
    }
}
