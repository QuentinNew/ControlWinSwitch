using ControlWin.Config;

namespace ControlWin.App;

internal enum CommandKind
{
    None,
    MouseMode,
    ToggleVirtualPad,
    ToggleHeadset,
    Sleep,
    Restart,
    Shutdown,
    Quit,
    OpenBluetoothSettings,
    OpenSettings,
    AddController,
    CloseMenu,
    Launch,
}

internal readonly record struct MenuCommand(CommandKind Kind, int Arg = 0);

internal sealed class MenuEntry
{
    public required string Label { get; init; }

    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code point.</summary>
    public char Glyph { get; init; }

    /// <summary>Right-aligned status text (evaluated on every paint), or null.</summary>
    public Func<string?>? Detail { get; init; }

    /// <summary>When it returns true, a small green dot is drawn on the node (evaluated on every paint).</summary>
    public Func<bool>? Active { get; init; }

    public MenuCommand Command { get; init; }

    /// <summary>Builds the child page each time the entry is opened, so it can reflect the current config.</summary>
    public Func<MenuPage>? Submenu { get; init; }

    /// <summary>When set, accepting the entry asks this yes/no question before the command runs.</summary>
    public string? ConfirmPrompt { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Radial menu: slot on the ring (0-7 on the root, 0-6 in a folder), -1 when not placed.</summary>
    public int Slot { get; init; } = -1;

    /// <summary>App shortcut entries (<see cref="CommandKind.Launch"/>): what to start.</summary>
    public ShortcutConfig? Launch { get; init; }

    /// <summary>The node's own color (COLORREF) as shown when highlighted; null = theme colors.</summary>
    public uint? Color { get; init; }

    /// <summary>Configured icon (glyph / auto / image); the glyph above is the fallback.</summary>
    public MenuIconConfig? Icon { get; init; }
}

internal sealed class MenuPage(string title, IReadOnlyList<MenuEntry> entries, bool slotted = false)
{
    public string Title { get; } = title;
    public IReadOnlyList<MenuEntry> Entries { get; } = entries;

    /// <summary>The radial menu places entries by <see cref="MenuEntry.Slot"/> instead of spreading them evenly; the list keeps their order.</summary>
    public bool Slotted { get; } = slotted;
}

/// <summary>
/// Menu state machine: a stack of pages with a selection each, plus the yes/no confirmation step.
/// Pure (no Win32), driven by <c>AppController</c> and painted by <c>MenuOverlay</c>.
/// </summary>
internal sealed class MenuModel
{
    private readonly List<(MenuPage Page, int Selected)> _stack = [];

    public bool IsConfirming { get; private set; }

    public int Depth => _stack.Count;

    public MenuPage Page => _stack[^1].Page;

    public int Selected => _stack[^1].Selected;

    public MenuEntry? CurrentEntry =>
        _stack.Count > 0 && Selected >= 0 && Selected < Page.Entries.Count ? Page.Entries[Selected] : null;

    public string? ConfirmPrompt => IsConfirming ? CurrentEntry?.ConfirmPrompt : null;

    public void Reset(MenuPage root)
    {
        _stack.Clear();
        IsConfirming = false;
        _stack.Add((root, FirstEnabled(root)));
    }

    /// <summary>Moves the selection by one entry in the direction of <paramref name="delta"/>, wrapping and skipping disabled entries.</summary>
    public void Move(int delta)
    {
        if (IsConfirming || delta == 0 || _stack.Count == 0) return;

        var entries = Page.Entries;
        int count = entries.Count;
        int step = Math.Sign(delta);
        int index = Selected;
        for (int i = 0; i < count; i++)
        {
            index = ((index + step) % count + count) % count;
            if (entries[index].Enabled)
            {
                _stack[^1] = (Page, index);
                return;
            }
        }
    }

    /// <summary>Selects an entry directly (mouse hover / click). Returns true when the selection changed.</summary>
    public bool Select(int index)
    {
        if (IsConfirming || _stack.Count == 0) return false;
        if (index < 0 || index >= Page.Entries.Count || !Page.Entries[index].Enabled || index == Selected) return false;
        _stack[^1] = (Page, index);
        return true;
    }

    /// <summary>
    /// Handles "A". Opens a submenu or the confirmation step and returns null, or returns the command to run
    /// (right away, or after the confirmation).
    /// </summary>
    public MenuCommand? Accept()
    {
        var entry = CurrentEntry;
        if (entry is null || !entry.Enabled) return null;

        if (IsConfirming)
        {
            IsConfirming = false;
            return entry.Command;
        }

        if (entry.Submenu is { } build)
        {
            var page = build();
            _stack.Add((page, FirstEnabled(page)));
            return null;
        }

        if (entry.ConfirmPrompt is not null)
        {
            IsConfirming = true;
            return null;
        }

        return entry.Command.Kind == CommandKind.None ? null : entry.Command;
    }

    /// <summary>Handles "B": cancels a confirmation or goes up one page. Returns false when the menu should close.</summary>
    public bool Back()
    {
        if (IsConfirming)
        {
            IsConfirming = false;
            return true;
        }
        if (_stack.Count > 1)
        {
            _stack.RemoveAt(_stack.Count - 1);
            return true;
        }
        return false;
    }

    private static int FirstEnabled(MenuPage page)
    {
        for (int i = 0; i < page.Entries.Count; i++)
            if (page.Entries[i].Enabled) return i;
        return 0;
    }
}

/// <summary>Icon code points shared by the menus and the node kinds.</summary>
internal static class MenuPages
{
    // Segoe Fluent Icons code points.
    internal const char Add = '';
    internal const char Folder = '', Close = '';
    internal const char Gear ='', Mouse = '', Play = '', Game = '', Headphone = '',
        Power = '', Sleep = '', Restart = '', Quit = '';
}
