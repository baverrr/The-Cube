using System.ComponentModel;
using System.Windows.Interop;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

/// <summary>System-wide hotkeys via RegisterHotKey on a hidden message-only window.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("TheCubeHotkeys")
        {
            ParentWindow = new nint(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Register <paramref name="text"/> (e.g. "Ctrl+F1"). Returns null on success, or why it failed.
    /// An empty <paramref name="text"/> means "unbound" and succeeds without registering anything.
    /// </summary>
    public string? Register(string text, Action action)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!Hotkey.TryParse(text, out var hotkey))
        {
            return $"'{text}' isn't a valid hotkey";
        }

        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT, hotkey.VirtualKey))
        {
            var error = new Win32Exception().Message;
            return $"{hotkey} is already used by Windows or another app ({error})";
        }

        _actions[id] = action;
        return null;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue((int)wParam, out var action))
        {
            handled = true;
            action();
        }

        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _actions.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
