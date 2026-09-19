using System.Globalization;

namespace DaBtDynamicLock.Engine;

/// <summary>
/// Remembers that the USER switched the app off, so the scheduled task can put
/// it back after a crash without undoing what the person meant to do.
///
/// The task repeats every few minutes, and Task Scheduler cannot tell a crash
/// from "I turned it off" - it only knows the program is not running. The app
/// knows the difference, so it leaves this note behind and a scheduled start
/// reads it and goes away again.
///
/// ⚠ THE NOTE LASTS UNTIL THE COMPUTER RESTARTS, and that is a decision, not a
/// side effect (David, 19.09.2026, of the two offered): switching the app off
/// now does not mean never again - that is what the "start at logon" switch is
/// for. Restarting therefore brings the watching back.
///
/// How the restart is spotted without asking Windows anything: the note carries
/// the machine's uptime counter as it stood when the app was switched off. That
/// counter only ever climbs while a Windows session lives and starts from zero
/// after a restart - so a counter now LOWER than the one in the note means the
/// computer has been restarted since, and the note no longer applies. Sleep and
/// hibernation leave it untouched, which is exactly right: closing the lid is
/// not a restart, and the app must stay off over it.
/// </summary>
public static class QuitMarker
{
    public const string FileName = "quit-by-user.txt";

    private static string PathIn(string dataFolder) => Path.Combine(dataFolder, FileName);

    /// <summary>
    /// Writes the note. Returns what went wrong, or null.
    ///
    /// Takes the uptime as a parameter rather than reading it, so a test can
    /// play a restart without one.
    /// </summary>
    public static string? Write(string dataFolder, DateTime when, long uptimeMs)
    {
        try
        {
            Directory.CreateDirectory(dataFolder);
            File.WriteAllLines(PathIn(dataFolder), new[]
            {
                when.ToString("o", CultureInfo.InvariantCulture),
                uptimeMs.ToString(CultureInfo.InvariantCulture),
            });
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Said out loud, never swallowed: without the note a scheduled start
            // would switch the app back on a few minutes later, and the person
            // would be left wondering why it will not stay off.
            return $"the note about being switched off could not be written ({e.Message})";
        }
    }

    /// <summary>
    /// Does a note apply right now? False when there is none, when the computer
    /// has restarted since, or when the file cannot be made sense of.
    ///
    /// ⚠ An unreadable note reads as "no note", so the failure can only ever
    /// lead to MORE watching, never to a computer left unguarded. The same way
    /// round as an unreadable Wi-Fi network.
    /// </summary>
    public static bool Applies(string dataFolder, long uptimeMs)
    {
        try
        {
            string[] lines = File.ReadAllLines(PathIn(dataFolder));
            if (lines.Length < 2
                || !long.TryParse(lines[1], NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out long wasUp))
                return false;

            // Lower than when it was written: the counter went back to zero,
            // so the computer has restarted and the note has had its day.
            return uptimeMs >= wasUp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tears the note up - the app is being started deliberately. Quiet when
    /// there is nothing to tear up, which is the usual case.
    /// </summary>
    public static void Clear(string dataFolder)
    {
        try { File.Delete(PathIn(dataFolder)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
