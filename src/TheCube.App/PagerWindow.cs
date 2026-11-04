using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

/// <summary>What one pager shows: the desktops, which is current, and each desktop's windows on this monitor.</summary>
internal sealed record PagerState(int Current, IReadOnlyList<IReadOnlyList<PixelRect>> WindowsPerDesktop)
{
    public int Count => WindowsPerDesktop.Count;
}

/// <summary>
/// The pager on one taskbar, like Plasma's Pager widget: a box per desktop with the windows drawn as
/// outlines, the current desktop highlighted. Click a box to switch to it; scroll to step through them.
/// It's a tool window (so Windows shows it on every virtual desktop) that never activates (so clicking it doesn't
/// pull focus from the app you're in.
/// </summary>
internal sealed class PagerWindow : Window
{
    private readonly PagerView _view;

    public PagerWindow(MonitorInfo monitor, Action<int> switchTo)
    {
        Monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "The Cube pager";
        Left = -32000; // off screen until placed in physical pixels
        Top = -32000;
        Width = 1;
        Height = 1;
        _view = new PagerView(switchTo);
        Content = _view;
        SourceInitialized += (_, _) =>
        {
            var hwnd = Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)(ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
            var off = 1;
            DwmSetWindowAttribute(hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref off, sizeof(int));
        };
    }

    public MonitorInfo Monitor { get; set; }

    private nint Handle => new WindowInteropHelper(this).Handle;

    public void Update(PagerState state) => _view.Update(state, Monitor.Bounds);

    /// <summary>Put the pager at <paramref name="rect"/> (physical pixels) and above the taskbar.</summary>
    public void Place(PixelRect rect)
    {
        var hwnd = Handle;
        if (hwnd == 0)
        {
            return;
        }

        const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
        var flags = SWP_NOACTIVATE | SWP_SHOWWINDOW;
        if (Capture.GetWindowRect(hwnd, out var current) && current.ToPixelRect() == rect)
        {
            flags |= SWP_NOMOVE | SWP_NOSIZE; // only re-assert "on top of the taskbar"
        }

        SetWindowPos(hwnd, -1 /* HWND_TOPMOST */, rect.X, rect.Y, rect.Width, rect.Height, flags);
    }

    public void HideNow()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>Draws the boxes and handles the mouse.</summary>
internal sealed class PagerView(Action<int> switchTo) : FrameworkElement
{
    private PagerState _state = new(0, []);
    private PixelRect _monitor;
    private int? _hover;
    private int _wheelRemainder;

    public void Update(PagerState state, PixelRect monitor)
    {
        if (Same(state, _state) && monitor == _monitor)
        {
            return;
        }

        _state = state;
        _monitor = monitor;
        InvalidateVisual();
    }

    private IReadOnlyList<BoxRect> Boxes() => Pager.Boxes(_state.Count, ActualWidth, ActualHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var light = UsesLightTaskbar();
        var ink = light ? Colors.Black : Colors.White;
        var accent = SystemParameters.WindowGlassColor is { A: > 0 } glass ? glass : Color.FromRgb(0x3d, 0xae, 0xe9);
        var boxes = Boxes();
        var px = 1.0 / (PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1); // one physical pixel

        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var rect = new Rect(box.X, box.Y, box.Width, box.Height);
            var current = i == _state.Current;
            var hover = i == _hover;

            // A near-invisible fill still catches clicks (fully transparent pixels click through).
            var fill = current ? WithAlpha(accent, 0x70) : WithAlpha(ink, (byte)(hover ? 0x30 : 0x12));
            var border = current ? WithAlpha(accent, 0xF0) : WithAlpha(ink, (byte)(hover ? 0xA0 : 0x60));
            var inset = new Rect(rect.X + px / 2, rect.Y + px / 2, Math.Max(0, rect.Width - px), Math.Max(0, rect.Height - px));
            dc.DrawRectangle(Frozen(fill), Pen(border, px), inset);

            dc.PushClip(new RectangleGeometry(rect));
            foreach (var window in _state.WindowsPerDesktop[i])
            {
                if (Pager.MiniWindow(window, _monitor, box) is { } mini)
                {
                    var r = new Rect(mini.X + px / 2, mini.Y + px / 2, Math.Max(0, mini.Width - px), Math.Max(0, mini.Height - px));
                    dc.DrawRectangle(Frozen(WithAlpha(ink, 0x30)), Pen(WithAlpha(ink, 0x90), px), r);
                }
            }

            dc.Pop();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var hover = Pager.BoxAt(Boxes(), p.X, p.Y);
        if (hover != _hover)
        {
            _hover = hover;
            ToolTip = hover is int i ? $"Desktop {i + 1}" : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Pager.BoxAt(Boxes(), p.X, p.Y) is int i && i != _state.Current)
        {
            switchTo(i);
        }

        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // At most one desktop per wheel event; smooth-scrolling touchpads send smaller steps, so add them up to a notch.
        _wheelRemainder += e.Delta;
        if (Math.Abs(_wheelRemainder) >= Mouse.MouseWheelDeltaForOneLine)
        {
            var target = Pager.Wheel(_state.Current, _state.Count, _wheelRemainder);
            _wheelRemainder = 0;
            if (target != _state.Current)
            {
                switchTo(target);
            }
        }

        e.Handled = true;
    }

    private static bool Same(PagerState a, PagerState b) =>
        a.Current == b.Current && a.Count == b.Count
        && a.WindowsPerDesktop.Zip(b.WindowsPerDesktop).All(p => p.First.SequenceEqual(p.Second));

    private static bool UsesLightTaskbar()
    {
        // "Choose your default Windows mode": 1 = light taskbar.
        return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen Pen(Color c, double width)
    {
        var p = new Pen(Frozen(c), width);
        p.Freeze();
        return p;
    }
}
