using DaBtDynamicLock.Core;
using DaBtDynamicLock.Engine;
using DaBtDynamicLock.Platform;

// Checks the loop, the settings and the log. The loop is run for real - with a
// clock, a screen and a radio the test supplies itself, because a test that
// quietly depends on the machine around it once sent an hour after a regression
// that did not exist. Nothing here touches the running app's settings or log:
// files go to a temporary folder that is deleted at the end.

internal static class EngineChecks
{
    static readonly List<string> Failures = new();

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string scratch = Path.Combine(Path.GetTempPath(),
            "ddl-engine-checks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            CheckSettings(scratch);
            CheckLog(scratch);
            CheckSleepGap(scratch);
            CheckLockScreenPause(scratch);
            CheckReturnFromNotWatching(scratch);
            CheckLockIsConfirmed(scratch);
            CheckDeafRadio(scratch);
            CheckFailedLock(scratch);
            CheckNoSignalWarning(scratch);
            CheckTrustedNetwork(scratch);
            CheckHistoryStore(scratch);
        }
        finally
        {
            // Always, even after a failure: a test that leaves rubbish behind
            // on every run is its own kind of fault.
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { Console.WriteLine($"\n(could not clean up {scratch})"); }
        }

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL OK");
            return 0;
        }
        Console.WriteLine($"FAILED: {Failures.Count}");
        foreach (var f in Failures) Console.WriteLine($"  - {f}");
        return 1;
    }

    static void Check(string what, object? expected, object? actual)
    {
        bool ok = Equals(expected, actual);
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {what}: {actual ?? "null"}"
            + (ok ? "" : $"  (expected {expected ?? "null"})"));
        if (!ok) Failures.Add(what);
    }

    // ------------------------------------------------------------ settings

    static void CheckSettings(string scratch)
    {
        Console.WriteLine("Settings:");
        string path = Path.Combine(scratch, "config.json");

        var (fresh, problem) = Settings.Load(path);
        Check("a missing file gives the defaults, quietly", null, problem);
        Check("...with the shipped silence threshold", 45.0, fresh.SilenceSeconds);

        // The "_name" lines are what explains the file to whoever opens it. A
        // save that dropped them would leave the user with a bare list of keys.
        File.WriteAllText(path, """
            {
              "_about": "documentation that must survive a save",
              "target": "DaKing",
              "silence_s": 30,
              "trusted_networks": [{"ssid": "Home", "bssid": "AA:BB:CC:DD:EE:FF"}],
              "something_a_newer_version_added": 7
            }
            """);
        var (loaded, ok) = Settings.Load(path);
        Check("a saved file reads back", null, ok);
        Check("...the target", "DaKing", loaded.Target);
        Check("...the threshold", 30.0, loaded.SilenceSeconds);
        Check("...and a saved network, both halves", "Home:AA:BB:CC:DD:EE:FF",
            $"{loaded.TrustedNetworks[0].Ssid}:{loaded.TrustedNetworks[0].Bssid}");

        loaded.SilenceSeconds = 60;
        Check("saving succeeds", null, loaded.Save(path));
        string written = File.ReadAllText(path);
        Check("the documentation lines survive", true, written.Contains("_about"));
        Check("...and so does a key this version does not know", true,
            written.Contains("something_a_newer_version_added"));
        Check("the change itself landed", 60.0, Settings.Load(path).Value.SilenceSeconds);

        File.WriteAllText(path, "{ this is not json");
        var (broken, complaint) = Settings.Load(path);
        Check("a damaged file does not stop the app", 45.0, broken.SilenceSeconds);
        Check("...but says so", true, complaint is not null && complaint.Contains("unreadable"));
    }

    // ------------------------------------------------------------ log

    static void CheckLog(string scratch)
    {
        Console.WriteLine("\nThe log:");
        string path = Path.Combine(scratch, "log", "dyn_lock.log");
        var log = new Log(path, () => true);
        log.Write("hello");
        Check("the folder is made and the line written", true,
            File.ReadAllText(path).Contains("hello"));
        Check("...with a date in front", true,
            File.ReadAllText(path).StartsWith(DateTime.Now.ToString("dd.MM.yyyy")));

        var off = new Log(Path.Combine(scratch, "log", "off.log"), () => false);
        off.Write("not wanted");
        Check("logging switched off writes no file", false,
            File.Exists(Path.Combine(scratch, "log", "off.log")));

        // Writing into a folder that is really a file cannot work - and must
        // not throw, because the loop calls this and an exception used to stop
        // the loop being scheduled ever again.
        string blocked = Path.Combine(scratch, "afile");
        File.WriteAllText(blocked, "");
        var hopeless = new Log(Path.Combine(blocked, "nested", "x.log"), () => true);
        bool threw = false;
        try { hopeless.Write("this cannot be written anywhere"); }
        catch (Exception) { threw = true; }
        Check("a log that cannot be written does not throw", false, threw);

        // The uninstaller removes the settings folder and then goes on logging.
        // Each line used to make the folder again, so "remove the settings as
        // well" left a folder with a log in it.
        string removed = Path.Combine(scratch, "removed");
        var leaving = new Log(Path.Combine(removed, "dyn_lock.log"), () => true);
        leaving.Write("before the folder goes");
        Directory.Delete(removed, true);
        leaving.StopWritingFile();
        leaving.Write("after the folder has gone");
        Check("a log told to stop writing does not bring its folder back", false,
            Directory.Exists(removed));
    }

    // ------------------------------------------------------------ the loop

    /// <summary>One loop wired up to a clock, a screen and a radio we control.</summary>
    sealed class Rig
    {
        public double Clock;
        public readonly FakeSystem System = new();
        public readonly FakeView View = new();
        public readonly Settings Cfg = new() { Target = "Phone", SilenceSeconds = 20,
            Countdown = true, CountdownFromSeconds = 5, AlertNoSignalMinutes = 0 };
        public readonly PhoneWatch Watch;
        public readonly Watcher Loop;
        public readonly List<string> Lines = new();

        /// <param name="dryRun">
        /// True by default so a test can never lock the screen of whoever runs
        /// it. Turned off only where the point IS what the lock returns - the
        /// fake system answers instead of Windows, so still nothing locks.
        /// </param>
        public Rig(string scratch, string name, bool dryRun = true)
        {
            Watch = new PhoneWatch(() => Clock);
            LogPath = Path.Combine(scratch, name + ".log");
            var log = new Log(LogPath, () => true);
            Loop = new Watcher(Watch, () => Cfg, System, View, log, () => Clock)
            {
                DryRun = dryRun,
            };
            // The log also goes to the console, so the lines are collected here
            // for the checks to look at.
            LogTap = log;
        }

        public Log LogTap { get; }

        /// <summary>What this rig has written to its log file so far.</summary>
        public string LogText => File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";

        public string LogPath { get; private set; } = "";

        /// <summary>
        /// Other devices in the room keep chattering, so the radio is
        /// demonstrably alive on every tick.
        ///
        /// WITHOUT THIS NO TEST CAN EVER SEE A LOCK. A test rig hears nothing
        /// at all, which is the signature of a scanner gone deaf, so the guard
        /// holds every lock off and the test quietly measures the hold-off
        /// instead of the lock. The Python side has the same trap written down.
        /// </summary>
        public bool RoomKeepsChattering;

        /// <summary>Advances the clock and ticks, the way the real loop runs.</summary>
        public void Tick(double seconds = 0.5)
        {
            Clock += seconds;
            if (RoomKeepsChattering)
                RoomIsNoisy();
            Loop.Tick();
        }

        /// <summary>An advertisement from the watched device arrives.</summary>
        public void Hear(int rssi = -60)
        {
            Watch.RecordHeard("AA:BB:CC:DD:EE:FF");
            Watch.Record(rssi, Cfg.ForWatching());
        }

        /// <summary>Other devices chattering, so the radio is demonstrably alive.</summary>
        public void RoomIsNoisy(int devices = 5)
        {
            for (int i = 0; i < devices; i++)
                for (int n = 0; n < 6; n++)
                    Watch.RecordHeard($"00:00:00:00:00:{i:X2}");
        }
    }

    sealed class FakeSystem : IWatcherSystem
    {
        public bool ScreenLocked;
        public double Idle = 999;
        public WifiConnection? Network;
        public string? NetworkProblem;
        public bool LockFails;
        public int LocksAttempted;
        public int ScannerRestarts;

        public Reading<bool> IsScreenLocked() => new(ScreenLocked);
        public Reading<double> IdleSeconds() => new(Idle);

        public Reading<WifiConnection?> CurrentNetwork() =>
            NetworkProblem is null
                ? new Reading<WifiConnection?>(Network)
                : Reading<WifiConnection?>.Failed(null, NetworkProblem);

        public Reading<bool> LockScreen()
        {
            LocksAttempted++;
            return LockFails
                ? Reading<bool>.Failed(false, "LockWorkStation failed (error 5)")
                : new Reading<bool>(true);
        }

        public void RestartScanner() => ScannerRestarts++;
    }

    sealed class FakeView : IWatcherView
    {
        public int CountdownShown;
        public int? LastCountdown;
        public bool CountdownVisible;
        public readonly List<string> Statuses = new();
        public int Warnings;

        public void ShowCountdown(int secondsLeft)
        {
            CountdownShown++;
            LastCountdown = secondsLeft;
            CountdownVisible = true;
        }

        public void HideCountdown() => CountdownVisible = false;

        public void ShowStatus(WatchIcon icon, Decision decision, int? rssi) =>
            Statuses.Add($"{icon}/{decision.LabelKey}");

        public void WarnNoSignal(int minutes) => Warnings++;

        /// <summary>
        /// Runs while the loop is between deciding to lock and locking - which
        /// is where a Windows notification and the menu rebuild really sit, and
        /// on 20.08.2026 they took six seconds. Used to let the phone come back
        /// in that gap.
        /// </summary>
        public Action? DuringRefresh;

        public void RefreshMenu()
        {
            var once = DuringRefresh;
            DuringRefresh = null;
            once?.Invoke();
        }

        public string Last => Statuses.Count > 0 ? Statuses[^1] : "none";
    }

    static void CheckSleepGap(string scratch)
    {
        Console.WriteLine("\nA gap in the loop (the machine slept):");
        var r = new Rig(scratch, "sleep") { RoomKeepsChattering = true };
        r.Hear();
        r.Tick();
        Check("watching, phone at the desk", "Ok/st_at_desk", r.View.Last);

        // Hibernation: no tick for eleven minutes. The silence that piled up
        // says nothing about where the phone is - measured 20.08.2026, this
        // produced "Locking (silence 0 s)" the moment the lid opened.
        //
        // The room is kept noisy ACROSS the gap on purpose. Without it the
        // deaf-radio guard would hold the lock off and restart the measurement
        // all by itself, so the test would pass with the stall check ripped out
        // - which is exactly what a sabotage run showed.
        r.Clock += 660;
        r.RoomIsNoisy();
        r.Loop.Tick();
        Check("the gap is recognised as such", true,
            r.LogText.Contains("The loop stood still"));
        Check("...nothing was locked", false,
            r.View.Statuses.Any(s => s.Contains("st_locked")));
        Check("...and the measurement starts over", 0.0, r.Watch.Silence());

        // And a phone that really is gone still locks - after the full delay,
        // with a countdown. A full reset would have switched watching off
        // instead, leaving the machine open.
        for (int i = 0; i < 48; i++) r.Tick();      // 24 s, past the 20 s threshold
        Check("a countdown came first", true, r.View.CountdownShown > 0);
        Check("...and then it locked", true,
            r.View.Statuses.Any(s => s.Contains("st_locked")));
        Check("...without really locking anyone's screen", 0, r.System.LocksAttempted);
    }

    static void CheckLockScreenPause(string scratch)
    {
        Console.WriteLine("\nBehind the lock screen:");
        var r = new Rig(scratch, "locked");
        r.Hear();
        r.Tick();

        r.System.ScreenLocked = true;
        for (int i = 0; i < 100; i++) r.Tick();     // 50 s of silence
        Check("nothing is locked behind a locked screen", false,
            r.View.Statuses.Skip(1).Any(s => s.Contains("st_locked")));
        Check("...and the status says why", "Off/st_screen_locked", r.View.Last);

        // Unlocking must restart the measurement, or the screen would lock
        // again the moment the user finished typing the PIN.
        r.System.ScreenLocked = false;
        r.Tick();
        Check("unlocking starts the silence over", true, r.Watch.Silence() < 1);
        Check("...so it does not lock straight away", "Ok/st_at_desk", r.View.Last);
    }

    static void CheckReturnFromNotWatching(string scratch)
    {
        Console.WriteLine("\nComing back from 'not watching':");
        var r = new Rig(scratch, "notwatching");
        r.Hear();
        r.Tick();

        r.Cfg.Active = false;
        for (int i = 0; i < 100; i++) r.Tick();     // 50 s with the switch off
        Check("switched off, nothing happens", "Off/st_off", r.View.Last);

        // Reported by David 11.09.2026: the silence kept counting while nothing
        // was watched, so the first tick after switching back on found it long
        // past the threshold and locked at once, with no countdown.
        r.Cfg.Active = true;
        r.Tick();
        Check("switching back on starts the silence over", true, r.Watch.Silence() < 1);
        Check("...so there is no instant lock", "Ok/st_at_desk", r.View.Last);
    }

    static void CheckLockIsConfirmed(string scratch)
    {
        Console.WriteLine("\nThe decision is checked again just before locking:");
        var r = new Rig(scratch, "confirm") { RoomKeepsChattering = true };
        r.Hear();
        for (int i = 0; i < 60; i++) r.Tick();      // 30 s: past the threshold
        Check("it locked", true, r.View.Statuses.Any(s => s.Contains("st_locked")));

        // Between the first decision and the lock sit a notification and the
        // menu rebuild; on 20.08.2026 that took six seconds and the phone was
        // back before the screen locked. The log read "Locking (silence 0 s)".
        var again = new Rig(scratch, "confirm2") { RoomKeepsChattering = true };
        again.Hear();
        for (int i = 0; i < 39; i++) again.Tick();  // just short of locking
        again.View.Statuses.Clear();

        // The phone speaks up DURING the tick that decided to lock, not before
        // it - that is the whole point, and the only way this branch is ever
        // reached. Handing it to the tick beforehand would just be an ordinary
        // decision, which a sabotage run showed passes with the second decision
        // ripped out entirely.
        again.View.DuringRefresh = () => again.Hear();
        again.Tick();
        Check("a phone that came back is not locked out", false,
            again.View.Statuses.Any(s => s.Contains("st_locked")));
        Check("...and the log says the lock was called off", true,
            again.LogText.Contains("Locking called off"));
    }

    static void CheckDeafRadio(string scratch)
    {
        Console.WriteLine("\nA silence the radio cannot vouch for:");
        var r = new Rig(scratch, "deaf");
        r.Hear();                                   // heard once, then nothing
        for (int i = 0; i < 60; i++) r.Tick();

        // The radio heard nothing from anyone, which is a deaf scanner rather
        // than a phone that walked away.
        Check("the lock is held off", false,
            r.View.Statuses.Any(s => s.Contains("st_locked")));
        Check("...and the scanner is restarted", true, r.System.ScannerRestarts >= 1);

        // But not for ever: a room really can be empty.
        for (int i = 0; i < 300; i++) r.Tick();
        Check("after the allowance runs out it locks anyway", true,
            r.View.Statuses.Any(s => s.Contains("st_locked")));
        Check("...having held off no more than twice", true,
            r.System.ScannerRestarts <= DeafRadioGuard.HoldOffMax);
    }

    static void CheckFailedLock(string scratch)
    {
        Console.WriteLine("\nWhen locking fails:");
        // Not a dry run here - the point is what happens when the lock is
        // refused, and it is the fake system that refuses it, so no real screen
        // is touched either way.
        var r = new Rig(scratch, "lockfail", dryRun: false) { RoomKeepsChattering = true };
        r.System.LockFails = true;
        r.Hear();
        for (int i = 0; i < 60; i++) r.Tick();

        Check("it tried to lock", true, r.System.LocksAttempted >= 1);
        Check("...said so", "Off/st_lock_failed", r.View.Last);
        // Left armed on purpose, so the next tick tries again. In 1.4 the app
        // recorded the lock, disarmed itself, and sat waiting for the phone
        // with the desktop in plain view.
        Check("...and stayed armed, so it tries again", true, r.Watch.Armed);
        Check("...which it did", true, r.System.LocksAttempted > 1);
        // The attempt repeats every tick; the complaint must not. Two lines a
        // second would bury the very log this gets diagnosed from.
        int complaints = r.LogText.Split("LockWorkStation failed").Length - 1;
        Check("...but complained only once in the first minute", 1, complaints);
    }

    static void CheckNoSignalWarning(string scratch)
    {
        Console.WriteLine("\nWarning that the phone has gone missing:");
        var r = new Rig(scratch, "warn");
        r.Cfg.AlertNoSignalMinutes = 1;
        r.Cfg.SilenceSeconds = 3600;        // do not let it lock during this
        r.Hear();
        for (int i = 0; i < 130; i++) r.Tick();     // 65 s of silence
        Check("warned once", 1, r.View.Warnings);
        for (int i = 0; i < 60; i++) r.Tick();
        Check("...and only once", 1, r.View.Warnings);

        r.Hear();
        r.Tick();
        for (int i = 0; i < 130; i++) r.Tick();
        Check("a phone that goes missing again warns again", 2, r.View.Warnings);
    }

    static void CheckTrustedNetwork(string scratch)
    {
        Console.WriteLine("\nNetworks where nothing is locked:");
        var r = new Rig(scratch, "wifi");
        r.Cfg.TrustedNetworkPause = true;
        r.Cfg.TrustedNetworks.Add(new TrustedNetwork
            { Ssid = "Home", Bssid = "AA:BB:CC:DD:EE:FF" });
        r.RoomIsNoisy();
        r.Hear();

        r.System.Network = new WifiConnection("Home", "AA:BB:CC:DD:EE:FF");
        for (int i = 0; i < 60; i++) r.Tick();
        Check("on a saved network nothing locks", "Off/st_trusted_network", r.View.Last);

        // The name on its own is trivially forged - call a hotspot after the
        // user's home network and protection would switch itself off. This is
        // the case that must NOT pass.
        r.System.Network = new WifiConnection("Home", "11:22:33:44:55:66");
        r.Tick();
        Check("the same name from another access point is not the same network",
            true, r.View.Last != "Off/st_trusted_network");

        // A network that cannot be read is not trusted, so a failure can only
        // ever lead to more locking.
        r.System.Network = new WifiConnection("Home", "AA:BB:CC:DD:EE:FF");
        r.System.NetworkProblem = "wlanapi is not present";
        r.Tick();
        Check("an unreadable network is not trusted either", true,
            r.View.Last != "Off/st_trusted_network");
    }

    // --------------------------------------------------- the chart's history

    static void CheckHistoryStore(string scratch)
    {
        Console.WriteLine("\nThe chart history on disk:");
        string path = Path.Combine(scratch, "history.json");

        // The history works in monotonic seconds - seconds since the machine
        // started - while the file keeps wall clock times. Both clocks are
        // supplied here, so the conversion can be checked rather than hoped for.
        double mono = 5_000;
        double wall = 1_700_000_000;

        var history = new SignalHistory();
        history.Add(mono - 120, -70);
        history.Add(mono - 60, -55);
        history.Locked(mono - 30);
        history.NotRunning(mono - 600, mono - 500);

        Check("saving reports no problem", null,
            HistoryStore.Save(history, path, mono, wall));
        Check("...and the file is there", true, File.Exists(path));

        // Read back into a DIFFERENT run: the machine has been up longer, so
        // the same moments are different monotonic seconds now. The readings
        // must keep their distance from "now", not their raw numbers.
        var reread = new SignalHistory();
        double laterMono = 900_000;             // a much longer uptime
        double laterWall = wall + 60;           // one minute later by the clock
        var (notRunning, problem) = HistoryStore.Load(reread, path, laterMono, laterWall);

        Check("reading it back reports no problem", null, problem);
        Check("...with both readings", 2, reread.Count);
        Check("...and the lock", 1, reread.Locks().Count);
        Check("a reading keeps its age, not its number", 180.0,
            Math.Round(laterMono - reread.Samples()[0].At));
        Check("...and its strength", -70, reread.Samples()[0].Rssi);
        Check("the minute the app was not running is noticed", 1.0,
            notRunning is double m ? Math.Round(m) : null);
        Check("...and drawn as a gap", 2, reread.Downtimes().Count);

        // A history older than the window the chart covers is not worth
        // restoring - it would draw a day that is already off the left edge.
        var ancient = new SignalHistory();
        var (_, stale) = HistoryStore.Load(ancient, path, laterMono,
            wall + SignalHistory.LengthSeconds + 60);
        Check("everything past the window is left behind", 0, ancient.Count);
        Check("...quietly, because that is not a fault", null, stale);

        // Valid JSON of the wrong shape. 1.5 unpacked it outside its guard and
        // then would not start at all - a lost chart is worth losing, a program
        // that will not run is not.
        File.WriteAllText(path, """
            {"until": 1, "samples": [[1], "nonsense", [2, 3]], "locks": "not a list"}
            """);
        var damaged = new SignalHistory();
        var (_, complaint) = HistoryStore.Load(damaged, path, laterMono, laterWall);
        Check("a damaged file is reported, not thrown", true, complaint is not null);
        Check("...and the app carries on with an empty chart", 0, damaged.Count);
    }
}
