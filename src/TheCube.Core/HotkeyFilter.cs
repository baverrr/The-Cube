using System.Diagnostics;

namespace TheCube.Core;

/// <summary>What the keyboard hook does with one key event.</summary>
public enum HookDecision
{
    /// <summary>Let it through to the next hook, Windows and apps.</summary>
    Pass,

    /// <summary>Hide it: a repeat or the release of a hotkey press that was hidden.</summary>
    Swallow,

    /// <summary>Hide it and run the hotkey's action: a fresh press of a hotkey.</summary>
    Fire,
}

/// <summary>
/// The filter's answer for one key-down event. Made only through its factories; <c>default</c> reads as a plain
/// <see cref="HookDecision.Pass"/>.
/// </summary>
public readonly record struct HookResult
{
    private readonly int _indexPlusOne; // so that default(HookResult) has Index -1

    private HookResult(HookDecision decision, int index, bool releaseWasLost)
    {
        Decision = decision;
        _indexPlusOne = index + 1;
        ReleaseWasLost = releaseWasLost;
    }

    /// <summary>What to do with the event.</summary>
    public HookDecision Decision { get; }

    /// <summary>For <see cref="HookDecision.Fire"/>, which hotkey in the list passed to that call; otherwise -1.</summary>
    public int Index => _indexPlusOne - 1;

    /// <summary>
    /// The key was still marked as held from an earlier hidden press, but this press came too late to be its
    /// auto-repeat, so that press's release probably never reached the hook. Worth logging.
    /// </summary>
    public bool ReleaseWasLost { get; }

    public static HookResult Passed(bool releaseWasLost = false) => new(HookDecision.Pass, -1, releaseWasLost);

    public static HookResult Swallowed { get; } = new(HookDecision.Swallow, -1, false);

    public static HookResult Fired(int index, bool releaseWasLost = false) =>
        index >= 0 ? new(HookDecision.Fire, index, releaseWasLost) : throw new ArgumentOutOfRangeException(nameof(index));
}

/// <summary>
/// The keyboard-hook fallback's decisions, kept apart from the Win32 hook so they can be tested.
/// Feed it the user's physical key events (not injected ones), in order, on one thread.
/// Once it hides a hotkey's press it also hides that key's auto-repeats (even after the modifiers are let go) and
/// its release, and it never hides a key event Windows needs to see: Windows must never see a key go down without
/// seeing it come back up, or the key stays pressed for every app. One cost: if a hidden press's release is lost
/// and the key is pressed again within <see cref="RepeatGapMs"/>, that press and its release are hidden too.
/// </summary>
public sealed class HotkeyFilter
{
    /// <summary>
    /// Gap in ms after which a key-down counts as a new press, not an auto-repeat. Windows' longest repeat delay is
    /// about 1 s (Control Panel, Keyboard, Repeat delay "Long"), and 2 s with Filter Keys (Accessibility).
    /// </summary>
    public const uint RepeatGapMs = 2500;

    private const uint VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly Dictionary<uint, uint> _held = []; // key whose hotkey press was hidden -> time of its last event
    private int _thread; // the one thread that may call this, set on first use (checked in Debug builds)

    /// <summary>Whether a hidden hotkey press of this key hasn't been released yet.</summary>
    public bool IsHeld(uint vk)
    {
        CheckThread();
        return _held.ContainsKey(vk);
    }

    /// <summary>Decide a key-down event (a press or an auto-repeat).</summary>
    /// <param name="vk">The key's virtual-key code.</param>
    /// <param name="modifiers">The modifier keys held down.</param>
    /// <param name="alreadyDown">
    /// Windows already counts this key as down: an earlier press of it got through (usually before the modifiers
    /// were held), so this isn't a hotkey press, and hiding its repeats and release would leave it stuck down.
    /// </param>
    /// <param name="hotkeys">The hotkeys handled by the hook.</param>
    /// <param name="time">The event's timestamp in ms (KBDLLHOOKSTRUCT.time, which wraps around).</param>
    public HookResult OnKeyDown(uint vk, HotkeyModifiers modifiers, bool alreadyDown, IReadOnlyList<Hotkey> hotkeys, uint time)
    {
        CheckThread();
        var lost = false;
        if (_held.TryGetValue(vk, out var last))
        {
            if (unchecked(time - last) <= RepeatGapMs)
            {
                _held[vk] = time;
                return HookResult.Swallowed; // auto-repeat, whether or not the modifiers are still held
            }

            // Too late for a repeat: the hidden press's release probably never reached the hook (it can miss
            // events, e.g. while the lock screen, UAC or an admin window has the keyboard). Judge this as a new press.
            _held.Remove(vk);
            lost = true;
        }

        if (!alreadyDown)
        {
            for (var i = 0; i < hotkeys.Count; i++)
            {
                if (hotkeys[i].VirtualKey == vk && hotkeys[i].Modifiers == modifiers)
                {
                    _held[vk] = time;
                    return HookResult.Fired(i, lost);
                }
            }
        }

        return HookResult.Passed(lost);
    }

    /// <summary>Decide a key-up event: hidden only if it ends a hidden hotkey press. Never <see cref="HookDecision.Fire"/>.</summary>
    /// <param name="vk">The key's virtual-key code.</param>
    public HookDecision OnKeyUp(uint vk)
    {
        CheckThread();
        return _held.Remove(vk) ? HookDecision.Swallow : HookDecision.Pass;
    }

    /// <summary>
    /// Stop hiding <paramref name="vk"/>: for when its key-down was let through after all (the hook failed while
    /// handling it), so that its repeats and release reach Windows too.
    /// </summary>
    public void Forget(uint vk)
    {
        CheckThread();
        _held.Remove(vk);
    }

    /// <summary>The modifiers held, given whether each Win32 virtual key is down.</summary>
    /// <remarks>On layouts with AltGr it reports Ctrl and Alt both down, so a Ctrl+Alt hotkey also fires on AltGr with that key.</remarks>
    public static HotkeyModifiers ModifiersFrom(Func<uint, bool> isDown)
    {
        var mods = HotkeyModifiers.None;
        if (isDown(VK_CONTROL)) mods |= HotkeyModifiers.Ctrl;
        if (isDown(VK_MENU)) mods |= HotkeyModifiers.Alt;
        if (isDown(VK_SHIFT)) mods |= HotkeyModifiers.Shift;
        if (isDown(VK_LWIN) || isDown(VK_RWIN)) mods |= HotkeyModifiers.Win;
        return mods;
    }

    [Conditional("DEBUG")]
    private void CheckThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_thread == 0)
        {
            _thread = current;
        }

        Debug.Assert(_thread == current, "HotkeyFilter is used from more than one thread");
    }
}
