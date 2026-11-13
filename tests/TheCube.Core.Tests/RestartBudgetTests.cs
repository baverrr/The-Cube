using TheCube.Core;

namespace TheCube.Core.Tests;

public class RestartBudgetTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromHours(5); // time since boot

    [Fact]
    public void AllowsThreeAnHourThenRefuses()
    {
        var budget = new RestartBudget(3, TimeSpan.FromHours(1));
        Assert.True(budget.TryRestart(T0));
        Assert.True(budget.TryRestart(T0 + TimeSpan.FromMinutes(10)));
        Assert.True(budget.TryRestart(T0 + TimeSpan.FromMinutes(20)));
        Assert.False(budget.TryRestart(T0 + TimeSpan.FromMinutes(30)));
        Assert.False(budget.TryRestart(T0 + TimeSpan.FromMinutes(59))); // refusals don't use up the budget, but don't free it either
    }

    [Fact]
    public void OldRestartsDropOut()
    {
        var budget = new RestartBudget(3, TimeSpan.FromHours(1));
        budget.TryRestart(T0);
        budget.TryRestart(T0 + TimeSpan.FromMinutes(10));
        budget.TryRestart(T0 + TimeSpan.FromMinutes(20));
        Assert.True(budget.TryRestart(T0 + TimeSpan.FromHours(1))); // the first one is exactly an hour old: out of the window
        Assert.False(budget.TryRestart(T0 + TimeSpan.FromMinutes(65)));
        Assert.True(budget.TryRestart(T0 + TimeSpan.FromHours(2)));
    }

    [Fact]
    public void ZeroBudgetNeverRestarts() => Assert.False(new RestartBudget(0, TimeSpan.FromHours(1)).TryRestart(T0));

    [Fact]
    public void RejectsBadSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestartBudget(-1, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestartBudget(3, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestartBudget(3, TimeSpan.FromMinutes(-1)));
    }
}
