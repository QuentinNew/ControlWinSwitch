using System.Runtime.InteropServices;
using ControlWin.Native;
using ControlWin.UI;
using static ControlWin.Native.Win32;

namespace ControlWin.Actions;

/// <summary>
/// The picture a user gave a controller. A picked file is scaled down (whole picture, its proportions kept, at most
/// 128 pixels on the longer side) and stored as %APPDATA%\ControlWin\controllers\&lt;id&gt;.png, so the original can
/// move or disappear. Loaded images are kept (GDI+) until the picture changes.
/// </summary>
internal static unsafe class ControllerPictures
{
    public const int Size = 128;

    private static readonly Dictionary<string, nint> s_images = new(StringComparer.OrdinalIgnoreCase);

    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ControlWin", "controllers");

    /// <summary>File name safe version of a controller id ("A4:5C:..." or "dev:HID\...").</summary>
    public static string FileName(string id)
    {
        var chars = id.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_').ToArray();
        return new string(chars) + ".png";
    }

    public static string PathFor(string id) => Path.Combine(Folder, FileName(id));

    public static bool Has(string id) => File.Exists(PathFor(id));

    /// <summary>The picture as a GDI+ image (0 when there is none). Owned by this class: do not dispose.</summary>
    public static nint Get(string id)
    {
        if (s_images.TryGetValue(id, out var cached)) return cached;
        nint image = 0;
        string path = PathFor(id);
        if (File.Exists(path) && CircleCanvas.EnsureStarted())
        {
            nint bitmap;
            if (GdipCreateBitmapFromFile(path, &bitmap) == 0) image = bitmap;
        }
        s_images[id] = image;
        return image;
    }

    /// <summary>Imports <paramref name="source"/> as the picture of <paramref name="id"/>. Returns null, or why it failed.</summary>
    public static string? Import(string source, string id)
    {
        if (!CircleCanvas.EnsureStarted()) return "Pictures are unavailable (GDI+)";
        nint src = 0, dst = 0, graphics = 0;
        try
        {
            if (GdipCreateBitmapFromFile(source, &src) != 0 || src == 0) return "That file is not a picture";
            uint w, h;
            GdipGetImageWidth(src, &w);
            GdipGetImageHeight(src, &h);
            if (w == 0 || h == 0) return "That file is not a picture";

            // The whole picture, longer side = Size (never enlarged).
            double scale = Math.Min(1.0, Size / (double)Math.Max(w, h));
            int dw = Math.Max(1, (int)Math.Round(w * scale)), dh = Math.Max(1, (int)Math.Round(h * scale));

            if (GdipCreateBitmapFromScan0(dw, dh, 0, PixelFormat32bppARGB, null, &dst) != 0) return "Not enough memory";
            if (GdipGetImageGraphicsContext(dst, &graphics) != 0) return "Not enough memory";
            GdipSetInterpolationMode(graphics, InterpolationModeHighQualityBicubic);
            GdipSetPixelOffsetMode(graphics, PixelOffsetModeHalf);
            GdipDrawImageRectRectI(graphics, src, 0, 0, dw, dh, 0, 0, (int)w, (int)h, UnitPixel, 0, 0, 0);

            Directory.CreateDirectory(Folder);
            Release(id); // the old file is locked while its image is loaded
            var png = new Guid("557CF406-1A04-11D3-9A73-0000F81EF32E");
            string target = PathFor(id);
            if (GdipSaveImageToFile(dst, target, &png, 0) != 0) return "Cannot save the picture";
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Controller picture import failed", ex);
            return "Cannot save the picture";
        }
        finally
        {
            if (graphics != 0) GdipDeleteGraphics(graphics);
            if (dst != 0) GdipDisposeImage(dst);
            if (src != 0) GdipDisposeImage(src);
        }
    }

    public static void Remove(string id)
    {
        Release(id);
        try { File.Delete(PathFor(id)); } catch (Exception ex) { Log.Error("Controller picture delete failed", ex); }
    }

    /// <summary>Moves a picture to another id (a controller identified by MAC after it was known by instance id).</summary>
    public static void Move(string from, string to)
    {
        Release(from);
        Release(to);
        try
        {
            if (File.Exists(PathFor(from)) && !File.Exists(PathFor(to))) File.Move(PathFor(from), PathFor(to));
        }
        catch (Exception ex)
        {
            Log.Error("Controller picture move failed", ex);
        }
    }

    private static void Release(string id)
    {
        if (s_images.Remove(id, out var image) && image != 0) GdipDisposeImage(image);
    }

    /// <summary>
    /// Draws the whole picture, centered and as large as fits the <paramref name="size"/> square (proportions kept),
    /// with rounded corners of <paramref name="diameter"/>.
    /// </summary>
    public static void DrawRounded(nint dc, nint image, int x, int y, int size, int diameter)
    {
        uint iw, ih;
        if (GdipGetImageWidth(image, &iw) != 0 || GdipGetImageHeight(image, &ih) != 0 || iw == 0 || ih == 0) return;
        double scale = size / (double)Math.Max(iw, ih);
        int w = Math.Max(1, (int)Math.Round(iw * scale)), h = Math.Max(1, (int)Math.Round(ih * scale));
        int left = x + (size - w) / 2, top = y + (size - h) / 2;
        int d = Math.Clamp(diameter, 2, Math.Min(w, h));

        nint graphics, path = 0;
        if (GdipCreateFromHDC(dc, &graphics) != 0) return;
        try
        {
            GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias);
            GdipSetInterpolationMode(graphics, InterpolationModeHighQualityBicubic);
            GdipSetPixelOffsetMode(graphics, PixelOffsetModeHalf);
            if (GdipCreatePath(0, &path) == 0)
            {
                GdipAddPathArcI(path, left, top, d, d, 180f, 90f);
                GdipAddPathArcI(path, left + w - d, top, d, d, 270f, 90f);
                GdipAddPathArcI(path, left + w - d, top + h - d, d, d, 0f, 90f);
                GdipAddPathArcI(path, left, top + h - d, d, d, 90f, 90f);
                GdipClosePathFigure(path);
                GdipSetClipPath(graphics, path, 0);
            }
            GdipDrawImageRectI(graphics, image, left, top, w, h);
        }
        finally
        {
            if (path != 0) GdipDeletePath(path);
            GdipDeleteGraphics(graphics);
        }
    }
}
