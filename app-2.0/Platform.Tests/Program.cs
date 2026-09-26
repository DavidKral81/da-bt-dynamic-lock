using System.Text.RegularExpressions;
using DaBtDynamicLock.Core;
using DaBtDynamicLock.Platform;

// Checks the layer that talks to Windows, against the real machine. Same shape
// as Core.Tests and tests/test_logic.py: OK/FAIL per case, a summary, an exit
// code.
//
// What these cannot do is run over made-up data - a structure laid out wrongly
// is only caught by comparing what Windows returns against something already
// known. So where a case depends on the surroundings (Wi-Fi, other BLE devices)
// it says SKIP out loud rather than passing quietly: a test that quietly
// assumes "the developer is sitting there" once sent an hour after a regression
// that did not exist.
//
// It deliberately never calls ScreenLock.Lock() - that would lock the screen of
// whoever is running it.

internal static class PlatformChecks
{
    static readonly List<string> Failures = new();
    static int _skipped;

    static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        CheckSession();
        CheckIdle();
        CheckFullScreen();
        CheckWifi();
        CheckMonitors();
        CheckAddressFormatting();
        CheckAutostart();
        await CheckScannerAsync();

        Console.WriteLine();
        if (_skipped > 0)
            Console.WriteLine($"({_skipped} case(s) skipped - see above for why)");
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

    static void Skip(string what, string why)
    {
        Console.WriteLine($"  SKIP  {what}: {why}");
        _skipped++;
    }

    /// <summary>Did this run without throwing?</summary>
    static bool Quiet(Action what)
    {
        try { what(); return true; }
        catch { return false; }
    }

    // ------------------------------------------------------------ autostart

    static void CheckAutostart()
    {
        Console.WriteLine("\nStart at logon:");

        string scratch = Path.Combine(Path.GetTempPath(),
            "ddl-autostart-" + Guid.NewGuid().ToString("N")[..8]);
        var target = new AutostartTarget(
            TaskName: "DaBtDynamicLock Check " + Guid.NewGuid().ToString("N")[..8],
            Program: Environment.ProcessPath ?? "C:\\nothing.exe",
            Arguments: "--dry-run",
            WorkingDirectory: AppContext.BaseDirectory,
            ShortcutPath: Path.Combine(scratch, "check.lnk"),
            ScratchFolder: scratch);

        // The XML, checked without going near Task Scheduler. These two settings
        // are the entire reason it is XML and not a plain schtasks command:
        // without them Windows stops the task after three days and never
        // restarts it after a crash.
        var registeredAt = new DateTime(2026, 9, 26, 12, 0, 0);
        string xml = Autostart.TaskXml(target, "DOMAIN\\Someone", registeredAt);
        Check("the task has no time limit", true,
            xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"));
        Check("...and restarts after a crash", true,
            xml.Contains("<RestartOnFailure>"));
        Check("...and starts the program it was given", true,
            xml.Contains($"<Command>{target.Program}</Command>"));

        // Restarting after a crash is NOT what RestartOnFailure does - measured
        // 19.09.2026, after the app died and was never put back: Task Scheduler
        // recorded the failing exit code (0xC00001AD) and still did nothing,
        // because that setting covers a task that cannot be STARTED. A repeat
        // is what actually brings a crashed app back.
        Check("the task repeats, so a crash cannot leave the computer unwatched", true,
            xml.Contains("<Interval>PT5M</Interval>"));
        Check("...without piling up copies when one is already running", true,
            xml.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>"));
        // A repeat with an end would stop watching after that long.
        Check("...and the repeat never runs out", false,
            xml.Contains("<Duration>"));

        // ⚠ WHERE the repeat hangs matters more than that it exists. Measured
        // 26.09.2026: a repeat on the logon trigger only starts when that
        // trigger fires, at the next logon - and the task is registered AFTER
        // logon, by the installer or the switch. So after every install there
        // was no repeat until the user signed in again: the app was killed and
        // stayed dead for an hour and a half, with no next run time in Task
        // Scheduler. A time trigger repeats from the moment it is registered.
        var mit = System.Xml.Linq.XNamespace.Get(
            "http://schemas.microsoft.com/windows/2004/02/mit/task");
        var triggers = System.Xml.Linq.XDocument.Parse(xml).Root!.Element(mit + "Triggers")!;
        Check("the repeat hangs on a time trigger, not on the logon", "PT5M",
            (string?)triggers.Element(mit + "TimeTrigger")
                ?.Element(mit + "Repetition")?.Element(mit + "Interval"));
        Check("...the logon trigger does not repeat on its own", null,
            triggers.Element(mit + "LogonTrigger")?.Element(mit + "Repetition")?.Name.LocalName);
        Check("...and the app still starts at logon", true,
            triggers.Element(mit + "LogonTrigger") is not null);
        // Not at once: an immediate first repeat would race the installer
        // starting the app, and the loser would stop on the single-copy lock.
        Check("...the repeat starts one interval after registering", "2026-09-26T12:05:00",
            (string?)triggers.Element(mit + "TimeTrigger")?.Element(mit + "StartBoundary"));

        // Valid XML is not a detail here: Task Scheduler rejects the whole file
        // over one duplicated element, and the task would simply not exist.
        Check("the task is valid XML", true,
            Quiet(() => System.Xml.Linq.XDocument.Parse(xml)));
        Check("...with one instance policy, not two", 1,
            xml.Split("<MultipleInstancesPolicy>").Length - 1);

        // A path with an ampersand in it is valid on Windows and would tear the
        // XML in half unescaped. Task Scheduler would reject the lot.
        string awkward = Autostart.TaskXml(target with { Program = "C:\\a & b.exe" },
            "DOMAIN\\Someone", registeredAt);
        Check("a path with & in it stays valid XML", true,
            awkward.Contains("C:\\a &amp; b.exe"));

        Check("the account is DOMAIN\\user", true,
            Autostart.CurrentUser().EndsWith(Environment.UserName,
                StringComparison.OrdinalIgnoreCase));

        // A task nobody created is reported as absent. Reading only - this
        // never writes to Task Scheduler.
        Check("a task that was never made is not found", false,
            Autostart.Enabled(target, refresh: true));

        // The FALLBACK branch, run for real. In the shipped version this half
        // carried an error for three weeks precisely because it only runs when
        // Task Scheduler refuses - written into a scratch folder, never into
        // the real Startup folder.
        try
        {
            string? problem = Autostart.WriteShortcut(target);
            Check("the Startup shortcut can be written", null, problem);
            Check("...and it is really there", true, File.Exists(target.ShortcutPath));
            Check("...and that makes autostart count as on", true,
                Autostart.Enabled(target, refresh: true));

            File.Delete(target.ShortcutPath);
            Check("...and removing it turns autostart off again", false,
                Autostart.Enabled(target, refresh: true));
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }

        // Deliberately not run: creating and deleting a real scheduled task.
        // Writing to Task Scheduler is the installer's business on this project,
        // and a test that leaves a logon task behind is worse than no test.
        Skip("registering a real task", "this project writes to Task Scheduler "
            + "only from the app itself, on purpose - switch it on in the "
            + "settings window to check that half");
    }

    // ------------------------------------------------------------ session

    static void CheckSession()
    {
        Console.WriteLine("Session state (WTS):");
        var locked = SessionState.IsLocked();
        Check("the query succeeds", true, locked.Ok);

        // ⚠ SKIPPED, not failed, when the screen really is locked. The answer
        // is then RIGHT and the test has nothing to compare it against - it
        // can only tell a working reading from a broken one while somebody is
        // looking at the screen. Reported as a failure (as it was until
        // 20.09.2026, when a run with the machine locked did exactly this), it
        // reads like a regression in the very layer being checked, and this
        // project has already lost an hour to that once.
        if (locked.Ok && locked.Value)
            Skip("whether the screen reads as unlocked",
                "the screen IS locked just now, so there is nothing to compare against");
        else
            Check("and says the screen is not locked", false, locked.Value);

        // The layout is what breaks silently: a shifted field returns
        // plausible-looking rubbish instead of an error. Compared against the
        // account this runs under - no name is written down here. A weaker check
        // on the first string alone once passed with a deliberately wrong
        // length, because the padding absorbed it.
        var user = SessionState.SessionUserName();
        Check("the user name comes back", true, user.Ok);
        Check("...and matches the account this runs under", true,
            user.Value.Length > 0 && string.Equals(user.Value, Environment.UserName,
                StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------ idle

    static void CheckIdle()
    {
        Console.WriteLine("\nIdle time:");
        var idle = UserIdle.Seconds();
        Check("the query succeeds", true, idle.Ok);
        Check("and gives a number that is not the stand-in", true,
            idle.Value >= 0 && Math.Abs(idle.Value - UserIdle.UnknownSeconds) > 0.001);
        // Anything above a few hours means the wrap-around arithmetic is wrong.
        Check("...and is not absurd", true, idle.Value < 24 * 3600);
        Console.WriteLine($"        (idle for {idle.Value:F1} s)");
    }

    // ------------------------------------------------------ full screen

    /// <summary>
    /// The guard that holds the lock off while something fills the screen.
    ///
    /// Asked of the REAL machine, because that is the half nothing else
    /// covers: Core.Tests proves the decision over made-up data and
    /// Engine.Tests proves the wiring, but neither can say whether Windows
    /// answers this question at all on this computer. If it did not, the
    /// setting would be switched on and quietly do nothing.
    ///
    /// What is NOT checked is the answer itself - whether something is full
    /// screen right now depends on what is on screen, and a test cannot put a
    /// film there. The answer is printed so a person can see it.
    /// </summary>
    static void CheckFullScreen()
    {
        Console.WriteLine("\nSomething filling the screen:");
        var reading = FullScreenApp.Running();
        Check("Windows answers the question", true, reading.Ok);
        if (reading.Problem is not null)
            Console.WriteLine($"        ({reading.Problem})");
        Console.WriteLine($"        (right now it says: "
            + $"{(reading.Value ? "yes, something is full screen" : "no")})");
    }

    // ------------------------------------------------------------ Wi-Fi

    static void CheckWifi()
    {
        Console.WriteLine("\nThe wireless network in use:");
        var wifi = WifiNetwork.Current();
        Check("the query succeeds", true, wifi.Ok);
        if (!wifi.Ok)
        {
            Console.WriteLine($"        ({wifi.Problem})");
            return;
        }
        if (wifi.Value is null)
        {
            Skip("the shape of what came back",
                "this machine is not on Wi-Fi right now");
            return;
        }
        // Neither the name nor the address is printed: they are kept out of the
        // log on purpose, and a test's output is no different.
        Check("the name is not empty", true, wifi.Value.Ssid.Length > 0);
        Check("...and at most 32 characters", true, wifi.Value.Ssid.Length <= 32);
        Check("the access point's address is six hex pairs", true,
            Regex.IsMatch(wifi.Value.Bssid, "^([0-9A-F]{2}:){5}[0-9A-F]{2}$"));
        Check("...and is not all zeroes or all ones", true,
            wifi.Value.Bssid is not ("00:00:00:00:00:00" or "FF:FF:FF:FF:FF:FF"));

        // The layout is the part that fails silently, and the checks above
        // cannot see it: they would pass on a field read from the wrong offset
        // just as happily. So the name is compared against a SECOND, unrelated
        // source - WinRT, which gives the SSID (and nothing else, which is why
        // the app cannot use it). Two ways of asking that disagree mean the
        // structure has shifted.
        string? viaWinRt = SsidThroughWinRt();
        if (viaWinRt is null)
            Skip("the name against a second source", "WinRT gave no WLAN profile");
        else
            Check("the name matches what WinRT reports", true, viaWinRt == wifi.Value.Ssid);
    }

    /// <summary>
    /// The connected network's name the managed way. Only ever used here, as
    /// the second opinion above: WinRT has no BSSID, so the app itself cannot
    /// be built on it (measured 13.09.2026 - GetConnectedSsid is the whole of
    /// WlanConnectionProfileDetails).
    /// </summary>
    static string? SsidThroughWinRt()
    {
        try
        {
            var details = Windows.Networking.Connectivity.NetworkInformation
                .GetInternetConnectionProfile()?.WlanConnectionProfileDetails;
            string? ssid = details?.GetConnectedSsid();
            return string.IsNullOrEmpty(ssid) ? null : ssid;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------ monitors

    static void CheckMonitors()
    {
        Console.WriteLine("\nMonitors:");
        var screens = Monitors.WorkAreas();
        Check("the enumeration succeeds", true, screens.Ok);
        Check("at least one monitor", true, screens.Value.Count >= 1);
        Check("exactly one of them is the primary", 1, screens.Value.Count(m => m.Primary));
        Check("every work area has a positive size", true,
            screens.Value.All(m => m.Width > 0 && m.Height > 0));
        Console.WriteLine($"        ({screens.Value.Count} monitor(s); "
            + string.Join(", ", screens.Value.Select(m => $"{m.Width}x{m.Height}")) + ")");

        // Ordering cannot be checked on one monitor: the only entry is first
        // whatever the sort does. Measured 13.09.2026 - reversing the sort left
        // the run green, so saying SKIP here is the honest answer. A check that
        // cannot fail looks exactly like one that works.
        if (screens.Value.Count < 2)
        {
            Skip("the primary monitor coming first",
                "only one monitor is attached, so any order passes");
            Skip("a monitor left of the primary one (negative x)",
                "only one monitor is attached");
        }
        else
        {
            Check("the primary one comes first", true, screens.Value[0].Primary);
            Console.WriteLine("        (leftmost x = "
                + $"{screens.Value.Min(m => m.Left)}; negative is normal and is the "
                + "case Tk once got wrong)");
        }
    }

    // ------------------------------------------------------------ addresses

    static void CheckAddressFormatting()
    {
        Console.WriteLine("\nAddresses from WinRT:");
        // WinRT hands the address over as a number; the rest of the app speaks
        // the colon form, which is also what a hand-typed target looks like.
        Check("a known number formats the usual way", "AA:BB:CC:DD:EE:FF",
            BleScanner.FormatAddress(0xAABBCCDDEEFFUL));
        Check("leading zeroes are kept", "00:11:22:33:44:55",
            BleScanner.FormatAddress(0x001122334455UL));
        // And the result has to be something DeviceMatch accepts as a MAC
        // target - otherwise a target typed as a MAC would never match.
        Check("...and matches itself as a target", true,
            DeviceMatch.Matches(BleScanner.FormatAddress(0xAABBCCDDEEFFUL), null, null,
                "aa:bb:cc:dd:ee:ff"));
    }

    // ------------------------------------------------------------ scanner

    static async Task CheckScannerAsync()
    {
        Console.WriteLine("\nThe BLE scanner, against the real radio:");
        var log = new List<string>();
        var watch = new PhoneWatch();
        var scanner = new BleScanner(watch,
            new ScannerSettings
            {
                // Nothing is being watched here - this only checks that the
                // radio is heard at all, which is what RecordHeard counts.
                Target = () => "",
                Watch = () => new WatchSettings(),
            },
            line => { lock (log) log.Add(line); });

        using var stopping = new CancellationTokenSource();
        var run = scanner.RunAsync(stopping.Token);
        await Task.Delay(TimeSpan.FromSeconds(8));
        stopping.Cancel();
        await run;      // must finish - a scanner left running keeps consuming

        var (adverts, devices) = watch.HeardRecently(60);
        Check("the scanner started and stopped cleanly", true, run.IsCompletedSuccessfully);
        lock (log)
        {
            Check("it said it started", true, log.Any(l => l.Contains("Scanner started")));
            Check("...and nothing failed", false,
                log.Any(l => l.Contains("failed") || l.Contains("could not be stopped")));
            foreach (var line in log) Console.WriteLine($"        log: {line}");
        }

        if (adverts == 0)
            Skip("hearing anything on the air",
                "no BLE device advertised in 8 s - nothing to hear, which the "
                + "radio cannot be blamed for");
        else
            Check("advertisements were counted, with their devices", true,
                adverts > 0 && devices > 0);
        Console.WriteLine($"        ({adverts} advertisements from {devices} devices in 8 s)");

        await CheckHistoryIsFilledAsync(watch);
    }

    /// <summary>
    /// Readings from the WATCHED device have to reach the chart's history - the
    /// one line that connects the radio to the chart, and the only place it can
    /// be checked is against a real radio.
    ///
    /// Whatever was heard a moment ago is used as the target. Its name is never
    /// printed: a test's output gets pasted into reports, and somebody's devices
    /// have no business being in one.
    /// </summary>
    static async Task CheckHistoryIsFilledAsync(PhoneWatch heardSoFar)
    {
        var nearby = heardSoFar.NearbyList();
        if (nearby.Count == 0)
        {
            Skip("readings reaching the chart history",
                "no NAMED device was heard just now, so there is nothing to point "
                + "the watcher at");
            return;
        }

        string target = nearby[0].Name;
        var history = new SignalHistory();
        var watch = new PhoneWatch();
        var scanner = new BleScanner(watch,
            new ScannerSettings
            {
                Target = () => target,
                Watch = () => new WatchSettings(),
            },
            _ => { }, history: history);

        using var stopping = new CancellationTokenSource();
        var run = scanner.RunAsync(stopping.Token);
        await Task.Delay(TimeSpan.FromSeconds(8));
        stopping.Cancel();
        await run;

        if (history.Count == 0)
            Skip("readings reaching the chart history",
                "the device heard a moment ago went quiet during this pass - it "
                + "may have stopped advertising or moved out of range");
        else
            Check("readings from the watched device reach the chart history", true,
                history.Count > 0 && history.Samples()[0].Rssi < 0);
        Console.WriteLine($"        ({history.Count} readings recorded in 8 s)");
    }
}
