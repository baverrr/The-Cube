using TheCube.Core;

namespace TheCube.Core.Tests;

public class HotkeyFilterTests
{
    private const uint C = 0x43, E = 0x45, V = 0x56, LWin = 0x5B;
    private const HotkeyModifiers Win = HotkeyModifiers.Win, None = HotkeyModifiers.None;
    private static readonly Hotkey[] WinC = [new(Win, C)];

    /// <summary>Feeds key events with a clock that moves 50 ms per key-down unless told otherwise.</summary>
    private sealed class Keys(params Hotkey[] hotkeys)
    {
        public HotkeyFilter Filter { get; } = new();

        public Hotkey[] Hotkeys { get; set; } = hotkeys.Length > 0 ? hotkeys : WinC;

        public uint Time { get; set; } = 1000;

        public HookResult Down(uint vk, HotkeyModifiers mods, bool alreadyDown = false, uint after = 50)
        {
            Time += after;
            return Filter.OnKeyDown(vk, mods, alreadyDown, Hotkeys, Time);
        }

        public HookDecision Up(uint vk) => Filter.OnKeyUp(vk);
    }

    [Fact]
    public void HidesTheHotkeysPressRepeatsAndRelease()
    {
        var k = new Keys();
        Assert.Equal(HookResult.Fired(0), k.Down(C, Win));
        Assert.Equal(HookResult.Swallowed, k.Down(C, Win, after: 500)); // first auto-repeat
        Assert.Equal(HookResult.Swallowed, k.Down(C, Win, after: 33));
        Assert.Equal(HookDecision.Swallow, k.Up(C));
        Assert.Equal(HookDecision.Pass, k.Up(C)); // a second release isn't ours
        Assert.False(k.Filter.IsHeld(C));
    }

    [Fact]
    public void KeepsHidingRepeatsAfterWinIsLetGoFirst()
    {
        // Win+C, then Win comes up while C is still held, and C keeps auto-repeating with no modifiers. Letting
        // those repeats through while hiding the release would leave C stuck down for Windows.
        var k = new Keys();
        Assert.Equal(HookDecision.Fire, k.Down(C, Win).Decision);
        Assert.Equal(HookDecision.Pass, k.Up(LWin)); // Win itself always goes through
        Assert.Equal(HookResult.Swallowed, k.Down(C, None, after: 500));
        Assert.Equal(HookResult.Swallowed, k.Down(C, None, after: 33));
        Assert.Equal(HookDecision.Swallow, k.Up(C));
    }

    [Fact]
    public void KeepsHidingRepeatsWhenAModifierIsAddedMidHold()
    {
        var k = new Keys();
        Assert.Equal(HookDecision.Fire, k.Down(C, Win).Decision);
        Assert.Equal(HookResult.Swallowed, k.Down(C, Win | HotkeyModifiers.Shift, after: 500));
        Assert.Equal(HookDecision.Swallow, k.Up(C));
    }

    [Theory]
    [InlineData(E, HotkeyModifiers.Win)] // Win+E is Windows'
    [InlineData(C, HotkeyModifiers.None)] // plain typing
    [InlineData(C, HotkeyModifiers.Win | HotkeyModifiers.Shift)] // an extra modifier: not the hotkey
    [InlineData(C, HotkeyModifiers.Ctrl)]
    [InlineData(LWin, HotkeyModifiers.Win)] // the Windows key itself
    public void LeavesOtherKeysAndCombosAlone(uint vk, HotkeyModifiers mods)
    {
        var k = new Keys();
        Assert.Equal(HookResult.Passed(), k.Down(vk, mods));
        Assert.Equal(HookResult.Passed(), k.Down(vk, mods, alreadyDown: true, after: 500));
        Assert.Equal(HookDecision.Pass, k.Up(vk));
    }

    [Fact]
    public void KeyWindowsAlreadyCountsAsDownIsNotTakenAsTheHotkey()
    {
        // C went down without Win and Windows saw it; a C key-down now arriving with Win held (sticky or injected
        // modifiers, or a keyboard that keeps repeating C) mustn't be hidden, or C's release would be too.
        var k = new Keys();
        Assert.Equal(HookDecision.Pass, k.Down(C, None).Decision);
        Assert.Equal(HookResult.Passed(), k.Down(C, Win, alreadyDown: true, after: 500));
        Assert.Equal(HookDecision.Pass, k.Up(C));
    }

    [Fact]
    public void ALostReleaseIsNoticedAtTheNextPressAndThatPressStillWorks()
    {
        var k = new Keys();
        Assert.Equal(HookDecision.Fire, k.Down(C, Win).Decision);

        // C's release never reached the hook. Much later, a plain C must go through, and so must its release.
        Assert.Equal(HookResult.Passed(releaseWasLost: true), k.Down(C, None, after: 5000));
        Assert.Equal(HookDecision.Pass, k.Up(C));

        // Same again, but the next press is the hotkey: it fires instead of being taken for a repeat.
        Assert.Equal(HookDecision.Fire, k.Down(C, Win, after: 1000).Decision);
        Assert.Equal(HookResult.Fired(0, releaseWasLost: true), k.Down(C, Win, after: 5000));
        Assert.Equal(HookDecision.Swallow, k.Up(C));
    }

    [Fact]
    public void ALostReleaseThenAQuickPressCostsThatPress()
    {
        // The documented cost: within RepeatGapMs a new press can't be told from a repeat, so it and its release
        // are both hidden. Nothing gets stuck, since Windows sees neither.
        var k = new Keys();
        k.Down(C, Win);
        Assert.Equal(HookResult.Swallowed, k.Down(C, None, after: 800));
        Assert.Equal(HookDecision.Swallow, k.Up(C));
    }

    [Theory]
    [InlineData(1100u, false)] // Windows' "Long" repeat delay (about 1 s) plus jitter: still a repeat
    [InlineData(2100u, false)] // Filter Keys' longest repeat delay (2 s): still a repeat
    [InlineData(2500u, false)] // the limit itself counts as a repeat
    [InlineData(2501u, true)]
    [InlineData(5000u, true)]
    public void RepeatOrNewPressByGap(uint gap, bool newPress)
    {
        var k = new Keys();
        k.Down(C, Win);
        var result = k.Down(C, None, after: gap);
        Assert.Equal(newPress, result.ReleaseWasLost);
        Assert.Equal(newPress ? HookDecision.Pass : HookDecision.Swallow, result.Decision);
    }

    [Fact]
    public void ClockWrapAroundStillCountsAsARepeat()
    {
        var k = new Keys { Time = uint.MaxValue - 100 };
        Assert.Equal(HookDecision.Fire, k.Down(C, Win, after: 0).Decision);
        Assert.Equal(HookResult.Swallowed, k.Down(C, Win, after: 500)); // time wrapped past 0
    }

    [Fact]
    public void TwoHotkeysHeldAtOnceEachHideTheirOwnRelease()
    {
        var k = new Keys(new(Win, C), new(Win, V));
        Assert.Equal(HookResult.Fired(0), k.Down(C, Win));
        Assert.Equal(HookResult.Fired(1), k.Down(V, Win));
        Assert.Equal(HookDecision.Swallow, k.Up(C)); // released in the other order
        Assert.Equal(HookDecision.Swallow, k.Up(V));
    }

    [Fact]
    public void PicksTheHotkeyWhoseModifiersMatch()
    {
        var k = new Keys(new(Win, C), new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, C));
        Assert.Equal(HookResult.Fired(1), k.Down(C, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt));
        Assert.Equal(HookDecision.Swallow, k.Up(C));
        Assert.Equal(HookResult.Fired(0), k.Down(C, Win));
    }

    [Fact]
    public void HotkeysRemovedWhileHeldStillEndTheHiddenPress()
    {
        var k = new Keys();
        Assert.Equal(HookDecision.Fire, k.Down(C, Win).Decision);
        k.Hotkeys = []; // settings changed while C is held (the app keeps one filter across changes)
        Assert.Equal(HookResult.Swallowed, k.Down(C, Win, after: 500));
        Assert.Equal(HookDecision.Swallow, k.Up(C));
        Assert.Equal(HookResult.Passed(), k.Down(C, Win));
    }

    [Fact]
    public void ForgetLetsTheRestOfAPressThrough()
    {
        // The hook failed after the filter chose Fire, so the press went to Windows after all.
        var k = new Keys();
        Assert.Equal(HookDecision.Fire, k.Down(C, Win).Decision);
        k.Filter.Forget(C);
        Assert.Equal(HookResult.Passed(), k.Down(C, Win, alreadyDown: true, after: 500));
        Assert.Equal(HookDecision.Pass, k.Up(C));
    }

    [Fact]
    public void AReleaseWithNoPressSeenPasses()
    {
        var k = new Keys();
        Assert.Equal(HookDecision.Pass, k.Up(C));
        Assert.Equal(HookDecision.Fire, k.Down(C, Win).Decision);
    }

    [Fact]
    public void ResultsOnlyComeInValidShapes()
    {
        Assert.Equal(-1, default(HookResult).Index);
        Assert.Equal(-1, HookResult.Passed().Index);
        Assert.Equal(-1, HookResult.Swallowed.Index);
        Assert.Equal(2, HookResult.Fired(2).Index);
        Assert.Throws<ArgumentOutOfRangeException>(() => HookResult.Fired(-1));
    }

    [Theory]
    [InlineData(new uint[0], HotkeyModifiers.None)]
    [InlineData(new[] { 0x11u }, HotkeyModifiers.Ctrl)]
    [InlineData(new[] { 0x12u }, HotkeyModifiers.Alt)]
    [InlineData(new[] { 0x10u }, HotkeyModifiers.Shift)]
    [InlineData(new[] { 0x5Bu }, HotkeyModifiers.Win)]
    [InlineData(new[] { 0x5Cu }, HotkeyModifiers.Win)] // right Windows key
    [InlineData(new[] { 0x5Bu, 0x5Cu }, HotkeyModifiers.Win)]
    [InlineData(new[] { 0x11u, 0x12u }, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)] // also what AltGr reports
    [InlineData(new[] { 0x10u, 0x5Bu }, HotkeyModifiers.Shift | HotkeyModifiers.Win)]
    [InlineData(new[] { 0x43u }, HotkeyModifiers.None)] // a letter isn't a modifier
    public void ModifiersFromKeyState(uint[] down, HotkeyModifiers expected) =>
        Assert.Equal(expected, HotkeyFilter.ModifiersFrom(down.Contains));

    /// <summary>
    /// Random physical typing: presses, auto-repeats (which, as on a real keyboard, belong to the last key pressed
    /// and go on when a modifier is let go), releases, and modifiers going up and down, with Windows modelled as
    /// the set of keys whose last event it was shown was a press. With <paramref name="loseReleases"/> some releases
    /// never reach the hook (or Windows), as when the lock screen or an admin window has the keyboard.
    /// Checks that a release is only hidden if Windows never saw that press, that a key Windows saw go down comes
    /// back up with the next release the hook sees, and that each event gets the expected decision.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void WindowsNeverSeesAKeyStuckDown(int seed, bool loseReleases)
    {
        var random = new Random(seed);
        Hotkey[] hotkeys = [new(Win, C), new(Win, V), new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, C)];
        uint[] letters = [C, V, E];
        HotkeyModifiers[] modifiers = [Win, HotkeyModifiers.Shift, HotkeyModifiers.Ctrl, HotkeyModifiers.Alt];
        var filter = new HotkeyFilter();
        var physical = new HashSet<uint>();
        var windows = new HashSet<uint>();
        var hidden = new Dictionary<uint, uint>(); // the model's own copy of what the filter should be hiding
        var lastEvent = new Dictionary<uint, uint>();
        var trace = new Queue<string>();
        var held = None;
        var time = uint.MaxValue - 1_000_000; // so the clock wraps during the run
        uint? lastPressed = null;
        int fires = 0, swallowedRepeats = 0, lostReleases = 0;

        for (var step = 0; step < 20000; step++)
        {
            var roll = random.Next(20);
            if (roll < 3)
            {
                var modifier = modifiers[random.Next(modifiers.Length)];
                held ^= modifier;
                time += (uint)random.Next(20, 1000);
                if ((held & modifier) != 0)
                {
                    lastPressed = null; // pressing a modifier moves the auto-repeat to it; letting go doesn't
                }

                Note($"{((held & modifier) != 0 ? "down" : "up")} {modifier}");
                continue;
            }

            var vk = letters[random.Next(letters.Length)];
            if (lastPressed is { } repeating && roll < 12)
            {
                vk = repeating; // weight auto-repeats, so hidden presses see plenty of them
            }

            if (!physical.Contains(vk))
            {
                time += (uint)random.Next(20, 4000);
                physical.Add(vk);
                lastPressed = vk;
                Feed(vk, isRepeat: false);
            }
            else if (roll < 12 && lastPressed == vk)
            {
                var gap = (uint)random.Next(30, 1001); // within Windows' "Long" repeat delay
                if (unchecked(time + gap - lastEvent[vk]) > 1000)
                {
                    continue; // the stream would have had repeats in between; skip rather than fake a pause
                }

                time += gap;
                Feed(vk, isRepeat: true);
            }
            else
            {
                time += (uint)random.Next(20, 4000);
                physical.Remove(vk);
                if (lastPressed == vk) lastPressed = null;
                if (loseReleases && random.Next(50) == 0)
                {
                    lostReleases++;
                    Note($"release of {vk:X2} lost");
                    continue; // neither the hook nor Windows saw it
                }

                var decision = filter.OnKeyUp(vk);
                Note($"up {vk:X2} -> {decision}");
                Assert.True(hidden.Remove(vk) == (decision == HookDecision.Swallow), Trace("release decision"));
                if (decision == HookDecision.Swallow)
                {
                    Assert.False(windows.Contains(vk), Trace("hid a release Windows was waiting for"));
                }

                windows.Remove(vk);
            }
        }

        Assert.True(fires > 100, $"only {fires} fires");
        Assert.True(swallowedRepeats > 100, $"only {swallowedRepeats} hidden repeats");
        Assert.Equal(loseReleases, lostReleases > 0);

        void Feed(uint vk, bool isRepeat)
        {
            var alreadyDown = windows.Contains(vk);
            var wasHidden = hidden.TryGetValue(vk, out var last);
            var stillRepeat = wasHidden && unchecked(time - last) <= HotkeyFilter.RepeatGapMs;
            var match = Array.FindIndex(hotkeys, h => h.VirtualKey == vk && h.Modifiers == held);
            lastEvent[vk] = time;

            var result = filter.OnKeyDown(vk, held, alreadyDown, hotkeys, time);
            Note($"{(isRepeat ? "repeat" : "down")} {vk:X2} with {held}{(alreadyDown ? " (Windows: down)" : "")} -> {result.Decision}");

            HookResult expected;
            if (stillRepeat)
            {
                expected = HookResult.Swallowed;
            }
            else if (!alreadyDown && match >= 0)
            {
                expected = HookResult.Fired(match, wasHidden);
            }
            else
            {
                expected = HookResult.Passed(wasHidden);
            }

            Assert.True(expected == result, Trace($"expected {expected}, got {result}"));
            if (!loseReleases)
            {
                Assert.False(result.ReleaseWasLost, Trace("no release is ever lost here"));
            }

            switch (result.Decision)
            {
                case HookDecision.Pass:
                    hidden.Remove(vk);
                    windows.Add(vk);
                    break;
                case HookDecision.Swallow:
                    hidden[vk] = time;
                    swallowedRepeats++;
                    break;
                case HookDecision.Fire:
                    hidden[vk] = time;
                    fires++;
                    break;
            }
        }

        void Note(string text)
        {
            trace.Enqueue(text);
            if (trace.Count > 20)
            {
                trace.Dequeue();
            }
        }

        string Trace(string what) => $"seed {seed}: {what}. Last events:\n  {string.Join("\n  ", trace)}";
    }
}
