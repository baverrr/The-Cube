using System.Globalization;
using TheCube.Core;

namespace TheCube.Core.Tests;

public class FrameStatsTests
{
    private static FrameStats With(params double[] times)
    {
        var stats = new FrameStats();
        foreach (var t in times)
        {
            stats.RecordFrame(t);
        }

        return stats;
    }

    [Fact]
    public void EmptyAndSingleFrame()
    {
        var stats = new FrameStats();
        Assert.Equal("frames=0", stats.Summary());
        stats.RecordFrame(100);
        Assert.Equal(1, stats.Frames);
        Assert.Equal("frames=1", stats.Summary());
    }

    [Fact]
    public void SteadySixteenMsGaps()
    {
        var stats = new FrameStats();
        for (var i = 0; i <= 60; i++)
        {
            stats.RecordFrame(i * 16.0);
        }

        Assert.Equal("frames=61 avg=16.0ms (63 fps) p95=16.0ms worst=16.0ms slow(>25ms)=0", stats.Summary());
    }

    [Fact]
    public void AverageP95AndWorstDiffer()
    {
        // 20 gaps of 10 ms and one of 100 ms: average 14.3, p95 still 10 (21 gaps, rank 20), worst 100.
        var times = Enumerable.Range(0, 21).Select(i => i * 10.0).Append(300).ToArray();
        var stats = With(times);
        Assert.Equal(21, stats.Gaps);
        Assert.Equal(300.0 / 21, stats.Average, 6);
        Assert.Equal(10, stats.P95);
        Assert.Equal(100, stats.Worst);
        Assert.Equal(1, stats.SlowCount);
    }

    [Theory]
    [InlineData(25.0, 0)] // the limit itself isn't slow
    [InlineData(25.1, 1)]
    [InlineData(33.3, 1)] // one missed 60 Hz frame
    public void SlowBoundary(double gap, int slow) => Assert.Equal(slow, With(0, gap).SlowCount);

    [Fact]
    public void SlowLimitCanBeSet()
    {
        var stats = new FrameStats(slowGapMs: 10);
        stats.RecordFrame(0);
        stats.RecordFrame(12);
        Assert.Equal(1, stats.SlowCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameStats(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameStats(double.NaN));
    }

    [Theory]
    [InlineData(16)] // repeated (e.g. WPF raising Rendering twice for one frame)
    [InlineData(10)] // backwards
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BadTimesAreIgnoredAndCounted(double bad)
    {
        var stats = With(0, 16, bad, 32, 48);
        Assert.Equal(4, stats.Frames);
        Assert.Equal(1, stats.Ignored);
        Assert.Equal(16, stats.Average);
        Assert.Equal(16, stats.Worst);
        Assert.EndsWith("ignored=1", stats.Summary());
    }

    [Fact]
    public void IgnoredShowsEvenWithoutGaps() => Assert.Equal("frames=1 ignored=1", With(5, 5).Summary());

    [Fact]
    public void SummaryIgnoresTheUsersNumberFormat()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Contains("avg=16.0ms", With(0, 16, 32).Summary());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void KeepsCountingPastTheGapCap()
    {
        var stats = new FrameStats();
        for (var i = 0; i <= FrameStats.MaxKeptGaps + 10; i++)
        {
            stats.RecordFrame(i * 16.0);
        }

        stats.RecordFrame((FrameStats.MaxKeptGaps + 10) * 16.0 + 200); // a late frame after the cap
        Assert.Equal(FrameStats.MaxKeptGaps + 11, stats.Gaps);
        Assert.Equal(200, stats.Worst);
        Assert.Equal(1, stats.SlowCount);
    }

    [Fact]
    public void ResetStartsOver()
    {
        var stats = With(0, 500);
        stats.Reset();
        Assert.Equal("frames=0", stats.Summary());
        stats.RecordFrame(1000);
        stats.RecordFrame(1016);
        Assert.Equal(16, stats.Worst);
        Assert.Equal(2, stats.Frames);
        Assert.Equal(0, stats.Ignored);
    }
}
