using System.Diagnostics;

namespace DaBtDynamicLock.Core;

/// <summary>What an arriving advertisement changed, for the caller to log.</summary>
public enum Sighting
{
    /// <summary>Nothing worth a line - the phone was already here.</summary>
    Nothing,
    /// <summary>The very first time this device was heard.</summary>
    FirstSeen,
    /// <summary>It went quiet for a while and is back.</summary>
    BackAtTheDesk,
}

/// <summary>
/// One advertisement's outcome. Deliberately not a finished sentence: the log
/// line is built by the caller, the same reason <see cref="Decision"/> carries
/// a key rather than translated text.
/// </summary>
public readonly record struct SightingNote(Sighting What, int Rssi, double SilenceSeconds);

/// <summary>A device heard recently, for the picker. Age tells a memory from a reading.</summary>
public sealed record NearbyDevice(string Name, int Rssi, double AgeSeconds);

/// <summary>
/// Everything the app knows about the watched device: when it was last at the
/// desk, how strong its signal is, and whether the radio is hearing anything
/// at all. Shared between the scanner and the main loop, so every method locks.
///
/// The clock is handed in rather than read from the machine, so the whole class
/// can be tested over plain data. The Python version calls time.monotonic()
/// inside, which is why its tests have to replay recordings in real time.
/// </summary>
public sealed class PhoneWatch
{
    /// <summary>How long advertisements from other devices are kept.</summary>
    public const double HeardKeepSeconds = 60;

    /// <summary>
    /// The window asked about when the question "is the radio still hearing
    /// anything?" comes up. Short on purpose, so the answer describes the
    /// moment being asked about and not the minute before it.
    /// </summary>
    public const double HeardWindowSeconds = 15;

    /// <summary>Silence shorter than this after a return is not worth a log line.</summary>
    private const double WorthMentioningSeconds = 5;

    private readonly Func<double> _now;
    private readonly object _gate = new();

    private readonly Queue<(double Time, int Rssi)> _samples = new();
    private readonly Queue<(double Time, string Address)> _adverts = new();
    private readonly Dictionary<string, (int Rssi, double Time)> _nearby = new();

    private double _seenAt;
    private double _nearAt;
    private bool _wasNear;
    private bool _armed = true;
    private double _pausedUntil;
    private int? _rssi;
    private int? _rssiMedian;

    /// <param name="now">
    /// Monotonic seconds. Defaults to the machine's; tests pass their own so
    /// they never depend on how long they take to run.
    /// </param>
    public PhoneWatch(Func<double>? now = null) => _now = now ?? MonotonicSeconds;

    /// <summary>Seconds from an arbitrary point, never going backwards.</summary>
    public static double MonotonicSeconds() =>
        (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    /// <summary>Last measured signal strength. Null means forgotten, not zero.</summary>
    public int? Rssi { get { lock (_gate) return _rssi; } }

    /// <summary>Median of the smoothing window - what the threshold is compared against.</summary>
    public int? RssiMedian { get { lock (_gate) return _rssiMedian; } }

    /// <summary>False once the screen was locked, until the device comes back.</summary>
    public bool Armed { get { lock (_gate) return _armed; } }

    /// <summary>Seconds left of a manual pause, 0 when not paused.</summary>
    public double PauseLeft
    {
        get { lock (_gate) return Math.Max(0, _pausedUntil - _now()); }
    }

    /// <summary>
    /// How long the device has not been at the desk. Null = it never was, which
    /// is not the same as "gone for a long time" and must not lock anything.
    /// </summary>
    public double? Silence()
    {
        lock (_gate)
            return _wasNear ? _now() - _nearAt : null;
    }

    /// <summary>
    /// An advertisement from the watched device arrived. Decides whether it
    /// counts as "at the desk".
    ///
    /// With a sensitivity threshold set, hearing the device is not enough - the
    /// signal also has to be strong enough, and it is the SMOOTHED value that
    /// is compared. Raw RSSI jumps by 8 dB with the phone lying still.
    /// </summary>
    public SightingNote Record(int rssi, WatchSettings cfg)
    {
        lock (_gate)
        {
            double now = _now();

            // Worked out before the state moves on, because it asks about what
            // was true until this advertisement arrived.
            Sighting what = Sighting.Nothing;
            double silence = 0;
            if (!_wasNear)
            {
                what = Sighting.FirstSeen;
            }
            else if (now - _nearAt > WorthMentioningSeconds)
            {
                what = Sighting.BackAtTheDesk;
                silence = now - _nearAt;
            }

            _seenAt = now;
            _rssi = rssi;

            _samples.Enqueue((now, rssi));
            while (_samples.Count > 0 && now - _samples.Peek().Time > cfg.ThresholdWindowSeconds)
                _samples.Dequeue();
            _rssiMedian = (int)Median(_samples.Select(s => s.Rssi));

            bool near = cfg.RssiThreshold is null || _rssiMedian >= cfg.RssiThreshold;
            if (!near)
                // Too weak counts as absent, so there is no "back at the desk"
                // to announce either.
                return new SightingNote(Sighting.Nothing, rssi, 0);

            _nearAt = now;
            _wasNear = true;
            _armed = true;
            return new SightingNote(what, rssi, silence);
        }
    }

    /// <summary>
    /// Note that SOMETHING was heard, whatever it was.
    ///
    /// Deliberately separate from <see cref="RecordNearby"/>, which keeps only
    /// NAMED devices for the picker - most BLE gadgets advertise without a name,
    /// so counting only those would answer a different question. This one
    /// answers: when the phone has gone quiet, is the radio hearing anything at
    /// all? A long-running scanner on Windows goes progressively deaf, and "the
    /// phone stopped broadcasting" and "the laptop stopped listening" look
    /// identical from outside - both end as silence, and both lock the screen.
    /// </summary>
    public void RecordHeard(string address)
    {
        lock (_gate)
        {
            double now = _now();
            _adverts.Enqueue((now, address));
            while (_adverts.Count > 0 && now - _adverts.Peek().Time > HeardKeepSeconds)
                _adverts.Dequeue();
        }
    }

    /// <summary>
    /// Advertisements and distinct devices heard in the last
    /// <paramref name="withinSeconds"/> seconds.
    ///
    /// The watched device is not filtered out and does not need to be: this only
    /// ever gets asked while it counts as silent. (With a threshold set a weak
    /// signal counts as silence while still being heard - then it is in here,
    /// which is itself worth seeing.)
    /// </summary>
    public (int Adverts, int Devices) HeardRecently(double withinSeconds)
    {
        lock (_gate)
        {
            double now = _now();
            var recent = _adverts.Where(a => now - a.Time <= withinSeconds)
                                 .Select(a => a.Address)
                                 .ToList();
            return (recent.Count, recent.Distinct().Count());
        }
    }

    /// <summary>
    /// Keep track of what is audible, so the user can pick the target from a list.
    ///
    /// Unnamed devices are never offered: a MAC would tell the user nothing and
    /// phones rotate theirs anyway.
    /// </summary>
    public void RecordNearby(string? name, int rssi)
    {
        if (string.IsNullOrEmpty(name))
            return;
        lock (_gate)
            _nearby[name] = (rssi, _now());
    }

    /// <summary>
    /// Devices heard in the last <paramref name="withinSeconds"/> seconds,
    /// strongest first. The age matters: without it a record that is only a
    /// memory would show its last known strength as if it had just arrived.
    /// </summary>
    public IReadOnlyList<NearbyDevice> NearbyList(double withinSeconds = 60)
    {
        lock (_gate)
        {
            double now = _now();
            return _nearby
                .Where(e => now - e.Value.Time <= withinSeconds)
                .Select(e => new NearbyDevice(e.Key, e.Value.Rssi, now - e.Value.Time))
                .OrderByDescending(d => d.Rssi)
                .ToList();
        }
    }

    /// <summary>
    /// The screen was locked; stop acting until the device comes back.
    /// </summary>
    public void Disarm()
    {
        lock (_gate) _armed = false;
    }

    /// <summary>Pause watching for a while, from the tray.</summary>
    public void PauseFor(double seconds)
    {
        lock (_gate) _pausedUntil = _now() + seconds;
    }

    /// <summary>End a manual pause now.</summary>
    public void ResumeNow()
    {
        lock (_gate) _pausedUntil = 0;
    }

    /// <summary>
    /// Forget everything about the previous device - otherwise readings from the
    /// old target would count for a while, and the app could lock, or fail to
    /// lock, on them.
    /// </summary>
    public void TargetChanged()
    {
        lock (_gate)
        {
            _samples.Clear();
            _seenAt = 0;
            _nearAt = 0;
            _wasNear = false;
            _rssi = null;
            _rssiMedian = null;
            _armed = true;
        }
    }

    /// <summary>
    /// Start counting the silence again from this moment.
    ///
    /// For when the main loop did not run for a while (sleep, hibernation, a
    /// frozen machine): nothing was measured in that time, so the silence that
    /// piled up says nothing about where the device was. Locking on it means
    /// locking the instant the lid opens with the phone lying right there -
    /// measured 20.08.2026, an 11.5 minute sleep produced "Locking (silence 0 s)".
    ///
    /// Guarding must NOT stop, though, so this is emphatically not
    /// <see cref="TargetChanged"/>: the device stays known and only the clock
    /// starts from zero. A full reset would make <see cref="Silence"/> null and
    /// the app would stop watching until the device was heard again - so waking
    /// up without the phone would leave the machine unlocked. This way a device
    /// that really is gone still locks the screen, after the full delay and with
    /// a countdown.
    /// </summary>
    public void RestartMeasurement()
    {
        lock (_gate)
        {
            if (!_wasNear)
                return;                 // never seen it - nothing to restart
            double now = _now();
            _nearAt = now;
            _seenAt = now;
            _samples.Clear();           // readings from before the gap are stale
            _rssi = null;
            _rssiMedian = null;
            _armed = true;
        }
    }

    /// <summary>
    /// Middle value, averaging the two middle ones for an even count - the same
    /// as Python's statistics.median, so the smoothing behaves identically.
    /// </summary>
    internal static double Median(IEnumerable<int> values)
    {
        int[] sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
            throw new ArgumentException("No samples to take the median of.", nameof(values));
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
