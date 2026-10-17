using ControlWin.App;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.UI;

/// <summary>
/// GDI+ images for radial nodes whose icon is a file's own icon ("auto") or a picture ("image"). Loaded on first
/// use and kept until <see cref="Clear"/> (the menu clears it each time it opens, so edits show up).
/// </summary>
internal static unsafe class RadialIcons
{
    private static readonly Dictionary<string, nint> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The image for the entry's icon, or 0 when it has none (the glyph is drawn instead).
    /// <paramref name="cover"/>: the image fills the node; otherwise it sits inside it.
    /// </summary>
    public static nint Get(MenuEntry entry, out bool cover) => Get(entry.Icon, entry.Launch?.Target, out cover);

    /// <summary>Same, for an icon setting and the shortcut target it belongs to (may be null).</summary>
    public static nint Get(Config.MenuIconConfig? icon, string? target, out bool cover)
    {
        cover = false;
        if (icon is null) return 0;

        if (icon.Kind.Equals("image", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(icon.Path))
        {
            cover = true;
            return Cached("image|" + icon.Path, () => LoadImage(icon.Path!));
        }
        if (icon.Kind.Equals("auto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(target))
            return Cached("auto|" + target, () => LoadFileIcon(target));
        return 0;
    }

    public static void Clear()
    {
        foreach (var image in s_cache.Values)
            if (image != 0) GdipDisposeImage(image);
        s_cache.Clear();
    }

    private static nint Cached(string key, Func<nint> load)
    {
        if (s_cache.TryGetValue(key, out var image)) return image;
        image = CircleCanvas.EnsureStarted() ? load() : 0;
        s_cache[key] = image;
        return image;
    }

    private static nint LoadImage(string path)
    {
        try
        {
            string full = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            nint bitmap;
            return File.Exists(full) && GdipCreateBitmapFromFile(full, &bitmap) == 0 ? bitmap : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static nint LoadFileIcon(string target)
    {
        try
        {
            string? file = Resolve(target);
            if (file is null) return 0; // URLs and unknown names: the glyph is used

            var info = new SHFILEINFOW();
            if (SHGetFileInfoW(file, 0, &info, (uint)sizeof(SHFILEINFOW), SHGFI_ICON | SHGFI_LARGEICON) == 0 || info.hIcon == 0)
                return 0;

            nint bitmap = 0;
            int status = GdipCreateBitmapFromHICON(info.hIcon, &bitmap);
            DestroyIcon(info.hIcon);
            return status == 0 ? bitmap : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The file a shortcut target points at: an existing path, or a bare name found on the PATH.</summary>
    private static string? Resolve(string target)
    {
        string text = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        if (text.Contains("://", StringComparison.Ordinal)) return null;
        if (File.Exists(text) || Directory.Exists(text)) return text;
        if (text.Contains('\\') || text.Contains('/')) return null;

        char* buffer = stackalloc char[520];
        uint length = SearchPathW(null, text, ".exe", 520, buffer, 0);
        return length is > 0 and < 520 ? new string(buffer, 0, (int)length) : null;
    }
}
