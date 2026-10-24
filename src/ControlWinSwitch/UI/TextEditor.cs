using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.UI;

/// <summary>
/// The editing behind a one-line text box drawn by hand: caret, selection, mouse placement and drag, double / triple
/// click, word moves, clipboard and undo. The window owns the painting and decides what Enter and Esc do.
/// </summary>
internal sealed unsafe class TextEditor(int maxLength)
{
    private readonly List<(string Text, int Caret)> _undo = [], _redo = [];
    private bool _typing;         // the last change was typed characters: further typing joins its undo step
    private long _clickTick;
    private int _clickX, _clicks;

    public string Text { get; private set; } = "";

    /// <summary>Insertion point in <see cref="Text"/>.</summary>
    public int Caret { get; private set; }

    /// <summary>Other end of the selection; equal to <see cref="Caret"/> when nothing is selected.</summary>
    public int Anchor { get; private set; }

    /// <summary>First character shown: the value scrolls so the caret stays visible.</summary>
    public int ViewStart { get; private set; }

    public int SelStart => Math.Min(Caret, Anchor);

    public int SelEnd => Math.Max(Caret, Anchor);

    public bool HasSelection => Caret != Anchor;

    public string Selected => Text[SelStart..SelEnd];

    /// <summary>Starts over on <paramref name="text"/>, the caret at the end: typing continues the value.</summary>
    public void Begin(string text)
    {
        Text = text.Length > maxLength ? text[..maxLength] : text;
        Caret = Anchor = Text.Length;
        ViewStart = 0;
        _undo.Clear();
        _redo.Clear();
        _typing = false;
        _clicks = 0;
    }

    /// <summary>Selects from <paramref name="anchor"/> to <paramref name="caret"/>; the caret is the end that moves.</summary>
    public void Select(int anchor, int caret)
    {
        Anchor = Math.Clamp(anchor, 0, Text.Length);
        Caret = Math.Clamp(caret, 0, Text.Length);
    }

    /// <summary>Moves the caret; with <paramref name="extend"/> (Shift) the selection grows or shrinks instead of going away.</summary>
    public void MoveCaret(int caret, bool extend)
    {
        Caret = Math.Clamp(caret, 0, Text.Length);
        if (!extend) Anchor = Caret;
        _typing = false;
    }

    /// <summary>Editing characters from WM_CHAR (not Enter or Esc). Returns true when handled.</summary>
    public bool OnChar(char c)
    {
        switch (c)
        {
            case '\b':
                if (!HasSelection) Select(Caret - 1, Caret);
                Replace("");
                return true;
            case (char)0x7F: // Ctrl+Backspace: the word before the caret
                if (!HasSelection) Select(WordLeft(Caret), Caret);
                Replace("");
                return true;
            case (char)0x01: // Ctrl+A
                Select(0, Text.Length);
                return true;
            case (char)0x03: // Ctrl+C
                if (HasSelection) SetClipboardText(Selected);
                return true;
            case (char)0x18: // Ctrl+X
                if (HasSelection) { SetClipboardText(Selected); Replace(""); }
                return true;
            case (char)0x16: // Ctrl+V
                Paste();
                return true;
            case (char)0x1A: // Ctrl+Z
                Undo(_undo, _redo);
                return true;
            case (char)0x19: // Ctrl+Y
                Undo(_redo, _undo);
                return true;
            default:
                if (c < ' ') return false;
                Replace(c.ToString(), typing: true);
                return true;
        }
    }

    public bool OnKey(int key) => OnKey(key, GetKeyState(VK_CONTROL) < 0, GetKeyState(VK_SHIFT) < 0);

    /// <summary>Arrow keys, Home, End, Delete and Insert from WM_KEYDOWN. Returns true when handled.</summary>
    public bool OnKey(int key, bool ctrl, bool shift)
    {
        switch (key)
        {
            case 0x25: // Left: a selection collapses to its start unless Shift is held
                MoveCaret(ctrl ? WordLeft(Caret) : HasSelection && !shift ? SelStart : Caret - 1, shift);
                return true;
            case 0x27: // Right
                MoveCaret(ctrl ? WordRight(Caret) : HasSelection && !shift ? SelEnd : Caret + 1, shift);
                return true;
            case 0x24: MoveCaret(0, shift); return true;           // Home
            case 0x23: MoveCaret(Text.Length, shift); return true; // End
            case 0x2E:                                             // Delete (Shift+Delete cuts)
                if (shift && HasSelection) SetClipboardText(Selected);
                if (!HasSelection) Select(Caret, ctrl ? WordRight(Caret) : Caret + 1);
                Replace("");
                return true;
            case 0x2D:                                             // Insert: Ctrl copies, Shift pastes
                if (ctrl && HasSelection) SetClipboardText(Selected);
                else if (shift) Paste();
                else return false;
                return true;
            default:
                return false;
        }
    }

    private void Paste() => Replace(ClipboardText().Replace("\r", "").Replace("\n", "").Replace("\t", " "));

    /// <summary>Replaces the selection (or inserts at the caret) with <paramref name="text"/>, as one undo step.</summary>
    private void Replace(string text, bool typing = false)
    {
        int room = maxLength - (Text.Length - (SelEnd - SelStart));
        if (text.Length > room) text = text[..Math.Max(0, room)];
        if (text.Length == 0 && !HasSelection) return;

        // Characters typed one after the other are undone together, like in any text box.
        if (!(typing && _typing && !HasSelection))
        {
            _undo.Add((Text, Caret));
            if (_undo.Count > 100) _undo.RemoveAt(0);
            _redo.Clear();
        }
        _typing = typing;

        int start = SelStart;
        Text = Text.Remove(start, SelEnd - start).Insert(start, text);
        Caret = Anchor = start + text.Length;
    }

    private void Undo(List<(string Text, int Caret)> from, List<(string Text, int Caret)> to)
    {
        if (from.Count == 0) return;
        to.Add((Text, Caret));
        (Text, Caret) = from[^1];
        from.RemoveAt(from.Count - 1);
        Anchor = Caret;
        _typing = false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Start of the word before <paramref name="i"/> (Ctrl+Left).</summary>
    private int WordLeft(int i)
    {
        while (i > 0 && !IsWordChar(Text[i - 1])) i--;
        while (i > 0 && IsWordChar(Text[i - 1])) i--;
        return i;
    }

    /// <summary>Start of the next word after <paramref name="i"/> (Ctrl+Right).</summary>
    private int WordRight(int i)
    {
        while (i < Text.Length && IsWordChar(Text[i])) i++;
        while (i < Text.Length && !IsWordChar(Text[i])) i++;
        return i;
    }

    // ---- layout and mouse ------------------------------------------------------------------

    /// <summary>Scrolls the value so the caret stays visible, without leaving empty room on the right.</summary>
    public void ScrollToCaret(nint font, int width)
    {
        ViewStart = Math.Clamp(ViewStart, 0, Caret);
        while (ViewStart < Caret && Measure(Text[ViewStart..Caret], font) > width) ViewStart++;
        while (ViewStart > 0 && Measure(Text[(ViewStart - 1)..], font) <= width) ViewStart--;
    }

    /// <summary>The part of the value that is shown, from <see cref="ViewStart"/>.</summary>
    public string Shown => Text[ViewStart..];

    /// <summary>X of the boundary before character <paramref name="i"/>, kept inside the box.</summary>
    public int XAt(int i, int textLeft, int textRight, nint font) =>
        Math.Min(textRight, textLeft + Measure(Text[ViewStart..Math.Max(ViewStart, i)], font));

    /// <summary>Character boundary nearest to <paramref name="x"/>; left of the box it scrolls back by one.</summary>
    public int PositionAt(int x, int textLeft, nint font)
    {
        if (x < textLeft) return Math.Max(0, ViewStart - 1);
        int relative = x - textLeft;
        int best = ViewStart, bestDelta = int.MaxValue;
        for (int i = ViewStart; i <= Text.Length; i++)
        {
            int width = Measure(Text[ViewStart..i], font);
            int delta = Math.Abs(width - relative);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = i;
            }
            if (width > relative) break;
        }
        return best;
    }

    /// <summary>Mouse down in the box: places the caret, Shift extends, double-click picks a word, triple-click all.</summary>
    public void MouseDown(int x, int textLeft, nint font, int slop)
    {
        long now = Environment.TickCount64;
        _clicks = _clicks > 0 && now - _clickTick <= GetDoubleClickTime() && Math.Abs(x - _clickX) <= slop ? _clicks + 1 : 1;
        _clickTick = now;
        _clickX = x;

        int at = PositionAt(x, textLeft, font);
        if (_clicks >= 3) Select(0, Text.Length);
        else if (_clicks == 2)
        {
            int start = at, end = at;
            while (start > 0 && IsWordChar(Text[start - 1])) start--;
            while (end < Text.Length && IsWordChar(Text[end])) end++;
            if (start == end && end < Text.Length) end++; // on punctuation: that one character
            Select(start, end);
        }
        else MoveCaret(at, GetKeyState(VK_SHIFT) < 0);
        _typing = false;
    }

    /// <summary>Dragging with the button down selects from where it went down; past either edge the value scrolls.</summary>
    public void MouseDrag(int x, int textLeft, nint font)
    {
        if (_clicks >= 3) return;
        MoveCaret(PositionAt(x, textLeft, font), extend: true);
    }

    public static int Measure(string text, nint font)
    {
        if (text.Length == 0) return 0;
        var dc = CreateCompatibleDC(0);
        var old = SelectObject(dc, font);
        var size = new SIZE();
        GetTextExtentPoint32W(dc, text, text.Length, &size);
        SelectObject(dc, old);
        DeleteDC(dc);
        return size.cx;
    }

    // ---- clipboard -------------------------------------------------------------------------

    private static void SetClipboardText(string text)
    {
        if (OpenClipboard(0) == 0) return;
        try
        {
            EmptyClipboard();
            var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)((text.Length + 1) * sizeof(char)));
            if (handle == 0) return;
            var ptr = (char*)GlobalLock(handle);
            if (ptr == null) { GlobalFree(handle); return; }
            text.AsSpan().CopyTo(new Span<char>(ptr, text.Length));
            ptr[text.Length] = '\0';
            GlobalUnlock(handle);
            if (SetClipboardData(CF_UNICODETEXT, handle) == 0) GlobalFree(handle); // the clipboard owns it on success
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string ClipboardText()
    {
        string text = "";
        if (OpenClipboard(0) == 0) return text;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle != 0)
            {
                var ptr = (char*)GlobalLock(handle);
                if (ptr != null)
                {
                    text = new string(ptr);
                    GlobalUnlock(handle);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }
        return text;
    }
}
