using DaBtDynamicLock.Core;
using DaBtDynamicLock.Platform;

namespace DaBtDynamicLock.Engine;

/// <summary>Which face the tray icon wears.</summary>
public enum WatchIcon
{
    /// <summary>Watching, everything as it should be.</summary>
    Ok,
    /// <summary>About to lock.</summary>
    Countdown,
    /// <summary>Not watching, or locking is blocked.</summary>
    Off,
}

/// <summary>
/// What the loop needs from whatever is showing it. Split out so the loop can
/// be run in a test with nothing on screen - the shipped version tests the very
/// same loop that way, and it is the only place where it shows that both
/// safeguards are really wired in.
/// </summary>
public interface IWatcherView
{
    void ShowCountdown(int secondsLeft);
    void HideCountdown();

    /// <summary>
    /// The icon and what it says. The decision is handed over whole - a key and
    /// its numbers - and translated at the last moment. Never decide anything
    /// from the finished text: an English build once kept the icon green while
    /// locking was blocked, because the check read the Czech wording.
    /// </summary>
    void ShowStatus(WatchIcon icon, Decision decision, int? rssi);

    /// <summary>
    /// The watched device has not been heard for a long time. Said once, out
    /// loud: after locking, the app waits for the device to come back, and if
    /// it never does (a phone in flight mode overnight) the machine is no
    /// longer being guarded and nobody knows.
    /// </summary>
    void WarnNoSignal(int minutes);

    /// <summary>The menu is rebuilt so it shows the current state when opened.</summary>
    void RefreshMenu();
}

/// <summary>What the loop asks Windows. An interface so a test can answer instead.</summary>
public interface IWatcherSystem
{
    Reading<bool> IsScreenLocked();
    Reading<double> IdleSeconds();

    /// <summary>Is something filling the screen - a video, a presentation, a game?</summary>
    Reading<bool> FullScreenAppRunning();

    Reading<WifiConnection?> CurrentNetwork();
    Reading<bool> LockScreen();

    /// <summary>Start the radio over - the silence may be the scanner's, not the device's.</summary>
    void RestartScanner();
}

/// <summary>
/// The loop. Ticks twice a second, decides, and acts.
///
/// Everything in here that looks like a detail was paid for in the log: the
/// stall check, the second decision before locking, the restart after coming
/// back from "not watching". Each has its own comment saying which fault it is
/// there for.
/// </summary>
public sealed class Watcher
{
    /// <summary>
    /// A gap longer than this means the loop was not running - the machine
    /// slept. Generously large on purpose: a busy machine delays a tick by a
    /// second or two, and treating that as a gap would restart the measurement
    /// for ever and never lock anything.
    /// </summary>
    public const double StallSeconds = 10;

    /// <summary>How often the loop ticks.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly PhoneWatch _watch;
    private readonly Func<Settings> _settings;
    private readonly IWatcherSystem _system;
    private readonly IWatcherView _view;
    private readonly Log _log;
    private readonly Func<double> _now;
    private readonly DeafRadioGuard _deafRadio = new();

    /// <summary>Null, not 0: a zero would make the very first tick look like a gap.</summary>
    private double? _lastTick;

    private LockAction? _previousAction;
    private bool _screenLocked;
    private bool _alerted;
    private string? _notWatching;
    private bool _wlanCheckFailed;
    private double? _lockFailureLoggedAt;

    /// <summary>How rarely a screen that refuses to lock is complained about.</summary>
    private const double LockFailureQuietSeconds = 60;

    /// <summary>Everything runs, but the screen is never really locked.</summary>
    public bool DryRun { get; init; }

    public Watcher(PhoneWatch watch, Func<Settings> settings, IWatcherSystem system,
        IWatcherView view, Log log, Func<double>? now = null)
    {
        _watch = watch;
        _settings = settings;
        _system = system;
        _view = view;
        _log = log;
        _now = now ?? PhoneWatch.MonotonicSeconds;
    }

    /// <summary>Ticks until cancelled.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            Tick();
            try
            {
                await Task.Delay(TickInterval, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One round. Never throws: whatever goes wrong, the next tick still has to
    /// happen. In 1.3 an exception escaping from here stopped the loop being
    /// scheduled ever again, and the app sat in the tray watching nothing.
    /// </summary>
    public void Tick()
    {
        try
        {
            Decide();
        }
        catch (Exception e)
        {
            try
            {
                _log.Write($"Error in the main loop: {e.Message}");
            }
            catch (Exception)
            {
                // Deliberate, and the only suppression here: carrying on is
                // what matters, and the log has already tried its best.
            }
        }
    }

    private void Decide()
    {
        double now = _now();
        double gap = _lastTick is double last ? now - last : 0;
        _lastTick = now;

        if (gap > StallSeconds)
        {
            // The loop stood still - the machine slept. Nothing was measured in
            // the meantime, so the silence that piled up says nothing about
            // where the device is; deciding on it would lock the screen the
            // instant the lid opens. Measured 20.08.2026: an 11.5 minute sleep
            // produced "Locking (silence 0 s)".
            _log.Write($"The loop stood still for {gap:F0} s (sleep?) "
                + "- the silence is measured again from now.");
            RestartAfterBlindSpell();
            return;
        }

        bool locked = ScreenLocked();
        if (locked != _screenLocked)
        {
            _screenLocked = locked;
            if (locked)
            {
                _log.Write("Screen locked - watching pauses until it is unlocked.");
            }
            else
            {
                // Silence that piled up behind the lock screen says nothing
                // about where the device is now - the same situation as waking
                // from sleep, so the same remedy. Without it the screen would
                // lock again the moment the user finished typing the PIN.
                RestartAfterBlindSpell();
                _log.Write("Screen unlocked - the silence is measured again from now.");
            }
        }

        var cfg = _settings();
        double pause = _watch.PauseLeft;
        bool trusted = OnTrustedNetwork(cfg);
        double idle = Idle();
        bool fullScreen = FullScreen(cfg);

        var decision = DecisionMaker.Decide(cfg.ForWatching(), _watch.Silence(),
            _watch.Armed, pause, idle, locked, trusted, fullScreen);

        // Back from a state in which nothing was watched: the switch was off, a
        // pause ran out, or the machine left a network without locking (or that
        // network was unticked). The silence kept counting meanwhile, so
        // deciding on it would lock at once and skip the countdown. Found in use
        // on 11.09.2026 after unticking a network; the other two had the
        // very same cause.
        //
        // A locked screen is NOT such a return. Decide() weighs screen_locked
        // above paused and trusted_network, so locking the screen while paused
        // or on a saved network changes the reason to one outside NotWatching -
        // and this used to fire, logging "Watching resumed" one line under
        // "Screen locked". On a saved network that happened at every manual
        // lock, and that is the very line the feature gets verified by.
        if (_notWatching is not null && !locked
            && !DecisionMaker.NotWatching.Contains(decision.Reason))
        {
            RestartAfterBlindSpell();
            _log.Write($"Watching resumed after '{_notWatching}' - the silence is "
                + "measured again from now.");
            decision = DecisionMaker.Decide(cfg.ForWatching(), _watch.Silence(),
                _watch.Armed, pause, idle, locked, trusted, fullScreen);
        }
        _notWatching = DecisionMaker.NotWatching.Contains(decision.Reason)
            ? decision.Reason : null;

        // Starting and cancelling a countdown are both logged, so it can be
        // checked afterwards that a lock really was preceded by a warning.
        if (decision.Action == LockAction.Countdown && _previousAction != LockAction.Countdown)
            _log.Write($"Countdown started - {decision.RemainingSeconds} s left "
                + $"(last RSSI {RssiText()}); {HeardText()}.");
        else if (_previousAction == LockAction.Countdown && decision.Action == LockAction.None)
            _log.Write("Countdown cancelled - the phone is back at the desk.");
        _previousAction = decision.Action;

        // Not while locked: the warning means "this machine is sitting here
        // unlocked and I have stopped guarding it", which is not the situation
        // behind a lock screen.
        if (cfg.Active && !locked)
            WarnIfSignalLost(cfg);
        _view.RefreshMenu();

        if (decision.Action == LockAction.Lock)
            decision = ConfirmLock(cfg, trusted);

        // The last question before the screen goes: has the device really gone,
        // or did the radio stop hearing anything at all? Asked here rather than
        // in Decide(), which is pure and knows nothing about the scanner.
        if (decision.Action == LockAction.Lock && HeldOffByDeafRadio())
        {
            decision = decision with { Action = LockAction.None };
            _previousAction = LockAction.None;
        }

        Show(decision);
    }

    /// <summary>
    /// Asks again with fresh numbers. Between the first decision and this point
    /// sit a possible Windows notification and the menu rebuild; on 20.08.2026
    /// that took about six seconds and the phone was back before the screen
    /// locked. The log then read "Locking (silence 0 s)" - a lock nobody
    /// deserved.
    ///
    /// The network is deliberately NOT asked about again: a change there is
    /// handled as a transition on the next tick, and caught here it would call
    /// the lock off with "the phone came back", which is not true.
    /// </summary>
    private Decision ConfirmLock(Settings cfg, bool trusted)
    {
        var again = DecisionMaker.Decide(cfg.ForWatching(), _watch.Silence(),
            _watch.Armed, _watch.PauseLeft, Idle(), ScreenLocked(), trusted);
        if (again.Action != LockAction.Lock)
        {
            _log.Write("Locking called off - the phone came back while the "
                + "decision was being carried out.");
            _previousAction = again.Action;
        }
        return again;
    }

    private bool HeldOffByDeafRadio()
    {
        var (adverts, devices) = _watch.HeardRecently(PhoneWatch.HeardWindowSeconds);
        switch (_deafRadio.Judge(adverts, devices))
        {
            case DeafRadioVerdict.HoldOff:
                _log.Write($"Locking held off ({_deafRadio.HoldOffsInARow}/"
                    + $"{DeafRadioGuard.HoldOffMax}) - {HeardText()}, which is the "
                    + "scanner gone deaf rather than the phone leaving. Restarting "
                    + "the scanner and measuring again.");
                _system.RestartScanner();
                _watch.RestartMeasurement();
                return true;

            case DeafRadioVerdict.LockAnyway:
                _log.Write($"Locking anyway - the radio stayed silent through "
                    + $"{DeafRadioGuard.HoldOffMax} hold-offs, so the room may really "
                    + $"be empty; {HeardText()}.");
                return false;

            default:
                return false;
        }
    }

    private void Show(Decision decision)
    {
        switch (decision.Action)
        {
            case LockAction.Lock:
                DoLock(decision);
                break;

            case LockAction.Countdown:
                _view.ShowCountdown(decision.RemainingSeconds);
                _view.ShowStatus(WatchIcon.Countdown, decision, _watch.Rssi);
                break;

            default:
                _view.HideCountdown();
                // Never decide from the label - that gets translated. This used
                // to test the Czech wording, which in English was never true,
                // so the icon stayed green even while locking was blocked.
                bool guardBlocks = decision.Reason == "idle_guard";
                var icon = decision.Action == LockAction.None && !guardBlocks
                    ? WatchIcon.Ok : WatchIcon.Off;
                _view.ShowStatus(icon, decision,
                    guardBlocks ? null : _watch.Rssi);
                break;
        }
    }

    private void DoLock(Decision decision)
    {
        double? silence = _watch.Silence();

        // Lock FIRST, then write it down. The other way round the log announced
        // a locked screen and only then found out it had not locked - and the
        // bookkeeping below left the app waiting for the phone with the desktop
        // in plain view.
        var locked = DryRun ? new Reading<bool>(true) : _system.LockScreen();
        if (!locked.Value)
        {
            // Left armed on purpose, so the NEXT TICK TRIES AGAIN - it is the
            // complaint that is throttled, never the attempt. Without the
            // throttle a screen that refuses to lock would write two lines a
            // second into the very log the fault has to be diagnosed from.
            if (locked.Problem is not null
                && (_lockFailureLoggedAt is not double when
                    || _now() - when > LockFailureQuietSeconds))
            {
                _lockFailureLoggedAt = _now();
                _log.Write(locked.Problem);
            }
            _view.ShowStatus(WatchIcon.Off, decision with { LabelKey = "st_lock_failed" },
                _watch.Rssi);
            return;
        }

        _log.Write($"Locking (silence {silence ?? 0:F0} s, last RSSI {RssiText()}).");
        if (DryRun)
            _log.Write("  (dry run - the screen is not really locked)");
        _watch.Disarm();
        _view.HideCountdown();
        _view.ShowStatus(WatchIcon.Off, decision with { LabelKey = "st_locked" }, null);
    }

    /// <summary>
    /// Coming out of a spell in which nothing could be measured - sleep, the
    /// lock screen, or a state where watching was off. One place, because all
    /// three need exactly the same four things done.
    /// </summary>
    private void RestartAfterBlindSpell()
    {
        _watch.RestartMeasurement();
        _view.HideCountdown();
        _previousAction = null;
        // Cleared quietly. The clock has just been set back to zero, so the
        // warning below would otherwise announce "Phone is advertising again"
        // about a phone that has said nothing at all - seen in the log on
        // 20.08.2026 16:08:03. The log is what these faults get diagnosed from;
        // it must not invent good news.
        _alerted = false;
    }

    private void WarnIfSignalLost(Settings cfg)
    {
        double? silence = _watch.Silence();
        if (silence is null || cfg.AlertNoSignalMinutes <= 0)
            return;

        if (silence > cfg.AlertNoSignalMinutes * 60)
        {
            if (_alerted)
                return;
            _alerted = true;
            int minutes = (int)Math.Round(silence.Value / 60);
            _log.Write($"Phone has not advertised for {minutes} min - warning the user.");
            _view.WarnNoSignal(minutes);
        }
        else if (_alerted)
        {
            _alerted = false;
            _log.Write("Phone is advertising again - watching resumed.");
        }
    }

    /// <summary>
    /// True while the machine is on one of the saved networks. Both the name
    /// and the access point have to match - see <see cref="WifiNetwork"/> for
    /// why the name alone will not do.
    /// </summary>
    private bool OnTrustedNetwork(Settings cfg)
    {
        if (!cfg.TrustedNetworkPause || cfg.TrustedNetworks.Count == 0)
            return false;

        var reading = _system.CurrentNetwork();
        if (!reading.Ok)
        {
            // A network that cannot be read is not trusted, so this can only
            // ever lead to MORE locking. Said once: the loop asks twice a second.
            if (!_wlanCheckFailed)
            {
                _wlanCheckFailed = true;
                _log.Write($"The wireless network could not be read ({reading.Problem}). "
                    + $"Watching carries on as if away from the "
                    + $"{cfg.TrustedNetworks.Count} saved network(s).");
            }
            return false;
        }
        _wlanCheckFailed = false;

        return reading.Value is WifiConnection here
            && cfg.TrustedNetworks.Any(n => n.Ssid == here.Ssid && n.Bssid == here.Bssid);
    }

    private bool ScreenLocked()
    {
        var reading = _system.IsScreenLocked();
        if (reading.Problem is not null)
            _log.Write(reading.Problem);
        return reading.Value;
    }

    private double Idle()
    {
        var reading = _system.IdleSeconds();
        if (reading.Problem is not null)
            _log.Write(reading.Problem);
        return reading.Value;
    }

    /// <summary>
    /// Is something filling the screen? Only asked when the setting is on -
    /// the loop ticks twice a second and there is no sense asking Windows a
    /// question whose answer would be thrown away.
    ///
    /// A failed reading means "no", so it can only ever lead to MORE locking.
    /// Said once, for the same reason the Wi-Fi check says it once.
    /// </summary>
    private bool FullScreen(Settings cfg)
    {
        if (!cfg.FullScreenGuard)
            return false;

        var reading = _system.FullScreenAppRunning();
        if (!reading.Ok)
        {
            if (!_fullScreenCheckFailed)
            {
                _fullScreenCheckFailed = true;
                _log.Write(reading.Problem ?? "the full screen check failed");
            }
            return false;
        }

        _fullScreenCheckFailed = false;
        return reading.Value;
    }

    private bool _fullScreenCheckFailed;

    /// <summary>
    /// Null does not mean zero - it means the reading was deliberately
    /// forgotten, which happens after a gap in the loop. A bare "None dBm"
    /// would read like a measurement.
    /// </summary>
    private string RssiText() =>
        _watch.Rssi is int rssi ? $"{rssi} dBm" : "unknown";

    /// <summary>
    /// One phrase telling a deaf scanner from a quiet phone. Written in ONE
    /// place because two log lines use it, and it is a phrase that gets
    /// compared between entries.
    /// </summary>
    private string HeardText()
    {
        var (adverts, devices) = _watch.HeardRecently(PhoneWatch.HeardWindowSeconds);
        return $"radio heard {adverts} advertisements from {devices} devices "
            + $"in the last {PhoneWatch.HeardWindowSeconds:F0} s";
    }
}
