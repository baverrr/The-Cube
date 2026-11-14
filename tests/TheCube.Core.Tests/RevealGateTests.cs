using TheCube.Core;

namespace TheCube.Core.Tests;

public class RevealGateTests
{
    [Fact]
    public void NewWindowRevealsOnceOnContentRendered()
    {
        var gate = new RevealGate();
        gate.Start();
        Assert.True(gate.OnContentRendered());
        Assert.True(gate.Revealed);
        Assert.False(gate.OnFrame(true, 16));
        Assert.False(gate.OnFrame(true, 32));
        Assert.False(gate.OnFallback());
    }

    [Fact]
    public void ContentRenderedBeforeStartOnlyMarksItDrawn()
    {
        // The warm-up draws the window before any open.
        var gate = new RevealGate();
        Assert.False(gate.OnContentRendered());
        Assert.False(gate.Revealed);
        gate.Start();
        Assert.False(gate.OnFrame(true, 16));
        Assert.True(gate.OnFrame(true, 32)); // reused: two distinct frames once visible
    }

    [Fact]
    public void ReusedWindowWaitsForTwoDistinctVisibleFrames()
    {
        var gate = new RevealGate();
        gate.OnContentRendered();
        gate.Start();
        Assert.False(gate.OnFrame(false, 10)); // not shown yet
        Assert.False(gate.OnFrame(false, 20));
        Assert.False(gate.OnFrame(true, 30));
        Assert.False(gate.OnFrame(true, 30)); // the same frame raised twice
        Assert.True(gate.OnFrame(true, 46));
        Assert.False(gate.OnFrame(true, 62));
    }

    [Fact]
    public void FrameCountStartsOverEachOpen()
    {
        var gate = new RevealGate();
        gate.OnContentRendered();
        gate.Start();
        gate.OnFrame(true, 10);
        gate.Stop();
        gate.Start();
        Assert.False(gate.OnFrame(true, 20)); // one frame of this open, not two
        Assert.True(gate.OnFrame(true, 36));
    }

    [Fact]
    public void FallbackRevealsWhenNoFrameCame()
    {
        var gate = new RevealGate();
        gate.Start();
        Assert.False(gate.OnFrame(true, 16)); // never drawn before: frames alone don't count
        Assert.True(gate.OnFallback());
        Assert.False(gate.OnContentRendered());
    }

    [Fact]
    public void AfterAFallbackRevealLaterOpensUseFrames()
    {
        var gate = new RevealGate();
        gate.Start();
        gate.OnFallback();
        gate.Stop();
        gate.Start();
        gate.OnFrame(true, 10);
        Assert.True(gate.OnFrame(true, 26));
    }

    [Fact]
    public void NothingRevealsAfterStop()
    {
        var gate = new RevealGate();
        gate.Start();
        gate.Stop();
        Assert.False(gate.OnContentRendered()); // a queued reveal after the window was kept for later
        Assert.False(gate.OnFallback());
        Assert.False(gate.OnFrame(true, 10));
        Assert.False(gate.OnFrame(true, 26));
        Assert.False(gate.Running);
    }
}
