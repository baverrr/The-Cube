namespace TheCube.Core;

/// <summary>
/// Decides when a cube window may be uncloaked: only while it's open, once per open, and only once a frame of this
/// open's scene has been drawn. A new window says so through WPF's ContentRendered; a reused window never raises
/// that again, so it waits for two distinct frames while visible instead. A fallback timer can force it. Kept apart
/// from the window so the rules can be tested. Used on one thread.
/// </summary>
public sealed class RevealGate
{
    private bool _running;
    private bool _hasRendered; // the window has been drawn before, so frames can be trusted (never resets)
    private int _frames;
    private double _lastFrame = double.NaN;

    /// <summary>The window has been uncloaked in this open.</summary>
    public bool Revealed { get; private set; }

    /// <summary>An open is in progress (between <see cref="Start"/> and <see cref="Stop"/>).</summary>
    public bool Running => _running;

    /// <summary>A new open: not revealed yet, frames counted from zero.</summary>
    public void Start()
    {
        _running = true;
        Revealed = false;
        _frames = 0;
        _lastFrame = double.NaN;
    }

    /// <summary>The open ended (closed or kept for later): nothing may reveal it now.</summary>
    public void Stop() => _running = false;

    /// <summary>WPF drew the window for the first time. True: reveal it now.</summary>
    public bool OnContentRendered()
    {
        _hasRendered = true;
        return TryReveal();
    }

    /// <summary>
    /// A UI frame tick at WPF's <paramref name="renderingTimeMs"/>. Repeats with the same time (WPF can raise a tick
    /// more than once per frame) don't count. True: reveal it now.
    /// </summary>
    public bool OnFrame(bool visible, double renderingTimeMs)
    {
        if (!_running || Revealed || !visible || !_hasRendered || renderingTimeMs == _lastFrame)
        {
            return false;
        }

        _lastFrame = renderingTimeMs;
        return ++_frames >= 2 && TryReveal();
    }

    /// <summary>The fallback timer fired: reveal it anyway if it isn't yet. True: reveal it now.</summary>
    public bool OnFallback() => TryReveal();

    private bool TryReveal()
    {
        if (!_running || Revealed)
        {
            return false;
        }

        Revealed = true;
        _hasRendered = true; // shown on screen once, so later opens can rely on frames even if ContentRendered never came
        return true;
    }
}
