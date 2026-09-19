namespace DaBtDynamicLock.App;

/// <summary>
/// The values every setting can be given, and what each one is called.
///
/// ONE place, because two of them now offer the same settings: the window and
/// the tray menu. 1.5 learned this the hard way in another shape - the menu
/// said "Active" where the window said "Automatic locking active" - and a
/// second list of seconds would drift the same way, only with numbers, where
/// nobody would notice until the two disagreed.
/// </summary>
internal static class Choices
{
    /// <summary>Seconds of silence before locking.</summary>
    public static readonly double[] Silence = { 12, 15, 20, 30, 45, 60, 90, 120 };

    /// <summary>
    /// dBm below which a phone counts as gone. Measured, not invented: a phone
    /// in a pocket reads about -80 dBm with the radio busy (14.-15.08.2026).
    /// </summary>
    public static readonly double[] Range =
        { -100, -95, -90, -85, -80, -75, -70, -65, -60 };

    /// <summary>Seconds before locking to show the countdown. 0 = never.</summary>
    public static readonly int[] Countdown = { 0, 5, 10, 15, 20, 30 };

    /// <summary>Percent from the top of the screen.</summary>
    public static readonly int[] Position = { 10, 20, 30, 40, 50, 60, 70, 80, 90 };

    /// <summary>Minutes of silence before warning. 0 = never.</summary>
    public static readonly int[] Warn = { 0, 1, 2, 5, 10, 20, 30, 60 };

    /// <summary>
    /// How long a pause can be. The same range the shipped version offers -
    /// five minutes for stepping out, two days for going away.
    /// </summary>
    public static readonly double[] Pause =
        { 0, 5, 15, 30, 60, 120, 240, 720, 1440, 2880 };

    public static string PauseLabel(double minutes) => Texts.Get(minutes switch
    {
        0 => "opt_not_paused",
        5 => "opt_5_min",
        15 => "opt_15_min",
        30 => "opt_30_min",
        60 => "opt_1_hour",
        120 => "opt_2_hours",
        240 => "opt_4_hours",
        720 => "opt_12_hours",
        1440 => "opt_1_day",
        _ => "opt_2_days",
    });

    /// <summary>
    /// The sensitivity entries say what the number MEANS where it is worth
    /// saying. -80 dBm is called out because a phone in a pocket measures about
    /// that - which is the whole reason this setting cannot be guessed at.
    /// </summary>
    public static string RangeLabel(double v)
    {
        int dbm = (int)v;
        if (v == Range[0]) return Texts.Get("opt_range_longest", dbm);
        if (v == Range[^1]) return Texts.Get("opt_range_shortest", dbm);
        if (v == -80) return Texts.Get("opt_range_edge", dbm);
        return Texts.Get("opt_range_plain", dbm);
    }

    public static string SilenceLabel(double seconds) =>
        Texts.Get("opt_seconds", (int)seconds);

    public static string CountdownLabel(int seconds) => seconds == 0
        ? Texts.Get("opt_countdown_off")
        : Texts.Get("opt_countdown_from", seconds);

    public static string WarnLabel(int minutes) => minutes == 0
        ? Texts.Get("opt_no_warning")
        : Texts.Get("opt_after_minutes", minutes);
}
