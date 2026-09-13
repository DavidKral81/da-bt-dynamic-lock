namespace DaBtDynamicLock.Core;

/// <summary>
/// Where things go in the chart: which moments get a grid line, and how a
/// signal strength turns into a height.
///
/// Kept out of the drawing code so it can be checked over plain numbers. The
/// Python version learned this the hard way with the time axis - the labels
/// were right and the grid was anchored to the wrong clock, which no amount of
/// looking at the picture would have settled.
/// </summary>
public static class ChartLayout
{
    /// <summary>The ranges offered, in seconds. Same set the shipped version offers.</summary>
    public static readonly double[] Ranges = { 120, 300, 900, 3600, 28800, 86400 };

    /// <summary>
    /// The strength axis, in dBm. It reaches above what is ever measured (a
    /// phone on the desk gives about -70) and below the receiver's sensitivity
    /// (~-100), so extremes still land inside the chart and are visibly outside
    /// the usual band rather than clipped onto its edge.
    /// </summary>
    public const int RssiTop = -30;
    public const int RssiBottom = -110;

    /// <summary>
    /// How far apart the grid lines are for a given range. The same spacing the
    /// shipped version uses: a quarter of an hour with three lines in it left
    /// whole minutes of the chart with nothing to read a position against.
    ///
    /// How many of those lines get a LABEL is decided while drawing, by what
    /// fits - a step chosen here cannot know how wide the window is.
    /// </summary>
    public static double GridStep(double rangeSeconds) => rangeSeconds switch
    {
        <= 120 => 30,
        <= 300 => 60,
        <= 900 => 60,
        <= 3600 => 300,
        <= 28800 => 3600,
        _ => 10800,
    };

    /// <summary>
    /// The moments that get a grid line, as wall clock seconds.
    ///
    /// Anchored to ROUND LOCAL TIMES - 09:46, 09:47 - not to "so many seconds
    /// back from now". Two reasons, both learned: with an offset from now the
    /// lines shifted on every refresh and the chart flickered, and the labels
    /// have to read as clock times because that is what the user compares
    /// against the log.
    ///
    /// The local offset is applied before rounding, so the lines fall on round
    /// times HERE rather than in UTC - half-hour time zones would otherwise put
    /// every label at :16 and :46.
    /// </summary>
    public static IReadOnlyList<double> GridLines(double fromWall, double toWall,
        double localOffsetSeconds)
    {
        double step = GridStep(Math.Max(toWall - fromWall, 1));
        var lines = new List<double>();

        double shifted = fromWall + localOffsetSeconds;
        double first = Math.Ceiling(shifted / step) * step - localOffsetSeconds;

        for (double at = first; at <= toWall; at += step)
            lines.Add(at);

        return lines;
    }

    /// <summary>
    /// Height for a signal strength, 0 at the top of the plot area.
    ///
    /// Anything off the ends is pinned to the edge rather than drawn outside:
    /// -127 means "unknown" and would otherwise drag the line off the picture.
    /// </summary>
    public static double Y(double rssi, double plotHeight)
    {
        double share = (RssiTop - rssi) / (double)(RssiTop - RssiBottom);
        return Math.Clamp(share, 0, 1) * plotHeight;
    }

    /// <summary>Horizontal position for a moment, 0 at the left of the plot area.</summary>
    public static double X(double at, double fromWall, double toWall, double plotWidth)
    {
        double span = toWall - fromWall;
        if (span <= 0)
            return 0;
        return Math.Clamp((at - fromWall) / span, 0, 1) * plotWidth;
    }

    /// <summary>
    /// What the summary line under the chart says, worked out from the samples
    /// in view: how many there were, how often, the middle strength, and the
    /// longest stretch of silence.
    /// </summary>
    public static Summary Summarise(IReadOnlyList<Sample> inView, double from, double to)
    {
        if (inView.Count == 0)
            return new Summary(0, 0, null, to - from);

        var strengths = inView.Select(s => s.Rssi).OrderBy(r => r).ToList();
        // The middle value, not the average: raw RSSI jumps by 8 dB with the
        // phone lying still, and one stray reading would drag an average.
        int median = strengths[strengths.Count / 2];

        double longestGap = inView[0].At - from;
        for (int i = 1; i < inView.Count; i++)
            longestGap = Math.Max(longestGap, inView[i].At - inView[i - 1].At);
        longestGap = Math.Max(longestGap, to - inView[^1].At);

        double minutes = (to - from) / 60;
        double perMinute = minutes > 0 ? inView.Count / minutes : 0;

        return new Summary(inView.Count, perMinute, median, longestGap);
    }
}

/// <summary>The line under the chart: what the range being looked at adds up to.</summary>
public readonly record struct Summary(int Count, double PerMinute, int? MedianRssi,
    double LongestSilenceSeconds);
