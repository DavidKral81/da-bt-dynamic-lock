using System.Runtime.InteropServices;

namespace DaBtDynamicLock.Platform;

/// <summary>
/// One monitor's usable area, in physical pixels, taskbar already taken off.
/// </summary>
/// <param name="Left">Can be NEGATIVE - a monitor to the left of the primary one.</param>
public sealed record WorkArea(int Left, int Top, int Right, int Bottom, bool Primary)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Where the countdown can be shown.
///
/// The list is asked for EVERY time it is needed, never remembered: a monitor
/// can be unplugged between two countdowns, and a box on a screen that is no
/// longer there is a box nobody sees.
///
/// Not through Microsoft.UI.Windowing.DisplayArea, which would be the managed
/// way: it needs a WinUI runtime around it, and this layer has to stay callable
/// from a plain console so the checks can run without a window.
/// </summary>
public static class Monitors
{
    private const int MonitorInfoPrimary = 1;

    /// <summary>
    /// Every monitor's work area, the primary one first. On failure: one entry
    /// for the primary monitor, and a problem to log.
    /// </summary>
    public static Reading<IReadOnlyList<WorkArea>> WorkAreas()
    {
        var found = new List<WorkArea>();
        string? problem = null;

        // The delegate is kept in a local for the duration of the call. Handing
        // a freshly made delegate straight to native code lets the GC collect it
        // mid-callback - the trap the tray prototype hit.
        MonitorEnumProc callback = (IntPtr monitor, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = new MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(monitor, ref info))
                found.Add(new WorkArea(info.rcWork.Left, info.rcWork.Top,
                    info.rcWork.Right, info.rcWork.Bottom,
                    (info.dwFlags & MonitorInfoPrimary) != 0));
            else
                problem = $"GetMonitorInfo failed (error {Marshal.GetLastWin32Error()})";
            return true;    // carry on: one bad monitor must not lose the rest
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            problem = $"EnumDisplayMonitors failed (error {Marshal.GetLastWin32Error()})";

        GC.KeepAlive(callback);

        if (found.Count == 0)
            return Reading<IReadOnlyList<WorkArea>>.Failed(
                new[] { PrimaryFallback() },
                (problem ?? "no monitors were enumerated")
                    + " - falling back to the primary monitor alone");

        // Primary first, so "the one box" case and the first of many are the
        // same screen.
        return new Reading<IReadOnlyList<WorkArea>>(
            found.OrderByDescending(m => m.Primary).ToList(), problem);
    }

    /// <summary>
    /// The primary monitor's work area on its own. Used when the enumeration
    /// fails, and it is the whole screen if even this fails - a countdown in
    /// the wrong place still beats no countdown.
    /// </summary>
    private static WorkArea PrimaryFallback()
    {
        var rect = new Rect();
        if (SystemParametersInfoW(SpiGetWorkArea, 0, ref rect, 0))
            return new WorkArea(rect.Left, rect.Top, rect.Right, rect.Bottom, true);
        return new WorkArea(0, 0,
            GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen), true);
    }

    private const uint SpiGetWorkArea = 0x0030;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect clip, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip,
        MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint action, uint param,
        ref Rect data, uint winIni);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
