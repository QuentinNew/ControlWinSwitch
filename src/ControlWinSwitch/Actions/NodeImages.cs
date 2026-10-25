using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ControlWinSwitch.Config;
using ControlWinSwitch.Native;
using ControlWinSwitch.UI;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>
/// The pictures of "image" node icons. A picked file is cropped to its centered square, scaled to 128x128 and stored as
/// %APPDATA%\ControlWinSwitch\nodes\&lt;hash&gt;.png, so the original can move or disappear. The name is the first 12 hex
/// characters of the SHA-256 of the picked file: the same picture on several nodes is one file.
/// </summary>
internal static unsafe partial class NodeImages
{
    public const int Size = 128;

    /// <summary>How a cached picture is written in the config; env vars are expanded when it is loaded.</summary>
    private const string ConfigFolder = @"%APPDATA%\ControlWinSwitch\nodes";

    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ControlWinSwitch", "nodes");

    [GeneratedRegex("^[0-9a-f]{12}\\.png$")]
    private static partial Regex CacheName();

    /// <summary>"3f9a1c5e7b20.png" for these file contents.</summary>
    public static string FileName(ReadOnlySpan<byte> contents) => Convert.ToHexStringLower(SHA256.HashData(contents))[..12] + ".png";

    public static bool IsCacheName(string fileName) => CacheName().IsMatch(fileName);

    /// <summary>The part of a w x h picture that is kept: the centered square.</summary>
    public static (int X, int Y, int Side) CropSquare(int width, int height)
    {
        int side = Math.Min(width, height);
        return ((width - side) / 2, (height - side) / 2, side);
    }

    /// <summary>Is <paramref name="path"/> (env vars allowed) a picture of the cache folder?</summary>
    public static bool IsCached(string path)
    {
        try
        {
            string full = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return IsCacheName(Path.GetFileName(full))
                && string.Equals(Path.GetDirectoryName(Path.GetFullPath(full)), Folder, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Imports <paramref name="source"/> into the cache. Returns null and the path to keep in the config, or why it failed
    /// (then <paramref name="path"/> is the source, unchanged).
    /// </summary>
    public static string? Import(string source, out string path)
    {
        path = source;
        nint src = 0, dst = 0, graphics = 0;
        try
        {
            string full = Environment.ExpandEnvironmentVariables(source.Trim().Trim('"'));
            if (!File.Exists(full)) return "That file does not exist";
            if (!CircleCanvas.EnsureStarted()) return "Pictures are unavailable (GDI+)";

            string name = FileName(File.ReadAllBytes(full));
            string target = Path.Combine(Folder, name);
            if (File.Exists(target))
            {
                path = ConfigPath(name);
                return null;
            }

            if (GdipCreateBitmapFromFile(full, &src) != 0 || src == 0) return "That file is not a picture";
            uint w, h;
            GdipGetImageWidth(src, &w);
            GdipGetImageHeight(src, &h);
            if (w == 0 || h == 0) return "That file is not a picture";

            var (x, y, side) = CropSquare((int)w, (int)h);
            if (GdipCreateBitmapFromScan0(Size, Size, 0, PixelFormat32bppARGB, null, &dst) != 0) return "Not enough memory";
            if (GdipGetImageGraphicsContext(dst, &graphics) != 0) return "Not enough memory";
            GdipSetInterpolationMode(graphics, InterpolationModeHighQualityBicubic);
            GdipSetPixelOffsetMode(graphics, PixelOffsetModeHalf);
            GdipDrawImageRectRectI(graphics, src, 0, 0, Size, Size, x, y, side, side, UnitPixel, 0, 0, 0);

            Directory.CreateDirectory(Folder);
            var png = new Guid("557CF406-1A04-11D3-9A73-0000F81EF32E");
            if (GdipSaveImageToFile(dst, target, &png, 0) != 0) return "Cannot save the picture";
            path = ConfigPath(name);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Node picture import failed", ex);
            return "Cannot save the picture";
        }
        finally
        {
            if (graphics != 0) GdipDeleteGraphics(graphics);
            if (dst != 0) GdipDisposeImage(dst);
            if (src != 0) GdipDisposeImage(src);
        }
    }

    /// <summary>The picture to keep in an icon: the cached copy of <paramref name="path"/>, or the path itself when it is no usable picture.</summary>
    public static string Cached(string path) =>
        path.Length == 0 || IsCached(path) || Import(path, out string cached) is not null ? path : cached;

    /// <summary>Every cached picture that an "image" icon of these trees uses (file names).</summary>
    public static HashSet<string> Referenced(IEnumerable<MenuNodeConfig>? trees)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(trees, names);
        return names;
    }

    private static void Collect(IEnumerable<MenuNodeConfig>? nodes, HashSet<string> names)
    {
        if (nodes is null) return;
        foreach (var node in nodes)
        {
            if (node.Icon is { Path: { Length: > 0 } path } icon
                && icon.Kind.Equals("image", StringComparison.OrdinalIgnoreCase) && IsCached(path))
                names.Add(Path.GetFileName(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))));
            Collect(node.Children, names);
        }
    }

    /// <summary>The cached pictures among <paramref name="fileNames"/> that no icon uses. Other files are never listed.</summary>
    public static List<string> Unused(IEnumerable<string> fileNames, HashSet<string> referenced) =>
        fileNames.Where(n => IsCacheName(n) && !referenced.Contains(n)).ToList();

    /// <summary>Deletes the cached pictures that neither menu uses any more.</summary>
    public static void Sweep(IEnumerable<MenuNodeConfig>? radial, IEnumerable<MenuNodeConfig>? list)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var referenced = Referenced(radial);
            referenced.UnionWith(Referenced(list));
            foreach (string name in Unused(Directory.EnumerateFiles(Folder).Select(p => Path.GetFileName(p)), referenced))
            {
                try { File.Delete(Path.Combine(Folder, name)); }
                catch (Exception ex) { Log.Error("Node picture delete failed", ex); }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Node picture cleanup failed", ex);
        }
    }

    private static string ConfigPath(string fileName) => ConfigFolder + @"\" + fileName;
}
