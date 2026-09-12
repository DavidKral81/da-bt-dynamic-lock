using System.Runtime.InteropServices;

namespace DaBtDynamicLock.Platform;

/// <summary>
/// How long nobody has touched the keyboard or the mouse.
///
/// Used by the guard that refuses to lock while somebody is clearly working.
/// .NET has nothing for this - checked 13.09.2026 - so the P/Invoke stays.
/// </summary>
public static class UserIdle
{
    /// <summary>
    /// What a failed reading claims. "Nobody is here" rather than "somebody is",
    /// so a broken check can only lead to more locking, never to a machine that
    /// stays open. It is a stand-in, not a measurement, and the caller logs it.
    /// </summary>
    public const double UnknownSeconds = 999;

    /// <summary>Seconds since the last keyboard or mouse input.</summary>
    public static Reading<double> Seconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return Reading<double>.Failed(UnknownSeconds,
                $"GetLastInputInfo failed (error {Marshal.GetLastWin32Error()}) - "
                + $"assuming {UnknownSeconds:F0} s of idleness");

        // Both are 32-bit tick counts that wrap after 49 days. Subtracting them
        // as unsigned wraps the same way and comes out right; widening to
        // TickCount64 first would give a wildly wrong answer across the wrap.
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return new Reading<double>(idleMs / 1000.0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
