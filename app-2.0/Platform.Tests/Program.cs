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
        CheckWifi();
        CheckMonitors();
        CheckAddressFormatting();
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

    // ------------------------------------------------------------ session

    static void CheckSession()
    {
        Console.WriteLine("Session state (WTS):");
        var locked = SessionState.IsLocked();
        Check("the query succeeds", true, locked.Ok);
        // Whoever runs this is looking at the screen, so it cannot be locked.
        // If this ever fails on a machine where it IS locked, that is the answer
        // being right, not the test.
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
    }
}
