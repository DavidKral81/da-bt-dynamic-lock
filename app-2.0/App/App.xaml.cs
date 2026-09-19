using System.Diagnostics;
using System.Security.Principal;
using DaBtDynamicLock.Core;
using DaBtDynamicLock.Engine;
using DaBtDynamicLock.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DaBtDynamicLock.App;

/// <summary>
/// The application. Holds the settings, the log, the radio and the loop, and
/// shows what they say.
///
/// It has no main window of its own on purpose: nearly every encounter with
/// this app is a two second glance at the tray, so that is where it lives. The
/// settings window is opened only when somebody asks for it.
/// </summary>
public partial class App : Application, IWatcherView, IWatcherSystem, IAppHost
{
    private Options _options = new();
    private Mutex? _onlyInstance;
    private Log _log = null!;
    private Settings _settings = new();
    private PhoneWatch _watch = null!;
    private BleScanner _scanner = null!;
    private Watcher _loop = null!;
    private readonly SignalHistory _history = new();
    private DispatcherQueueTimer? _saveTimer;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _stopping;

    private readonly List<CountdownWindow> _boxes = new();
    private WatchIcon _icon = WatchIcon.Ok;
    private string _tip = "";

    /// <summary>
    /// The last trouble the tray icon reported, so the same one is not written
    /// twice a minute for as long as it lasts. Null means the icon is fine.
    /// </summary>
    private string? _iconTrouble;

    public App() => InitializeComponent();

    public Settings Settings => _settings;
    public PhoneWatch Watch => _watch;
    public Decision? Latest { get; private set; }
    public DateTime? LastLockedAt { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Anything that goes wrong while starting up is written down before the
        // process dies. Without this a start that fails leaves NOTHING: WinUI
        // turns an unhandled exception here into a silent stowed-exception exit
        // code, with no window, no message and not one line in the log. That is
        // indistinguishable from "it just would not start" - which this project
        // has already been told once, and had nothing to look at.
        try
        {
            Start();
        }
        catch (Exception e)
        {
            // _log may not exist yet if it was the settings that failed, so the
            // fallback writes beside the executable.
            try { _log?.Write($"Starting failed: {e}"); }
            catch (Exception) { /* the log is not worth dying for */ }

            try
            {
                File.AppendAllText(
                    Path.Combine(AppInfo.ProgramFolder, "startup-error.txt"),
                    $"{DateTime.Now:dd.MM.yyyy HH:mm:ss}  {e}\n\n");
            }
            catch (Exception) { /* nowhere to write - nothing more to try */ }

            throw;
        }
    }

    private void Start()
    {
        // Where an installation puts the program is handed in, because that is
        // what tells a downloaded copy apart from an installed one.
        _options = Options.Parse(Environment.GetCommandLineArgs(),
            installedDir: InstallerWindow.Where().TargetDir);

        // The settings and the log come FIRST, before the check for a second
        // copy. That order is deliberate: the check used to run first, so a
        // second copy left no trace at all - no window, no message, not one
        // line in the log. Reported 13.09.2026 as "it just would not start
        // again", and there was nothing to look at.
        var (settings, problem) = Settings.Load(_options.SettingsPath);
        _settings = settings;

        // Nobody has chosen a language yet - follow Windows. Written back so
        // the file says what the app is actually doing rather than leaving the
        // answer to be worked out again on every start.
        string? saveProblem = null;
        if (string.IsNullOrEmpty(_settings.Language))
        {
            _settings.Language = Texts.SystemLanguage();
            saveProblem = _settings.Save(_options.SettingsPath);
        }
        Texts.Language = _settings.Language;

        _log = new Log(_options.LogPath, () => _settings.Log);
        // Written now rather than swallowed: whether to log at all is one of
        // the settings, so a damaged file cannot report itself while it is
        // being read. The same for a write that failed before the log existed -
        // 1.x once let exactly that pass unnoticed.
        if (problem is not null)
            _log.Write(problem);
        if (saveProblem is not null)
            _log.Write(saveProblem);

        // Setup, if that is what this copy was started as. Before the
        // single-copy check on purpose: the installer's whole job is to replace
        // a copy that is very likely running, so refusing to start because one
        // is would make it useless.
        if (_options.Setup is SetupRole role)
        {
            RunSetup(role);
            return;
        }

        // Setting start at logon and quitting again. Before the single-copy
        // check on purpose: the installer runs this while the app may well be
        // running, and refusing to do it then would leave the box ticked in the
        // installer and nothing registered in Windows.
        if (_options.Autostart is bool wanted)
        {
            var done = SetAutostart(wanted);
            if (!done.Ok)
                _log.Write($"Start at logon: {done.Problem}");
            Exit();
            Environment.Exit(done.Ok ? 0 : 1);
            return;
        }

        // One instance per signed-in user (see AppInfo.MutexName). A dry run
        // uses its own name, so it can be started beside the installed copy.
        _onlyInstance = new Mutex(true, _options.MutexName, out bool mine);
        if (!mine)
        {
            _log.Write("Another copy is already running - this one is stopping.");
            // Told to the person, not just to the file. A copy that vanishes
            // without a word is indistinguishable from one that crashed.
            // Not in a batch run: a modal dialog there would wait forever for
            // a click nobody is going to make.
            if (!_options.Batch)
                Native.MessageBoxW(0, Texts.Get("msg_already_running"), AppInfo.Name,
                    Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_SETFOREGROUND);
            Exit();
            // Application.Exit() ends the message loop but leaves the process
            // running - measured here on 13.09.2026. A copy that stays alive
            // would hold the very mutex it just complained about.
            Environment.Exit(0);
            return;
        }

        // The scheduled task repeats every few minutes so a crash cannot leave
        // the computer unwatched - but it cannot tell a crash from "I switched
        // it off". The app can, and left a note saying so.
        //
        // A dry run is exempt: it never writes the note and must not read the
        // installed copy's either.
        if (!_options.DryRun)
        {
            if (_options.Scheduled)
            {
                if (QuitMarker.Applies(_options.DataFolder, Environment.TickCount64))
                {
                    _log.Write("Started by the schedule, but the user switched the app "
                        + "off since the computer started - stopping again.");
                    Exit();
                    Environment.Exit(0);
                    return;
                }
                _log.Write("Started by the schedule - the app was not running.");
            }
            else
            {
                // Started by hand (or at logon), which says the opposite of the
                // note. Torn up here rather than when quitting, so a note left
                // by a copy that crashed mid-quit cannot outlive its meaning.
                QuitMarker.Clear(_options.DataFolder);
            }
        }

        _log.Write($"{AppInfo.Name} {AppInfo.Version} starting.");

        // The picture run draws everything at one frozen instant. See
        // FreezeClockForPictures for why.
        if (_options.ScreenshotFolder is not null)
            FreezeClockForPictures();
        _watch = new PhoneWatch(NowMonotonic);

        // The previous run's chart, and the gap while the app was not running.
        //
        // A run that draws made-up data starts from an EMPTY chart: loading the
        // stored one mixed real gaps and real readings into the pictures, so two
        // runs an hour apart produced different charts and the picture could not
        // be compared with the last one. Measured on 13.09.2026 - the Czech
        // picture said 805 signals, the English one 408.
        if (!MadeUpData)
        {
            var (notRunning, historyProblem) = HistoryStore.Load(_history, HistoryPath,
                PhoneWatch.MonotonicSeconds(), Wall());
            if (historyProblem is not null)
                _log.Write(historyProblem);
            if (notRunning is double minutes)
                _log.Write($"The app was not running for {minutes:F0} min "
                    + "- marked as a gap in the chart.");
        }

        _scanner = new BleScanner(_watch, new ScannerSettings
        {
            Target = () => _settings.Target,
            Watch = () => _settings.ForWatching(),
            RestartAfterSeconds = _settings.ScannerRestartSeconds,
            WatchdogSeconds = _settings.SilenceWatchdogSeconds,
        }, _log.Write, history: _history);

        _loop = new Watcher(_watch, () => _settings, this, this, _log)
        {
            DryRun = _options.DryRun,
        };
        if (_options.DryRun)
            _log.Write("Dry run - the screen will not really be locked, and "
                + $"the settings and log used are in {_options.DataFolder}.");

        _tray = new TrayIcon(AppInfo.Name + ".TrayWindow");
        // As in 1.x: a left click opens the chart, a right click the menu. The
        // flyout panel that stood in between was dropped - what is looked for
        // on a click is the chart.
        _tray.LeftClicked += OpenChart;
        _tray.RightClicked += ShowTrayMenu;
        // Not just written down: the icon putting ITSELF back can fail too (the
        // shell answers ERROR_TIMEOUT while it restarts), and _iconTrouble is
        // what makes the next tick try again. Without this the tray would stay
        // empty until the state happened to change - which, on a locked screen,
        // it does not.
        _tray.Trouble += problem =>
        {
            _log.Write(problem);
            _iconTrouble = problem;
        };
        ShowStatus(WatchIcon.Off, new Decision(LockAction.Stop, "waiting", 0, "st_waiting"), null);

        // The app lives in the tray and usually has no window open at all, so
        // it ends only when told to - not when its last window goes.
        //
        // Said outright rather than left to a hidden window. The flyout panel
        // used to be built up front "to keep the message loop alive"; measured
        // 17.09.2026, a run with no window at all and without this line lived
        // its full 20 s too, so that was never what kept it going. The line is
        // for the case that was not measured: a window that really closes.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _stopping = new CancellationTokenSource();
        // Not for the pictures: whatever the radio hears in the room would be
        // mixed into the made-up readings, so two runs of the same build drew
        // different pictures - and a real phone's signal has no business in a
        // picture anyway.
        if (_options.ScreenshotFolder is null)
            _ = _scanner.RunAsync(_stopping.Token);
        else
            _log.Write("Taking pictures - the radio is not listened to.");

        // The loop ticks on the UI thread, so everything it shows is already on
        // the right thread. The radio runs on its own and only ever touches
        // PhoneWatch, which locks.
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = Watcher.TickInterval;
        _timer.Tick += (_, _) => _loop.Tick();
        _timer.Start();

        // The chart is written out on its own slow timer, not with every tick:
        // the loop runs twice a second and writing the file that often would be
        // pointless work. A crash then costs at most a minute of chart.
        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMinutes(1);
        _saveTimer.Tick += (_, _) => SaveHistory();
        _saveTimer.Start();

        if (_options.SelfCheck)
        {
            _ = RunSelfCheckAsync();
        }
        else if (_options.ScreenshotFolder is not null)
        {
            string? unusable = _options.WhyScreenshotFolderIsUnusable();
            if (unusable is not null)
            {
                // Refused rather than written anyway: a folder made where
                // nobody meant it is worse than no pictures.
                _log.Write(unusable);
                Shutdown();
                return;
            }
            _ = TakePicturesAsync(_options.ScreenshotFolder);
        }
        else if (_options.QuitAfterSeconds > 0)
            _ = QuitAfterAsync(TimeSpan.FromSeconds(_options.QuitAfterSeconds));
    }

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Runs as the installer rather than as the app: no tray icon, no radio, no
    /// loop - one window that asks, does the work, and reports.
    /// </summary>
    private void RunSetup(SetupRole role)
    {
        // Program Files and HKLM both need administrator rights. Asked for
        // rather than complained about: what a person does with a downloaded
        // setup is double-click it, and telling them to go and start it again
        // as an administrator is a step a normal installer does not ask for.
        if (!IsAdministrator())
        {
            if (Elevate(role))
            {
                _log.Write("Setup restarted with administrator rights.");
                Exit();
                Environment.Exit(0);
                return;
            }

            // The prompt was refused, or elevation is not available at all.
            _log.Write("Setup was not given administrator rights - stopping.");
            Texts.Language = SetupLanguage(role);
            Native.MessageBoxW(0, Texts.Get("ins_admin_refused"), AppInfo.Name,
                Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_SETFOREGROUND);
            Exit();
            Environment.Exit(1);
            return;
        }

        Texts.Language = SetupLanguage(role);
        var window = new InstallerWindow(role == SetupRole.Uninstall, _log.Write,
            _log.StopWritingFile);
        window.ShowWindow();
    }

    /// <summary>
    /// Which language setup speaks.
    ///
    /// A removal uses what the installation was done in, kept in the registry
    /// for exactly this: it runs elevated, so the user's settings file is not
    /// the one it can read. An installation has nothing to go on yet and
    /// follows WINDOWS - the app must not come up in Czech for somebody whose
    /// computer is in English.
    /// </summary>
    private static string SetupLanguage(SetupRole role)
    {
        if (role == SetupRole.Uninstall
            && Installer.Setup.StoredLanguage(InstallerWindow.Where()) is string stored
            && stored.Length > 0)
            return stored;

        return Texts.SystemLanguage();
    }

    private static bool IsAdministrator()
    {
        using var who = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(who).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts this same file again, asking Windows for the rights.</summary>
    private static bool Elevate(SetupRole role)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = role == SetupRole.Uninstall ? "--uninstall" : "--install",
                // Both are needed: runas is what raises the prompt, and it only
                // works when Windows is asked to open the file rather than the
                // process being started directly.
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Exception)
        {
            // Refusing the prompt throws. That is a decision, not a fault, so
            // the caller says so plainly instead of this reporting an error.
            return false;
        }
    }

    /// <summary>
    /// Opens what the app can show, saves a picture of each, and quits.
    ///
    /// Everything is torn down in a finally block. A sabotage run of the Tk
    /// tests once left a countdown box stuck on the screen because the cleanup
    /// sat after the last check instead of inside finally - and the person who
    /// noticed was the user, not the test.
    /// </summary>
    private async Task TakePicturesAsync(string folder)
    {
        var problems = new List<string>();
        var prints = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));      // let the first tick run

            // The loop is stopped for the rest of this. It ticks twice a second
            // and hides the countdown whenever the decision is not "countdown",
            // so a box put up for a picture was taken down again before the
            // camera got to it - measured, and it looked like the box was never
            // made at all.
            _timer?.Stop();

            // Sample readings, so the picture shows the state worth looking at
            // rather than "waiting for the phone". Made up, like the network
            // above - a picture must not carry anybody's real device either.
            _watch.Record(-62, _settings.ForWatching());
            // Heard a moment ago rather than this very instant, so the silence
            // ring shows something - the same 12 s in every picture.
            _frozenMono += PictureSilence;
            _frozenWall += PictureSilence;
            // The same lock the chart marks (FillSampleHistory), so the footer
            // and the chart cannot disagree about when it was.
            LastLockedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                (long)((NowWall() - PictureLockAgo) * 1000)).LocalDateTime;
            // A made-up device to be watching, so the overview shows a working
            // setup rather than "none chosen". It goes into the dry run's own
            // settings file, never the installed app's.
            _settings.Target = SampleDevices[0].Name;
            FillSampleHistory();
            _loop.Tick();

            string[] languages = _options.AllLanguages ? new[] { "cs", "en" } : new[] { "cs" };
            if (!_options.AllLanguages)
                _log.Write("Pictures in Czech only - add --all-languages for the full set.");
            foreach (string language in languages)
            {
                Texts.Language = language;
                // Not just the field: windows already built keep the labels
                // they were built with. Setting the field alone produced an
                // "English" picture of a window that was entirely in Czech.
                LanguageChanged();

                // Every page of the settings window, not just the first: the
                // look is checked by LOOKING, and a page nobody photographs is
                // a page nobody checks. Czech and English both, because Czech
                // is the longer language and sets the widths.
                OpenSettingsWindow();
                if (_settingsWindow is not null)
                {
                    for (int page = 0; page < _settingsWindow.PageCount; page++)
                    {
                        _settingsWindow.ShowPage(page);
                        // Longer for the first page of a freshly opened window:
                        // the selection highlight is still fading in after
                        // 400 ms, and two runs of the same build then differed
                        // in that one picture (measured 16.09.2026).
                        await Task.Delay(page == 0 ? 1200 : 400);
                        string name = $"settings-{page + 1}-{_settingsWindow.PageName}-{language}";
                        Note(problems, Screenshot.Save(_settingsWindow.Handle,
                            Path.Combine(folder, name + ".png"), prints));

                        // A page taller than the window gets a second picture,
                        // scrolled down. Without it the lower half is never
                        // looked at - which is exactly where a newly added card
                        // ends up.
                        if (_settingsWindow.PageScrolls)
                        {
                            _settingsWindow.ScrollPage(toBottom: true);
                            await Task.Delay(400);
                            Note(problems, Screenshot.Save(_settingsWindow.Handle,
                                Path.Combine(folder, name + "-bottom.png"), prints));
                            _settingsWindow.ScrollPage(toBottom: false);
                            await Task.Delay(200);
                        }
                    }
                    _settingsWindow.HideWindow();
                }

                // The installer, in both roles and with its outcome screen.
                // It is this same program under another name, so it belongs in
                // the same pass: a window nobody photographs is a window whose
                // faults nobody sees, and this one was written last.
                //
                // Only shown, never worked: the buttons are what would install
                // anything, and nothing presses them here.
                foreach (bool uninstall in new[] { false, true })
                {
                    string role = uninstall ? "uninstall" : "install";
                    var setup = new InstallerWindow(uninstall, _log.Write);
                    setup.ShowWindow();
                    await Task.Delay(600);
                    Note(problems, Screenshot.Save(setup.Handle,
                        Path.Combine(folder, $"setup-{role}-{language}.png"), prints));

                    setup.ShowSampleResult(withProblems: uninstall);
                    await Task.Delay(400);
                    Note(problems, Screenshot.Save(setup.Handle,
                        Path.Combine(folder, $"setup-{role}-done-{language}.png"), prints));
                    setup.HideWindow();
                }

                ShowCountdown(9);
                await Task.Delay(600);
                if (_boxes.Count == 0)
                    problems.Add("no countdown box was made, so there was nothing "
                        + "to photograph");
                for (int i = 0; i < _boxes.Count; i++)
                {
                    if (CatchesClicks(_boxes[i]))
                        problems.Add($"countdown box {i + 1} catches clicks meant for the "
                            + "window underneath");
                    string to = Path.Combine(folder, $"countdown-{language}-{i + 1}.png");
                    Note(problems, Screenshot.Save(_boxes[i].Handle, to, prints));
                    // Checked rather than assumed: a save that reports success
                    // and leaves no file is the kind of quiet failure this
                    // project keeps a list of.
                    if (!File.Exists(to))
                        problems.Add($"{Path.GetFileName(to)} was reported saved but is "
                            + "not there");
                }
                HideCountdown();
            }
        }
        catch (Exception e)
        {
            problems.Add($"taking the pictures failed: {e.Message}");
        }
        finally
        {
            // One line per picture, sorted, so the file of one run can be
            // compared with the file of another and only the pictures that
            // actually changed need looking at. Looking at all of them after
            // every small change costs far more than it finds.
            try
            {
                File.WriteAllLines(Path.Combine(folder, "_fingerprints.txt"),
                    prints.Select(p => $"{p.Value}  {p.Key}"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"the fingerprints could not be written ({e.Message})");
            }

            foreach (string p in problems)
                _log.Write(p);
            _log.Write(problems.Count == 0
                ? $"Pictures saved to {folder}."
                : $"Pictures finished with {problems.Count} problem(s).");
            Shutdown(problems.Count == 0 ? 0 : 1);
        }
    }

    private static void Note(List<string> problems, string? problem)
    {
        if (problem is not null)
            problems.Add(problem);
    }

    /// <summary>
    /// Opens the settings window, works its controls, and reports whether each
    /// one reached the settings file. Ends with 0 when everything held and 1
    /// when it did not, so it can be run before a release like the other checks.
    /// </summary>
    private async Task RunSelfCheckAsync()
    {
        int failed = 1;      // anything short of a clean finish counts as failure
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));      // let the first tick run
            // The loop is stopped for the rest: it ticks twice a second and
            // would refresh the window mid-check, putting saved values back
            // into controls this is in the middle of working.
            _timer?.Stop();

            // Started from a KNOWN file, not from whatever the last run left
            // behind. Without this the check reads back its own history: the
            // first sabotage run - with the save taken out of a handler - went
            // green, because the value was already in the file from before.
            _settings = new Settings { Target = SampleDevices[0].Name };
            string? problem = _settings.Save(_options.SettingsPath);
            if (problem is not null)
                throw new IOException(problem);

            _watch.Record(-62, _settings.ForWatching());
            _loop.Tick();

            OpenSettingsWindow();
            await Task.Delay(400);                          // let it draw

            var lines = _settingsWindow!.SelfCheck(_options.SettingsPath).ToList();

            // The warning notification, in two parts - and both are needed.
            //
            // Windows ACCEPTS the call and reports success even when the flags
            // tell it to display nothing, so "the call worked" proves almost
            // nothing on its own; a sabotage run passed that way while showing
            // no notification at all. So what gets handed over is checked too.
            //
            // The menu is NOT checked here: it waits for a click, so a run with
            // nobody at the keyboard would hang on it.
            string warning = Texts.Get("msg_no_signal", 10);
            var carried = _tray?.NotificationData(AppInfo.Name, warning);
            lines.Add(carried is { } data
                    && (data.uFlags & Native.NIF_INFO) != 0
                    && data.szInfo.Length > 0
                ? "  OK    the notification really carries text to display"
                : "  FAIL  the notification would be sent with nothing to display");

            // Skipped with the screen locked, for the same measured reason the
            // tray icon check skips: Windows lets nothing into the tray then,
            // so this would report a fault that is not the app's.
            // Skipped when there is no icon in the tray to show it from - the
            // screen being locked, or the shell restarting. Both are the
            // machine's doing, not the app's, and a check that blames the app
            // for them sends the next person hunting a fault that is not there.
            string? shown = _tray?.Notify(AppInfo.Name, warning);
            lines.Add(shown is null
                ? "  OK    Windows accepted the warning notification"
                : shown.Contains("not in the tray")
                    ? $"  SKIP  the warning notification: {shown}"
                    : $"  FAIL  {shown}");

            // A check nobody calls is worse than none - it only buys false
            // calm. The phone app once carried exactly this check with no
            // caller at all. It used to run only with the pictures, which are
            // taken now and then; the self-check runs on every setup build, so
            // a text missing in one language cannot ship unnoticed.
            // What a left click on the tray icon does - the same method the
            // click calls, so this is the wiring and not a copy of it.
            // Parked on another page first, so "the chart is showing" cannot
            // pass just because it already was.
            _settingsWindow?.SelectPage("app");
            OpenChart();
            await Task.Delay(300);
            lines.Add(_settingsWindow?.PageName == "signal"
                ? "  OK    a left click on the icon opens the chart"
                : $"  FAIL  a left click on the icon opened '{_settingsWindow?.PageName}', not the chart");

            // Per user, not per machine - a Global\ name shut out a second
            // signed-in user. Checked on the name this run really holds.
            lines.Add(!_options.MutexName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase)
                ? "  OK    one copy per signed-in user, not per machine"
                : $"  FAIL  the single-instance lock is machine-wide ({_options.MutexName})");

            // The tray icon losing its place in the notification area is what
            // killed the app on 18.09.2026, so it is staged here rather than
            // waited for. Its own file says how.
            lines.AddRange(TrayIcon.SelfCheck());

            lines.AddRange(CheckTrayMenu());

            var missing = Texts.Missing().ToList();
            lines.Add(missing.Count == 0
                ? "  OK    every text exists in both languages"
                : "  FAIL  these texts exist in one language only: " + string.Join(", ", missing));

            _log.Write($"Self-check of the settings window ({lines.Count} checks):");
            foreach (string line in lines)
                _log.Write(line);

            failed = lines.Count(l => l.Contains("FAIL"));
            _log.Write(failed == 0 ? "Self-check: ALL OK" : $"Self-check: {failed} FAILED");
        }
        catch (Exception e)
        {
            // Written out whole: a check that dies halfway has to say where,
            // or it looks exactly like a check that passed.
            _log.Write($"Self-check stopped on an error: {e}");
        }
        finally
        {
            Shutdown();
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }

    private async Task QuitAfterAsync(TimeSpan how)
    {
        await Task.Delay(how);
        Shutdown();
    }

    /// <summary>Where this run keeps the chart.</summary>
    private string HistoryPath => Path.Combine(_options.DataFolder, "history.json");

    /// <summary>Seconds since 1970, the clock the history file is written in.</summary>
    private static double Wall() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // Set only for the picture run; null means the real clocks.
    private double? _frozenMono;
    private double? _frozenWall;

    private const double PictureSilence = 12;
    private const double PictureLockAgo = 600;

    public double NowMonotonic() => _frozenMono ?? PhoneWatch.MonotonicSeconds();

    public double NowWall() => _frozenWall ?? Wall();

    /// <summary>
    /// Stops time for the picture run. Everything drawn from the clock - the
    /// silence, the chart's time axis, how long ago a reading was - otherwise
    /// comes out different in every run: measured 16.09.2026, two runs of the
    /// same build a minute apart differed in 16 of 26 pictures. A picture that
    /// changes when nothing changed cannot tell anybody what did.
    ///
    /// The wall clock is a fixed morning, not today: the axis labels would
    /// otherwise move with the hour the pictures happened to be taken at.
    ///
    /// The monotonic clock is a fixed number too, not "how long this computer
    /// has been up": the chart's pixel columns sit on whole multiples of that
    /// clock, so a real uptime moved them by a fraction of a pixel from run to
    /// run and the signal page came out different every time.
    /// </summary>
    private void FreezeClockForPictures()
    {
        var at = new DateTime(2026, 9, 16, 9, 51, 0) - TimeSpan.FromSeconds(PictureSilence);
        _frozenMono = 1_000_000;
        _frozenWall = new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at))
            .ToUnixTimeMilliseconds() / 1000.0;
    }

    private void SaveHistory()
    {
        // Made-up readings must not end up in the file a later run reads back:
        // the chart would then show invented signal as if it had been measured.
        if (MadeUpData)
            return;

        string? problem = HistoryStore.Save(_history, HistoryPath,
            PhoneWatch.MonotonicSeconds(), Wall());
        if (problem is not null)
            _log.Write(problem);
    }

    /// <param name="exitCode">What a batch run ends with. A picture run that
    /// reported problems used to end with 0, so a script waiting on it saw
    /// success.</param>
    private void Shutdown(int exitCode = 0)
    {
        // Written before anything is torn down: without this the last stretch
        // since the previous save would be lost on every ordinary quit, and the
        // chart would show a gap the app was actually running through.
        SaveHistory();
        _saveTimer?.Stop();
        _timer?.Stop();
        _stopping?.Cancel();
        HideCountdown();
        _settingsWindow?.HideWindow();
        _tray?.Dispose();
        _log.Write("Stopped.");
        Exit();

        // Application.Exit() ends the message loop but does NOT end the
        // process here - measured 13.09.2026, a screenshot run wrote "Stopped."
        // and then sat there until it was killed. For a batch run that is a
        // hang, and a hang is what leaves windows on somebody's screen, so the
        // process is ended outright. An interactive session never reaches this.
        if (_options.ScreenshotFolder is not null || _options.QuitAfterSeconds > 0)
            Environment.Exit(exitCode);
    }

    // ------------------------------------------------------------ IWatcherView

    public void ShowCountdown(int secondsLeft)
    {
        string text = Texts.Get("st_countdown", secondsLeft);
        var screens = ScreensForCountdown();

        // The screens are asked for every time, never remembered: a monitor can
        // be unplugged between two countdowns, and a box on a screen that is no
        // longer there is a box nobody sees.
        if (!SameScreens(screens))
        {
            HideCountdown();
            foreach (var screen in screens)
            {
                var box = new CountdownWindow();
                // The widest the text ever gets, so the box is measured once
                // and does not twitch as the number counts down.
                box.ShowOn(screen, _settings.CountdownVertical, text,
                    Texts.Get("st_countdown", 88));
                _boxes.Add(box);

                // What actually happened, not just what was decided. "Countdown
                // started" cannot settle an argument about whether anybody saw
                // it: a box on another monitor, a box behind a full-screen
                // window and a box nobody noticed all look identical in a log
                // that stops at the intention.
                Native.GetWindowRect(box.Handle, out Native.RECT where);
                bool visible = Native.IsWindowVisible(box.Handle);
                bool topmost = (Native.GetWindowLongW(box.Handle, Native.GWL_EXSTYLE)
                    & Native.WS_EX_TOPMOST) != 0;
                _log.Write($"Countdown box {_boxes.Count}/{screens.Count} at {where}, "
                    + $"{(visible ? "visible" : "NOT VISIBLE")}, "
                    + $"{(topmost ? "topmost" : "NOT TOPMOST")}, "
                    + $"{(CatchesClicks(box) ? "CATCHES CLICKS" : "clicks pass through")}, on a work area of "
                    + $"{screen.Width}x{screen.Height} at {screen.Left},{screen.Top}.");
            }
            return;
        }

        foreach (var box in _boxes)
            box.Update(text);
    }

    /// <summary>
    /// Whether a click in the middle of the box would land on the box. It must
    /// not: the box appears over whatever the user is working in, and 1.x made
    /// clicks go through it to the window underneath. Asked of Windows the way
    /// a click is routed, so it is measured without clicking anything.
    /// </summary>
    private static bool CatchesClicks(CountdownWindow box)
    {
        Native.GetWindowRect(box.Handle, out Native.RECT r);
        var middle = new Native.POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 };
        nint hit = Native.WindowFromPoint(middle);
        return hit != 0 && Native.GetAncestor(hit, Native.GA_ROOT) == box.Handle;
    }

    public void HideCountdown()
    {
        foreach (var box in _boxes)
            box.HideBox();
        _boxes.Clear();
    }

    public void ShowStatus(WatchIcon icon, Decision decision, int? rssi)
    {
        Latest = decision;

        string label = Texts.Get(decision.LabelKey,
            (object?)decision.LabelSeconds ?? decision.LabelMinutes);
        string tip = rssi is int dbm ? $"{label} ({dbm} dBm)" : label;

        if (decision.LabelKey == "st_locked")
        {
            LastLockedAt = DateTime.Now;
            // Reported exactly once per lock, so this cannot pile up duplicates.
            _history.Locked(NowMonotonic());
        }

        // Only when something actually changed: rebuilding the icon twice a
        // second would be a lot of work for a picture nobody watches.
        //
        // ...unless the last attempt failed, and then on every tick until it
        // works. ⚠ These failures are TEMPORARY and measured: the shell answers
        // ERROR_TIMEOUT (1460) or ERROR_NO_TOKEN (1008) while it is restarting,
        // and refuses everything with 0x80004005 while the screen is locked.
        // Trying once and giving up leaves no icon at all until the state
        // happens to change - which, on a locked screen, it does not.
        if (_tray is not null && (icon != _icon || tip != _tip || _iconTrouble is not null))
        {
            _icon = icon;
            _tip = tip;
            var colour = icon switch
            {
                WatchIcon.Ok => IconArt.Ok,
                WatchIcon.Countdown => IconArt.Countdown,
                _ => IconArt.Off,
            };
            try
            {
                _tray.Show(TrayIcon.MakeIcon(32, colour), $"{AppInfo.Name} - {tip}");
                if (_iconTrouble is not null)
                {
                    _log.Write("The tray icon can be updated again.");
                    _iconTrouble = null;
                }
            }
            catch (Exception e)
            {
                // The icon is how the app is reached at all, so this must not
                // pass unnoticed - but it must not stop the watching either.
                //
                // ⚠ SAID ONCE, not on every attempt. Measured 19.09.2026: with
                // the screen locked Windows refuses to update a tray icon at
                // all, and the app kept saying so twice a minute - 37 identical
                // lines in five minutes, and a night would have rotated the log
                // away. The line that matters is the first one and the one
                // saying it works again.
                if (_iconTrouble != e.Message)
                {
                    _log.Write($"The tray icon could not be updated ({e.Message}). "
                        + "Repeats of this are not logged.");
                    _iconTrouble = e.Message;
                }
            }
        }

        _settingsWindow?.RefreshIfShown();
    }

    public void WarnNoSignal(int minutes)
    {
        string text = Texts.Get("msg_no_signal", minutes);
        _log.Write($"Warning shown: {text}");

        // Shown where the user will actually see it, not only in a log nobody
        // reads. The whole point of this warning is that watching has silently
        // stopped working - a warning that is itself silent would be useless.
        string? problem = _tray?.Notify(AppInfo.Name, text);
        if (problem is not null)
            _log.Write(problem);

        _tip = "";      // force the tooltip to be rewritten on the next status
    }

    public void RefreshMenu()
    {
        _settingsWindow?.RefreshIfShown();
    }

    // ---------------------------------------------------------- IWatcherSystem

    public Reading<bool> IsScreenLocked() => SessionState.IsLocked();

    public Reading<double> IdleSeconds() => UserIdle.Seconds();

    public Reading<WifiConnection?> CurrentNetwork() => WifiNetwork.Current();

    public Reading<bool> LockScreen() => ScreenLock.Lock();

    public void RestartScanner() => _scanner.RequestRestart();

    // --------------------------------------------------------------- IAppHost

    public void SaveSettings()
    {
        // The path THIS RUN uses, never AppInfo's. A dry run loads its own
        // settings but was writing back to the installed app's file, so a flip
        // of the network switch during a screenshot run would have overwritten
        // somebody's working config - with a made-up network, and with the
        // trusted-network pause switched ON, which turns protection off.
        string? problem = _settings.Save(_options.SettingsPath);
        if (problem is not null)
            _log.Write(problem);
    }

    public void PauseFor(TimeSpan how)
    {
        _watch.PauseFor(how.TotalSeconds);
        _log.Write($"Watching paused for {how.TotalMinutes:F0} min.");
    }

    public void ResumePausing()
    {
        _watch.ResumeNow();
        _log.Write("Pause ended by the user.");
    }

    public string DataFolder => _options.DataFolder;

    public SignalHistory History => _history;

    public void OpenSettingsWindow()
    {
        // Built on first use and kept afterwards: most runs never open it, and
        // keeping it means reopening lands on the page last looked at.
        _settingsWindow ??= new SettingsWindow(this);
        _settingsWindow.ShowWindow();
    }

    public void LanguageChanged()
    {
        // The tooltip is built from a key and remembered as finished text, so
        // it has to be forced to rebuild - a service that kept rendered status
        // text is on this project's list of repeated faults.
        _tip = "";
        if (Latest is not null)
            ShowStatus(_icon, Latest, _watch.Rssi);
        _settingsWindow?.LanguageChanged();
    }

    public void Report(string problem) => _log.Write(problem);

    public void QuitApp()
    {
        _log.Write("Quitting, asked for by the user.");

        // Left BEFORE the process goes, so the repeating task knows this was
        // meant. It lasts until the computer restarts - switching the app off
        // now is not the same as never again, and "never again" is what the
        // start-at-logon switch is for.
        if (!_options.DryRun)
        {
            string? trouble = QuitMarker.Write(
                _options.DataFolder, DateTime.Now, Environment.TickCount64);
            if (trouble is not null)
                _log.Write($"The app will be started again by the schedule: {trouble}");
        }

        Shutdown();
        // Shutdown() only ends the message loop for an interactive run, which
        // is not enough when the user asked the app to go away: the process has
        // to be gone, or the tray icon comes back on the next status.
        Environment.Exit(0);
    }

    WifiConnection? IAppHost.CurrentNetwork()
    {
        // A picture is shared far more easily than a log, and the real network
        // name and router address have no business being in one. The log keeps
        // them out for the same reason; a preview that drew the real network
        // was found in review on 12.09.2026, so this one does not.
        if (MadeUpData)
            return SampleNetwork;

        var reading = WifiNetwork.Current();
        return reading.Ok ? reading.Value : null;
    }

    IReadOnlyList<NearbyDevice> IAppHost.NearbyDevices() =>
        MadeUpData ? SampleDevices : _watch.NearbyList();

    // ------------------------------------------------------------- autostart

    /// <summary>
    /// Where the logon entry points. Built from where THIS copy is, so an app
    /// moved or reinstalled elsewhere registers itself and not a path that no
    /// longer exists.
    /// </summary>
    private AutostartTarget AutostartWhere() => new(
        TaskName: AppInfo.Name,
        Program: Environment.ProcessPath
            ?? Path.Combine(AppInfo.ProgramFolder, AppInfo.Name + ".exe"),
        // Tells the copy the task starts that it was the SCHEDULE, not a
        // person - so it respects the note left when the user switched the app
        // off, instead of undoing it every five minutes.
        Arguments: "--scheduled",
        // Where the .exe is, never AppContext.BaseDirectory: in a single-file
        // build that is a temporary unpack folder, so the logon task would name
        // a working directory that is gone by the next sign-in.
        WorkingDirectory: AppInfo.ProgramFolder,
        ShortcutPath: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            AppInfo.Name + ".lnk"),
        ScratchFolder: _options.DataFolder);

    public bool AutostartOn() => !_options.DryRun && Autostart.Enabled(AutostartWhere());

    public Reading<bool> SetAutostart(bool on)
    {
        // A dry run shares the machine with the installed copy. Registering a
        // logon task from here would hand the morning to whichever build
        // happened to be under test - and it would outlive this run.
        if (_options.DryRun)
        {
            _log.Write("Dry run - start at logon was left exactly as it was.");
            return Reading<bool>.Failed(false,
                "a test run does not touch Task Scheduler");
        }

        return Autostart.Set(AutostartWhere(), on, _log.Write);
    }

    /// <summary>
    /// Whether this run shows made-up devices and networks instead of the real
    /// ones. True for both the pictures and the self-check: the self-check
    /// writes what it picked INTO THE LOG, and a log is attached to fault
    /// reports.
    /// </summary>
    private bool MadeUpData => _options.ScreenshotFolder is not null || _options.SelfCheck;

    /// <summary>
    /// Fills the chart with a made-up quarter of an hour, so the picture run
    /// has something to photograph. A real history cannot be used: the machine
    /// taking the pictures may have heard nothing at all, and an empty chart
    /// shows neither the curve, nor a lock, nor a gap.
    /// </summary>
    private void FillSampleHistory()
    {
        double now = NowMonotonic();
        var wobble = new Random(1);     // fixed seed: the same picture every time

        // The settings the pictures are taken with, so what they show does not
        // depend on what a previous run happened to leave in the file. The
        // threshold matters most: without one the chart draws no threshold line
        // and no weak signal at all, so half the legend would be missing from
        // every picture.
        _settings.Target = SampleDevices[0].Name;
        _settings.RssiThreshold = -80;
        _settings.SilenceSeconds = 45;

        // The stretch the app was not running for. Named once and used by both
        // the band and the readings: a picture that draws signal THROUGH the
        // band says the app measured while it was not running, which is exactly
        // what the band is there to deny. It looked like a drawing fault in the
        // 13.09.2026 pictures and was made-up data contradicting itself.
        const double downFrom = 400, downTo = 300;

        for (double back = 900; back > 0; back -= 2)
        {
            // A phone on the desk sits around -65 dBm and jumps by several dB
            // even lying still, which is why the app smooths before deciding.
            double at = now - back;
            if (back is < 640 and > 560)     // a spell out of range
                continue;
            if (back <= downFrom && back >= downTo)      // nothing was running
                continue;

            // A stretch where the phone was audible but too weak to count: it
            // is what the grey line and the amber band in the legend mean, and
            // a picture without it cannot show either.
            bool faint = back is < 760 and > 660;
            int rssi = faint
                ? -86 + wobble.Next(-3, 3)
                : -65 + wobble.Next(-9, 9);
            _history.Add(at, rssi);
        }
        _history.Locked(now - PictureLockAgo);
        _history.NotRunning(now - downFrom, now - downTo);
    }

    /// <summary>Made-up data for the pictures, so nothing real ends up in one.</summary>
    private static readonly WifiConnection SampleNetwork =
        new("Wi-Fi doma", "AA:BB:CC:DD:EE:FF");

    /// <summary>
    /// Made-up devices for the pictures. Names a maker would broadcast, not
    /// anybody's - the review on 12.09.2026 caught the preview drawing the real
    /// network, and a device name gives away just as much.
    /// </summary>
    private static readonly NearbyDevice[] SampleDevices =
    {
        new("My Phone", -62, 0),
        new("Headphones", -74, 0),
        new("Living room TV", -88, 23),
    };

    // ------------------------------------------------------------------ tray

    // What the tray menu can do. Numbers, not labels: the menu returns the id
    // of what was chosen, and branching on displayed text would break the
    // moment the language changes.
    private const int MenuWatching = 1;
    // 2 was a single "Pause 15 min" line; the menu now offers the same lengths
    // the window does, in a submenu. 3 was "Lock now": locking is what this
    // program does by itself, and Windows has its own shortcut.
    private const int MenuSettings = 4;
    private const int MenuQuit = 5;
    private const int MenuChart = 6;
    private const int MenuIdleGuard = 7;
    private const int MenuAutostart = 8;
    private const int MenuEndPause = 9;

    // Submenu entries are numbered by BLOCK plus the position in their list,
    // so one comparison says both which setting was chosen and which value.
    // Ids, never the displayed text: the menu is translated, and branching on
    // a label breaks the moment the language changes.
    private const int MenuDeviceBase = 100;
    private const int MenuSilenceBase = 200;
    private const int MenuCountdownBase = 300;
    private const int MenuRangeBase = 400;
    private const int MenuWarnBase = 500;
    private const int MenuPauseBase = 600;
    private const int BlockSize = 100;

    /// <summary>
    /// The menu on a right click - the one 1.5 had, item for item (David,
    /// 19.09.2026), so everything reachable there is reachable here.
    ///
    /// The labels come from the SAME keys as the settings window. 1.5 learned
    /// why: its menu said "Active" where the window said "Automatic locking
    /// active", and one thing with two names is one thing too many. The values
    /// come from Choices for the same reason.
    /// </summary>
    private void ShowTrayMenu()
    {
        if (_tray is null)
            return;

        int chosen = _tray.ShowMenu(TrayMenuItems());

        // Submenus first: their ids carry a value, so they are read as a block
        // plus a position rather than matched one by one.
        if (chosen >= MenuDeviceBase)
        {
            ChooseFromSubmenu(chosen);
            return;
        }

        ChooseFromMenu(chosen);
    }

    /// <summary>
    /// What the menu holds. Built apart from showing it, because showing it
    /// waits for a click and a run with nobody at the keyboard would hang -
    /// so this is the half a check can look at.
    /// </summary>
    internal IReadOnlyList<TrayIcon.MenuItem> TrayMenuItems()
    {
        bool paused = _watch.PauseLeft > 0;

        var items = new List<TrayIcon.MenuItem>
        {
            new(MenuChart, Texts.Get("nav_signal")),
            new(MenuDeviceBase, Texts.Get("card_phone"), Children: DeviceItems()),
            new(0, null),
            new(MenuWatching, Texts.Get("sw_active"), Ticked: _settings.Active),
            new(MenuIdleGuard, Texts.Get("sw_idle_guard"), Ticked: _settings.IdleGuard),
            new(MenuAutostart, Texts.Get("sw_autostart"), Ticked: AutostartOn()),
            new(0, null),
            new(MenuSilenceBase, Texts.Get("lbl_silence"), Children: Pick(
                Choices.Silence, MenuSilenceBase, Choices.SilenceLabel,
                v => v == _settings.SilenceSeconds)),
            new(MenuCountdownBase, Texts.Get("lbl_countdown"), Children: Pick(
                Choices.Countdown, MenuCountdownBase, Choices.CountdownLabel,
                v => v == 0 ? !_settings.Countdown
                            : _settings.Countdown && v == _settings.CountdownFromSeconds)),
            new(MenuRangeBase, Texts.Get("lbl_range"), Children: RangeItems()),
            new(MenuWarnBase, Texts.Get("card_gone"), Children: Pick(
                Choices.Warn, MenuWarnBase, Choices.WarnLabel,
                m => m == (int)_settings.AlertNoSignalMinutes)),
            new(0, null),
            new(MenuPauseBase, Texts.Get("card_pause"), Children: Pick(
                Choices.Pause.Where(m => m > 0).ToArray(), MenuPauseBase,
                Choices.PauseLabel, _ => false)),
        };

        // Only while a pause is running: an item that does nothing is worse
        // than no item, and 1.5 hid it the same way.
        if (paused)
            items.Add(new(MenuEndPause, Texts.Get("act_end_pause")));

        items.Add(new(0, null));
        items.Add(new(MenuSettings, Texts.Get("act_settings")));
        items.Add(new(MenuQuit, Texts.Get("btn_quit")));
        return items;
    }

    /// <summary>
    /// The tray menu, checked without a click. Showing it waits for one, so a
    /// run with nobody at the keyboard can only look at what it holds and at
    /// what choosing a line does - which is the half that breaks.
    ///
    /// ⚠ A picture proves a menu line is drawn; it never proves that choosing
    /// it saves anything. That exact fault shipped in 1.4, where a switch
    /// moved and the file did not - so every value here is read back OUT OF
    /// THE SETTINGS FILE.
    /// </summary>
    private IReadOnlyList<string> CheckTrayMenu()
    {
        var lines = new List<string>();
        var items = TrayMenuItems();

        void Check(string what, bool ok, string wrong) =>
            lines.Add(ok ? $"  OK    {what}" : $"  FAIL  {wrong}");

        // Every setting 1.5 could reach from the tray is reachable here.
        int[] wanted = { MenuDeviceBase, MenuSilenceBase, MenuCountdownBase,
                         MenuRangeBase, MenuWarnBase, MenuPauseBase };
        var withChildren = items
            .Where(i => i.Children is { Count: > 0 })
            .Select(i => i.Id).ToList();
        Check("the tray menu offers the same six submenus 1.5 had",
            wanted.All(withChildren.Contains),
            "the tray menu is missing a submenu: "
                + string.Join(", ", wanted.Except(withChildren)));

        // Ids that collide would send one line's job to another line.
        var ids = items.SelectMany(i => i.Children ?? new[] { i })
            .Where(i => i.Id != 0).Select(i => i.Id).ToList();
        Check("...with no two lines sharing an id",
            ids.Count == ids.Distinct().Count(),
            "two tray menu lines share an id, so one would do the other's job");

        // Choosing a value has to reach the file, not just the object.
        double wasSilence = _settings.SilenceSeconds;
        int pick = Choices.Silence[0] == wasSilence ? 1 : 0;
        ChooseFromSubmenu(MenuSilenceBase + pick);
        double saved = Settings.Load(_options.SettingsPath).Value.SilenceSeconds;
        Check("choosing a silence from the tray menu is saved",
            saved == Choices.Silence[pick],
            $"the tray menu chose {Choices.Silence[pick]} s, the file says {saved} s");

        // ...and the sensitivity, which is the one with "no limit" in front of
        // the numbers - so its positions are shifted and easy to get wrong.
        ChooseFromSubmenu(MenuRangeBase + 1);
        double? threshold = Settings.Load(_options.SettingsPath).Value.RssiThreshold;
        Check("...and so is a sensitivity, shifted position and all",
            threshold == Choices.Range[0],
            $"the tray menu chose {Choices.Range[0]} dBm, the file says {threshold?.ToString() ?? "no limit"}");

        ChooseFromSubmenu(MenuRangeBase);
        Check("...and \"no limit\" saves no threshold at all",
            Settings.Load(_options.SettingsPath).Value.RssiThreshold is null,
            "choosing no limit from the tray menu left a threshold behind");

        return lines;
    }

    /// <summary>What a top-level menu line does. Apart, so a check can call it.</summary>
    internal void ChooseFromMenu(int chosen)
    {
        bool paused = _watch.PauseLeft > 0;

        switch (chosen)
        {
            case MenuWatching:
                _settings.Active = !_settings.Active;
                SaveSettings();
                // The loop notices on its own tick, and coming back from "not
                // watching" restarts the silence measurement there - which is
                // what stops it locking the instant it is switched on.
                RefreshMenu();
                break;

            case MenuChart:
                OpenChart();
                break;

            case MenuSettings:
                OpenSettingsWindow();
                break;

            case MenuIdleGuard:
                _settings.IdleGuard = !_settings.IdleGuard;
                SaveSettings();
                RefreshMenu();
                break;

            case MenuAutostart:
                // Through the app's own method, the same one the switch in the
                // window calls - so the menu and the window cannot register two
                // different tasks. 1.5 chose this and said why.
                var done = SetAutostart(!AutostartOn());
                if (!done.Ok)
                    _log.Write($"Start at logon: {done.Problem}");
                RefreshMenu();
                break;

            case MenuEndPause:
                ResumePausing();
                RefreshMenu();
                break;

            case MenuQuit:
                QuitApp();
                break;
        }
    }

    /// <summary>
    /// One submenu line per value, ticked where it is the one in force.
    /// </summary>
    private static IReadOnlyList<TrayIcon.MenuItem> Pick<T>(
        IReadOnlyList<T> values, int block, Func<T, string> label, Func<T, bool> inForce) =>
        values.Select((v, i) =>
            new TrayIcon.MenuItem(block + i, label(v), Ticked: inForce(v))).ToList();

    /// <summary>
    /// Sensitivity, which has "no limit" in front of the numbers - so its
    /// positions are one further along than the value list.
    /// </summary>
    private IReadOnlyList<TrayIcon.MenuItem> RangeItems()
    {
        var items = new List<TrayIcon.MenuItem>
        {
            new(MenuRangeBase, Texts.Get("opt_range_max"),
                Ticked: _settings.RssiThreshold is null),
        };
        items.AddRange(Choices.Range.Select((v, i) => new TrayIcon.MenuItem(
            MenuRangeBase + 1 + i, Choices.RangeLabel(v),
            Ticked: _settings.RssiThreshold == v)));
        return items;
    }

    /// <summary>
    /// What the radio can hear, to pick the watched device from. Only named
    /// devices, as in 1.5: an address alone tells nobody anything, and it can
    /// still be typed into the settings file.
    /// </summary>
    private IReadOnlyList<TrayIcon.MenuItem> DeviceItems()
    {
        var seen = NearbyForMenu();
        if (seen.Count == 0)
            return new List<TrayIcon.MenuItem>
            {
                new(0, Texts.Get("dev_none_heard"), Enabled: false),
            };

        return seen.Select((d, i) => new TrayIcon.MenuItem(
            MenuDeviceBase + i, d.Name,
            Ticked: d.Name == _settings.Target)).ToList();
    }

    /// <summary>
    /// The devices the menu may offer.
    ///
    /// ⚠ CAPPED, and not for tidiness. The ids are a block plus a position, so
    /// the 101st device would come out as MenuSilenceBase and picking it would
    /// silently change "silence before locking" instead of the watched device.
    /// A hundred named devices is far-fetched at a desk and perfectly ordinary
    /// on a train. The full list is on the settings page, which needs no ids.
    /// </summary>
    private IReadOnlyList<NearbyDevice> NearbyForMenu()
    {
        var seen = ((IAppHost)this).NearbyDevices();
        return seen.Count <= BlockSize - 1
            ? seen
            : seen.Take(BlockSize - 1).ToList();
    }

    /// <summary>
    /// A value chosen from one of the submenus. The id says which setting and
    /// which value in one number.
    /// </summary>
    internal void ChooseFromSubmenu(int chosen)
    {
        int block = chosen / BlockSize * BlockSize;
        int at = chosen - block;

        switch (block)
        {
            case MenuDeviceBase:
                // The same capped list the menu was built from, so a position
                // means the same device on the way back as it did on the way out.
                var seen = NearbyForMenu();
                if (at >= seen.Count)
                    return;
                _settings.Target = seen[at].Name;
                // The same call the window makes when the target changes:
                // silence measured against the old device says nothing about
                // the new one.
                _watch.TargetChanged();
                break;

            case MenuSilenceBase:
                if (at >= Choices.Silence.Length) return;
                _settings.SilenceSeconds = Choices.Silence[at];
                break;

            case MenuCountdownBase:
                if (at >= Choices.Countdown.Length) return;
                int seconds = Choices.Countdown[at];
                _settings.Countdown = seconds > 0;
                if (seconds > 0)
                    _settings.CountdownFromSeconds = seconds;
                else
                    HideCountdown();
                break;

            case MenuRangeBase:
                // Position 0 is "no limit"; the numbers start one along.
                if (at > Choices.Range.Length) return;
                _settings.RssiThreshold = at == 0 ? null : Choices.Range[at - 1];
                break;

            case MenuWarnBase:
                if (at >= Choices.Warn.Length) return;
                _settings.AlertNoSignalMinutes = Choices.Warn[at];
                break;

            case MenuPauseBase:
                // The pause submenu leaves out "not paused", so its positions
                // run along the list of real lengths.
                var lengths = Choices.Pause.Where(m => m > 0).ToArray();
                if (at >= lengths.Length) return;
                PauseFor(TimeSpan.FromMinutes(lengths[at]));
                RefreshMenu();
                return;         // a pause is not a setting to save

            default:
                return;
        }

        SaveSettings();
        RefreshMenu();
    }

    /// <summary>
    /// The settings window, on the chart. Picked by the page's key, never by
    /// its position in the list: a check that picked a page by number once
    /// landed on the wrong one after a page was added.
    /// </summary>
    private void OpenChart()
    {
        OpenSettingsWindow();
        _settingsWindow?.SelectPage("signal");
    }

    private IReadOnlyList<WorkArea> ScreensForCountdown()
    {
        var screens = Monitors.WorkAreas();
        if (screens.Problem is not null)
            _log.Write(screens.Problem);
        return _settings.CountdownPrimaryOnly
            ? screens.Value.Where(s => s.Primary).Take(1).ToList()
            : screens.Value;
    }

    /// <summary>
    /// Whether the boxes already up belong to exactly these screens. The state
    /// lives on the boxes themselves rather than in a variable beside them, so
    /// it cannot outlive what it describes.
    /// </summary>
    private bool SameScreens(IReadOnlyList<WorkArea> screens) =>
        _boxes.Count == screens.Count
        && _boxes.Select(b => b.Screen).SequenceEqual(screens);
}
