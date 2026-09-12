using DaBtDynamicLock.Core;

// Same shape as tests/test_logic.py: prints OK/FAIL per case, a summary at the
// end, and returns 0 or 1. No test framework on purpose - the project convention
// is a plain runnable check, and one more dependency would have to be restored
// before anything could be verified.

var failures = new List<string>();

// object? and not object: the label parameters are nullable by design (a
// decision with no seconds to show must be checkable too).
void Check(string description, object? expected, object? actual)
{
    bool ok = Equals(expected, actual);
    Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {description}: {actual ?? "null"}"
        + (ok ? "" : $"  (expected {expected ?? "null"})"));
    if (!ok) failures.Add(description);
}

var cfg = new WatchSettings { Active = true, SilenceSeconds = 20 };

Console.WriteLine("Basic situations:");
Check("all quiet, phone audible", LockAction.None,
    DecisionMaker.Decide(cfg, 2, true, 0, 99).Action);
Check("silence 19 s (not yet)", LockAction.None,
    DecisionMaker.Decide(cfg, 19, true, 0, 99).Action);
Check("silence 20 s (now yes)", LockAction.Lock,
    DecisionMaker.Decide(cfg, 20, true, 0, 99).Action);
Check("app switched off", LockAction.Stop,
    DecisionMaker.Decide(cfg with { Active = false }, 60, true, 0, 99).Action);
Check("paused from the tray", LockAction.Stop,
    DecisionMaker.Decide(cfg, 60, true, 300, 99).Action);
Check("phone never seen yet", LockAction.Stop,
    DecisionMaker.Decide(cfg, null, true, 0, 99).Action);
Check("already locked, waiting for the return", LockAction.Stop,
    DecisionMaker.Decide(cfg, 300, false, 0, 99).Action);

Console.WriteLine("\nCountdown (when enabled):");
var c = cfg with { Countdown = true };
Check("silence 9 s - do not show yet", LockAction.None,
    DecisionMaker.Decide(c, 9, true, 0, 99).Action);
Check("silence 11 s - show", LockAction.Countdown,
    DecisionMaker.Decide(c, 11, true, 0, 99).Action);
Check("correct number of seconds left", 5,
    DecisionMaker.Decide(c, 15, true, 0, 99).RemainingSeconds);
Check("countdown disabled = nothing", LockAction.None,
    DecisionMaker.Decide(cfg, 15, true, 0, 99).Action);

Console.WriteLine("\nIdle guard:");
var p = cfg with { IdleGuard = true };
Check("typing right now - do not lock", LockAction.None,
    DecisionMaker.Decide(p, 60, true, 0, 3).Action);
Check("nothing happening at the PC - lock", LockAction.Lock,
    DecisionMaker.Decide(p, 60, true, 0, 60).Action);
Check("guard disabled - lock even while working", LockAction.Lock,
    DecisionMaker.Decide(cfg, 60, true, 0, 3).Action);
var po = cfg with { IdleGuard = true, Countdown = true };
Check("while working do NOT show the countdown", LockAction.None,
    DecisionMaker.Decide(po, 15, true, 0, 3).Action);
Check("without work show the countdown", LockAction.Countdown,
    DecisionMaker.Decide(po, 15, true, 0, 60).Action);
Check("countdown hides while working even just before locking", LockAction.None,
    DecisionMaker.Decide(po, 19.5, true, 0, 1).Action);

Console.WriteLine("\nBehind the lock screen nothing is decided:");
// 41 of 68 locks in the log were locking an already locked screen.
Check("locked screen wins over a long silence", LockAction.Stop,
    DecisionMaker.Decide(cfg, 300, true, 0, 99, screenLocked: true).Action);
Check("and it says why", "screen_locked",
    DecisionMaker.Decide(cfg, 300, true, 0, 99, screenLocked: true).Reason);
Check("switched off beats the lock screen", "off",
    DecisionMaker.Decide(cfg with { Active = false }, 300, true, 0, 99, screenLocked: true).Reason);

Console.WriteLine("\nA network with no locking:");
Check("on a saved network nothing locks", LockAction.Stop,
    DecisionMaker.Decide(cfg, 300, true, 0, 99, trustedNetwork: true).Action);
Check("and it says why", "trusted_network",
    DecisionMaker.Decide(cfg, 300, true, 0, 99, trustedNetwork: true).Reason);
// The manual pause is the more specific of the two: it can say when it ends.
Check("a manual pause is reported before the network", "paused",
    DecisionMaker.Decide(cfg, 300, true, 300, 99, trustedNetwork: true).Reason);
// On a trusted network it does not matter whether the phone can be heard at all.
Check("the network wins over a phone never seen", "trusted_network",
    DecisionMaker.Decide(cfg, null, true, 0, 99, trustedNetwork: true).Reason);
Check("the lock screen still wins over the network", "screen_locked",
    DecisionMaker.Decide(cfg, 300, true, 0, 99, screenLocked: true, trustedNetwork: true).Reason);

Console.WriteLine("\nThe label is a key, never finished text:");
// A rendered string kept in state is how the phone app ended up showing the
// old language after a switch. The key plus its parameter travel separately.
var paused = DecisionMaker.Decide(cfg, 60, true, 300, 99);
Check("paused carries a key", "st_paused", paused.LabelKey);
Check("and the minutes beside it", 6, paused.LabelMinutes);
var counting = DecisionMaker.Decide(c, 15, true, 0, 99);
Check("countdown carries a key", "st_countdown", counting.LabelKey);
Check("and the seconds beside it", 5, counting.LabelSeconds);

Console.WriteLine("\nComing back from 'not watching' has to be noticed:");
foreach (var reason in new[] { "off", "paused", "trusted_network" })
    Check($"'{reason}' counts as not watching", true,
        DecisionMaker.NotWatching.Contains(reason));
Check("'at_desk' does not", false, DecisionMaker.NotWatching.Contains("at_desk"));
// "waiting" is deliberately NOT in the list: the measurement cannot be restarted
// for a phone that has never been heard - there is nothing to measure from.
Check("'waiting' does not either", false, DecisionMaker.NotWatching.Contains("waiting"));

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL OK");
    return 0;
}

Console.WriteLine($"FAILED: {failures.Count}");
foreach (var f in failures) Console.WriteLine($"  - {f}");
return 1;
