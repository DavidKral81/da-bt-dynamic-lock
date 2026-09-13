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
    private PanelWindow? _panel;
    private SettingsWindow? _settingsWindow;
    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _stopping;

    private readonly List<CountdownWindow> _boxes = new();
    private WatchIcon _icon = WatchIcon.Ok;
    private string _tip = "";

    public App() => InitializeComponent();

    public Settings Settings => _settings;
    public PhoneWatch Watch => _watch;
    public Decision? Latest { get; private set; }
    public DateTime? LastLockedAt { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _options = Options.Parse(Environment.GetCommandLineArgs());

        // The settings and the log come FIRST, before the check for a second
        // copy. That order is deliberate: the check used to run first, so a
        // second copy left no trace at all - no window, no message, not one
        // line in the log. Reported 13.09.2026 as "it just would not start
        // again", and there was nothing to look at.
        var (settings, problem) = Settings.Load(_options.SettingsPath);
        _settings = settings;
        Texts.Language = _settings.Language;

        _log = new Log(_options.LogPath, () => _settings.Log);
        // Written now rather than swallowed: whether to log at all is one of
        // the settings, so a damaged file cannot report itself while it is
        // being read.
        if (problem is not null)
            _log.Write(problem);

        // One instance only. The installer looks for this name too, so it is a
        // constant rather than a literal - a renamed mutex once left the
        // installer unable to notice the app was running. A dry run uses its
        // own name, so it can be started beside the installed copy.
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

        _log.Write($"{AppInfo.Name} {AppInfo.Version} starting.");

        _watch = new PhoneWatch();

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
        _tray.LeftClicked += TogglePanel;
        _tray.RightClicked += ShowTrayMenu;
        ShowStatus(WatchIcon.Off, new Decision(LockAction.Stop, "waiting", 0, "st_waiting"), null);

        // Created up front and kept hidden: it is what keeps the message loop
        // alive in an app with no main window, and building it on the first
        // click would show an empty panel for a moment.
        _panel = new PanelWindow(this);

        _stopping = new CancellationTokenSource();
        _ = _scanner.RunAsync(_stopping.Token);

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
        try
        {
            // A check nobody calls is worse than none - it only buys false
            // calm. The phone app once carried exactly this check with no
            // caller at all, so it runs here, in the pass made before a
            // release, where its answer is actually read.
            var missing = Texts.Missing().ToList();
            if (missing.Count > 0)
                problems.Add("these texts exist in one language only: "
                    + string.Join(", ", missing));

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
            LastLockedAt = DateTime.Today.AddHours(9).AddMinutes(41);
            // A made-up device to be watching, so the overview shows a working
            // setup rather than "none chosen". It goes into the dry run's own
            // settings file, never the installed app's.
            _settings.Target = SampleDevices[0].Name;
            FillSampleHistory();
            _loop.Tick();

            foreach (string language in new[] { "cs", "en" })
            {
                Texts.Language = language;
                // Not just the field: windows already built keep the labels
                // they were built with. Setting the field alone produced an
                // "English" picture of a window that was entirely in Czech.
                LanguageChanged();

                if (_tray?.TryGetRect(out Native.RECT rect) == true && _panel is not null)
                {
                    _panel.ShowAt(rect);
                    await Task.Delay(600);                  // let it draw
                    Note(problems, Screenshot.Save(_panel.Handle,
                        Path.Combine(folder, $"panel-{language}.png")));
                    _panel.Hide();
                }
                else
                {
                    problems.Add("the tray icon's rectangle could not be read, "
                        + "so the panel could not be photographed");
                }

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
                        await Task.Delay(400);
                        Note(problems, Screenshot.Save(_settingsWindow.Handle,
                            Path.Combine(folder,
                                $"settings-{page + 1}-{_settingsWindow.PageName}-{language}.png")));
                    }
                    _settingsWindow.HideWindow();
                }

                ShowCountdown(9);
                await Task.Delay(600);
                if (_boxes.Count == 0)
                    problems.Add("no countdown box was made, so there was nothing "
                        + "to photograph");
                for (int i = 0; i < _boxes.Count; i++)
                {
                    string to = Path.Combine(folder, $"countdown-{language}-{i + 1}.png");
                    Note(problems, Screenshot.Save(_boxes[i].Handle, to));
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
            foreach (string p in problems)
                _log.Write(p);
            _log.Write(problems.Count == 0
                ? $"Pictures saved to {folder}."
                : $"Pictures finished with {problems.Count} problem(s).");
            Shutdown();
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

            string? shown = _tray?.Notify(AppInfo.Name, warning);
            lines.Add(shown is null
                ? "  OK    Windows accepted the warning notification"
                : $"  FAIL  {shown}");

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

    private void Shutdown()
    {
        // Written before anything is torn down: without this the last stretch
        // since the previous save would be lost on every ordinary quit, and the
        // chart would show a gap the app was actually running through.
        SaveHistory();
        _saveTimer?.Stop();
        _timer?.Stop();
        _stopping?.Cancel();
        HideCountdown();
        _panel?.Hide();
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
            Environment.Exit(0);
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
                    + $"{(topmost ? "topmost" : "NOT TOPMOST")}, on a work area of "
                    + $"{screen.Width}x{screen.Height} at {screen.Left},{screen.Top}.");
            }
            return;
        }

        foreach (var box in _boxes)
            box.Update(text);
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
            _history.Locked(PhoneWatch.MonotonicSeconds());
        }

        // Only when something actually changed: rebuilding the icon twice a
        // second would be a lot of work for a picture nobody watches.
        if (_tray is not null && (icon != _icon || tip != _tip))
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
            }
            catch (Exception e)
            {
                // The icon is how the app is reached at all, so this must not
                // pass unnoticed - but it must not stop the watching either.
                _log.Write($"The tray icon could not be updated ({e.Message}).");
            }
        }

        if (_panel?.IsShown == true)
            _panel.Refresh();
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
        if (_panel?.IsShown == true)
            _panel.Refresh();
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

    public void LockNow()
    {
        _log.Write("Locking now, asked for by the user.");
        var locked = ScreenLock.Lock();
        if (locked.Problem is not null)
            _log.Write(locked.Problem);
        else
            _watch.Disarm();
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
        _panel?.Refresh();
        _settingsWindow?.LanguageChanged();
    }

    public void Report(string problem) => _log.Write(problem);

    public void QuitApp()
    {
        _log.Write("Quitting, asked for by the user.");
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
        Program: Environment.ProcessPath ?? AppContext.BaseDirectory + AppInfo.Name + ".exe",
        Arguments: "",
        WorkingDirectory: AppContext.BaseDirectory,
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
        double now = PhoneWatch.MonotonicSeconds();
        var wobble = new Random(1);     // fixed seed: the same picture every time

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
            int rssi = -65 + wobble.Next(-9, 9);
            _history.Add(at, rssi);
        }
        _history.Locked(now - 600);
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
    private const int MenuPause = 2;
    private const int MenuLockNow = 3;
    private const int MenuSettings = 4;
    private const int MenuQuit = 5;

    /// <summary>
    /// The menu on a right click. Short on purpose: the panel is where things
    /// get done, and this is the shortcut for the two or three that are worth
    /// reaching without opening anything - plus Quit, which has to be reachable
    /// from the tray at all.
    /// </summary>
    private void ShowTrayMenu()
    {
        if (_tray is null)
            return;

        _panel?.Hide();
        bool paused = _watch.PauseLeft > 0;

        var items = new List<TrayIcon.MenuItem>
        {
            new(MenuWatching, Texts.Get("sw_active"), Ticked: _settings.Active),
            new(MenuPause, Texts.Get(paused ? "act_resume" : "act_pause")),
            new(MenuLockNow, Texts.Get("act_lock_now")),
            new(0, null),
            new(MenuSettings, Texts.Get("act_settings")),
            new(MenuQuit, Texts.Get("btn_quit")),
        };

        switch (_tray.ShowMenu(items))
        {
            case MenuWatching:
                _settings.Active = !_settings.Active;
                SaveSettings();
                // The loop notices on its own tick, and coming back from "not
                // watching" restarts the silence measurement there - which is
                // what stops it locking the instant it is switched on.
                RefreshMenu();
                break;

            case MenuPause:
                if (paused)
                    ResumePausing();
                else
                    PauseFor(TimeSpan.FromMinutes(15));
                RefreshMenu();
                break;

            case MenuLockNow:
                LockNow();
                break;

            case MenuSettings:
                OpenSettingsWindow();
                break;

            case MenuQuit:
                QuitApp();
                break;
        }
    }

    private void TogglePanel()
    {
        if (_panel is null || _tray is null)
            return;

        if (_panel.IsShown)
        {
            _panel.Hide();
            return;
        }

        if (_tray.TryGetRect(out Native.RECT rect))
        {
            _panel.ShowAt(rect);
        }
        else
        {
            // Without the icon's rectangle there is nowhere to point at. Said
            // out loud rather than opening the panel in a corner.
            _log.Write("The tray icon's position could not be read, "
                + "so the panel has nowhere to open.");
        }
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
