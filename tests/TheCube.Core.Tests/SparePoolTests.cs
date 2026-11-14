using TheCube.Core;

namespace TheCube.Core.Tests;

public class SparePoolTests
{
    private sealed record Item(string Monitor);

    private static SparePool<string, Item> Pool() => new(i => i.Monitor);

    private static readonly object SettingsA = new(), SettingsB = new();

    [Fact]
    public void KeepsAndHandsBackPerMonitor()
    {
        var pool = Pool();
        var left = new Item("left");
        Assert.Empty(pool.Add(left, SettingsA, 0, SettingsA, 0));
        Assert.True(pool.TryTake("left", SettingsA, 0, out var taken));
        Assert.Same(left, taken);
        Assert.False(pool.TryTake("left", SettingsA, 0, out _)); // taken once
        Assert.False(pool.TryTake("right", SettingsA, 0, out _));
    }

    [Fact]
    public void StaleItemsAreHandedBackToDispose()
    {
        var pool = Pool();
        var item = new Item("left");
        Assert.Equal([item], pool.Add(item, SettingsA, 0, SettingsB, 0)); // settings changed while it was open
        Assert.Equal([item], pool.Add(item, SettingsA, 1, SettingsA, 2)); // screens changed while it was open
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void ASecondItemForAMonitorReplacesTheFirst()
    {
        var pool = Pool();
        var first = new Item("left");
        var second = new Item("left");
        pool.Add(first, SettingsA, 0, SettingsA, 0);
        Assert.Equal([first], pool.Add(second, SettingsA, 0, SettingsA, 0));
        Assert.True(pool.TryTake("left", SettingsA, 0, out var taken));
        Assert.Same(second, taken);
    }

    [Fact]
    public void NewSettingsDisposeEverythingKeptForTheOldOnes()
    {
        var pool = Pool();
        var left = new Item("left");
        var right = new Item("right");
        pool.Add(left, SettingsA, 0, SettingsA, 0);
        pool.Add(right, SettingsA, 0, SettingsA, 0);
        var fresh = new Item("left");
        var dispose = pool.Add(fresh, SettingsB, 0, SettingsB, 0);
        Assert.Equal(2, dispose.Count);
        Assert.Contains(left, dispose);
        Assert.Contains(right, dispose);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void NothingIsTakenForOtherSettingsOrLayout()
    {
        var pool = Pool();
        pool.Add(new Item("left"), SettingsA, 0, SettingsA, 0);
        Assert.False(pool.TryTake("left", SettingsB, 0, out _));
        Assert.False(pool.TryTake("left", SettingsA, 1, out _));
        Assert.Single(pool.TakeAll()); // still there to be disposed of
    }

    [Fact]
    public void TakeAllEmptiesThePool()
    {
        var pool = Pool();
        pool.Add(new Item("left"), SettingsA, 0, SettingsA, 0);
        pool.Add(new Item("right"), SettingsA, 0, SettingsA, 0);
        Assert.Equal(2, pool.TakeAll().Count);
        Assert.Equal(0, pool.Count);
        Assert.Empty(pool.Add(new Item("left"), SettingsB, 0, SettingsB, 0)); // any settings may start again
    }

    [Fact]
    public void AfterShutDownNothingIsKept()
    {
        var pool = Pool();
        pool.Add(new Item("left"), SettingsA, 0, SettingsA, 0);
        Assert.Single(pool.ShutDown());
        var late = new Item("right"); // e.g. a warm-up that finished during exit
        Assert.Equal([late], pool.Add(late, SettingsA, 0, SettingsA, 0));
        Assert.Equal(0, pool.Count);
    }
}
