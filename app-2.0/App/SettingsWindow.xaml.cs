using DaBtDynamicLock.Core;
using DaBtDynamicLock.Engine;
using DaBtDynamicLock.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace DaBtDynamicLock.App;

/// <summary>
/// The settings window: an overview page and one page per topic.
///
/// Everything is saved the moment it is flipped, the way the shipped version
/// does it - there is no OK button to forget to press. The values offered are
/// the same ones 1.5 offers, so a settings file written by either version still
/// makes sense to the other.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    // Asked for in DIPs and scaled by the monitor DPI, like every other window
    // here. The minimum comes from what the window HOLDS - 212 for the
    // navigation, 640 for the widest page, and the padding around both - rather
    // than from a guess, so the content cannot end up cut off with no way to
    // reach it.
    // 920 = 212 for the navigation + 640 for the widest a page ever gets + the
    // padding around both. Wider than that only adds empty space to the right,
    // because the pages stop growing at 640.
    private const int WidthDip = 920;
    private const int HeightDip = 700;

    // The smallest it may be shrunk to still shows the navigation and a usable
    // page - the cards shrink with it, they are not fixed at 640.
    private const int MinWidthDip = 212 + 380 + 60;
    private const int MinHeightDip = 520;

    // The choices are the ones the shipped 1.5 offers, value for value
    // (dyn_lock.py, the settings cards). Inventing different ones here would
    // leave a settings file that one version writes and the other cannot show.
    private static readonly double[] SilenceChoices = { 12, 15, 20, 30, 45, 60, 90, 120 };
    private static readonly double[] RangeChoices =
        { -100, -95, -90, -85, -80, -75, -70, -65, -60 };
    private static readonly int[] CountdownChoices = { 0, 5, 10, 15, 20, 30 };
    private static readonly int[] PositionChoices = { 10, 20, 30, 40, 50, 60, 70, 80, 90 };
    private static readonly int[] WarnChoices = { 0, 1, 2, 5, 10, 20, 30, 60 };

    private static readonly TimeSpan ShortPause = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LongPause = TimeSpan.FromHours(1);

    /// <summary>One entry of a drop-down: what it says and what it means.</summary>
    private sealed record Choice(string Label, double? Value);

    private readonly IAppHost _host;

    /// <summary>
    /// True while the window is filling its own controls in. Every control
    /// raises its change event when it is set from code exactly as it does when
    /// a person moves it, and acting on that would save settings nobody touched.
    /// </summary>
    private bool _filling;

    public nint Handle { get; }
    public bool IsShown { get; private set; }

    public SettingsWindow(IAppHost host)
    {
        _host = host;
        InitializeComponent();

        Handle = WindowNative.GetWindowHandle(this);
        Title = AppInfo.Name;

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsResizable = true;
        presenter.IsMaximizable = true;
        presenter.PreferredMinimumWidth = MinWidthDip;
        presenter.PreferredMinimumHeight = MinHeightDip;

        PaintTitleBar();

        // Hidden rather than destroyed, so reopening is instant and the page
        // last looked at is still the one showing.
        AppWindow.Closing += (_, e) => { e.Cancel = true; HideWindow(); };

        ApplyTexts();
        Nav.SelectedIndex = 0;
    }

    /// <summary>
    /// Paints the title bar to match the window.
    ///
    /// An unpackaged WinUI 3 window does NOT get the system theme on its title
    /// bar: the first picture showed a white strip over a dark window. The
    /// colours are set from the same theme brushes the content uses, so there
    /// is one decision about what the window looks like rather than two.
    /// </summary>
    private void PaintTitleBar()
    {
        var bar = AppWindow.TitleBar;
        var background = (Root.Background as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color;
        if (background is not Windows.UI.Color colour)
            return;

        bar.BackgroundColor = colour;
        bar.InactiveBackgroundColor = colour;
        bar.ButtonBackgroundColor = colour;
        bar.ButtonInactiveBackgroundColor = colour;

        // The foreground follows the content's, so this stays right in either
        // theme instead of being a hard-coded white that vanishes on a light one.
        var ink = (Application.Current.Resources["TextFillColorPrimaryBrush"]
            as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color;
        if (ink is Windows.UI.Color text)
        {
            bar.ForegroundColor = text;
            bar.ButtonForegroundColor = text;
            bar.ButtonHoverForegroundColor = text;
        }
    }

    // ------------------------------------------------------------- showing

    /// <summary>Shows the window, centred on the primary screen, filled in.</summary>
    public void ShowWindow()
    {
        Refresh();

        if (!IsShown)
        {
            var (x, y, w, h) = Placement();
            AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        }

        AppWindow.Show(true);
        // Without this it can open behind whatever had focus - the panel needed
        // the same push.
        Native.SetForegroundWindow(Handle);
        IsShown = true;
    }

    public void HideWindow()
    {
        IsShown = false;
        AppWindow.Hide();
    }

    /// <summary>
    /// The language changed - every fixed label has to be written again.
    ///
    /// Refresh() alone is NOT enough: it fills in values, not labels, so the
    /// window would keep the language it was built in. Measured, and it showed
    /// up as an English picture of an entirely Czech window.
    /// </summary>
    public void LanguageChanged()
    {
        ApplyTexts();
        Refresh();
    }

    /// <summary>Refreshes only when somebody is actually looking at it.</summary>
    public void RefreshIfShown()
    {
        if (IsShown)
            Refresh();
    }

    /// <summary>How many pages there are - what the picture run walks through.</summary>
    internal int PageCount => Nav.Items.Count;

    /// <summary>Switches to a page by position, for the picture run.</summary>
    internal void ShowPage(int index) => Nav.SelectedIndex = index;

    /// <summary>
    /// The page showing, by its key rather than its label - so the picture
    /// files are named the same in both languages and can be compared.
    /// </summary>
    internal string PageName =>
        (Nav.SelectedItem as FrameworkElement)?.Tag as string ?? "page";

    private static (int X, int Y, int W, int H) Placement()
    {
        var screens = Monitors.WorkAreas();
        var screen = screens.Value.FirstOrDefault(s => s.Primary)
            ?? screens.Value.FirstOrDefault();

        if (screen is null)
            return (120, 120, WidthDip, HeightDip);

        // The scale comes from the MONITOR: the window is not on screen yet, so
        // XamlRoot would report 1.0 and the window would come out a fifth too
        // small on a 125 % display - measured on the countdown box.
        var rect = new Native.RECT
        {
            Left = screen.Left,
            Top = screen.Top,
            Right = screen.Right,
            Bottom = screen.Bottom,
        };
        nint monitor = Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONEAREST);
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;

        // Kept inside the work area: on a small screen the size asked for is
        // bigger than the space there is, and a window taller than the screen
        // puts its bottom edge out of reach.
        int w = Math.Min((int)Math.Round(WidthDip * scale), screen.Width);
        int h = Math.Min((int)Math.Round(HeightDip * scale), screen.Height);

        return (screen.Left + (screen.Width - w) / 2,
                screen.Top + (screen.Height - h) / 2, w, h);
    }

    // --------------------------------------------------------------- texts

    /// <summary>
    /// Every fixed label in the window, in one place.
    ///
    /// The XAML deliberately carries no text at all: with one place to fill
    /// them all in, a label that gets forgotten shows up EMPTY in the pictures
    /// instead of quietly staying in the language it was written in. The
    /// shipped version rebuilds its entire window on a language switch for the
    /// same reason - it could not trust itself to remember every label.
    /// </summary>
    private void ApplyTexts()
    {
        _filling = true;
        try
        {
            AppName.Text = AppInfo.Name;
            NavFoot.Text = Texts.Get("app_version", AppInfo.Version);

            NavOverview.Text = Texts.Get("nav_overview");
            NavSignal.Text = Texts.Get("nav_signal");
            NavPhone.Text = Texts.Get("nav_phone");
            NavLocking.Text = Texts.Get("nav_locking");
            NavNetworks.Text = Texts.Get("nav_networks");
            NavApp.Text = Texts.Get("nav_app");

            TitleOverview.Text = Texts.Get("nav_overview");
            TitleSignal.Text = Texts.Get("nav_signal");
            TitlePhone.Text = Texts.Get("nav_phone");
            ChartTitle.Text = Texts.Get("chart_title");
            // The legend is drawn, not written: BuildLegend() puts it together
            // from the chart's own brushes, and DrawChart() calls it. Setting
            // the language rebuilds it there.
            BuildRangeButtons();
            TitleLocking.Text = Texts.Get("nav_locking");
            TitleNetworks.Text = Texts.Get("nav_networks");
            TitleApp.Text = Texts.Get("nav_app");

            // overview
            KeyPhone.Text = Texts.Get("key_phone");
            KeySignal.Text = Texts.Get("key_signal");
            KeyNetwork.Text = Texts.Get("key_network");
            KeyLastLock.Text = Texts.Get("key_last_lock");
            LogLink.Content = Texts.Get("foot_log");
            Pause15.Content = Texts.Get("act_pause_15");
            Pause60.Content = Texts.Get("act_pause_60");

            // phone
            CardPhone.Text = Texts.Get("card_phone");
            CardPhoneHint.Text = Texts.Get("card_phone_hint");
            CardGone.Text = Texts.Get("card_gone");
            CardGoneHint.Text = Texts.Get("card_gone_hint");

            // locking
            SwActive.Text = Texts.Get("sw_active");
            SwActiveHint.Text = Texts.Get("sw_active_hint");
            GroupWhen.Text = Texts.Get("group_when");
            LblSilence.Text = Texts.Get("lbl_silence");
            LblSilenceHint.Text = Texts.Get("lbl_silence_hint");
            LblRange.Text = Texts.Get("lbl_range");
            LblRangeHint.Text = Texts.Get("lbl_range_hint");
            SwIdleGuard.Text = Texts.Get("sw_idle_guard");
            SwIdleGuardHint.Text = Texts.Get("sw_idle_guard_hint");
            GroupCountdown.Text = Texts.Get("group_countdown");
            LblCountdown.Text = Texts.Get("lbl_countdown");
            LblCountdownHint.Text = Texts.Get("lbl_countdown_hint");
            LblPosition.Text = Texts.Get("lbl_position");
            LblPositionHint.Text = Texts.Get("lbl_position_hint");
            SwPrimaryOnly.Text = Texts.Get("sw_primary_only");
            SwPrimaryOnlyHint.Text = Texts.Get("sw_primary_only_hint");

            // networks
            SwTrusted.Text = Texts.Get("sw_trusted");
            SwTrustedHint.Text = Texts.Get("sw_trusted_hint");
            CardNetworks.Text = Texts.Get("card_networks");
            CardNetworksHint.Text = Texts.Get("card_networks_hint");

            // app
            SwAutostart.Text = Texts.Get("sw_autostart");
            SwAutostartHint.Text = Texts.Get("sw_autostart_hint");
            SwLog.Text = Texts.Get("sw_log");
            SwLogHint.Text = Texts.Get("sw_log_hint");
            LblFolder.Text = Texts.Get("lbl_folder");
            FolderPath.Text = _host.DataFolder;
            OpenFolder.Content = Texts.Get("act_open_folder");
            LblVersion.Text = Texts.Get("lbl_version", AppInfo.Version);
            LblVersionHint.Text = Texts.Get("lbl_version_hint");
            UpdatesLink.Content = Texts.Get("link_updates");
            ProjectLink.Content = Texts.Get("link_project");
            PhoneAppLink.Content = Texts.Get("link_phone_app");
            QuitButton.Content = Texts.Get("btn_quit");

            FillChoices();
        }
        finally
        {
            _filling = false;
        }

        // The lists carry translated text, so what is drawn no longer matches
        // the language: they are forced to redraw rather than left waiting for
        // their contents to change.
        DeviceList.Tag = null;
        NetworkList.Tag = null;
    }

    /// <summary>
    /// Rebuilds the drop-downs. Their entries are translated, so this runs
    /// again on a language switch; the selected VALUE is put back by Refresh().
    /// </summary>
    private void FillChoices()
    {
        Silence.ItemsSource = SilenceChoices
            .Select(s => new Choice(Texts.Get("opt_seconds", (int)s), s)).ToList();

        Range.ItemsSource = new[] { new Choice(Texts.Get("opt_range_max"), (double?)null) }
            .Concat(RangeChoices.Select(v => new Choice(RangeLabel(v), v))).ToList();

        CountdownFrom.ItemsSource = CountdownChoices
            .Select(s => new Choice(
                s == 0 ? Texts.Get("opt_countdown_off") : Texts.Get("opt_countdown_from", s),
                (double?)s))
            .ToList();

        Position.ItemsSource = PositionChoices
            .Select(p => new Choice(Texts.Get("opt_from_top", p), (double?)p)).ToList();

        WarnAfter.ItemsSource = WarnChoices
            .Select(m => new Choice(
                m == 0 ? Texts.Get("opt_no_warning") : Texts.Get("opt_after_minutes", m),
                (double?)m))
            .ToList();
    }

    /// <summary>
    /// The sensitivity entries say what the number MEANS where it is worth
    /// saying. -80 dBm is called out because a phone in a pocket measures about
    /// that - which is the whole reason this setting cannot be guessed at.
    /// </summary>
    private static string RangeLabel(double v)
    {
        int dbm = (int)v;
        if (v == RangeChoices[0]) return Texts.Get("opt_range_longest", dbm);
        if (v == RangeChoices[^1]) return Texts.Get("opt_range_shortest", dbm);
        if (v == -80) return Texts.Get("opt_range_edge", dbm);
        return Texts.Get("opt_range_plain", dbm);
    }

    // -------------------------------------------------------------- filling

    /// <summary>Puts the live state and the saved settings into the controls.</summary>
    public void Refresh()
    {
        _filling = true;
        try
        {
            var cfg = _host.Settings;

            FillOverview(cfg);

            Select(Silence, cfg.SilenceSeconds);
            Select(Range, cfg.RssiThreshold);
            Select(CountdownFrom, cfg.Countdown ? cfg.CountdownFromSeconds : 0);
            Select(Position, Math.Round(cfg.CountdownVertical * 100));
            Select(WarnAfter, cfg.AlertNoSignalMinutes);

            Active.IsOn = cfg.Active;
            IdleGuard.IsOn = cfg.IdleGuard;
            PrimaryOnly.IsOn = cfg.CountdownPrimaryOnly;
            TrustedOn.IsOn = cfg.TrustedNetworkPause;
            LogOn.IsOn = cfg.Log;
            // Read from Windows, not from the settings file: the logon task can
            // be removed in Task Scheduler, and a remembered "on" would then be
            // a promise the app cannot keep. The answer is cached for half a
            // minute inside Autostart, so this costs nothing per tick.
            AutostartOn.IsOn = _host.AutostartOn();

            MarkLanguage();
            FillDevices(cfg);
            FillNetworks(cfg);

            // Only while its page is showing: redrawing a chart nobody is
            // looking at, twice a second, is work for nothing.
            if (PageSignal.Visibility == Visibility.Visible)
                DrawChart();
        }
        finally
        {
            _filling = false;
        }
    }

    private void FillOverview(Settings cfg)
    {
        var decision = _host.Latest;

        StateLine.Text = decision is null
            ? Texts.Get("st_waiting")
            : Texts.Get(decision.LabelKey,
                (object?)decision.LabelSeconds ?? decision.LabelMinutes);
        StateWhy.Text = WhyText(decision, cfg);

        double? silence = _host.Watch.Silence();
        RingValue.Text = silence is double s ? Texts.Get("ring_seconds", (int)s) : "–";
        RingOf.Text = Texts.Get("ring_of", (int)cfg.SilenceSeconds);

        double share = silence is double sec && cfg.SilenceSeconds > 0
            ? Math.Clamp(sec / cfg.SilenceSeconds, 0, 1)
            : 0;
        // StrokeDashArray counts in multiples of the stroke thickness, not in
        // pixels: a 104 px circle with an 8 px stroke is about 327 px round, so
        // a full ring is roughly 41 units.
        const double units = 41;
        double on = share * units;
        Ring.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection
            { on, Math.Max(units - on, 0.001) };

        ValPhone.Text = cfg.Target.Length > 0 ? cfg.Target : Texts.Get("val_no_phone");
        ValSignal.Text = _host.Watch.Rssi is int dbm
            ? Texts.Get("val_dbm", dbm)
            : Texts.Get("val_no_reading");

        var here = _host.CurrentNetwork();
        ValNetwork.Text = here is null
            ? Texts.Get("net_none")
            : Texts.Get(IsTrusted(cfg, here) ? "val_net_trusted" : "val_net_locking", here.Ssid);

        ValLastLock.Text = _host.LastLockedAt is DateTime when
            ? when.ToString("H:mm")
            : Texts.Get("foot_never_locked");

        // One button for "stop watching now", whichever way it is stopped -
        // and it always offers the way back, so the overview can never show a
        // state it cannot undo.
        bool paused = _host.Watch.PauseLeft > 0;
        PauseStop.Content = Texts.Get(paused ? "act_end_pause"
            : cfg.Active ? "act_switch_off" : "act_switch_on");
    }

    /// <summary>
    /// One sentence saying what happens next. The overview answers "is it
    /// watching?", and the follow-up is always "why not" or "when will it
    /// lock" - worth a line rather than a number to work out.
    /// </summary>
    private static string WhyText(Decision? decision, Settings cfg) => decision?.Reason switch
    {
        "off" => Texts.Get("why_off"),
        "screen_locked" => Texts.Get("why_screen_locked"),
        "paused" => Texts.Get("why_paused"),
        "trusted_network" => Texts.Get("why_trusted_network"),
        "waiting" => Texts.Get("why_waiting"),
        "locked" => Texts.Get("why_locked"),
        "idle_guard" => Texts.Get("why_idle_guard"),
        _ => Texts.Get("why_watching", (int)cfg.SilenceSeconds),
    };

    private static bool IsTrusted(Settings cfg, WifiConnection here) =>
        cfg.TrustedNetworks.Any(n => n.Ssid == here.Ssid && n.Bssid == here.Bssid);

    /// <summary>Marks the language in use with the bar under its flag.</summary>
    private void MarkLanguage()
    {
        bool english = Texts.Language == "en";
        FlagCsMark.Opacity = english ? 0 : 1;
        FlagEnMark.Opacity = english ? 1 : 0;
    }

    // ------------------------------------------------------- device list

    /// <summary>
    /// The devices heard recently, with the watched one ticked.
    ///
    /// Redrawn only when the list actually changes - it refreshes twice a
    /// second, and rebuilding it every time would fight anybody trying to click
    /// it. WHAT IS DRAWN is remembered ON the panel: a signature kept in a
    /// field beside it once outlived the rows it described, and the list then
    /// never redrew again.
    /// </summary>
    private void FillDevices(Settings cfg)
    {
        var rows = _host.NearbyDevices()
            .Select(d => (d.Name, Label: DeviceLabel(d))).ToList();

        // The watched device belongs on the list even when it cannot be heard
        // right now - otherwise the one row that matters disappears the moment
        // the phone goes quiet, which is exactly when somebody comes looking.
        if (cfg.Target.Length > 0 && !rows.Any(r => r.Name == cfg.Target))
            rows.Insert(0, (cfg.Target, Texts.Get("dev_not_heard", cfg.Target)));

        string signature = string.Join("\n", rows.Select(r => r.Label));
        if ((string?)DeviceList.Tag == signature)
        {
            // Nothing changed on screen, but the tick still has to follow the
            // settings: the target can be changed from somewhere else.
            foreach (var button in DeviceList.Children.OfType<RadioButton>())
                button.IsChecked = (string?)button.Tag == cfg.Target;
            return;
        }
        DeviceList.Tag = signature;

        DeviceList.Children.Clear();
        if (rows.Count == 0)
        {
            DeviceList.Children.Add(Note("dev_none_heard"));
            return;
        }

        foreach (var (name, label) in rows)
        {
            var button = new RadioButton
            {
                Content = label,
                Tag = name,
                IsChecked = name == cfg.Target,
                GroupName = "watched",
                MinWidth = 0,
            };
            button.Checked += OnDeviceChosen;
            DeviceList.Children.Add(button);
        }
    }

    private static string DeviceLabel(NearbyDevice d) =>
        // A reading a minute old shown as a plain number reads as "the phone is
        // right here" when the phone is actually off. It carries its age.
        d.AgeSeconds >= 5
            ? Texts.Get("dev_last_heard", d.Name, (int)d.AgeSeconds)
            : Texts.Get("dev_dbm", d.Name, d.Rssi);

    // ------------------------------------------------------ network list

    /// <summary>
    /// The saved networks plus the one in use, each its own row - a mesh then
    /// reads as several rows of one name and every access point can be ticked
    /// on its own. Built like the device list above, which is the pattern this
    /// project already had for exactly this job.
    /// </summary>
    private void FillNetworks(Settings cfg)
    {
        var here = _host.CurrentNetwork();
        var rows = cfg.TrustedNetworks
            .Select(n => new TrustedNetwork { Ssid = n.Ssid, Bssid = n.Bssid })
            .ToList();

        if (here is not null && !rows.Any(n => n.Ssid == here.Ssid && n.Bssid == here.Bssid))
            rows.Insert(0, new TrustedNetwork { Ssid = here.Ssid, Bssid = here.Bssid });

        string signature = string.Join("\n",
            rows.Select(n => $"{n.Ssid}\t{n.Bssid}\t{Same(n, here)}"));
        if ((string?)NetworkList.Tag == signature)
        {
            foreach (var box in NetworkList.Children.OfType<CheckBox>())
                if (box.Tag is TrustedNetwork known)
                    box.IsChecked = cfg.TrustedNetworks
                        .Any(n => n.Ssid == known.Ssid && n.Bssid == known.Bssid);
            return;
        }
        NetworkList.Tag = signature;

        NetworkList.Children.Clear();
        if (rows.Count == 0)
        {
            NetworkList.Children.Add(Note("net_none_connected"));
            return;
        }

        foreach (var network in rows)
        {
            var box = new CheckBox
            {
                // The MAC is always on show: it is half of what a network is
                // recognised by, and without it two access points of one mesh
                // are two identical-looking rows.
                Content = Texts.Get(Same(network, here) ? "net_here" : "net_row",
                    network.Ssid, network.Bssid),
                // The network ITSELF in the tag, not a joined-up string: a
                // separator in a name would otherwise split the wrong way and
                // untick somebody else's network.
                Tag = network,
                IsChecked = cfg.TrustedNetworks
                    .Any(n => n.Ssid == network.Ssid && n.Bssid == network.Bssid),
                MinWidth = 0,
            };
            box.Checked += OnNetworkTicked;
            box.Unchecked += OnNetworkTicked;
            NetworkList.Children.Add(box);
        }
    }

    private static bool Same(TrustedNetwork n, WifiConnection? here) =>
        here is not null && n.Ssid == here.Ssid && n.Bssid == here.Bssid;

    private static TextBlock Note(string key) => new()
    {
        Text = Texts.Get(key),
        FontSize = 13,
        Opacity = 0.95,
        TextWrapping = TextWrapping.Wrap,
    };

    // ------------------------------------------------------------ handlers

    private void OnPageChosen(object sender, SelectionChangedEventArgs e)
    {
        // Read from the Tag, never from the label on screen: branching on
        // displayed text breaks the moment the language changes.
        string page = (Nav.SelectedItem as FrameworkElement)?.Tag as string ?? "overview";

        PageOverview.Visibility = Shown(page == "overview");
        PageSignal.Visibility = Shown(page == "signal");
        PagePhone.Visibility = Shown(page == "phone");
        PageLocking.Visibility = Shown(page == "locking");
        PageNetworks.Visibility = Shown(page == "networks");
        PageApp.Visibility = Shown(page == "app");

        if (page == "signal")
            DrawChart();
    }

    // Not called Visible: Window already has a member by that name, and hiding
    // it compiles into a warning that TreatWarningsAsErrors turns into a stop.
    private static Visibility Shown(bool yes) =>
        yes ? Visibility.Visible : Visibility.Collapsed;

    private void OnFlagPressed(object sender, PointerRoutedEventArgs e)
    {
        string language = (sender as FrameworkElement)?.Tag as string ?? "cs";
        if (language == Texts.Language)
            return;

        Texts.Language = language;
        _host.Settings.Language = language;
        _host.SaveSettings();

        // Everything that carries text is redrawn from ONE place - the tray
        // tooltip, the panel and this window included. Redrawing this window
        // here as well would be a second path doing the same job, and the two
        // would drift.
        _host.LanguageChanged();
    }

    private void OnActiveToggled(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        _host.Settings.Active = Active.IsOn;
        _host.SaveSettings();
        Refresh();
    }

    private void OnIdleGuardToggled(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        _host.Settings.IdleGuard = IdleGuard.IsOn;
        _host.SaveSettings();
    }

    private void OnPrimaryOnlyToggled(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        _host.Settings.CountdownPrimaryOnly = PrimaryOnly.IsOn;
        _host.SaveSettings();
    }

    private void OnTrustedToggled(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        _host.Settings.TrustedNetworkPause = TrustedOn.IsOn;
        _host.SaveSettings();
        Refresh();
    }

    private void OnLogToggled(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        _host.Settings.Log = LogOn.IsOn;
        _host.SaveSettings();
    }

    /// <summary>
    /// Start at logon. The switch is NOT a setting in the file - it shows what
    /// Windows has, and the answer comes back from a fresh look rather than
    /// from what was asked for. The shipped version announced "enabled" on a
    /// return code, and after a restart nothing came up.
    /// </summary>
    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_filling) return;

        bool wanted = AutostartOn.IsOn;
        var actual = _host.SetAutostart(wanted);
        if (actual.Ok)
            return;

        // Put the switch where reality is, and say why out loud: a switch that
        // stays on while nothing was registered is the app lying to the user.
        _host.Report($"Start at logon: {actual.Problem}");
        _filling = true;
        try
        {
            AutostartOn.IsOn = actual.Value;
        }
        finally
        {
            _filling = false;
        }
        SwAutostartHint.Text = Texts.Get("sw_autostart_failed");
    }

    private void OnSilenceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Chosen(Silence) is not double seconds) return;
        _host.Settings.SilenceSeconds = seconds;
        _host.SaveSettings();
        Refresh();
    }

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        // No value check here: "as far as possible" IS null, and that is a
        // legitimate choice rather than a missing one.
        if (_filling) return;
        _host.Settings.RssiThreshold = Chosen(Range);
        _host.SaveSettings();
    }

    private void OnCountdownChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Chosen(CountdownFrom) is not double seconds) return;
        // One list, not a switch plus a list: "do not show" is simply another
        // entry, the way the shipped version does it. Both settings behind it
        // stay as they are, so a file written here still reads in 1.5.
        _host.Settings.Countdown = seconds > 0;
        if (seconds > 0)
            _host.Settings.CountdownFromSeconds = (int)seconds;
        _host.SaveSettings();
    }

    private void OnPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Chosen(Position) is not double percent) return;
        _host.Settings.CountdownVertical = percent / 100.0;
        _host.SaveSettings();
    }

    private void OnWarnAfterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Chosen(WarnAfter) is not double minutes) return;
        _host.Settings.AlertNoSignalMinutes = minutes;
        _host.SaveSettings();
    }

    private void OnDeviceChosen(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        string name = (sender as FrameworkElement)?.Tag as string ?? "";
        if (name == _host.Settings.Target)
            return;

        _host.Settings.Target = name;
        _host.SaveSettings();
        // The readings so far belong to a different device. Without this the
        // silence measured against the old phone carries over and can lock the
        // screen seconds after the choice is made.
        _host.Watch.TargetChanged();
        Refresh();
    }

    private void OnNetworkTicked(object sender, RoutedEventArgs e)
    {
        if (_filling || sender is not CheckBox box || box.Tag is not TrustedNetwork network)
            return;

        var cfg = _host.Settings;
        if (box.IsChecked == true)
        {
            if (!cfg.TrustedNetworks.Any(n => n.Ssid == network.Ssid && n.Bssid == network.Bssid))
                cfg.TrustedNetworks.Add(network);
        }
        else
        {
            cfg.TrustedNetworks.RemoveAll(n =>
                n.Ssid == network.Ssid && n.Bssid == network.Bssid);
        }
        _host.SaveSettings();
        Refresh();
    }

    private void OnPause15(object sender, RoutedEventArgs e) => Pause(ShortPause);

    private void OnPause60(object sender, RoutedEventArgs e) => Pause(LongPause);

    private void Pause(TimeSpan how)
    {
        _host.PauseFor(how);
        Refresh();
    }

    /// <summary>
    /// Ends a pause, switches watching off, or switches it back on - whichever
    /// of the three the current state calls for.
    /// </summary>
    private void OnEndPause(object sender, RoutedEventArgs e)
    {
        if (_host.Watch.PauseLeft > 0)
        {
            _host.ResumePausing();
        }
        else
        {
            _host.Settings.Active = !_host.Settings.Active;
            _host.SaveSettings();
        }
        Refresh();
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => Open(_host.DataFolder);

    private void OnOpenReleases(object sender, RoutedEventArgs e) =>
        Open(AppInfo.ProjectUrl + "/releases/latest");

    private void OnOpenProject(object sender, RoutedEventArgs e) => Open(AppInfo.ProjectUrl);

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        HideWindow();
        _host.QuitApp();
    }

    /// <summary>
    /// Hands a folder or an address to whatever opens it. Nothing here talks to
    /// the network itself - the address is passed to the browser, which is a
    /// different program.
    /// </summary>
    private void Open(string what)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = what,
                UseShellExecute = true,
            });
        }
        catch (Exception problem)
        {
            // Said out loud rather than swallowed: a link that silently does
            // nothing is a fault this project already had to fix once.
            _host.Report($"{what} could not be opened ({problem.Message}).");
        }
    }

    // ---------------------------------------------------------- drop-downs

    /// <summary>The value behind what is picked, or null for "none".</summary>
    private static double? Chosen(ComboBox box) => (box.SelectedItem as Choice)?.Value;

    /// <summary>
    /// Picks the entry for a saved value, snapping to the nearest one offered.
    ///
    /// Snapping matters: a value typed into config.json by hand - or written by
    /// a version with a different list - would otherwise leave the drop-down
    /// blank while the app happily went on using it. The shipped version snaps
    /// the countdown position for the very same reason.
    /// </summary>
    private static void Select(ComboBox box, double? value)
    {
        var items = (List<Choice>)box.ItemsSource;

        if (value is not double wanted)
        {
            box.SelectedItem = items.FirstOrDefault(c => c.Value is null)
                ?? items.FirstOrDefault();
            return;
        }

        Choice? best = null;
        double bestGap = double.MaxValue;
        foreach (var item in items)
        {
            if (item.Value is not double v)
                continue;
            double gap = Math.Abs(v - wanted);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = item;
            }
        }
        box.SelectedItem = best ?? items.FirstOrDefault();
    }
}
