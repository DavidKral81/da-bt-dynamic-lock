using System.Runtime.InteropServices;

namespace DaBtDynamicLock.Platform;

/// <summary>
/// Is something filling the whole screen right now - a video, a presentation,
/// a game?
///
/// Used by the guard that refuses to lock while the person is plainly at the
/// computer but not touching anything. Watching a film is the case the typing
/// guard cannot cover: nobody moves the mouse for twenty minutes, and the
/// phone in a pocket across the room is not proof that the chair is empty.
///
/// ⚠ A FAILED READING MEANS "no, nothing is full screen", so a broken check
/// can only ever lead to MORE locking, never to a computer left open. Same way
/// round as the idle time and the Wi-Fi network.
/// </summary>
public static class FullScreenApp
{
    public static Reading<bool> Running()
    {
        // Windows already answers this for its own notifications, and it is the
        // answer that matches what the user means: it covers exclusive
        // full-screen games (D3D), presentation mode, and an ordinary window
        // blown up to fill the screen.
        int result = SHQueryUserNotificationState(out QUNS state);
        if (result != 0)
            return Reading<bool>.Failed(false,
                $"SHQueryUserNotificationState failed (0x{result:X8}) - "
                + "assuming nothing is full screen");

        switch (state)
        {
            case QUNS.Busy:
            case QUNS.RunningD3DFullScreen:
            case QUNS.PresentationMode:
            case QUNS.App:
                return new Reading<bool>(true);

            // The screen is locked or a screen saver is on. Not full screen,
            // and the loop pauses on a locked screen anyway.
            case QUNS.NotPresent:
                return new Reading<bool>(false);
        }

        // Windows says notifications are fine, which is not quite the same
        // question: a browser playing a video full screen has been seen to
        // answer that way. So the foreground window is measured against the
        // screen it is on.
        return new Reading<bool>(ForegroundCoversItsScreen());
    }

    /// <summary>
    /// Does the window in front cover the whole of its monitor?
    ///
    /// The desktop itself always does, so it is ruled out by class name - it is
    /// the one window that is full screen and means the opposite.
    /// </summary>
    private static bool ForegroundCoversItsScreen()
    {
        nint window = GetForegroundWindow();
        if (window == 0)
            return false;

        var className = new System.Text.StringBuilder(64);
        if (GetClassNameW(window, className, className.Capacity) > 0)
        {
            string name = className.ToString();
            // Progman and WorkerW are the desktop; Shell_TrayWnd is the task
            // bar. All three fill a screen and none of them is somebody
            // watching something.
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd")
                return false;
        }

        if (!GetWindowRect(window, out Rect rect))
            return false;

        nint monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
            return false;

        // The MONITOR rectangle, not the work area: a window that merely fills
        // the space above the task bar is an ordinary maximised window, and a
        // maximised text editor is not something anybody is watching.
        return rect.Left <= info.rcMonitor.Left
            && rect.Top <= info.rcMonitor.Top
            && rect.Right >= info.rcMonitor.Right
            && rect.Bottom >= info.rcMonitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>What Windows thinks the user is doing.</summary>
    private enum QUNS
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUNS state);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hWnd,
        System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect rect);

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfo info);
}
