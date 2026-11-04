using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TheCube.Core;

namespace TheCube.App;

/// <summary>
/// Fallback for hotkeys Windows won't let us register (e.g. Win+C when Windows keeps it for Copilot):
/// a low-level keyboard hook that sees the key first, swallows it and runs the action.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// <summary>An unassigned virtual key, sent so that releasing Win doesn't open the Start menu.</summary>
    private const ushort VK_NONE = 0xE8;

    private readonly Dispatcher _dispatcher;
    private readonly List<(Hotkey Key, Action Action)> _entries = [];
    private readonly HookProc _proc;
    private nint _hook;
    private uint? _heldKey;

    public KeyboardHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = Callback; // keep the delegate alive while the hook is installed
    }

    public int Count => _entries.Count;

    /// <summary>Handle <paramref name="hotkey"/> through the hook. Returns false if the hook can't be installed.</summary>
    public bool Add(Hotkey hotkey, Action action)
    {
        if (_hook == 0)
        {
            using var module = Process.GetCurrentProcess().MainModule;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
            if (_hook == 0)
            {
                return false;
            }
        }

        _entries.Add((hotkey, action));
        return true;
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var message = (int)wParam;
            if ((info.flags & LLKHF_INJECTED) == 0)
            {
                if (message is WM_KEYUP or WM_SYSKEYUP && info.vkCode == _heldKey)
                {
                    _heldKey = null;
                    return 1; // swallow the release of a key whose press we swallowed
                }

                if (message is WM_KEYDOWN or WM_SYSKEYDOWN && Match(info.vkCode) is { } match)
                {
                    if (_heldKey != info.vkCode) // ignore auto-repeat while held
                    {
                        _heldKey = info.vkCode;
                        if (match.Key.Modifiers.HasFlag(HotkeyModifiers.Win))
                        {
                            TapDummyKey();
                        }

                        _dispatcher.BeginInvoke(match.Action);
                    }

                    return 1;
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private (Hotkey Key, Action Action)? Match(uint vk)
    {
        var mods = CurrentModifiers();
        foreach (var entry in _entries)
        {
            if (entry.Key.VirtualKey == vk && entry.Key.Modifiers == mods)
            {
                return entry;
            }
        }

        return null;
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        static bool Down(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var mods = HotkeyModifiers.None;
        if (Down(VK_CONTROL)) mods |= HotkeyModifiers.Ctrl;
        if (Down(VK_MENU)) mods |= HotkeyModifiers.Alt;
        if (Down(VK_SHIFT)) mods |= HotkeyModifiers.Shift;
        if (Down(VK_LWIN) || Down(VK_RWIN)) mods |= HotkeyModifiers.Win;
        return mods;
    }

    private static void TapDummyKey()
    {
        var inputs = new INPUT[]
        {
            new() { type = 1, ki = new KEYBDINPUT { wVk = VK_NONE } },
            new() { type = 1, ki = new KEYBDINPUT { wVk = VK_NONE, dwFlags = 0x2 /* KEYEVENTF_KEYUP */ } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }

        _entries.Clear();
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;

        // The union starts after the 4-byte type, padded to pointer alignment on x64.
        [FieldOffset(8)] public KEYBDINPUT ki;

        // Size the union for its largest member (MOUSEINPUT: 32 bytes on x64).
        [FieldOffset(8)] private readonly MouseInputPadding _padding;
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct MouseInputPadding;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
