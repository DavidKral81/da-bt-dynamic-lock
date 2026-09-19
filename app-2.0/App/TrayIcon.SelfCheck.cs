using System;
using System.Collections.Generic;

namespace DaBtDynamicLock.App;

/// <summary>
/// What the tray icon does when the shell throws it away - the failure that
/// killed 2.0 on 18.09.2026.
///
/// The log of that day tells the whole story: Shell_NotifyIconW(MODIFY) began
/// to fail at 12:16:50, failed 3305 times, and at 15:01:42 the icon could not
/// even be BUILT any more (CreateIconIndirect failed: 0) because every failed
/// update had left one icon behind. Eight minutes later the app tried to open
/// a countdown box, had no handles left to do it with, and died without
/// writing a line.
///
/// So this check does not reason about the bug, it stages it: the icon really
/// is removed from the tray behind the app's back, and the counts Windows
/// keeps are read before and after.
/// </summary>
internal sealed partial class TrayIcon
{
    /// <summary>How many failed updates to stage. 3305 happened for real.</summary>
    private const int LeakCycles = 200;

    /// <summary>
    /// Room for the odd object something else in the process allocates while
    /// this runs. The bug leaks one icon per cycle, so anything near the cycle
    /// count is the bug and anything near zero is not.
    /// </summary>
    private const int LeakAllowance = 20;

    internal static IReadOnlyList<string> SelfCheck()
    {
        var lines = new List<string>();
        TrayIcon? tray = null;
        try
        {
            tray = new TrayIcon(AppInfo.Name + ".SelfCheckTrayWindow");
            tray.Show(MakeIcon(32, IconArt.Off), "self-check");

            // The real situation, not an imitation of it: the icon is deleted
            // from the tray without telling the TrayIcon, which is exactly what
            // it is left with when the shell rebuilds the notification area.
            var gone = tray.NewData();
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref gone);

            uint before = Native.GetGuiResources(Native.GetCurrentProcess(), Native.GR_USEROBJECTS);
            int refused = 0;
            for (int i = 0; i < LeakCycles; i++)
            {
                try { tray.Show(MakeIcon(32, IconArt.Off), "self-check"); }
                catch (InvalidOperationException) { refused++; }
            }
            uint after = Native.GetGuiResources(Native.GetCurrentProcess(), Native.GR_USEROBJECTS);
            long leaked = (long)after - before;

            // Without this the whole check is deaf: if the updates quietly
            // SUCCEEDED, nothing leaked and the count would look perfect.
            lines.Add(refused == LeakCycles
                ? $"  OK    an icon removed behind the app's back really does fail to update ({refused}x)"
                : $"  FAIL  staging the failure did not work: {refused} of {LeakCycles} updates failed");

            lines.Add(leaked <= LeakAllowance
                ? $"  OK    {LeakCycles} failed icon updates leaked nothing ({leaked} objects)"
                : $"  FAIL  {LeakCycles} failed icon updates leaked {leaked} objects - this is what ran the app out of handles");

            // And the reason those updates failed at all: nothing was listening
            // for the message that says the tray was rebuilt, so the icon was
            // gone for good and every later update had an empty tray to talk to.
            Native.SendMessageW(tray.WindowHandle,
                Native.RegisterWindowMessageW("TaskbarCreated"), 0, 0);

            // ⚠ ASKED BEFORE ANYTHING ELSE TOUCHES THE ICON, because the fault
            // this catches is undone by the very next update. Putting the icon
            // back hands Show() the icon it is already holding, so without the
            // "not the one just handed over" guard it destroys what it has just
            // given Windows to draw. Windows accepts a destroyed icon without a
            // word - it only shows as a broken picture by the clock - and the
            // next Show() then replaces it with a healthy one. Measured: asked
            // one line later, this check passed the sabotage it exists for.
            bool aliveAfterRebuild = Alive(tray._hIcon);

            string? trouble = null;
            try { tray.Show(MakeIcon(32, IconArt.Off), "self-check"); }
            catch (Exception e) { trouble = e.Message; }

            lines.Add(trouble is null
                ? "  OK    the icon comes back after the tray is rebuilt"
                : $"  FAIL  the icon stays lost after the tray is rebuilt ({trouble})");

            lines.Add(aliveAfterRebuild
                ? "  OK    ...and comes back as a live icon"
                : "  FAIL  the icon put back into the tray has been destroyed - it would draw as a broken picture");
        }
        catch (Exception e)
        {
            // Said out loud rather than swallowed: a check that dies halfway
            // looks just like one that passed.
            lines.Add($"  FAIL  the tray icon check stopped on an error: {e}");
        }
        finally
        {
            // In finally, not after the last check: a failure here would
            // otherwise leave a stray icon sitting in the user's tray.
            tray?.Dispose();
        }
        return lines;
    }

    /// <summary>Is this handle still a live icon?</summary>
    private static bool Alive(nint icon)
    {
        if (icon == 0 || !Native.GetIconInfo(icon, out Native.ICONINFO info))
            return false;
        // The bitmaps it just handed over belong to the caller now.
        if (info.hbmColor != 0) Native.DeleteObject(info.hbmColor);
        if (info.hbmMask != 0) Native.DeleteObject(info.hbmMask);
        return true;
    }
}
