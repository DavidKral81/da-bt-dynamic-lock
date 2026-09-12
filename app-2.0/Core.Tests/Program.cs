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

Console.WriteLine("\nMatching the watched device:");
string[] uuids = { "0000feaa-0000-1000-8000-00805f9b34fb" };
// An empty target used to match EVERYTHING, so any BLE device around counted
// as the phone. Found in use, not by the tests.
Check("no target picked = nothing counts", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Xiaomi 15 DaKing", uuids, ""));
Check("null target counts as none either", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Xiaomi 15 DaKing", uuids, null));
Check("part of the name matches", true,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Xiaomi 15 DaKing", uuids, "DaKing"));
Check("and case does not matter", true,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Xiaomi 15 DaKing", uuids, "daking"));
Check("a different name does not match", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Xiaomi 15 DaKing", uuids, "Pixel"));
Check("a MAC address matches", true,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, uuids, "AA:BB:CC:DD:EE:FF"));
Check("a MAC in lower case matches too", true,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, uuids, "aa:bb:cc:dd:ee:ff"));
Check("a different MAC does not", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, uuids, "11:22:33:44:55:66"));
Check("an unnamed device does not match a name target", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, uuids, "DaKing"));
Check("a service UUID matches", true,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, uuids, "feaa"));
Check("no UUIDs at all is not a crash", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, null, "feaa"));

// From here on the clock is ours, so nothing depends on how long the test runs.
double clock = 0;
PhoneWatch NewWatch() => new(() => clock);
var plain = new WatchSettings();

Console.WriteLine("\nMeasuring the silence:");
var w = NewWatch();
Check("never heard = no silence to speak of", null, w.Silence());
Check("and the first advertisement says so", Sighting.FirstSeen,
    w.Record(-60, plain).What);
Check("just heard = no silence", 0.0, w.Silence());
clock += 2;
Check("a repeat two seconds later is not worth a log line", Sighting.Nothing,
    w.Record(-60, plain).What);
clock += 12;
Check("twelve seconds on, that is the silence", 12.0, w.Silence());
clock += 30;
var back = w.Record(-58, plain);
Check("back after a real gap is worth one", Sighting.BackAtTheDesk, back.What);
Check("and it carries how long it was gone", 42.0, back.SilenceSeconds);
Check("which resets the silence", 0.0, w.Silence());

Console.WriteLine("\nLocked, then the phone returns:");
w = NewWatch();
w.Record(-60, plain);
Check("armed to begin with", true, w.Armed);
w.Disarm();
Check("locking disarms", false, w.Armed);
w.Record(-60, plain);
Check("and the phone coming back arms it again", true, w.Armed);

Console.WriteLine("\nA restart of the measurement is NOT a reset:");
// A full reset would make Silence() null and the app would stop watching until
// the phone was heard again - waking up without the phone would leave the
// machine unlocked.
w = NewWatch();
w.Record(-60, plain);
clock += 600;
Check("before the restart the silence has piled up", 600.0, w.Silence());
w.RestartMeasurement();
Check("after it the clock starts from zero", 0.0, w.Silence());
Check("but watching goes on - the silence is a number, not null", true,
    w.Silence() is not null);
clock += 50;
Check("so a phone that really is gone still locks", 50.0, w.Silence());
Check("the strength is forgotten, and null does not mean zero", null, w.Rssi);

w = NewWatch();
w.RestartMeasurement();
Check("a phone never heard stays never heard", null, w.Silence());

Console.WriteLine("\nChanging the target forgets everything:");
w = NewWatch();
w.Record(-60, plain);
w.Disarm();
w.TargetChanged();
Check("the old device's silence is gone", null, w.Silence());
Check("its strength too", null, w.Rssi);
Check("and it is armed again", true, w.Armed);

Console.WriteLine("\nSmoothing and the sensitivity threshold:");
// Raw RSSI jumps by 8 dB with the phone lying still, so the threshold is
// compared against the median of the window, never against one reading.
w = NewWatch();
w.Record(-60, plain);
Check("one sample is its own median", -60, w.RssiMedian);
clock += 1;
w.Record(-70, plain);
Check("two samples average, truncated as Python does", -65, w.RssiMedian);
clock += 1;
w.Record(-90, plain);
Check("three samples take the middle one", -70, w.RssiMedian);
clock += 10;
w.Record(-50, plain);
Check("samples older than the window drop out", -50, w.RssiMedian);

var strict = plain with { RssiThreshold = -80 };
w = NewWatch();
Check("a strong signal counts as at the desk", Sighting.FirstSeen,
    w.Record(-60, strict).What);
Check("so the silence starts", 0.0, w.Silence());
w = NewWatch();
Check("a weak one is as if absent", Sighting.Nothing, w.Record(-95, strict).What);
Check("so there is still nothing to measure", null, w.Silence());

Console.WriteLine("\nIs the radio hearing the room at all?");
w = NewWatch();
Check("nothing heard yet", (0, 0), w.HeardRecently(PhoneWatch.HeardWindowSeconds));
w.RecordHeard("AA:1");
w.RecordHeard("AA:1");
w.RecordHeard("BB:2");
Check("three advertisements from two devices", (3, 2),
    w.HeardRecently(PhoneWatch.HeardWindowSeconds));
clock += 20;
Check("and they fall out of the 15 s window", (0, 0),
    w.HeardRecently(PhoneWatch.HeardWindowSeconds));
Check("while a longer question still finds them", (3, 2), w.HeardRecently(60));
// Old entries are dropped by the next advertisement, not by asking - the same
// as the Python version. So a radio gone completely silent keeps its last
// minute on the books, and only questions narrower than that see the silence.
clock += 50;
Check("nothing arriving does not clear the record", (3, 2), w.HeardRecently(600));
w.RecordHeard("CC:3");
Check("the next advertisement drops what is over a minute old", (1, 1),
    w.HeardRecently(600));

Console.WriteLine("\nThe list of devices to pick from:");
w = NewWatch();
w.RecordNearby("Phone", -60);
w.RecordNearby("TV", -40);
w.RecordNearby(null, -30);
w.RecordNearby("", -30);
Check("unnamed devices are never offered", 2, w.NearbyList().Count);
Check("strongest first", "TV", w.NearbyList()[0].Name);
Check("fresh reading has no age", 0.0, w.NearbyList()[0].AgeSeconds);
clock += 30;
Check("an older one carries its age, so it cannot pass for a reading", 30.0,
    w.NearbyList()[0].AgeSeconds);
clock += 40;
Check("and past the window it is gone", 0, w.NearbyList().Count);

Console.WriteLine("\nA lock the radio cannot vouch for:");
var deaf = new DeafRadioGuard();
Check("a room full of chatter means the phone really left",
    DeafRadioVerdict.RadioHearsTheRoom, deaf.Judge(30, 5));
Check("one device chattering is enough as well",
    DeafRadioVerdict.RadioHearsTheRoom, deaf.Judge(30, 1));
Check("two devices are enough even when quiet",
    DeafRadioVerdict.RadioHearsTheRoom, deaf.Judge(2, 2));
Check("silence from everyone holds the lock off", DeafRadioVerdict.HoldOff,
    deaf.Judge(3, 1));
Check("a second time too", DeafRadioVerdict.HoldOff, deaf.Judge(0, 0));
// A guard that silence can switch off is no guard at all.
Check("the third time it locks anyway", DeafRadioVerdict.LockAnyway,
    deaf.Judge(0, 0));
Check("and the allowance starts over", DeafRadioVerdict.HoldOff,
    deaf.Judge(0, 0));
// Two unrelated deaf spells must not share one allowance.
Check("a radio that hears again clears the count", DeafRadioVerdict.RadioHearsTheRoom,
    deaf.Judge(9, 3));
Check("so the next spell gets the full allowance", DeafRadioVerdict.HoldOff,
    deaf.Judge(0, 0));

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL OK");
    return 0;
}

Console.WriteLine($"FAILED: {failures.Count}");
foreach (var f in failures) Console.WriteLine($"  - {f}");
return 1;
