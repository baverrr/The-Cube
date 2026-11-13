using System.Globalization;

namespace TheCube.Core;

/// <summary>
/// Frame timing for one stretch of drawing (The Cube uses it from open to close): how many UI frame ticks ran,
/// how far apart, and how many came late, for the log, so lag can be measured instead of guessed.
/// It sees only the UI thread's ticks: a stall on WPF's render thread or the GPU doesn't show up here.
/// Used on one thread.
/// </summary>
public sealed class FrameStats
{
    /// <summary>Gaps kept for the percentile: about 14 minutes at 60 Hz. Later gaps still count everywhere else.</summary>
    public const int MaxKeptGaps = 50_000;

    private readonly List<double> _gaps = [];
    private double _last;
    private bool _started;
    private double _sum;

    /// <param name="slowGapMs">A gap between ticks longer than this (ms) counts as slow.</param>
    public FrameStats(double slowGapMs = DefaultSlowGapMs)
    {
        if (!(slowGapMs > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(slowGapMs));
        }

        SlowGapMs = slowGapMs;
    }

    /// <summary>The default for <see cref="SlowGapMs"/>: 1.5 frames at 60 Hz, so a single missed 60 Hz frame (a 33 ms gap) counts.</summary>
    public const double DefaultSlowGapMs = 25;

    public double SlowGapMs { get; }

    /// <summary>Ticks recorded.</summary>
    public int Frames => _started ? Gaps + 1 : 0;

    /// <summary>Gaps between recorded ticks.</summary>
    public int Gaps { get; private set; }

    /// <summary>Ticks skipped because their time wasn't a finite number later than the last one (e.g. a repeat).</summary>
    public int Ignored { get; private set; }

    public double Average => Gaps == 0 ? 0 : _sum / Gaps;

    public double Worst { get; private set; }

    public int SlowCount { get; private set; }

    /// <summary>The 95th-percentile gap (nearest rank; with under 20 gaps that is the worst one).</summary>
    public double P95
    {
        get
        {
            if (_gaps.Count == 0)
            {
                return 0;
            }

            var sorted = _gaps.Order().ToArray();
            return sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        }
    }

    /// <summary>Record a tick at <paramref name="nowMs"/>, on a clock that only goes forward (ms).</summary>
    public void RecordFrame(double nowMs)
    {
        if (!double.IsFinite(nowMs) || (_started && nowMs <= _last))
        {
            Ignored++;
            return;
        }

        if (_started)
        {
            var gap = nowMs - _last;
            Gaps++;
            _sum += gap;
            Worst = Math.Max(Worst, gap);
            if (gap > SlowGapMs)
            {
                SlowCount++;
            }

            if (_gaps.Count < MaxKeptGaps)
            {
                _gaps.Add(gap);
            }
        }

        _last = nowMs;
        _started = true;
    }

    /// <summary>Forget everything, for the next stretch.</summary>
    public void Reset()
    {
        _gaps.Clear();
        _started = false;
        _last = _sum = Worst = 0;
        Gaps = SlowCount = Ignored = 0;
    }

    /// <summary>One log line: tick count, average gap (and fps), 95th-percentile and worst gap, and how many gaps were slow.</summary>
    public string Summary()
    {
        var ignored = Ignored > 0 ? $" ignored={Ignored}" : "";
        return Gaps == 0
            ? string.Create(CultureInfo.InvariantCulture, $"frames={Frames}{ignored}")
            : string.Create(CultureInfo.InvariantCulture,
                $"frames={Frames} avg={Average:0.0}ms ({1000 / Average:0} fps) p95={P95:0.0}ms worst={Worst:0.0}ms slow(>{SlowGapMs:0}ms)={SlowCount}{ignored}");
    }
}
