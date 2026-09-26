using System.Runtime.InteropServices;

namespace DaBtDynamicLock.Platform;

/// <summary>
/// Whether the screen is locked.
///
/// Nothing is decided behind a locked screen: the countdown would be drawn
/// behind it and locking would lock what is already locked. In the shipped 1.5
/// log that mistake accounted for 41 of 68 "locks".
///
/// A failure here reads as UNLOCKED, deliberately. If a broken check guessed
/// "locked", watching would switch itself off for good; guessing "unlocked" can
/// only ever lead to more locking, never less.
///
/// There is no managed way to ask - measured 13.09.2026: SystemEvents.
/// SessionSwitch is an event, so it says nothing about the state at startup,
/// and .NET exposes nothing else. Hence the P/Invoke.
/// </summary>
public static class SessionState
{
    private const int WtsCurrentSession = -1;
    private const int WtsSessionInfoEx = 25;

    /// <summary>WTS_SESSIONSTATE_LOCK - the value SessionFlags takes when locked.</summary>
    private const int SessionFlagLocked = 0;

    /// <summary>WTS_SESSIONSTATE_UNLOCK.</summary>
    private const int SessionFlagUnlocked = 1;

    /// <summary>
    /// True while the lock screen is up. Falls back to the desktop test if the
    /// session query fails, and to "unlocked" if both do.
    /// </summary>
    public static Reading<bool> IsLocked()
    {
        var session = FromSession();
        if (session.Ok)
            return session;

        // The desktop test answers a narrower question - is the secure desktop
        // in front RIGHT NOW - and that is only true while the lock screen is
        // being drawn. Measured 29.08.2026: it reported "unlocked" one second
        // after every lock. Good enough as a fallback, useless as the main way.
        var desktop = FromInputDesktop();
        return desktop.Ok
            ? desktop with { Problem = session.Problem }
            : Reading<bool>.Failed(false,
                $"{session.Problem}; the desktop test failed too ({desktop.Problem}) "
                + "- carrying on as if the screen were unlocked");
    }

    /// <summary>
    /// Asks Windows about the session itself. The main way since 29.08.2026.
    /// </summary>
    private static Reading<bool> FromSession()
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession,
                WtsSessionInfoEx, out IntPtr buffer, out uint size))
            return Reading<bool>.Failed(false,
                $"WTSQuerySessionInformation failed (error {Marshal.GetLastWin32Error()})");

        try
        {
            // A structure laid out wrongly does not raise - it returns
            // plausible-looking rubbish. Two cheap proofs that it did not:
            // Windows returned exactly as many bytes as are declared here, and
            // the flags are one of the two documented values.
            //
            // EXACTLY, not "at least": a structure declared too short still
            // fits inside the answer. Measured 13.09.2026 - shortening one
            // string by a character sailed past an "at least" check, and only
            // comparing the user name against the real account caught it.
            int expected = Marshal.SizeOf<WtsInfoEx>();
            if (size != expected)
                return Reading<bool>.Failed(false,
                    $"WTSQuerySessionInformation returned {size} B, expected exactly "
                    + $"{expected} - the structure is not laid out as declared");

            var info = Marshal.PtrToStructure<WtsInfoEx>(buffer);
            int flags = info.Data.SessionFlags;
            if (flags != SessionFlagLocked && flags != SessionFlagUnlocked)
                return Reading<bool>.Failed(false,
                    $"session flags {flags} are not one of the documented values");

            return new Reading<bool>(flags == SessionFlagLocked);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// The old way, kept only as a fallback: can this process open the desktop
    /// that is receiving input? The secure desktop refuses.
    /// </summary>
    private static Reading<bool> FromInputDesktop()
    {
        IntPtr desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
            return new Reading<bool>(true);   // refused = the secure desktop is up
        CloseDesktop(desktop);
        return new Reading<bool>(false);
    }

    private const uint DesktopReadObjects = 0x0001;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId,
        int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    // Returns a HANDLE. Declared as IntPtr rather than left to guesswork: a
    // handle read as a 32-bit int is cut in half on 64-bit Windows, and the
    // damage shows up at random. The project has that one written down.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoEx
    {
        public uint Level;
        public WtsInfoExLevel1 Data;
    }

    // WTSINFOEX_LEVEL1_W. The string lengths are the ones Windows declares
    // (WINSTATIONNAME_LENGTH + 1, USERNAME_LENGTH + 1, DOMAIN_LENGTH + 1) - a
    // short one shifts everything after it and the padding can hide the damage,
    // so the size check above is what actually guards this.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoExLevel1
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string DomainName;
        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
    }

    /// <summary>
    /// When the user signed in to this Windows session, in UTC.
    ///
    /// The identity of a sign-in: it stays the same across sleep, locking and
    /// unlocking, and is a different moment after signing out, restarting, or
    /// shutting down with fast startup - which does NOT reset the uptime
    /// counter, so uptime cannot tell those apart. The note the app leaves when
    /// the user switches it off lasts exactly this long.
    ///
    /// Checked against the CurrentTime field of the same answer: a shifted
    /// structure gives a number, not an error, and a sign-in in the future or
    /// before 2000 is how that rubbish would show.
    /// </summary>
    public static Reading<DateTime?> SignedInAt()
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession,
                WtsSessionInfoEx, out IntPtr buffer, out uint size))
            return Reading<DateTime?>.Failed(null,
                $"WTSQuerySessionInformation failed (error {Marshal.GetLastWin32Error()})");
        try
        {
            if (size != Marshal.SizeOf<WtsInfoEx>())
                return Reading<DateTime?>.Failed(null, $"returned {size} B, expected exactly "
                    + $"{Marshal.SizeOf<WtsInfoEx>()}");

            var data = Marshal.PtrToStructure<WtsInfoEx>(buffer).Data;
            long earliest = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
            if (data.LogonTime < earliest || data.LogonTime > data.CurrentTime)
                return Reading<DateTime?>.Failed(null,
                    $"the sign-in time {data.LogonTime} makes no sense against the current "
                    + $"time {data.CurrentTime}");

            return new Reading<DateTime?>(DateTime.FromFileTimeUtc(data.LogonTime));
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// The user name Windows reports for this session, for the layout check in
    /// the tests. Compared there against the account the test runs under - a
    /// shifted field gives rubbish, and checking the first string alone is not
    /// enough (a deliberately wrong length once slipped past it, absorbed by
    /// the padding).
    /// </summary>
    public static Reading<string> SessionUserName()
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession,
                WtsSessionInfoEx, out IntPtr buffer, out uint size))
            return Reading<string>.Failed("",
                $"WTSQuerySessionInformation failed (error {Marshal.GetLastWin32Error()})");
        try
        {
            if (size != Marshal.SizeOf<WtsInfoEx>())
                return Reading<string>.Failed("", $"returned {size} B, expected exactly "
                    + $"{Marshal.SizeOf<WtsInfoEx>()}");
            return new Reading<string>(Marshal.PtrToStructure<WtsInfoEx>(buffer).Data.UserName);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}
