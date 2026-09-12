using System.Runtime.InteropServices;

namespace AtcWatcher.Core;

public static class Hotkeys
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Parses "ctrl+alt+a", "ctrl+shift+f9", etc. into RegisterHotKey arguments.</summary>
    public static (uint Modifiers, uint VirtualKey) Parse(string spec)
    {
        uint mods = 0;
        uint? vk = null;
        foreach (var raw in spec.ToLowerInvariant().Split('+'))
        {
            var part = raw.Trim();
            switch (part)
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                default:
                    if (part.Length >= 2 && part[0] == 'f' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 12)
                        vk = (uint)(0x70 + f - 1);
                    else if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                        vk = char.ToUpperInvariant(part[0]);
                    else
                        throw new FormatException($"Unrecognised hotkey part '{part}' in '{spec}'.");
                    break;
            }
        }
        if (vk is null) throw new FormatException($"Hotkey '{spec}' has no main key.");
        return (mods | MOD_NOREPEAT, vk.Value);
    }
}
