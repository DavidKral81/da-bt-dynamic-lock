using System.Runtime.InteropServices;

namespace DaBtDynamicLock.Platform;

/// <summary>Locking the screen - the one thing the whole app exists to do.</summary>
public static class ScreenLock
{
    /// <summary>
    /// Locks the workstation.
    ///
    /// The result MUST be acted on. In 1.4 the return value was ignored and the
    /// app went on to record "Locking" and disarm itself, so a failed lock left
    /// the screen open while the log said otherwise - the one failure this app
    /// cannot afford to hide.
    ///
    /// This cannot run as a service or a scheduled task in session 0: there is
    /// no desktop there and LockWorkStation locks nothing.
    /// </summary>
    public static Reading<bool> Lock()
    {
        if (LockWorkStation())
            return new Reading<bool>(true);
        return Reading<bool>.Failed(false,
            $"LockWorkStation failed (error {Marshal.GetLastWin32Error()}) - "
            + "the screen is still unlocked");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();
}
