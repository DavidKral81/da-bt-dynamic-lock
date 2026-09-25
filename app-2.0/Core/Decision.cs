namespace DaBtDynamicLock.Core;

/// <summary>What the app should do right now.</summary>
public enum LockAction
{
    /// <summary>All quiet, nothing is happening.</summary>
    None,
    /// <summary>Locking is near - show the countdown.</summary>
    Countdown,
    /// <summary>Lock now.</summary>
    Lock,
    /// <summary>Off, paused, or waiting for a signal.</summary>
    Stop,
}

/// <summary>
/// The settings that decide whether to lock - the ones
/// <see cref="DecisionMaker.Decide"/> and <see cref="PhoneWatch"/> read. The
/// window, the tray and the chart have their own.
/// </summary>
public sealed record WatchSettings
{
    public bool Active { get; init; } = true;
    public double SilenceSeconds { get; init; } = 20;
    public bool Countdown { get; init; }
    public int CountdownFromSeconds { get; init; } = 10;
    public bool IdleGuard { get; init; }
    public double IdleGuardSeconds { get; init; } = 15;

    /// <summary>Hold the lock off while something fills the screen.</summary>
    public bool FullScreenGuard { get; init; }

    /// <summary>
    /// How strong the signal has to be to count as "at the desk", in dBm. Null
    /// means hearing it at all is enough.
    /// </summary>
    public double? RssiThreshold { get; init; }

    /// <summary>
    /// How long a window the signal strength is smoothed over. Raw RSSI jumps
    /// by 8 dB with the phone lying still, so the threshold is compared against
    /// the median of this window, never against one reading.
    /// </summary>
    public double ThresholdWindowSeconds { get; init; } = 6;
}

/// <summary>
/// The outcome of one decision.
///
/// <see cref="Reason"/> is a KEY and callers branch on it. <see cref="LabelKey"/>
/// plus <see cref="LabelSeconds"/>/<see cref="LabelMinutes"/> are what the text
/// gets built from - deliberately NOT a finished string. The Python app learned
/// this twice the hard way: a service that remembered rendered status text kept
/// showing the old language after a switch, and a state parameter stored as
/// translated text produced "Broadcasting - rychly (100 ms)" in English.
/// </summary>
public sealed record Decision(
    LockAction Action,
    string Reason,
    int RemainingSeconds,
    string LabelKey,
    int? LabelSeconds = null,
    int? LabelMinutes = null);

public static class DecisionMaker
{
    /// <summary>
    /// Reasons under which the lock is being held off. Coming back from any of
    /// them has to restart the silence measurement - otherwise the first tick
    /// after the return finds silence long past the threshold and locks
    /// instantly, with no countdown. Reported by David 11.09.2026 after
    /// unticking a network; unpausing and switching the app back on had the
    /// very same cause.
    ///
    /// ⚠ "idle_guard" BELONGS HERE, and leaving it out was the same fault a
    /// second time (reported 26.09.2026): leaving a full-screen film locked the
    /// screen that instant, because the silence had been piling up for the
    /// whole film. Whether the phone was really silent is beside the point -
    /// what matters is that the person got no countdown and no chance. The
    /// typing guard is the same story on a smaller scale.
    ///
    /// The name is about the LOCK, not the radio: the silence goes on being
    /// measured throughout, it just stops meaning anything once it is no
    /// longer what decides.
    /// </summary>
    public static readonly string[] NotWatching =
        { "off", "paused", "trusted_network", "idle_guard" };

    /// <summary>
    /// Pure decision function - no UI, no locking, nothing asked of Windows.
    ///
    /// <paramref name="screenLocked"/> and <paramref name="trustedNetwork"/> are
    /// passed in rather than worked out here, so this stays testable over plain
    /// data. The loop asks Windows and hands the answers over.
    /// </summary>
    /// <param name="silence">Seconds since the phone was last heard, or null if it never was.</param>
    /// <param name="armed">False once locked, until the phone comes back.</param>
    /// <param name="pauseLeft">Seconds left of a manual pause.</param>
    /// <param name="idle">Seconds since the last keyboard or mouse input.</param>
    /// <param name="fullScreen">Something is filling the screen - a video, a
    /// presentation, a game.</param>
    public static Decision Decide(
        WatchSettings cfg,
        double? silence,
        bool armed,
        double pauseLeft,
        double idle,
        bool screenLocked = false,
        bool trustedNetwork = false,
        bool fullScreen = false)
    {
        if (!cfg.Active)
            return new Decision(LockAction.Stop, "off", 0, "st_off");

        // Nothing below applies behind the lock screen: the countdown box would
        // be drawn behind it and locking would lock what is already locked.
        // Measured 19.-22.08.2026 - 41 of 68 "locks" in the log were exactly that.
        if (screenLocked)
            return new Decision(LockAction.Stop, "screen_locked", 0, "st_screen_locked");

        if (pauseLeft > 0)
            return new Decision(LockAction.Stop, "paused", 0, "st_paused",
                LabelMinutes: (int)pauseLeft / 60 + 1);

        // After the manual pause, which is the more specific of the two and can
        // say when it runs out; before the phone is considered at all, because
        // on a trusted network it does not matter whether it can be heard.
        if (trustedNetwork)
            return new Decision(LockAction.Stop, "trusted_network", 0, "st_trusted_network");

        if (silence is null)
            return new Decision(LockAction.Stop, "waiting", 0, "st_waiting");

        if (!armed)
            return new Decision(LockAction.Stop, "locked", 0, "st_locked_wait");

        double limit = cfg.SilenceSeconds;
        int remaining = (int)Math.Round(limit - silence.Value, MidpointRounding.AwayFromZero);

        // The guards are evaluated BEFORE the countdown. When we already know
        // there will be no locking, there is no point in scaring the user with a
        // countdown that ends in nothing.
        //
        // Two of them, and they answer the same question in different ways:
        // typing says "somebody is working here", full screen says "somebody is
        // watching this" - which is the case typing cannot cover, because a film
        // runs for two hours without a single keystroke. Either one is enough to
        // hold the lock off; they are not two halves of one condition.
        bool idleGuard = (cfg.IdleGuard && idle < cfg.IdleGuardSeconds)
            || (cfg.FullScreenGuard && fullScreen);

        if (silence.Value >= limit)
        {
            if (idleGuard)
                return new Decision(LockAction.None, "idle_guard", 0, "st_guard");
            return new Decision(LockAction.Lock, "locking", 0, "st_locking");
        }

        if (cfg.Countdown && remaining <= cfg.CountdownFromSeconds)
        {
            if (idleGuard)
                return new Decision(LockAction.None, "idle_guard", 0, "st_guard_countdown");
            return new Decision(LockAction.Countdown, "countdown", Math.Max(remaining, 0),
                "st_countdown", LabelSeconds: remaining);
        }

        return new Decision(LockAction.None, "at_desk", remaining, "st_at_desk");
    }
}
