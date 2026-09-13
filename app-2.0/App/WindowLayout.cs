using DaBtDynamicLock.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DaBtDynamicLock.App;

/// <summary>
/// Where a window of a given size belongs on screen.
///
/// One place for it, because two windows now ask the same question and the
/// answer carries two lessons that are easy to get wrong separately.
/// </summary>
public static class WindowLayout
{
    /// <summary>
    /// A window of this many DIPs, centred on the primary screen, in the
    /// physical pixels MoveAndResize wants.
    /// </summary>
    public static (int X, int Y, int W, int H) Centred(int widthDip, int heightDip)
    {
        var screens = Monitors.WorkAreas();
        var screen = screens.Value.FirstOrDefault(s => s.Primary)
            ?? screens.Value.FirstOrDefault();

        if (screen is null)
            return (120, 120, widthDip, heightDip);

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
        int w = Math.Min((int)Math.Round(widthDip * scale), screen.Width);
        int h = Math.Min((int)Math.Round(heightDip * scale), screen.Height);

        return (screen.Left + (screen.Width - w) / 2,
                screen.Top + (screen.Height - h) / 2, w, h);
    }

    /// <summary>
    /// Paints a window's title bar to match what is inside it.
    ///
    /// An unpackaged WinUI 3 window does NOT get the system theme on its title
    /// bar: the first picture taken of this app showed a white strip over a dark
    /// window. The colours are read from the same theme brushes the content
    /// uses, so there is one decision about how a window looks rather than one
    /// per window.
    /// </summary>
    public static void PaintTitleBar(AppWindow window, Panel root)
    {
        var bar = window.TitleBar;
        if ((root.Background as SolidColorBrush)?.Color is not Windows.UI.Color colour)
            return;

        bar.BackgroundColor = colour;
        bar.InactiveBackgroundColor = colour;
        bar.ButtonBackgroundColor = colour;
        bar.ButtonInactiveBackgroundColor = colour;

        // The foreground follows the content's, so this stays right in either
        // theme instead of being a hard-coded white that vanishes on a light one.
        if ((Application.Current.Resources["TextFillColorPrimaryBrush"]
            as SolidColorBrush)?.Color is Windows.UI.Color ink)
        {
            bar.ForegroundColor = ink;
            bar.ButtonForegroundColor = ink;
            bar.ButtonHoverForegroundColor = ink;
        }
    }
}
