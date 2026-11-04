namespace TheCube.Core;

/// <summary>Modifier flags with the same values as Win32 RegisterHotKey's MOD_* constants.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global hotkey: modifiers plus a Win32 virtual-key code.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    /// <summary>Parse text like "Win+C", "Ctrl+F1" or "Ctrl+Alt+Left". Returns false for empty or invalid text.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var mods = HotkeyModifiers.None;
        uint? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    mods |= HotkeyModifiers.Ctrl;
                    break;
                case "alt":
                    mods |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    mods |= HotkeyModifiers.Shift;
                    break;
                case "win" or "meta" or "super":
                    mods |= HotkeyModifiers.Win;
                    break;
                default:
                    if (key is not null || !TryKey(part, out var vk))
                    {
                        return false;
                    }

                    key = vk;
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        hotkey = new Hotkey(mods, key.Value);
        return true;
    }

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E,
    };

    private static bool TryKey(string part, out uint vk)
    {
        vk = 0;
        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
        {
            vk = char.ToUpperInvariant(part[0]);
            return true;
        }

        if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part.AsSpan(1), out var n) && n is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + n - 1);
            return true;
        }

        return Named.TryGetValue(part, out vk);
    }

    private static string KeyName(uint vk)
    {
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk is >= '0' and <= '9' or >= 'A' and <= 'Z') return ((char)vk).ToString();
        foreach (var (name, code) in Named)
        {
            if (code == vk) return name;
        }

        return $"0x{vk:X2}";
    }
}
