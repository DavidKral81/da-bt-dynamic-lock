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
/// the moment THIS Windows session started, worked out as "now minus how long
/// the machine has been up". That moment stays the same for as long as the
/// session lives and becomes a different one after a restart - so a note whose
/// session start no longer matches belongs to a session that is over. Sleep
/// moves the clock and the counter on together (measured 20.09.2026, see
/// Engine.Tests), so the session start stays put, which is exactly right:
/// closing the lid is not a restart, and the app must stay off over it.
///
/// ⚠ NOT the uptime on its own, which is what this did until review caught it
/// on 20.09.2026. A note written after eight hours of running came back to life
/// once the NEW session had also been up eight hours - so an app that crashed
/// that evening would not have been restarted, and the log would have said the
/// user switched it off. A note has to die with its session, not go quiet for a
/// while.
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
                SessionStart(when, uptimeMs).ToString("o", CultureInfo.InvariantCulture),
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
    public static bool Applies(string dataFolder, long uptimeMs) =>
        Applies(dataFolder, DateTime.Now, uptimeMs);

    /// <param name="now">The clock, handed in so a test can play a restart.</param>
    public static bool Applies(string dataFolder, DateTime now, long uptimeMs)
    {
        try
        {
            string[] lines = File.ReadAllLines(PathIn(dataFolder));
            if (lines.Length < 2
                || !DateTime.TryParse(lines[1], CultureInfo.InvariantCulture,
                                      DateTimeStyles.RoundtripKind, out DateTime was))
                return false;

            // A different session start means the computer has restarted since,
            // and the note went with the session that wrote it.
            //
            // A minute of slack, because "now minus uptime" is not exact to the
            // millisecond: the counter and the clock are read a moment apart,
            // and the clock itself can be nudged by time synchronisation. Well
            // inside that, and far short of any real restart.
            return Math.Abs((SessionStart(now, uptimeMs) - was).TotalSeconds) < 60;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Is this the first scheduled start the note turns away? Marks the note,
    /// so the log says it once rather than every five minutes - 288 identical
    /// lines a day would push everything else out of it.
    ///
    /// The mark lives in the note itself (a third line, which
    /// <see cref="Applies(string, DateTime, long)"/> ignores), so a new quit
    /// writes a fresh note and gets reported again. A note that cannot be read
    /// or marked answers true: one line too many beats none.
    /// </summary>
    public static bool FirstRefusal(string dataFolder)
    {
        try
        {
            string path = PathIn(dataFolder);
            if (File.ReadAllLines(path).Length > 2)
                return false;
            File.AppendAllLines(path, new[] { "reported" });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
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

    /// <summary>When this Windows session began.</summary>
    private static DateTime SessionStart(DateTime now, long uptimeMs) =>
        now.AddMilliseconds(-uptimeMs);
}
