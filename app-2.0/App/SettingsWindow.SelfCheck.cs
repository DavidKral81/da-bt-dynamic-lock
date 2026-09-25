using DaBtDynamicLock.Engine;
using DaBtDynamicLock.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DaBtDynamicLock.App;

/// <summary>
/// Works the settings window's controls the way a person would and checks that
/// each one reaches the settings FILE.
///
/// This exists because the pictures cannot answer the question that matters. A
/// screenshot proves a switch is drawn; it says nothing about whether flipping
/// it saves anything - and 1.4 shipped exactly that fault, a switch that moved
/// while the file did not.
///
/// Controls are driven the same way a click drives them (IsOn, IsChecked,
/// SelectedItem), so the real handlers run. Calling the handlers directly would
/// test the methods and skip the wiring, which is the half that actually breaks.
/// </summary>
public sealed partial class SettingsWindow
{
    /// <summary>Runs the checks. Returns one line per check, "OK" or "FAIL".</summary>
    /// <remarks>
    /// EVERY value tried below has to differ from the default the file is reset
    /// to first. That is not tidiness - it is the whole check. Without the
    /// reset, the file still held the previous run's answers, so "it was saved"
    /// passed even with the save taken out of the handler; the first sabotage
    /// run went green and proved the check was worthless.
    /// </remarks>
    internal IReadOnlyList<string> SelfCheck(string settingsPath)
    {
        var lines = new List<string>();

        void Check(string what, object? wanted, Func<Settings, object?> read)
        {
            // Read back from DISK, not from the settings object in memory: the
            // point of the check is that the write happened, and an object the
            // handler just changed would say yes either way.
            var (saved, problem) = Settings.Load(settingsPath);
            object? got = problem is null ? read(saved) : problem;
            bool ok = Equals(wanted, got);
            lines.Add(ok
                ? $"  OK    {what}"
                : $"  FAIL  {what}: wanted {Show(wanted)}, saved {Show(got)}");
        }

        // ---- switches ----------------------------------------------------
        Active.IsOn = false;
        Check("switching watching off is saved", false, s => s.Active);
        Active.IsOn = true;
        Check("switching watching on is saved", true, s => s.Active);

        IdleGuard.IsOn = true;
        Check("the typing safeguard is saved", true, s => s.IdleGuard);

        // The second half of "do not lock while...", and a separate switch on
        // purpose: a film runs for hours without a keystroke, so the two are
        // different situations rather than one setting with two names.
        FullScreenGuard.IsOn = true;
        Check("the full screen safeguard is saved", true, s => s.FullScreenGuard);
        FullScreenGuard.IsOn = false;
        Check("...and switching it off is saved too", false, s => s.FullScreenGuard);

        PrimaryOnly.IsOn = true;
        Check("countdown on the main monitor only is saved", true,
            s => s.CountdownPrimaryOnly);

        LogOn.IsOn = false;
        Check("switching the log off is saved", false, s => s.Log);
        LogOn.IsOn = true;

        TrustedOn.IsOn = true;
        Check("the Wi-Fi exception switch is saved", true, s => s.TrustedNetworkPause);

        // Start at logon is deliberately NOT switched on for real here: this
        // run shares the machine with the installed copy, and a logon task left
        // behind would start a test build every morning. What is checked is
        // that the switch refuses and puts ITSELF back - a switch that stays on
        // while nothing was registered is the app lying about being armed.
        AutostartOn.IsOn = true;
        lines.Add(AutostartOn.IsOn == false
            ? "  OK    a test run refuses to touch Task Scheduler, and the "
                + "switch goes back"
            : "  FAIL  the autostart switch stayed on in a test run, so "
                + "something was written to Task Scheduler");

        // ---- drop-downs --------------------------------------------------
        Pick(Silence, 90);
        Check("the silence before locking is saved", 90.0, s => s.SilenceSeconds);

        // The threshold first, "no limit" second - and that order matters. No
        // limit IS the default, so checking it first would pass on a file that
        // was never written to.
        Pick(Range, -80);
        Check("a sensitivity threshold is saved", -80.0, s => s.RssiThreshold);
        Pick(Range, null);
        Check("\"no limit\" saves no threshold at all", null, s => s.RssiThreshold);

        // 0 is not a number of seconds, it is "do not show" - the two settings
        // behind one list are what keeps the file readable by 1.5.
        Pick(CountdownFrom, 0);
        Check("\"do not show\" switches the countdown off", false, s => s.Countdown);
        Pick(CountdownFrom, 20);
        Check("choosing a countdown switches it back on", true, s => s.Countdown);
        Check("...and saves the seconds", 20, s => s.CountdownFromSeconds);

        Pick(Position, 70);
        Check("the countdown position is saved as a fraction", 0.70,
            s => Math.Round(s.CountdownVertical, 2));

        Pick(WarnAfter, 30);
        Check("the warning delay is saved", 30.0, s => s.AlertNoSignalMinutes);

        // ---- the lists ---------------------------------------------------
        var devices = DeviceList.Children.OfType<RadioButton>().ToList();
        if (devices.Count < 2)
        {
            lines.Add("  FAIL  the device list should offer the made-up devices, "
                + $"but it holds {devices.Count} row(s)");
        }
        else
        {
            string wanted = (string)devices[1].Tag;
            devices[1].IsChecked = true;
            Check("picking a device is saved", wanted, s => s.Target);
        }

        var networks = NetworkList.Children.OfType<CheckBox>().ToList();
        if (networks.Count == 0)
        {
            lines.Add("  FAIL  the network list should hold the made-up network, "
                + "but it is empty");
        }
        else
        {
            var network = (TrustedNetwork)networks[0].Tag;
            networks[0].IsChecked = true;
            Check("ticking a network saves it", true,
                s => s.TrustedNetworks.Any(n => n.Ssid == network.Ssid
                    && n.Bssid == network.Bssid));
            networks[0].IsChecked = false;
            Check("unticking it forgets it", false,
                s => s.TrustedNetworks.Any(n => n.Ssid == network.Ssid
                    && n.Bssid == network.Bssid));
        }

        // ---- the pause -----------------------------------------------------
        // Not a setting in the file, so this looks at what the app is actually
        // doing: a pause that only moved a drop-down would leave the screen
        // guarded while the card says it is paused.
        Pick(Pause, 15);
        double left = _host.Watch.PauseLeft;
        lines.Add(left > 14 * 60 && left <= 15 * 60
            ? "  OK    choosing a pause really pauses watching"
            : $"  FAIL  choosing 15 minutes left {left:F0} s of pause");

        Pick(Pause, 0);
        lines.Add(_host.Watch.PauseLeft == 0
            ? "  OK    ...and \"not paused\" ends it"
            : $"  FAIL  ending the pause left {_host.Watch.PauseLeft:F0} s of it");

        // ---- pages and language -----------------------------------------
        // Chosen by its key, never by its position: the check used to set index
        // 4, and adding the chart page in between moved "Application" to 5. It
        // then failed as a change in the window, which is exactly the wrong
        // place to look - nothing about that window was broken.
        SelectPage("app");
        lines.Add(PageApp.Visibility == Visibility.Visible
                && PageSignal.Visibility == Visibility.Collapsed
            ? "  OK    choosing a topic shows that page and hides the others"
            : "  FAIL  choosing a topic did not switch the page");
        SelectPage("signal");

        // Now an ordinary drop-down, so it is worked the way every other choice
        // here is: setting the item raises the same event a person's click
        // does. Stronger than what this used to do, which was call the handler
        // directly because a flag has no property to set.
        string before = NavSignal.Text;
        SelectPage("app");
        LanguageChoice.SelectedItem = Texts.Languages.First(l => l.Code == "en");
        lines.Add(NavSignal.Text != before && Texts.Language == "en"
            ? "  OK    the flag switches the language and redraws the labels"
            : $"  FAIL  the language did not redraw: \"{before}\" -> "
                + $"\"{NavSignal.Text}\" ({Texts.Language})");
        Check("the chosen language is saved", "en", s => s.Language);

        LanguageChoice.SelectedItem = Texts.Languages.First(l => l.Code == "cs");

        // ---- which role this copy is in -------------------------------------
        //
        // What a person does with a downloaded file is double-click it, with no
        // switch at all, so a copy running from anywhere but its installed home
        // offers to install itself. If this breaks, that double-click starts
        // the application instead of the installer and nothing looks wrong.
        //
        // Checked here because it cannot be checked by running: starting the
        // real setup asks for administrator rights and offers to install.
        const string installed = @"C:\Program Files\Da BT Dynamic Lock";

        void Role(string what, string from, string[] argv, SetupRole? wanted)
        {
            var got = Options.Parse(argv, from, installed).Setup;
            lines.Add(got == wanted
                ? $"  OK    {what}"
                : $"  FAIL  {what}: wanted {Show(wanted)}, got {Show(got)}");
        }

        Role("a downloaded copy offers to install, with no switch",
            @"C:\Users\Someone\Downloads\DaBtDynamicLock.exe",
            Array.Empty<string>(), SetupRole.Install);
        Role("the installed copy is the application, not an installer",
            installed + @"\DaBtDynamicLock.exe", Array.Empty<string>(), null);
        // A trailing slash and a different case must not turn the installed
        // copy into an installer that overwrites itself.
        Role("...and its folder is matched whatever the case or slash",
            @"c:\program files\da bt dynamic lock\DaBtDynamicLock.exe",
            Array.Empty<string>(), null);
        Role("--uninstall removes, wherever it runs from",
            installed + @"\DaBtDynamicLock.exe", new[] { "--uninstall" },
            SetupRole.Uninstall);
        Role("--install installs, wherever it runs from",
            installed + @"\DaBtDynamicLock.exe", new[] { "--install" },
            SetupRole.Install);
        // A run asked for explicitly wins over the location. Without this,
        // photographing or dry-running a fresh build would install it.
        Role("a picture run of a downloaded copy takes pictures",
            @"C:\Users\Someone\Downloads\DaBtDynamicLock.exe",
            new[] { "--screenshot", @"C:\x" }, null);
        Role("a dry run of a downloaded copy stays a dry run",
            @"C:\Users\Someone\Downloads\DaBtDynamicLock.exe",
            new[] { "--dry-run" }, null);

        // The switch the repeating task starts the app with. If it stopped
        // being recognised, every scheduled start would read as a start by
        // hand and would tear up the note saying the user switched the app off
        // - so a deliberate quit would undo itself five minutes later.
        var scheduled = Options.Parse(new[] { "--scheduled" },
            installed + @"\DaBtDynamicLock.exe", installed);
        lines.Add(scheduled.Scheduled && scheduled.Setup is null
            ? "  OK    a start by the schedule is told apart from one by hand"
            : "  FAIL  --scheduled was not recognised, so a deliberate quit would be undone");
        lines.Add(NavSignal.Text == before
            ? "  OK    switching back restores the first language"
            : $"  FAIL  switching back left \"{NavSignal.Text}\"");

        // ---- the manual link ---------------------------------------------
        // The address is read, never opened: nothing in this application goes
        // near the network, and the fault worth catching is the address itself
        // - a link that ignores the language hands an English reader the Czech
        // manual, and nobody would notice until they clicked it.
        SelectPage("app");
        string wasLanguage = Texts.Language;

        LanguageChoice.SelectedItem = Texts.Languages.First(l => l.Code == "en");
        string english = ManualUrl();
        LanguageChoice.SelectedItem = Texts.Languages.First(l => l.Code == "cs");
        string czech = ManualUrl();
        LanguageChoice.SelectedItem = Texts.Languages.First(l => l.Code == wasLanguage);

        lines.Add(english.EndsWith("___INFO-READ.txt") && czech.EndsWith("___INFO-CTI.txt")
            ? "  OK    the manual link follows the language"
            : $"  FAIL  the manual link ignores the language: en -> {english}, cs -> {czech}");
        lines.Add(ManualLink.Content as string is { Length: > 0 }
            ? "  OK    ...and the link is labelled"
            : "  FAIL  the manual link has no label");

        // ---- the size the window is left at ------------------------------
        // Closing it used to throw away whatever the person had done to it
        // (David, 19.09.2026). Checked through HideWindow/ShowWindow, the very
        // pair that does it, rather than by calling the saving method on its
        // own - the wiring is the half that breaks.
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.Restore();
        double scale = WindowLayout.ScaleOf(Handle);
        int wantWide = MinWidthDip + 140;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(wantWide * scale),
            (int)Math.Round((MinHeightDip + 80) * scale)));

        HideWindow();
        Check("the width the window was left at is saved", wantWide,
            s => s.WindowWidth);
        Check("...and it was not left marked as maximised", false,
            s => s.WindowMaximized);

        ShowWindow();
        int cameBack = (int)Math.Round(AppWindow.Size.Width / WindowLayout.ScaleOf(Handle));
        // A screen narrower than the size asked for makes Centred() trim the
        // window to fit, which is right and would look like a failure here.
        // Skipped rather than failed: the machine's doing, not the app's.
        int roomDip = (int)Math.Round(
            (Monitors.WorkAreas().Value.FirstOrDefault(s => s.Primary)?.Width ?? 0)
            / WindowLayout.ScaleOf(Handle));
        lines.Add(roomDip > 0 && roomDip < wantWide
            ? $"  SKIP  reopening at the size it was left: this screen is only "
                + $"{roomDip} wide, less than the {wantWide} being checked"
            : Math.Abs(cameBack - wantWide) <= 2
                ? "  OK    ...and the window opens that wide again"
                : $"  FAIL  the window was left {wantWide} wide and opened {cameBack}");

        // Maximised is remembered as a FLAG, and the ordinary size is left
        // alone - otherwise un-maximising would fill the screen just the same.
        presenter.Maximize();
        HideWindow();
        Check("a maximised window is remembered as maximised", true,
            s => s.WindowMaximized);
        Check("...without overwriting the ordinary size", wantWide,
            s => s.WindowWidth);

        presenter.Restore();
        ShowWindow();

        // ⚠ PUT BACK, or this check quietly breaks the pictures. The self-check
        // and the picture run share one settings file, and Placement() reads
        // the size from it - so a size left behind here would come out in every
        // screenshot and change every fingerprint, and "look only at the
        // pictures that changed" would stop meaning anything (found in review,
        // 20.09.2026). Cleared rather than restored: nothing about this run
        // represents a size the user chose.
        var cfg = _host.Settings;
        cfg.WindowWidth = 0;
        cfg.WindowHeight = 0;
        cfg.WindowMaximized = false;
        _host.SaveSettings();
        Check("the check leaves no window size behind for the pictures", 0,
            s => s.WindowWidth);

        return lines;
    }

    /// <summary>Shows the page with this key, the way clicking its topic would.</summary>
    internal void SelectPage(string key)
    {
        var item = Nav.Items.OfType<FrameworkElement>()
            .FirstOrDefault(i => (i.Tag as string) == key);
        if (item is not null)
            Nav.SelectedItem = item;
    }

    /// <summary>Picks the entry with this value, the way a click on it would.</summary>
    private static void Pick(ComboBox box, double? value)
    {
        var items = (List<Choice>)box.ItemsSource;
        var wanted = items.FirstOrDefault(c => Equals(c.Value, value));
        if (wanted is not null)
            box.SelectedItem = wanted;
    }

    private static string Show(object? value) => value switch
    {
        null => "nothing",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "?",
    };
}
