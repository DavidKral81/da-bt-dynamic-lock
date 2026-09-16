using DaBtDynamicLock.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DaBtDynamicLock.App;

/// <summary>
/// One countdown box, belonging to one monitor.
///
/// It never takes focus: it appears over whatever the user is doing, and a
/// warning that steals the keyboard mid-sentence would be worse than the lock
/// it warns about.
/// </summary>
public sealed partial class CountdownWindow : Window
{
    /// <summary>
    /// Used only if measuring the content fails. The size is normally taken
    /// FROM THE CONTENT: a guessed width cut "Zamknutí za 9 s" in half the
    /// first time this was photographed, and Czech is the longer language.
    /// </summary>
    private const int FallbackWidthDip = 340;
    private const int FallbackHeightDip = 76;

    private readonly nint _handle;

    public CountdownWindow()
    {
        InitializeComponent();
        _handle = WindowNative.GetWindowHandle(this);

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.IsShownInSwitchers = false;

        // Clicks go THROUGH the box to the window underneath, and even a click
        // on it cannot take the keyboard. Show(false) alone only keeps it from
        // activating when it appears; the box sits mid-screen over whatever is
        // being worked in, so a click there would otherwise be swallowed. The
        // same three styles 1.x put on its box - the first 2.0 had lost them.
        //
        // Plus LAYERED, which 1.x did not need: measured, a WinUI window with
        // TRANSPARENT alone still took the click. Windows only lets a click
        // through a window that is both. A layered window stays invisible until
        // it is given an opacity, hence the full 255 straight after.
        int style = Native.GetWindowLongW(_handle, Native.GWL_EXSTYLE);
        Native.SetWindowLongW(_handle, Native.GWL_EXSTYLE, style
            | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
            | Native.WS_EX_LAYERED);
        Native.SetLayeredWindowAttributes(_handle, 0, 255, Native.LWA_ALPHA);

        AppWindow.Closing += (_, e) => { e.Cancel = true; AppWindow.Hide(); };
    }

    /// <summary>Which work area this box belongs to, so it can be told when to move.</summary>
    public WorkArea? Screen { get; private set; }

    /// <summary>
    /// Shows the box in the middle of its screen, at the height the user chose.
    ///
    /// The position is worked out from the size asked for rather than measured
    /// off the window: the window is still hidden while it is being placed (or
    /// it would flash in a corner first), and a hidden window measures 1 px.
    /// </summary>
    public void ShowOn(WorkArea screen, double verticalShare, string text, string widestText)
    {
        Screen = screen;
        // Measured against the WIDEST text this countdown will ever show, not
        // against the one going up now: the number shrinks from two digits to
        // one as it counts down, and a box that resized every second would
        // twitch on screen.
        Message.Text = widestText;

        var root = Root();
        root.Measure(new Windows.Foundation.Size(double.PositiveInfinity,
            double.PositiveInfinity));
        var wanted = root.DesiredSize;

        // The scale comes from the MONITOR, not from XamlRoot: the window is
        // still hidden while it is being placed (showing it first would make it
        // flash in a corner) and a hidden window has no XamlRoot, so that route
        // silently reports 1.0 - which on a 125 % screen makes the box a fifth
        // too small and cuts the text off.
        double scale = ScaleOf(screen);
        int w = wanted.Width > 0
            ? (int)Math.Ceiling(wanted.Width * scale)
            : (int)Math.Round(FallbackWidthDip * scale);
        int h = wanted.Height > 0
            ? (int)Math.Ceiling(wanted.Height * scale)
            : (int)Math.Round(FallbackHeightDip * scale);

        Message.Text = text;

        int x = screen.Left + (screen.Width - w) / 2;
        int y = screen.Top + (int)Math.Round((screen.Height - h) * verticalShare);

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        // Show(false): activating it would take the keyboard away from whatever
        // the user is typing into.
        AppWindow.Show(false);
    }

    /// <summary>Just the number, when the box is already up on the right screen.</summary>
    public void Update(string text) => Message.Text = text;

    public void HideBox() => AppWindow.Hide();

    public nint Handle => _handle;

    private FrameworkElement Root() => (FrameworkElement)Content;

    /// <summary>How many physical pixels one DIP is on the screen this box is on.</summary>
    private static double ScaleOf(WorkArea screen)
    {
        var rect = new Native.RECT
        {
            Left = screen.Left,
            Top = screen.Top,
            Right = screen.Right,
            Bottom = screen.Bottom,
        };
        nint monitor = Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;
    }
}
