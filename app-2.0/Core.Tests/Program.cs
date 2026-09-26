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

Console.WriteLine("\nFull screen guard:");
// The case the typing guard cannot cover: a film runs for two hours without a
// keystroke, so "nobody has touched anything" says nothing about the chair.
var fs = cfg with { FullScreenGuard = true };
Check("a film playing full screen - do not lock", LockAction.None,
    DecisionMaker.Decide(fs, 60, true, 0, 999, fullScreen: true).Action);
Check("...and it says which guard", "idle_guard",
    DecisionMaker.Decide(fs, 60, true, 0, 999, fullScreen: true).Reason);
Check("nothing full screen - lock", LockAction.Lock,
    DecisionMaker.Decide(fs, 60, true, 0, 999, fullScreen: false).Action);
Check("guard disabled - lock even with a film on", LockAction.Lock,
    DecisionMaker.Decide(cfg, 60, true, 0, 999, fullScreen: true).Action);

// The two guards are separate situations, not two halves of one condition:
// either on its own has to hold the lock off, and each has to work with the
// other switched off.
Check("typing alone holds it off with the film guard off", LockAction.None,
    DecisionMaker.Decide(cfg with { IdleGuard = true }, 60, true, 0, 3,
        fullScreen: false).Action);
Check("a film alone holds it off with the typing guard off", LockAction.None,
    DecisionMaker.Decide(fs, 60, true, 0, 999, fullScreen: true).Action);
Check("both on, neither true - lock", LockAction.Lock,
    DecisionMaker.Decide(cfg with { IdleGuard = true, FullScreenGuard = true },
        60, true, 0, 999, fullScreen: false).Action);

// A full screen film must not put up a countdown that ends in nothing either.
Check("no countdown while a film is on", LockAction.None,
    DecisionMaker.Decide(fs with { Countdown = true }, 15, true, 0, 999,
        fullScreen: true).Action);

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
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Galaxy S24 Work", uuids, ""));
Check("null target counts as none either", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Galaxy S24 Work", uuids, null));
Check("part of the name matches", true,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Galaxy S24 Work", uuids, "Work"));
Check("and case does not matter", true,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Galaxy S24 Work", uuids, "work"));
Check("a different name does not match", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", "Galaxy S24 Work", uuids, "Pixel"));
Check("a MAC address matches", true,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, uuids, "AA:BB:CC:DD:EE:FF"));
Check("a MAC in lower case matches too", true,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, uuids, "aa:bb:cc:dd:ee:ff"));
Check("a different MAC does not", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, uuids, "11:22:33:44:55:66"));
Check("an unnamed device does not match a name target", false,
    DeviceMatch.Matches("AA:BB:CC:DD:EE:FF", null, uuids, "Work"));
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

Console.WriteLine("\n'Heard at all' is not the same as 'at the desk':");
// The scanner's watchdog asks the first one - a device that is audible but too
// weak means the radio works and there is nothing to restart, even though the
// silence keeps growing.
w = NewWatch();
Check("nothing heard yet", null, w.SinceSeen());
w.Record(-95, strict);
Check("a weak signal still counts as heard", 0.0, w.SinceSeen());
Check("...while the silence has not even started", null, w.Silence());
clock += 8;
Check("and the time since is measured", 8.0, w.SinceSeen());
w.Record(-60, strict);
Check("a strong one starts the silence too", 0.0, w.Silence());
// One weak reading right after a strong one does NOT count as leaving: the
// median of the window still clears the threshold. That is what the smoothing
// is for - raw RSSI jumps by 8 dB with the phone lying still.
clock += 5;
w.Record(-95, strict);
Check("one weak reading does not undo a strong one", 0.0, w.Silence());
// Once the strong sample ages out of the 6 s window, the weak one stands alone.
clock += 7;
w.Record(-95, strict);
Check("a weak one on its own keeps 'heard' fresh", 0.0, w.SinceSeen());
// 7, not 12: the silence runs from the last moment the MEDIAN cleared the
// threshold, which was the mixed window five seconds in - not from the last
// strong reading itself.
Check("...but the silence runs from when the median last cleared", 7.0, w.Silence());
w.TargetChanged();
Check("changing the target forgets it was ever heard", null, w.SinceSeen());

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

Console.WriteLine("\nThe history the chart is drawn from:");
var history = new SignalHistory();
history.Add(1000, -60);
history.Add(1001, -62);
history.Locked(1002);
Check("readings are kept", 2, history.Count);
Check("so are the locks", 1, history.Locks().Count);

// Older than the window the chart covers at all - it has to fall off, or a
// machine left running for a week would carry a week of samples. The new
// reading is far enough ahead that all three earlier entries fall outside;
// putting it exactly on the boundary would keep them, since the cut is "older
// than", not "as old as".
history.Add(1010 + SignalHistory.LengthSeconds, -70);
Check("anything past the window is forgotten", 1, history.Count);
Check("and so are its locks", 0, history.Locks().Count);

// Thinning: the last hour stays sample for sample, older stretches keep about
// one every ten seconds.
history = new SignalHistory();
double start = 10_000;
for (int i = 0; i < 600; i++)              // 600 samples one second apart,
    history.Add(start + i, -60);           // all of them older than an hour
for (int i = 0; i < 30; i++)               // and 30 from the last minute
    history.Add(start + 7200 + i, -60);
Check("before thinning, everything is there", 630, history.Count);

history.Thin(start + 7230);
// 600 samples one second apart span 599 s, so keeping one every ten gives the
// one at 0 s and then 10, 20 … 590 - sixty in all. The 30 recent ones are never
// touched.
Check("the old stretch is thinned to one sample per ten seconds", 60,
    history.Samples().Count(s => s.At < start + 7200));
Check("the last hour is left alone", 30,
    history.Samples().Count(s => s.At >= start + 7200));

// Merging per pixel column - what keeps the chart from drawing a hundred
// thousand objects and freezing, as the Python one did before it.
var merged = SignalHistory.Columns(
    new[] { new Sample(5, -70), new Sample(6, -50), new Sample(9, -60) }, 5, 5, 2);
Check("a column knows how many readings fell into it", 3, merged[0]?.Count);
Check("...and the weakest of them", -70, merged[0]?.Weakest);
Check("...and the strongest", -50, merged[0]?.Strongest);
Check("...and when its newest reading arrived", 9.0, merged[0]?.LastAt);
Check("a column with no reading is nothing to draw", null, merged[1]);

// The column edges belong to the CLOCK, not to the left edge of the chart. The
// left edge moves on every refresh; columns counted from it moved too, readings
// hopped between neighbouring columns and the line flickered - fixed once in
// 1.x and lost again when the chart was rewritten. Two refreshes a second
// apart must put the same two readings in the same column.
var pair = new[] { new Sample(10, -70), new Sample(11, -50) };
var earlier = SignalHistory.Columns(pair, 8.5, 2, 3);
var later = SignalHistory.Columns(pair, 9.5, 2, 3);
Check("readings share a column however far the chart has scrolled", "2|2",
    $"{earlier.Max(c => c?.Count ?? 0)}|{later.Max(c => c?.Count ?? 0)}");

Console.WriteLine("\nWhere things go in the chart:");

// The grid is anchored to round LOCAL times, not to "so many seconds back from
// now": with an offset from now the lines shift on every refresh and the chart
// flickers, and the labels have to read as clock times.
// 1700000000 is 23:33:20 UTC; with a one hour offset the half-minute lines fall
// at :33:30, :34:00 and so on.
var grid = ChartLayout.GridLines(1_700_000_000, 1_700_000_120, 3600);
Check("a two minute range gets a line every 30 s", 30.0,
    grid.Count > 1 ? grid[1] - grid[0] : 0);
Check("...anchored to a round local time", 0.0, (grid[0] + 3600) % 30);

// The case that catches an anchor done in UTC: a half-hour time zone AND a
// step the offset is not a whole multiple of. With a 15 minute step it would
// pass either way - every real time zone is a multiple of 15 minutes - so the
// eight hour range is used, where the step is a full hour.
const double halfHourZone = 3600 * 5.5;
var half = ChartLayout.GridLines(1_700_000_000, 1_700_028_800, halfHourZone);
Check("...on the local hour in a half-hour time zone, not the UTC one", 0.0,
    (half[0] + halfHourZone) % 3600);

Check("a day's range is spaced three-hourly", 10800.0, ChartLayout.GridStep(86400));

// The spacing the shipped version uses. A quarter of an hour carrying three
// lines left whole minutes of the chart with nothing to read against; how many
// of them get a label is decided by what fits while drawing.
Check("a quarter hour gets a line every minute", 60.0, ChartLayout.GridStep(900));
Check("an hour gets one every five minutes", 300.0, ChartLayout.GridStep(3600));

// The strength axis. Anything past either end is pinned to the edge - -127
// means "unknown" and would otherwise drag the line off the picture.
Check("the top of the axis is the top of the plot", 0.0, ChartLayout.Y(-30, 200));
Check("the bottom is the bottom", 200.0, ChartLayout.Y(-110, 200));
Check("halfway down is halfway", 100.0, ChartLayout.Y(-70, 200));
Check("an impossible reading is pinned, not drawn outside", 200.0,
    ChartLayout.Y(-127, 200));

// The summary under the chart.
var seen = new[]
{
    new Sample(10, -80), new Sample(20, -60), new Sample(70, -70),
};
var summary = ChartLayout.Summarise(seen, 0, 100);
Check("the summary counts what is in view", 3, summary.Count);
Check("...uses the middle strength, not the average", -70, summary.MedianRssi);
Check("...and finds the longest silence, including the tail", 50.0,
    summary.LongestSilenceSeconds);
Check("an empty range is all silence", 100.0,
    ChartLayout.Summarise(Array.Empty<Sample>(), 0, 100).LongestSilenceSeconds);

// ---- the silence the chart shades --------------------------------------
Console.WriteLine("\nStretches the phone went quiet for:");

const double lockAfter = 45;
const double now = 1000;

static List<Sample> Heard(double from, double to, int rssi, double skipFrom = 0,
    double skipTo = 0)
{
    var samples = new List<Sample>();
    for (double t = from; t <= to; t += 1)
        if (!(t >= skipFrom && t <= skipTo && skipTo > skipFrom))
            samples.Add(new Sample(t, rssi));
    return samples;
}

var none = Array.Empty<Downtime>();

var steady = Heard(now - 300, now, -65);
Check("a phone heard the whole time leaves nothing shaded", 0,
    Silences.Bands(steady, now - 300, now, null, none, lockAfter, true).Count);

// A minute of nothing, with the phone heard on both sides of it.
var gap = Heard(now - 300, now, -65, now - 200, now - 140);
var shaded = Silences.Bands(gap, now - 300, now, null, none, lockAfter, true);
Check("a minute of nothing is one stretch", 1, shaded.Count);
Check("...and it is long enough to have locked the screen", true,
    shaded.Count == 1 && shaded[0].LongEnoughToLock);

// Twenty seconds is a gap, but not one the screen would lock for.
var blip = Heard(now - 300, now, -65, now - 200, now - 180);
var blips = Silences.Bands(blip, now - 300, now, null, none, lockAfter, true);
Check("twenty seconds counts as a gap", 1, blips.Count);
Check("...but not as one that locks", false,
    blips.Count == 1 && blips[0].LongEnoughToLock);

// The same gap, with the app not running through it. Without this a stopped
// app masquerades as the longest loss of signal - and that is the reading
// somebody would go hunting a fault in.
var stopped = new[] { new Downtime(now - 201, now - 139) };
Check("a gap the app slept through is not the phone's fault", 0,
    Silences.Bands(gap, now - 300, now, null, stopped, lockAfter, true).Count);

// With a threshold, a weak reading does not count as the phone being here -
// the same rule the app itself decides by.
var weak = Heard(now - 300, now, -85);
Check("readings below the threshold leave the range silent", 1,
    Silences.Bands(weak, now - 300, now, -70, none, lockAfter, true).Count);
Check("...and above it they do not", 0,
    Silences.Bands(weak, now - 300, now, -90, none, lockAfter, true).Count);

// Nothing recorded before the left edge means the app was not watching yet.
var late = Heard(now - 100, now, -65);
Check("time before the first ever reading is not shaded", 0,
    Silences.Bands(late, now - 300, now, null, none, lockAfter, false).Count);
Check("...but with older records behind it, it is", 1,
    Silences.Bands(late, now - 300, now, null, none, lockAfter, true).Count);

// 62 and not 60: the silence is measured from the last reading that arrived
// to the first one after it, so it reaches a second either side of the minute
// with nothing in it. Written down because the first version of this check
// expected 60 and was wrong - the numbers the chart shows are these, not the
// ones somebody had in mind while making up the data.
Check("the longest of them is reported", 62.0,
    Math.Round(Silences.Longest(shaded)));

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL OK");
    return 0;
}

Console.WriteLine($"FAILED: {failures.Count}");
foreach (var f in failures) Console.WriteLine($"  - {f}");
return 1;
