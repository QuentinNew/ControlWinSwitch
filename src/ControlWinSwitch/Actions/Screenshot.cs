using System.Globalization;
using System.Text;
using ControlWinSwitch.Native;
using ControlWinSwitch.UI;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>
/// A picture of the monitor under the cursor, saved as "&lt;window title&gt; yyyy-MM-dd HHmmss.png". <see cref="Capture"/>
/// runs on the UI thread right after the menu is hidden; <see cref="Save"/> encodes and writes on a worker thread.
/// </summary>
internal static unsafe class Screenshot
{
    private const int MaxTitleLength = 80;
    private static readonly Guid PngEncoder = new("557CF406-1A04-11D3-9A73-0000F81EF32E");
    private static readonly Guid FolderIdScreenshots = new("B7BEDE81-DF94-4682-A7D8-57A52620B86F");
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>A captured monitor: a bottom-up 32-bit DIB section, freed by <see cref="Dispose"/>.</summary>
    internal sealed class Shot : IDisposable
    {
        public nint Bitmap;
        public uint* Bits;
        public int Width, Height;
        public string Title = "";
        public DateTime Time;

        public void Dispose()
        {
            if (Bitmap != 0) DeleteObject(Bitmap);
            Bitmap = 0;
            Bits = null;
        }
    }

    /// <summary>Grabs the monitor under the cursor. Returns null and an error message when it cannot.</summary>
    public static Shot? Capture(out string? error)
    {
        error = null;
        DwmFlush(); // wait for one composition so the menu and toast that were just hidden are gone from the screen

        POINT pt;
        GetCursorPos(&pt);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (GetMonitorInfoW(MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY), &info) == 0)
        {
            error = "No screen to capture";
            return null;
        }
        var bounds = info.rcMonitor;
        int w = bounds.Width, h = bounds.Height;

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = h, // positive height: bottom-up, as CF_DIB expects
            biPlanes = 1, biBitCount = 32, biCompression = BI_RGB,
        };
        nint screen = GetDC(0);
        void* bits;
        nint dib = CreateDIBSection(screen, &header, DIB_RGB_COLORS, &bits, 0, 0);
        if (dib == 0)
        {
            ReleaseDC(0, screen);
            error = "Not enough memory for a screenshot";
            return null;
        }

        nint dc = CreateCompatibleDC(screen);
        nint old = SelectObject(dc, dib);
        bool ok = BitBlt(dc, 0, 0, w, h, screen, bounds.Left, bounds.Top, SRCCOPY | CAPTUREBLT) != 0;
        SelectObject(dc, old);
        DeleteDC(dc);
        ReleaseDC(0, screen);
        GdiFlush();
        if (!ok)
        {
            DeleteObject(dib);
            error = "Windows refused the screenshot";
            return null;
        }
        return new Shot { Bitmap = dib, Bits = (uint*)bits, Width = w, Height = h, Title = ForegroundTitle(), Time = DateTime.Now };
    }

    /// <summary>
    /// Writes the shot as a PNG (and copies it when asked), then frees it. A custom folder that cannot be created or
    /// written falls back to the Windows Screenshots folder. Returns the toast text.
    /// </summary>
    public static string Save(Shot shot, string? saveFolder, bool copy)
    {
        using (shot)
        {
            if (!CircleCanvas.EnsureStarted()) return "Screenshots are unavailable (GDI+)";

            string? custom = CustomFolder(saveFolder);
            string? saved = custom is null ? null : TrySave(shot, custom);
            bool fellBack = !string.IsNullOrWhiteSpace(saveFolder) && saved is null;
            saved ??= TrySave(shot, DefaultFolder());
            if (saved is null) return "Cannot save the screenshot - see log";

            Log.Info($"Screenshot saved: {saved}");
            string text = fellBack ? "Folder unavailable - saved to Screenshots" : "Screenshot saved";
            if (copy) text += CopyToClipboard(shot) ? " and copied" : " (not copied)";
            return text;
        }
    }

    /// <summary>Where Windows itself puts screenshots (follows a Pictures folder moved to OneDrive).</summary>
    internal static string DefaultFolder()
    {
        var id = FolderIdScreenshots;
        char* path = null;
        try
        {
            if (SHGetKnownFolderPath(&id, KF_FLAG_DONT_VERIFY, 0, &path) >= 0 && path != null) return new string(path);
        }
        finally
        {
            if (path != null) CoTaskMemFree(path);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    /// <summary>The node's folder with %VARIABLES% expanded and quotes removed; null when unset or not a full path.</summary>
    internal static string? CustomFolder(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        string path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"').Trim());
        if (!Path.IsPathFullyQualified(path)) return null;
        return Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>"&lt;title&gt; yyyy-MM-dd HHmmss" without the extension; unsafe characters become spaces.</summary>
    internal static string FileName(string title, DateTime time)
    {
        var sb = new StringBuilder(title.Length);
        foreach (char c in title)
            sb.Append(char.IsControl(c) || Array.IndexOf(InvalidNameChars, c) >= 0 ? ' ' : c);
        string clean = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > MaxTitleLength)
            clean = clean[..(char.IsHighSurrogate(clean[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength)];
        clean = clean.Trim().TrimEnd('.').Trim();
        if (clean.Length == 0) clean = "Screenshot";
        return $"{clean} {time.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture)}";
    }

    /// <summary>folder\name.png, or folder\name (2).png, (3)... when taken.</summary>
    internal static string UniquePath(string folder, string name, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string path = Path.Combine(folder, name + ".png");
        for (int n = 2; exists(path); n++) path = Path.Combine(folder, $"{name} ({n}).png");
        return path;
    }

    private static string? TrySave(Shot shot, string folder)
    {
        nint image = 0;
        try
        {
            Directory.CreateDirectory(folder);
            string path = UniquePath(folder, FileName(shot.Title, shot.Time));
            if (GdipCreateBitmapFromHBITMAP(shot.Bitmap, 0, &image) != 0 || image == 0)
            {
                Log.Error("Screenshot: GDI+ cannot read the capture");
                return null;
            }
            var png = PngEncoder;
            int status = GdipSaveImageToFile(image, path, &png, 0);
            if (status != 0)
            {
                Log.Error($"Screenshot: cannot write {path} (GDI+ status {status})");
                return null;
            }
            return path;
        }
        catch (Exception ex)
        {
            Log.Error($"Screenshot: folder {folder} unusable", ex);
            return null;
        }
        finally
        {
            if (image != 0) GdipDisposeImage(image);
        }
    }

    /// <summary>Puts the shot on the clipboard as a 32-bit CF_DIB with opaque alpha (some apps read the 4th byte).</summary>
    private static bool CopyToClipboard(Shot shot)
    {
        nuint pixels = (nuint)shot.Width * (nuint)shot.Height;
        for (nuint i = 0; i < pixels; i++) shot.Bits[i] |= 0xFF000000;

        nuint bytes = pixels * 4, headerSize = (nuint)sizeof(BITMAPINFOHEADER);
        nint handle = GlobalAlloc(GMEM_MOVEABLE, headerSize + bytes);
        if (handle == 0) return false;
        byte* dst = (byte*)GlobalLock(handle);
        if (dst == null)
        {
            GlobalFree(handle);
            return false;
        }
        *(BITMAPINFOHEADER*)dst = new BITMAPINFOHEADER
        {
            biSize = (uint)headerSize, biWidth = shot.Width, biHeight = shot.Height,
            biPlanes = 1, biBitCount = 32, biCompression = BI_RGB, biSizeImage = (uint)bytes,
        };
        Buffer.MemoryCopy(shot.Bits, dst + headerSize, bytes, bytes);
        GlobalUnlock(handle);

        // Another program may hold the clipboard for a moment.
        for (int tries = 0; OpenClipboard(0) == 0; tries++)
        {
            if (tries == 10)
            {
                GlobalFree(handle);
                Log.Info("Screenshot: clipboard busy");
                return false;
            }
            Thread.Sleep(20);
        }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_DIB, handle) != 0) return true; // the clipboard owns it now
            GlobalFree(handle);
            return false;
        }
        finally { CloseClipboard(); }
    }

    private static string ForegroundTitle()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd != 0) hwnd = GetAncestor(hwnd, GA_ROOT);
        if (hwnd == 0 || hwnd == GetShellWindow()) return "";
        char* buffer = stackalloc char[256];
        int length = GetWindowTextW(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }
}
