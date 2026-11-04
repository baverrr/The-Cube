namespace TheCube.Core;

/// <summary>Easing curves.</summary>
public static class Easing
{
    public static double OutCubic(double t)
    {
        var u = 1 - Math.Clamp(t, 0, 1);
        return 1 - u * u * u;
    }
}

/// <summary>
/// A value that animates towards a target with OutCubic easing. Setting a new target while running
/// restarts from the current (in-between) value, like a QML Behavior.
/// </summary>
public sealed class Animation<T>(T initial, double durationMs, Func<T, T, double, T> lerp)
{
    private T _from = initial;
    private T _to = initial;
    private double _start;

    public T Value { get; private set; } = initial;
    public T Target => _to;
    public bool Running { get; private set; }
    public double DurationMs { get; } = durationMs;

    /// <summary>Jump to <paramref name="value"/> and stop any running animation.</summary>
    public void Set(T value)
    {
        Value = _from = _to = value;
        Running = false;
    }

    /// <summary>Stop where it is.</summary>
    public void Stop()
    {
        _from = _to = Value;
        Running = false;
    }

    public void AnimateTo(T target, double now)
    {
        if (DurationMs <= 0)
        {
            Set(target);
            return;
        }

        _from = Value;
        _to = target;
        _start = now;
        Running = true;
    }

    public void Update(double now)
    {
        if (!Running)
        {
            return;
        }

        var t = (now - _start) / DurationMs;
        if (t >= 1)
        {
            Value = _to;
            Running = false;
            return;
        }

        Value = lerp(_from, _to, Easing.OutCubic(t));
    }
}
