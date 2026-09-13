using System.Runtime.InteropServices;
using DaBtDynamicLock.Core;
using DaBtDynamicLock.Engine;
using DaBtDynamicLock.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DaBtDynamicLock.App;

/// <summary>
/// What the windows need from the running app, and what they ask it to do.
///
/// One interface for the panel and the settings window rather than one each:
/// they ask for the same things, and two of them would drift apart.
/// </summary>
public interface IAppHost
{
    Settings Settings { get; }
    PhoneWatch Watch { get; }
    Decision? Latest { get; }
    DateTime? LastLockedAt { get; }

    /// <summary>
    /// Where THIS run keeps its settings and log. Asked for rather than taken
    /// from AppInfo, so a dry run opens its own folder and not the installed
    /// app's - the same split the settings file has to make.
    /// </summary>
    string DataFolder { get; }

    void SaveSettings();
    void PauseFor(TimeSpan how);
    void ResumePausing();
    void LockNow();
    void OpenSettingsWindow();

    /// <summary>Redraw everything that carries text - the tray tip, the panel.</summary>
    void LanguageChanged();

    /// <summary>Puts something in the log from a window that has no log of its own.</summary>
    void Report(string problem);

    /// <summary>Stops everything and quits.</summary>
    void QuitApp();

    /// <summary>The network in use, or null when there is none or it cannot be read.</summary>
    WifiConnection? CurrentNetwork();

    /// <summary>
    /// The devices heard recently. Asked of the host rather than read straight
    /// off PhoneWatch so that a picture run can hand over made-up ones: a
    /// screenshot is shared far more easily than a log, and somebody's actual
    /// phone has no business being in one.
    /// </summary>
    IReadOnlyList<NearbyDevice> NearbyDevices();

    /// <summary>What the chart is drawn from.</summary>
    SignalHistory History { get; }
}

/// <summary>
/// The flyout panel from the tray icon.
///
/// No border, no title bar, placed against the icon on whichever monitor the
/// taskbar is on, and it closes when it loses focus. All three were measured in
/// a throwaway prototype before this was written.
/// </summary>
public sealed partial class PanelWindow : Window
{
    // Content size in DIPs; the window rectangle is physical pixels, so this
    // gets scaled by the monitor DPI. Getting that wrong is invisible at 100 %
    // and obvious on a 150 % laptop screen.
    //
    // 392 and not 340: at 340 the capture showed Czech labels cut off
    // ("Zamknout ted", "nezamykat, kdyz"). CZECH IS THE LONGER OF THE TWO
    // LANGUAGES AND SETS THE WIDTH.
    private const int WidthDip = 392;
    private const int HeightDip = 268;

    /// <summary>How long the pause button pauses for.</summary>
    private static readonly TimeSpan PauseLength = TimeSpan.FromMinutes(15);

    private readonly IAppHost _host;

    /// <summary>
    /// True while the panel is filling its own controls in. The network switch
    /// raises Toggled when it is set from code just as it does when a person
    /// flips it, and acting on that would save a network nobody asked to save.
    /// </summary>
    private bool _filling;

    public nint Handle { get; }
    public bool IsShown { get; private set; }

    public PanelWindow(IAppHost host)
    {
        _host = host;
        InitializeComponent();

        Handle = WindowNative.GetWindowHandle(this);

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        // Keep it out of Alt+Tab and the taskbar - it is a flyout, not a window.
        AppWindow.IsShownInSwitchers = false;

        Activated += OnActivated;
        // Cancelled and hidden instead: closing would destroy the flyout and
        // the next click would have nothing to show.
        AppWindow.Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && IsShown)
            Hide();
    }

    /// <summary>Shows the panel tucked against the tray icon, filled in.</summary>
    internal void ShowAt(Native.RECT iconRect)
    {
        Refresh();
        var (x, y, w, h) = PlaceNear(iconRect);
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        AppWindow.Show(true);
        // Without this the panel opens behind whatever was in front.
        Native.SetForegroundWindow(Handle);
        IsShown = true;
    }

    public void Hide()
    {
        IsShown = false;
        AppWindow.Hide();
    }

    /// <summary>Fills everything in from the live state. Cheap enough to do on every show.</summary>
    public void Refresh()
    {
        _filling = true;
        try
        {
            var cfg = _host.Settings;
            var decision = _host.Latest;

            StateLine.Text = decision is null
                ? Texts.Get("st_waiting")
                : Texts.Get(decision.LabelKey,
                    (object?)decision.LabelSeconds ?? decision.LabelMinutes);

            int? rssi = _host.Watch.Rssi;
            DetailLine.Text = rssi is int dbm
                ? Texts.Get("detail_at_desk", dbm)
                : Texts.Get("detail_no_reading");

            FillRing(cfg);
            FillActions();
            FillNetwork(cfg);
            FillFooter();
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>
    /// The ring fills as the silence approaches the threshold, so how close a
    /// lock is can be seen at a glance.
    /// </summary>
    private void FillRing(Settings cfg)
    {
        double? silence = _host.Watch.Silence();
        Silence.Text = silence is double s ? $"{s:F0} s" : "–";

        double share = silence is double sec && cfg.SilenceSeconds > 0
            ? Math.Clamp(sec / cfg.SilenceSeconds, 0, 1)
            : 0;

        // StrokeDashArray counts in multiples of the stroke thickness, not in
        // pixels: the circumference of a 56 px circle with a 5 px stroke is
        // about 160 px, so a full ring is roughly 32 units.
        const double units = 32;
        double on = share * units;
        Ring.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection
            { on, Math.Max(units - on, 0.001) };
    }

    private void FillActions()
    {
        bool paused = _host.Watch.PauseLeft > 0;
        PauseLabel.Text = Texts.Get(paused ? "act_resume" : "act_pause");
        // Escapes, not the characters themselves: these are private-use code
        // points from the Segoe icon font, and a file that travelled through a
        // tool with the wrong encoding would turn them into rubbish silently.
        PauseGlyph.Glyph = paused ? "\uE768" : "\uE769";    // play / pause
        LockLabel.Text = Texts.Get("act_lock_now");
        SettingsLabel.Text = Texts.Get("act_settings");
    }

    private void FillNetwork(Settings cfg)
    {
        var here = _host.CurrentNetwork();
        NetworkName.Text = here?.Ssid ?? Texts.Get("net_none");
        NetworkHint.Text = Texts.Get("net_hint");
        NetworkSwitch.IsEnabled = here is not null;
        // Both halves have to match. A name on its own is trivially forged, and
        // this switch turns protection OFF.
        NetworkSwitch.IsOn = here is not null
            && cfg.TrustedNetworks.Any(n => n.Ssid == here.Ssid && n.Bssid == here.Bssid);
    }

    private void FillFooter()
    {
        LastLock.Text = _host.LastLockedAt is DateTime when
            ? Texts.Get("foot_last_lock", when.ToString("H:mm"))
            : Texts.Get("foot_never_locked");
        LogLink.Content = Texts.Get("foot_log");
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (_host.Watch.PauseLeft > 0)
            _host.ResumePausing();
        else
            _host.PauseFor(PauseLength);
        Refresh();
    }

    private void OnLockNow(object sender, RoutedEventArgs e)
    {
        Hide();
        _host.LockNow();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        Hide();
        _host.OpenSettingsWindow();
    }

    private void OnNetworkToggled(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return;

        var here = _host.CurrentNetwork();
        if (here is null)
            return;

        var cfg = _host.Settings;
        if (NetworkSwitch.IsOn)
        {
            if (!cfg.TrustedNetworks.Any(n => n.Ssid == here.Ssid && n.Bssid == here.Bssid))
                cfg.TrustedNetworks.Add(new TrustedNetwork
                    { Ssid = here.Ssid, Bssid = here.Bssid });
            // Saving one network is also what switches the whole feature on -
            // otherwise the switch would move and nothing would happen.
            cfg.TrustedNetworkPause = true;
        }
        else
        {
            cfg.TrustedNetworks.RemoveAll(n => n.Ssid == here.Ssid && n.Bssid == here.Bssid);
        }
        _host.SaveSettings();
        Refresh();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        Hide();
        // The FOLDER, not the file: the log rotates, so yesterday evening may
        // well be in the older half and a link to the file would sometimes open
        // the empty one.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _host.DataFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Nothing to do about it and nowhere useful to say it - the folder
            // path is also shown on the settings page for exactly this case.
        }
    }

    /// <summary>
    /// Right above the icon, pulled inside the working area. The working area
    /// is read per monitor, because the taskbar does not have to be on the
    /// primary one - the same lesson the countdown boxes taught.
    /// </summary>
    internal static (int X, int Y, int W, int H) PlaceNear(Native.RECT icon)
    {
        nint monitor = Native.MonitorFromRect(ref icon, Native.MONITOR_DEFAULTTONEAREST);
        Native.RECT work = WorkAreaOf(monitor, icon);

        uint dpi = 96;
        if (Native.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            dpi = dpiX;

        int w = (int)Math.Round(WidthDip * dpi / 96.0);
        int h = (int)Math.Round(HeightDip * dpi / 96.0);
        int margin = (int)Math.Round(12 * dpi / 96.0);

        // Horizontally centred on the icon, vertically above or below it
        // depending on which side of the work area the taskbar sits.
        int x = icon.Left + icon.Width / 2 - w / 2;
        int y = icon.Top > (work.Top + work.Bottom) / 2
            ? work.Bottom - h - margin      // taskbar at the bottom
            : work.Top + margin;            // taskbar at the top

        if (x + w > work.Right - margin) x = work.Right - margin - w;
        if (x < work.Left + margin) x = work.Left + margin;

        return (x, y, w, h);
    }

    private static Native.RECT WorkAreaOf(nint monitor, Native.RECT fallback)
    {
        var mi = new Native.MONITORINFOEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEXW>(),
            szDevice = string.Empty,
        };
        return Native.GetMonitorInfoW(monitor, ref mi) ? mi.rcWork : fallback;
    }
}
