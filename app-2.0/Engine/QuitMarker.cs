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
/// ⚠ THE NOTE LASTS UNTIL THE USER SIGNS IN AGAIN - after signing out,
/// restarting, or shutting down. Switching the app off now does not mean never
/// again; that is what the "start at logon" switch is for.
///
/// How that is spotted: the note carries the moment the user signed in to this
/// Windows session, as Windows reports it. That moment survives sleep, locking
/// and unlocking, and is a different one after any new sign-in.
///
/// ⚠ NOT the uptime, which is what this used until review on 26.09.2026. Uptime
/// does not start again after signing out, nor after "Shut down" with fast
/// startup (the Windows default) - so a note written in the evening still held
/// the next morning, the app did not start at sign-in, and nothing said so. An
/// earlier version compared uptimes alone and came back to life once a new
/// session had run as long as the old one. Both were ways of guessing the
/// session; the sign-in time names it.
/// </summary>
public static class QuitMarker
{
    public const string FileName = "quit-by-user.txt";

    private static string PathIn(string dataFolder) => Path.Combine(dataFolder, FileName);

    /// <summary>
    /// Writes the note. Returns what went wrong, or null.
    ///
    /// Takes the sign-in time as a parameter rather than asking Windows, so a
    /// test can play a new sign-in without one.
    /// </summary>
    public static string? Write(string dataFolder, DateTime when, DateTime? signedInAt)
    {
        // Said out loud, never skipped quietly: a note that does not know its
        // sign-in could never be matched, and the app would come back a few
        // minutes after every deliberate quit with nobody knowing why.
        if (signedInAt is not DateTime signedIn)
            return "the note about being switched off needs the sign-in time, "
                + "and Windows did not give it";
        try
        {
            Directory.CreateDirectory(dataFolder);
            File.WriteAllLines(PathIn(dataFolder), new[]
            {
                when.ToString("o", CultureInfo.InvariantCulture),
                signedIn.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
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
    /// Does a note apply right now? False when there is none, when the user has
    /// signed in again since, when the sign-in time is unknown, or when the file
    /// cannot be made sense of.
    ///
    /// ⚠ Every doubt reads as "no note", so a failure can only ever lead to
    /// MORE watching, never to a computer left unguarded. The same way round as
    /// an unreadable Wi-Fi network.
    /// </summary>
    public static bool Applies(string dataFolder, DateTime? signedInAt)
    {
        if (signedInAt is not DateTime signedIn)
            return false;
        try
        {
            string[] lines = File.ReadAllLines(PathIn(dataFolder));
            if (lines.Length < 2
                || !DateTime.TryParse(lines[1], CultureInfo.InvariantCulture,
                                      DateTimeStyles.RoundtripKind, out DateTime was))
                return false;

            // A second of slack for the round trip through text; any real new
            // sign-in is a different moment by far more than that.
            return Math.Abs((signedIn.ToUniversalTime() - was.ToUniversalTime())
                .TotalSeconds) < 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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
    /// <see cref="Applies"/> ignores), so a new quit writes a fresh note and
    /// gets reported again. A note that cannot be read or marked answers true:
    /// one line too many beats none.
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
}
