namespace ControlWinSwitch.Config;

/// <summary>
/// A global keyboard shortcut written as text, for example "Ctrl+Alt+M": modifiers (Ctrl, Alt, Shift, Win) then one key.
/// <see cref="Modifiers"/> uses the MOD_* flags of RegisterHotKey, <see cref="Key"/> is a virtual-key code.
/// </summary>
internal readonly record struct Hotkey(uint Modifiers, uint Key)
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

    private static readonly (string Name, uint Flag)[] ModifierNames =
        [("Ctrl", Ctrl), ("Alt", Alt), ("Shift", Shift), ("Win", Win)];

    private static readonly Dictionary<string, uint> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Control"] = Ctrl, ["Ctl"] = Ctrl, ["Windows"] = Win, ["Meta"] = Win,
    };

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Backspace"] = 0x08,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Pause"] = 0x13, ["ScrollLock"] = 0x91, ["PrintScreen"] = 0x2C,
    };

    private static readonly Dictionary<string, uint> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = 0x0D, ["Escape"] = 0x1B, ["PgUp"] = 0x21, ["PgDn"] = 0x22, ["Ins"] = 0x2D, ["Del"] = 0x2E,
    };

    /// <summary>Parses "Ctrl+Alt+M" (any case, spaces allowed). A key without modifier is only accepted for F1-F24.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0, key = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0) return false;
            if (ModifierFlag(part) is uint flag)
            {
                if (i == parts.Length - 1 || (modifiers & flag) != 0) return false; // the key comes last, once
                modifiers |= flag;
                continue;
            }
            if (i != parts.Length - 1 || KeyCode(part) is not uint code) return false;
            key = code;
        }

        bool functionKey = key is >= 0x70 and <= 0x87;
        if (key == 0 || (modifiers == 0 && !functionKey)) return false;
        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    /// <summary>The canonical text: "Ctrl+Alt+M".</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        foreach (var (name, flag) in ModifierNames)
            if ((Modifiers & flag) != 0) parts.Add(name);
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static uint? ModifierFlag(string name)
    {
        foreach (var (n, flag) in ModifierNames)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return flag;
        return Aliases.TryGetValue(name, out var alias) ? alias : null;
    }

    private static uint? KeyCode(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out int n) && n is >= 1 and <= 24)
            return (uint)(0x70 + n - 1);
        if (NamedKeys.TryGetValue(name, out var code) || KeyAliases.TryGetValue(name, out code)) return code;
        return null;
    }

    private static string KeyName(uint key)
    {
        if (key is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87) return "F" + (key - 0x70 + 1);
        foreach (var (name, code) in NamedKeys)
            if (code == key) return name;
        return "0x" + key.ToString("X2");
    }
}
