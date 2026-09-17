namespace DaBtDynamicLock.Core;

/// <summary>A stretch the phone was not heard for, as the chart shades it.</summary>
/// <param name="From">When the silence started.</param>
/// <param name="To">When it ended.</param>
/// <param name="LongEnoughToLock">True when it lasted past the locking threshold -
/// that is, when this is a silence the screen would have been locked for.</param>
public readonly record struct SilenceBand(double From, double To, bool LongEnoughToLock);

/// <summary>
/// Working out where the phone went quiet, for the chart to shade.
///
/// Kept out of the drawing for the usual reason: it can then be checked over
/// plain numbers. It is also the part with the two rules that are easy to get
/// wrong and impossible to see in a picture.
/// </summary>
public static class Silences
{
    /// <summary>
    /// A spacing between readings that is ordinary rather than a gap. Readings
    /// arrive a few times a second while the phone is near, so five seconds of
    /// nothing already means something.
    /// </summary>
    public const double OrdinarySpacingSeconds = 5;

    /// <summary>
    /// The stretches with no reading that counted, inside the range in view.
    /// </summary>
    /// <param name="inView">The readings in the range, oldest first.</param>
    /// <param name="from">Left edge of the range, in the same clock as the samples.</param>
    /// <param name="to">Right edge - usually now.</param>
    /// <param name="threshold">The sensitivity limit in dBm, or null for none.</param>
    /// <param name="downtimes">When the app was not running at all.</param>
    /// <param name="lockAfterSeconds">The silence the app locks the screen after.</param>
    /// <param name="haveOlder">Whether anything is recorded from before the left
    /// edge. When nothing is, the range does not start with a silence - the app
    /// simply was not watching yet, and shading that would be a lie.</param>
    public static IReadOnlyList<SilenceBand> Bands(IReadOnlyList<Sample> inView,
        double from, double to, int? threshold, IReadOnlyList<Downtime> downtimes,
        double lockAfterSeconds, bool haveOlder)
    {
        // "Audible" is not the same as "at the desk". With a threshold set, a
        // weak reading does not count as the phone being here - so the chart
        // has to use the same rule, or it would draw a continuous line through
        // a stretch the app itself counted as silence.
        var counted = new List<double>();
        foreach (var sample in inView)
            if (threshold is null || sample.Rssi >= threshold)
                counted.Add(sample.At);

        var edges = new List<double>();
        if (haveOlder)
            edges.Add(from);
        edges.AddRange(counted);
        edges.Add(to);

        var bands = new List<SilenceBand>();
        for (int i = 1; i < edges.Count; i++)
            foreach (var (a, b) in WithoutDowntime(edges[i - 1], edges[i], downtimes))
                if (b - a > Spacing(a, to))
                    bands.Add(new SilenceBand(a, b, b - a >= lockAfterSeconds));

        return bands;
    }

    /// <summary>The longest of those stretches, or 0 when there is none.</summary>
    public static double Longest(IReadOnlyList<SilenceBand> bands)
    {
        double longest = 0;
        foreach (var band in bands)
            longest = Math.Max(longest, band.To - band.From);
        return longest;
    }

    /// <summary>
    /// What still counts as ordinary spacing at this moment. Older readings are
    /// thinned to one per ten seconds, so measuring them against five seconds
    /// would paint the whole of yesterday as one long dropout.
    /// </summary>
    public static double Spacing(double at, double now) =>
        now - at <= SignalHistory.DetailedSeconds
            ? OrdinarySpacingSeconds
            : SignalHistory.ThinnedStepSeconds * 2;

    /// <summary>
    /// Cuts the stretches when the app was not running out of (a, b). Without
    /// this a stopped app would masquerade as the longest loss of signal - and
    /// that is the reading somebody would then go looking for a fault in.
    /// </summary>
    private static List<(double From, double To)> WithoutDowntime(double a, double b,
        IReadOnlyList<Downtime> downtimes)
    {
        var parts = new List<(double From, double To)> { (a, b) };
        foreach (var off in downtimes)
        {
            var kept = new List<(double From, double To)>();
            foreach (var (x, y) in parts)
            {
                if (off.To <= x || off.From >= y)
                {
                    kept.Add((x, y));
                    continue;
                }
                if (x < off.From)
                    kept.Add((x, off.From));
                if (off.To < y)
                    kept.Add((off.To, y));
            }
            parts = kept;
        }
        return parts;
    }
}
