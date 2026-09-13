namespace DaBtDynamicLock.Core;

/// <summary>One reading kept for the chart: when it arrived and how strong it was.</summary>
public readonly record struct Sample(double At, int Rssi);

/// <summary>A stretch of time the app was not running at all.</summary>
public readonly record struct Downtime(double From, double To);

/// <summary>
/// What the chart draws: the readings, the locks, and the gaps when the app was
/// not running.
///
/// Kept apart from <see cref="PhoneWatch"/> on purpose. PhoneWatch answers "is
/// the phone here right now" and has to stay small and quick, because the loop
/// asks it twice a second; this is a record for looking at afterwards, and it
/// is the part that grows to megabytes if nobody thins it.
/// </summary>
public sealed class SignalHistory
{
    /// <summary>
    /// How far back the chart remembers. 26 hours rather than 24, so the "one
    /// day" range is always full rather than running out at its left edge.
    /// </summary>
    public const double LengthSeconds = 26 * 3600;

    /// <summary>The last hour is kept sample for sample.</summary>
    public const double DetailedSeconds = 3600;

    /// <summary>
    /// Older than that, one sample per ten seconds is enough.
    ///
    /// A day at full detail is about 150 000 samples - several megabytes in the
    /// file, and pointless to draw: over an 8 hour range several of them land on
    /// the same pixel anyway.
    /// </summary>
    public const double ThinnedStepSeconds = 10;

    private readonly object _gate = new();
    private readonly List<Sample> _samples = new();
    private readonly List<double> _locks = new();
    private readonly List<Downtime> _downtime = new();

    /// <summary>Adds a reading and drops whatever fell off the back.</summary>
    public void Add(double at, int rssi)
    {
        lock (_gate)
        {
            _samples.Add(new Sample(at, rssi));
            Forget(at);
        }
    }

    /// <summary>Marks that the screen was locked at this moment.</summary>
    public void Locked(double at)
    {
        lock (_gate)
        {
            _locks.Add(at);
            Forget(at);
        }
    }

    /// <summary>Marks a stretch when the app was not running.</summary>
    public void NotRunning(double from, double to)
    {
        lock (_gate)
            _downtime.Add(new Downtime(from, to));
    }

    public IReadOnlyList<Sample> Samples()
    {
        lock (_gate) return _samples.ToList();
    }

    public IReadOnlyList<double> Locks()
    {
        lock (_gate) return _locks.ToList();
    }

    public IReadOnlyList<Downtime> Downtimes()
    {
        lock (_gate) return _downtime.ToList();
    }

    public int Count
    {
        get { lock (_gate) return _samples.Count; }
    }

    /// <summary>
    /// Thins out what is older than an hour, keeping roughly one sample every
    /// ten seconds. Invisible in the chart and the difference between a file of
    /// a few kilobytes and one of several megabytes.
    /// </summary>
    public void Thin(double now)
    {
        lock (_gate)
        {
            var kept = new List<Sample>(_samples.Count);
            double? last = null;
            foreach (var sample in _samples)
            {
                // Recent samples are always kept; older ones only when they are
                // far enough from the last one KEPT - not from the last one
                // seen, or a dense stretch would thin itself away entirely.
                if (now - sample.At <= DetailedSeconds
                    || last is null
                    || sample.At - last >= ThinnedStepSeconds)
                {
                    kept.Add(sample);
                    last = sample.At;
                }
            }
            _samples.Clear();
            _samples.AddRange(kept);
        }
    }

    /// <summary>Throws away everything older than the window worth keeping.</summary>
    private void Forget(double now)
    {
        double cutoff = now - LengthSeconds;
        _samples.RemoveAll(s => s.At < cutoff);
        _locks.RemoveAll(t => t < cutoff);
        _downtime.RemoveAll(d => d.To < cutoff);
    }

    /// <summary>
    /// Replaces everything - used when the previous run's history is read back
    /// from disk. Clock values are the caller's business; this only stores them.
    /// </summary>
    public void Restore(IEnumerable<Sample> samples, IEnumerable<double> locks,
        IEnumerable<Downtime> downtimes)
    {
        lock (_gate)
        {
            _samples.Clear();
            _samples.AddRange(samples);
            _locks.Clear();
            _locks.AddRange(locks);
            _downtime.Clear();
            _downtime.AddRange(downtimes);
        }
    }

    /// <summary>
    /// What to draw for one pixel column of the chart, or null when that column
    /// holds no reading at all.
    ///
    /// The chart merges samples per PIXEL rather than drawing them all: without
    /// it a day's worth meant drawing hundreds of thousands of objects, which in
    /// the Python version froze the window and took half a gigabyte of memory.
    /// </summary>
    public static Column? Merge(IReadOnlyList<Sample> samples, double from, double to)
    {
        int count = 0, weakest = int.MaxValue, strongest = int.MinValue;
        foreach (var sample in samples)
        {
            if (sample.At < from || sample.At >= to)
                continue;
            count++;
            weakest = Math.Min(weakest, sample.Rssi);
            strongest = Math.Max(strongest, sample.Rssi);
        }
        return count == 0 ? null : new Column(count, weakest, strongest);
    }
}

/// <summary>What one pixel column of the chart shows.</summary>
public readonly record struct Column(int Count, int Weakest, int Strongest);
