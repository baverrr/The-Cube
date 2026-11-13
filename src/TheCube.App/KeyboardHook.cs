using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TheCube.Core;

namespace TheCube.App;

/// <summary>
/// Fallback for hotkeys Windows won't let us register (e.g. Win+C when Windows keeps it for Copilot):
/// a low-level keyboard hook that sees the key before apps and registered hotkeys, hides it (the rules are in
/// <see cref="HotkeyFilter"/>) and runs the action.
/// The hook runs on its own thread with its own message loop: Windows silently removes a low-level hook
/// that answers too slowly, and the UI thread can be busy for a moment while the cube opens. For the same reason
/// nothing on the hook thread writes the log file; it goes through the UI thread.
/// One instance lives as long as the app (hotkey changes go through <see cref="Clear"/> and <see cref="Add"/>), so
/// a hotkey press it hid still gets its release hidden after the hotkeys change.
/// </summary>
/// <remarks>
/// Known limits:
/// <list type="bullet">
/// <item>It isn't called while UAC or the lock screen is up, or (unless The Cube runs as admin) while an admin window
/// has the keyboard, so the hotkey does nothing there.</item>
/// <item>Injected key presses (the on-screen keyboard, key remappers, other apps) never fire a hotkey.</item>
/// <item>If Windows removes the hook for answering too slowly, nothing notices until the hotkeys are set up again
/// (saving settings, recording a shortcut, or restarting The Cube), which reinstalls it through <see cref="Reinstall"/>.
/// Watching for it would mean sending a test key now and then, which every app (games included) would see.</item>
/// </list>
/// </remarks>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    /// <summary>An unassigned virtual key, tapped so that releasing Win or Alt doesn't open Start or a menu bar.</summary>
    private const ushort VK_NONE = 0xE8;

    private const uint WM_QUIT = 0x0012;
    private const uint WM_REINSTALL = 0x8000 + 1; // WM_APP + 1, posted to the hook thread
    private const int ERROR_INVALID_THREAD_ID = 1444;

    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _stoppedUnexpectedly;
    private readonly HookProc _proc;
    private readonly HotkeyFilter _filter = new(); // touched only on the hook thread
    private volatile Entries _entries = Entries.Empty; // replaced whole, read on the hook thread
    private Thread? _thread; // UI thread only
    private uint _threadId; // written on the hook thread before installed.Set(), read after Wait()
    private nint _hook; // touched only on the hook thread
    private bool _disposed; // UI thread only
    private volatile bool _stopping; // Dispose asked the hook thread to stop
    private volatile bool _ended; // the hook thread's message loop has stopped, so the hook is gone
    private int _errors; // callback errors so far, counted on the hook thread
    private volatile int _generation; // bumped by Clear, so actions queued before it don't run after it

    /// <summary>The hooked hotkeys and their actions, at matching positions; never changed once made.</summary>
    private sealed class Entries
    {
        public static readonly Entries Empty = new([], []);

        private readonly Hotkey[] _keys;
        private readonly Action[] _actions;

        public Entries(Hotkey[] keys, Action[] actions)
        {
            if (keys.Length != actions.Length)
            {
                throw new ArgumentException("Every hotkey needs exactly one action.", nameof(actions));
            }

            _keys = keys;
            _actions = actions;
        }

        public IReadOnlyList<Hotkey> Keys => _keys;

        public Action ActionAt(int index) => _actions[index];

        public bool HasKey(uint vk)
        {
            foreach (var key in _keys)
            {
                if (key.VirtualKey == vk)
                {
                    return true;
                }
            }

            return false;
        }

        public Entries With(Hotkey key, Action action) => new([.. _keys, key], [.. _actions, action]);
    }

    /// <param name="dispatcher">The UI thread, where actions and log writes run.</param>
    /// <param name="stoppedUnexpectedly">Called on the UI thread, with the reason, if the hook stops without <see cref="Dispose"/>.</param>
    public KeyboardHook(Dispatcher dispatcher, Action<string> stoppedUnexpectedly)
    {
        _dispatcher = dispatcher;
        _stoppedUnexpectedly = stoppedUnexpectedly;
        _proc = Callback; // keep the delegate alive while the hook is installed
    }

    public int Count => _entries.Keys.Count;

    /// <summary>Why the hook couldn't start or stopped, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether <see cref="Add"/> can still work: the hook hasn't failed, stopped or been disposed.</summary>
    public bool IsUsable => LastError is null && !_ended && !_disposed;

    /// <summary>Handle <paramref name="hotkey"/> through the hook, starting it if needed. Returns false if it can't run.</summary>
    public bool Add(Hotkey hotkey, Action action)
    {
        if (!IsUsable || (_thread is null && !Start()))
        {
            return false;
        }

        _entries = _entries.With(hotkey, action);
        return true;
    }

    /// <summary>Stop handling all hotkeys, but keep the hook so hidden presses still get their releases hidden.</summary>
    public void Clear()
    {
        _entries = Entries.Empty;
        _generation++; // UI thread only
    }

    /// <summary>
    /// Install the hook again on its own thread (keeping what the filter tracks), in case Windows removed it for
    /// answering too slowly. Does nothing if the hook isn't running.
    /// </summary>
    public void Reinstall()
    {
        if (_thread is not null && !_ended && !_disposed && !PostThreadMessage(_threadId, WM_REINSTALL, 0, 0))
        {
            EventLog.Write($"keyboard hook: couldn't ask for a reinstall (error {Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>On the hook thread: remove the hook (noticing if Windows already had) and set it again.</summary>
    private string? ReinstallOnHookThread()
    {
        if (!UnhookWindowsHookEx(_hook))
        {
            Log($"keyboard hook: Windows had removed it (error {Marshal.GetLastWin32Error()}), so the hooked hotkeys weren't working before now");
        }

        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook == 0 ? $"reinstalling it failed, error {Marshal.GetLastWin32Error()}" : null;
    }

    private bool Start()
    {
        using var installed = new ManualResetEventSlim();
        string? error = null;
        _thread = new Thread(() =>
        {
            try
            {
                _threadId = GetCurrentThreadId();

                // A low-level hook needs no DLL of its own: any module handle of this process will do.
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (_hook == 0)
                {
                    error = $"SetWindowsHookEx failed, error {Marshal.GetLastWin32Error()}";
                }
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}"; // anything here just means no fallback
            }
            finally
            {
                installed.Set();
            }

            if (error is not null)
            {
                return;
            }

            // The hook is called through this thread's message loop.
            int result;
            string? reason = null;
            while ((result = GetMessage(out var msg, 0, 0, 0)) > 0)
            {
                if (msg.hwnd == 0 && msg.message == WM_REINSTALL)
                {
                    if ((reason = ReinstallOnHookThread()) is not null)
                    {
                        break;
                    }

                    continue;
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            var loopError = result < 0 ? Marshal.GetLastWin32Error() : 0;
            var unhooked = _hook == 0 || UnhookWindowsHookEx(_hook);
            var unhookError = unhooked ? 0 : Marshal.GetLastWin32Error();
            _hook = 0;
            _ended = true;
            if (!unhooked)
            {
                Log($"keyboard hook: Windows had already removed it (error {unhookError}), so the hooked hotkeys weren't working before this");
            }

            if (!_stopping)
            {
                reason ??= result < 0 ? $"its message loop failed, error {loopError}" : "its thread was told to quit";
                _dispatcher.BeginInvoke(() =>
                {
                    LastError = reason;
                    EventLog.Write($"keyboard hook: stopped unexpectedly: {reason}");
                    if (!_disposed) // already replaced: nothing to report or restart
                    {
                        _stoppedUnexpectedly(reason);
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "The Cube keyboard hook",
        };
        _thread.Start();
        installed.Wait();
        if (error is not null)
        {
            LastError = error;
            EventLog.Write($"keyboard hook: couldn't start: {error}");
            _thread = null;
            return false;
        }

        EventLog.Write("keyboard hook: installed");
        return true;
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var vk = 0u;
            var keyDown = false;
            try
            {
                // KBDLLHOOKSTRUCT: vkCode at 0, flags at 8, time at 12. Read in place, without allocating.
                vk = (uint)Marshal.ReadInt32(lParam, 0);
                var flags = (uint)Marshal.ReadInt32(lParam, 8);
                var time = (uint)Marshal.ReadInt32(lParam, 12);
                var message = (int)wParam;
                keyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                var keyUp = message is WM_KEYUP or WM_SYSKEYUP;

                // Injected events (the on-screen keyboard, remappers, other apps, our own dummy key) aren't the
                // user's physical keys: they go through untouched and never change what the filter tracks.
                if ((flags & LLKHF_INJECTED) == 0 && (keyDown || keyUp))
                {
                    var decision = keyUp ? _filter.OnKeyUp(vk) : OnKeyDown(vk, time);
                    if (decision != HookDecision.Pass)
                    {
                        return 1;
                    }
                }
            }
            catch (Exception ex)
            {
                // An exception escaping a native callback ends the whole app, so let the key through instead, and
                // make the filter agree, or it would hide this press's repeats and release from Windows.
                try
                {
                    if (keyDown)
                    {
                        _filter.Forget(vk);
                    }

                    if (++_errors is 1 or 10 or 100 or 1000)
                    {
                        Log($"keyboard hook: error #{_errors}, key let through: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                catch
                {
                    // Nothing more can be done here without risking the app; the key still goes through.
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private HookDecision OnKeyDown(uint vk, uint time)
    {
        var entries = _entries;
        if (!_filter.IsHeld(vk) && !entries.HasKey(vk))
        {
            return HookDecision.Pass; // not a hotkey's key: answer at once, as for almost every key
        }

        // In a low-level hook, GetAsyncKeyState still shows the state from before this event (the hook runs before
        // Windows updates it), and a press the hook hid never reaches it. So "down" means an earlier press of this
        // key got through, and this is usually its auto-repeat.
        var result = _filter.OnKeyDown(vk, HotkeyFilter.ModifiersFrom(Down), Down(vk), entries.Keys, time);
        if (result.ReleaseWasLost)
        {
            Log($"keyboard hook: the release of hidden key 0x{vk:X2} probably never arrived; judging this as a new press");
        }

        if (result.Decision == HookDecision.Fire)
        {
            // The decision is final once the filter made it: a failure below must not let the press through.
            try
            {
                if ((entries.Keys[result.Index].Modifiers & (HotkeyModifiers.Win | HotkeyModifiers.Alt)) != 0)
                {
                    TapDummyKey();
                }

                var action = entries.ActionAt(result.Index);
                var generation = _generation;
                _dispatcher.BeginInvoke(() =>
                {
                    if (generation == _generation) // not cleared meanwhile (e.g. a shortcut is being recorded)
                    {
                        action();
                    }
                });
            }
            catch (Exception ex)
            {
                try
                {
                    Log($"keyboard hook: couldn't run the hotkey {entries.Keys[result.Index]}: {ex.GetType().Name}: {ex.Message}");
                }
                catch
                {
                    // The log goes through the same dispatcher that just failed; the press stays hidden either way.
                }
            }
        }

        return result.Decision;
    }

    private static bool Down(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    /// <summary>Write to the log from any thread, through the UI thread (best effort: lost if the app is exiting).</summary>
    private void Log(string text) => _dispatcher.BeginInvoke(() => EventLog.Write(text));

    private void TapDummyKey()
    {
        // INPUT below is laid out for 64-bit processes only (The Cube is published for win-x64).
        Debug.Assert(Marshal.SizeOf<INPUT>() == 40, "INPUT layout is wrong for this process");
        var inputs = new INPUT[]
        {
            new() { type = 1, ki = new KEYBDINPUT { wVk = VK_NONE } },
            new() { type = 1, ki = new KEYBDINPUT { wVk = VK_NONE, dwFlags = 0x2 /* KEYEVENTF_KEYUP */ } },
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
        {
            Log($"keyboard hook: couldn't send the dummy key (error {Marshal.GetLastWin32Error()}); Start or a menu bar may open when Win or Alt is released");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entries = Entries.Empty; // stop firing at once, even if the thread is slow to stop
        if (_thread is { } thread)
        {
            _stopping = true;
            if (!_ended && !PostThreadMessage(_threadId, WM_QUIT, 0, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (!(_ended || error == ERROR_INVALID_THREAD_ID)) // the thread ended on its own meanwhile
                {
                    EventLog.Write($"keyboard hook: couldn't ask the hook thread to stop (error {error})");
                }
            }
            else if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                EventLog.Write("keyboard hook: the hook thread didn't stop within 2 s; its hook may still be installed");
            }
            else
            {
                EventLog.Write("keyboard hook: removed"); // written here: at exit the UI thread no longer runs queued work
            }

            _thread = null;
        }
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
