namespace TheCube.Core;

/// <summary>
/// Allows at most a set number of automatic restarts within a sliding window of time, so something that keeps
/// failing isn't restarted over and over. Times come from a clock that only goes forward (e.g. time since boot),
/// so a change of the PC's clock can't stretch or shrink the window. Used on one thread.
/// </summary>
public sealed class RestartBudget
{
    private readonly Queue<TimeSpan> _restarts = new();
    private readonly int _max;
    private readonly TimeSpan _window;

    /// <param name="max">Restarts allowed within <paramref name="window"/>; 0 allows none.</param>
    /// <param name="window">How long a restart counts against the budget.</param>
    public RestartBudget(int max, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _max = max;
        _window = window;
    }

    /// <summary>Note a failure at <paramref name="now"/>; true if it may be restarted (and counts it), false if the budget is used up.</summary>
    public bool TryRestart(TimeSpan now)
    {
        while (_restarts.Count > 0 && now - _restarts.Peek() >= _window)
        {
            _restarts.Dequeue();
        }

        if (_restarts.Count >= _max)
        {
            return false;
        }

        _restarts.Enqueue(now);
        return true;
    }
}
