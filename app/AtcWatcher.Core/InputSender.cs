using System.Runtime.InteropServices;
using System.Text;

namespace AtcWatcher.Core;

/// <summary>Sends keystrokes with hardware scan codes (what games listen for) and reads the foreground window.</summary>
public static class InputSender
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type; public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const ushort SC_ALT = 0x38;

    public static IntPtr ForegroundWindowHandle() => GetForegroundWindow();

    private static string TitleOf(IntPtr h)
    {
        var len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>First visible top-level window whose title contains <paramref name="needle"/>.</summary>
    public static IntPtr FindWindowByTitle(string needle)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && TitleOf(h).Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Makes <paramref name="hwnd"/> the active window even though we are a background process:
    /// attach to the current foreground thread's input queue and tap ALT, which Windows treats as
    /// the user's own foreground-change permission.
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var fg = GetForegroundWindow();
        if (fg == hwnd) return true;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        var me = GetCurrentThreadId();
        var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            SendScan(SC_ALT, false);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SendScan(SC_ALT, true);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }

        for (var i = 0; i < 12; i++)
        {
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static void SendScan(ushort sc, bool up)
    {
        var flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
        var input = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = sc, dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static readonly IReadOnlyDictionary<string, ushort> ScanCodes = BuildScanCodes();

    private static Dictionary<string, ushort> BuildScanCodes()
    {
        var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        var digits = "1234567890";
        for (var i = 0; i < digits.Length; i++) d[digits[i].ToString()] = (ushort)(0x02 + i);
        d["num0"] = 0x52; d["num1"] = 0x4F; d["num2"] = 0x50; d["num3"] = 0x51; d["num4"] = 0x4B;
        d["num5"] = 0x4C; d["num6"] = 0x4D; d["num7"] = 0x47; d["num8"] = 0x48; d["num9"] = 0x49;
        for (var i = 1; i <= 10; i++) d[$"f{i}"] = (ushort)(0x3B + i - 1);
        d["f11"] = 0x57; d["f12"] = 0x58;
        var letters = new (char, ushort)[]
        {
            ('a', 0x1E), ('b', 0x30), ('c', 0x2E), ('d', 0x20), ('e', 0x12), ('f', 0x21), ('g', 0x22), ('h', 0x23),
            ('i', 0x17), ('j', 0x24), ('k', 0x25), ('l', 0x26), ('m', 0x32), ('n', 0x31), ('o', 0x18), ('p', 0x19),
            ('q', 0x10), ('r', 0x13), ('s', 0x1F), ('t', 0x14), ('u', 0x16), ('v', 0x2F), ('w', 0x11), ('x', 0x2D),
            ('y', 0x15), ('z', 0x2C),
        };
        foreach (var (c, sc) in letters) d[c.ToString()] = sc;
        return d;
    }

    public static bool IsValidKeyName(string name) => ScanCodes.ContainsKey(name);

    public static void Press(string keyName, int holdMs = 80)
    {
        if (!ScanCodes.TryGetValue(keyName, out var sc))
            throw new ArgumentException($"Unknown key name '{keyName}'.");
        var down = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = sc, dwFlags = KEYEVENTF_SCANCODE } } };
        var up = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = sc, dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP } } };
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(holdMs);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }

    public static string ForegroundWindowTitle() => TitleOf(GetForegroundWindow());
}
