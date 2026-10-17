using ControlWin.Actions;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.UI;

/// <summary>
/// The controller tile shared by the settings page and the radial menu: picture (or gamepad icon), battery badge
/// on the bottom-right corner, name underneath.
/// </summary>
internal static class ControllerTile
{
    public const int SizeDip = 64, NameHeightDip = 28, NameGapDip = 6;
    public const string GamepadGlyph = "", NoneGlyph = "";

    private static nint s_redBrush, s_redHotBrush, s_amberBrush;
    public static nint RedBrush => s_redBrush != 0 ? s_redBrush : s_redBrush = CreateSolidBrush(Rgb(0xD6, 0x3B, 0x3B));
    public static nint RedHotBrush => s_redHotBrush != 0 ? s_redHotBrush : s_redHotBrush = CreateSolidBrush(Rgb(0xF2, 0x5C, 0x5C));
    public static nint AmberBrush => s_amberBrush != 0 ? s_amberBrush : s_amberBrush = CreateSolidBrush(Rgb(0xE0, 0xA1, 0x2B));

    public const int PicGapDip = 6, EntryGapDip = 18;

    /// <summary>
    /// Layout of the controller strip (picture, gap, battery bars per controller, a bigger gap between controllers):
    /// the x offset of each entry from the strip's left edge is put in <paramref name="xs"/>; returns the total width.
    /// </summary>
    public static int StripLayout(uint dpi, IReadOnlyList<SlotTile> controllers, int picRadius, out int[] xs)
    {
        xs = new int[controllers.Count];
        int barsW = Px(dpi, 4) * 4 + Px(dpi, 2) * 3, x = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            xs[i] = x;
            bool plus = controllers[i].Charging && controllers[i].Battery >= 0;
            x += picRadius * 2 + Px(dpi, PicGapDip) + barsW + (plus ? Px(dpi, 10) : 0) + (i < xs.Length - 1 ? Px(dpi, EntryGapDip) : 0);
        }
        return x;
    }

    private static int Px(uint dpi, int dip) => (int)Math.Round(dip * dpi / 96.0);

    /// <summary>The rounded square: picture, or gamepad icon (none icon when nothing is connected).</summary>
    public static void PaintFace(nint dc, FontSet fonts, uint dpi, SlotTile info, int l, int t)
    {
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        bool on = info.Connected;
        int tile = Px(dpi, SizeDip);
        Gdi.RoundedFill(dc, on && info.Image == 0 ? Gdi.OnBrush : Gdi.PanelBrush, l, t, l + tile, t + tile, Px(dpi, 14));
        if (on && info.Image != 0)
            ControllerPictures.DrawRounded(dc, info.Image, l, t, tile, Px(dpi, 14));
        else
            Gdi.Text(dc, fonts.Glyph, on ? Gdi.TextColor : Gdi.Dim, on ? GamepadGlyph : NoneGlyph, l, t, l + tile, t + tile, DT_CENTER | oneLine);
    }

    /// <summary>The name under the tile; it may use up to <paramref name="reach"/> pixels on each side.</summary>
    public static void PaintName(nint dc, FontSet fonts, uint dpi, SlotTile info, int l, int t, int reach)
    {
        int tile = Px(dpi, SizeDip), top = t + tile + Px(dpi, NameGapDip);
        Gdi.Text(dc, fonts.Detail, Gdi.TextColor, info.Name, l - reach, top, l + tile + reach, top + Px(dpi, NameHeightDip),
            DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS);
    }

    /// <summary>A small battery badge on the bottom-right corner of a tile: four bars, a plus while charging.</summary>
    public static void PaintBattery(nint dc, uint dpi, SlotTile info, int right, int bottom)
    {
        if (info.Battery < 0) return;
        int w = Px(dpi, 38), h = Px(dpi, 18);
        int l = right - w + Px(dpi, 6), t = bottom - h + Px(dpi, 4);
        Gdi.RoundedFill(dc, Gdi.BackgroundBrush, l, t, l + w, t + h, Px(dpi, 8));

        nint color = info.Battery <= 1 ? RedBrush : info.Battery == 2 ? AmberBrush : Gdi.OnBrush;

        int barW = Px(dpi, 5), gap = Px(dpi, 2), barH = Px(dpi, 10), x = l + Px(dpi, 6), y = t + (h - barH) / 2;
        int filled = Math.Max(info.Battery, info.Battery == 0 ? 1 : 0); // an empty battery still shows one (red) bar
        for (int i = 0; i < 4; i++)
        {
            int bx = x + i * (barW + gap);
            Gdi.Fill(dc, i < filled ? color : Gdi.DimBrush, bx, y, bx + barW, y + barH);
        }
        if (info.Charging)
        {
            int cx = x + 4 * (barW + gap) + Px(dpi, 2), cy = t + h / 2, arm = Px(dpi, 3), thick = Px(dpi, 1);
            Gdi.Fill(dc, Gdi.TextBrush, cx - arm, cy - thick, cx + arm + thick, cy + thick + 1);
            Gdi.Fill(dc, Gdi.TextBrush, cx - thick, cy - arm, cx + thick + 1, cy + arm + thick);
        }
    }

    /// <summary>Four battery bars (a plus after them while charging), centered on <paramref name="cy"/>. Returns the width used.</summary>
    public static int PaintBars(nint dc, uint dpi, SlotTile info, int x, int cy)
    {
        int barW = Px(dpi, 4), gap = Px(dpi, 2), barH = Px(dpi, 10), y = cy - barH / 2;
        int width = 4 * barW + 3 * gap;
        if (info.Battery >= 0)
        {
            nint color = info.Battery <= 1 ? RedBrush : info.Battery == 2 ? AmberBrush : Gdi.OnBrush;
            int filled = Math.Max(info.Battery, info.Battery == 0 ? 1 : 0); // an empty battery still shows one (red) bar
            for (int i = 0; i < 4; i++)
            {
                int bx = x + i * (barW + gap);
                Gdi.Fill(dc, i < filled ? color : Gdi.DimBrush, bx, y, bx + barW, y + barH);
            }
            if (info.Charging)
            {
                int px = x + width + Px(dpi, 6), arm = Px(dpi, 3), thick = Px(dpi, 1);
                Gdi.Fill(dc, Gdi.TextBrush, px - arm, cy - thick, px + arm + thick, cy + thick + 1);
                Gdi.Fill(dc, Gdi.TextBrush, px - thick, cy - arm, px + thick + 1, cy + arm + thick);
            }
        }
        return width + Px(dpi, 6) + Px(dpi, 7); // bars, then room for the charging plus (kept so the bar doesn't jump)
    }
}
