using DaBtDynamicLock.Platform;

namespace DaBtDynamicLock.Engine;

/// <summary>
/// Copies into the log the crashes of this program that Windows wrote down and
/// the log could not.
///
/// A crash inside WinUI - an exception in a DispatcherQueue callback, or a
/// native failure inside Microsoft.UI.Xaml.dll - ends the process before any
/// .NET handler runs, so the log stops mid-sentence. Measured 24.11.2026 on the
/// crash after a click on the tray icon: not one line. Windows writes such a
/// crash to its Application log (Application Error 1000), so the next start
/// asks for it and writes it down where a bug report will carry it.
///
/// Nothing is stored to remember what was already told: the log is that
/// record. Every line ends with the crash's report id, and a crash whose id is
/// already in the log (or in the one rotated file) is not told again. That
/// also needs no clock comparison - the log's times are local and carry no
/// zone, and a local-time comparison once broke the quit note when the clocks
/// went back.
/// </summary>
public static class CrashReport
{
    /// <summary>
    /// How far back Windows is asked. Two weeks: the log (2 MB plus one
    /// rotated file) covered about eight weeks on 24.11.2026, so a crash in
    /// this window that was told is still in the log to be recognised.
    /// </summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(14);

    /// <summary>
    /// Crashes told at one start. A crash at every start of the five-minute
    /// repeat makes 288 a day; each start then tells only its own new one, but
    /// the first start after a fix would otherwise bury the log.
    /// </summary>
    public const int MostLines = 10;

    /// <summary>
    /// Asks Windows about this program's crashes and writes the ones the log
    /// does not mention yet. Never throws: everything that goes wrong is a line
    /// in the log.
    /// </summary>
    /// <param name="programPath">The file this copy runs from. Crashes of other
    /// copies - the installed app, a dry run, a build - belong to their own logs.</param>
    /// <param name="recordsSince">Windows' answer from that UTC time on;
    /// <see cref="CrashRecords.Since"/> outside the tests.</param>
    public static void WriteEarlierCrashes(Log log, string logPath, string programPath,
        Func<DateTime, Reading<IReadOnlyList<CrashRecord>>> recordsSince, DateTime nowUtc)
    {
        // A partial answer still carries the records that could be read, so
        // the problem is said and those are told all the same.
        var records = recordsSince(nowUtc - LookBack);
        if (!records.Ok)
            log.Write($"Windows' record of this program's crashes could not be read "
                + $"({records.Problem}) - a crash before this start may have gone unnoted.");
        if (records.Value.Count == 0)
            return;

        string told;
        try
        {
            told = ReadIfThere(logPath + ".1") + ReadIfThere(logPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not knowing what was told would mean telling everything again at
            // every start - better to say once that this start could not check.
            log.Write($"The log could not be read back ({e.Message}) - crashes Windows "
                + "recorded are not copied into it at this start.");
            return;
        }

        var fresh = records.Value
            .Where(r => SamePath(r.AppPath, programPath))
            .Where(r => !told.Contains(Tag(r), StringComparison.Ordinal))
            .OrderByDescending(r => r.TimeUtc)
            .ToList();

        foreach (var r in fresh.Take(MostLines).Reverse())
            log.Write(Describe(r));
        if (fresh.Count > MostLines)
            log.Write($"...and {fresh.Count - MostLines} more crashes Windows recorded in the "
                + $"last {LookBack.TotalDays:F0} days, earlier than these.");
    }

    /// <summary>One line about one crash - in English, like the rest of the log.</summary>
    public static string Describe(CrashRecord r)
    {
        string code = $"0x{r.ExceptionCode:X8}";
        string? meaning = Meaning(r.ExceptionCode);
        // The module's NAME only. Its folder is often the unpacked copy in
        // %TEMP%, under the user's profile - the user's name has no business in
        // a log that gets attached to bug reports.
        return $"Windows recorded a crash of this program at "
            + $"{r.TimeUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}, which the log could not write down: "
            + $"exception {code}{(meaning is null ? "" : $" ({meaning})")} "
            + $"in {r.ModuleName} {r.ModuleVersion}, version {r.AppVersion}, "
            + $"process {r.ProcessId} {Tag(r)}";
    }

    /// <summary>
    /// What names the crash in the log. Windows' report id where it gave one;
    /// otherwise the process and the moment it started, which together name
    /// one run.
    /// </summary>
    private static string Tag(CrashRecord r) =>
        string.IsNullOrEmpty(r.ReportId)
            ? $"[run {r.ProcessId}-{r.ProcessCreated:x}]"
            : $"[report {r.ReportId}]";

    /// <summary>
    /// The codes this app has met, or is likely to. Only well-known Windows
    /// status codes; anything else is left as the bare number.
    /// </summary>
    private static string? Meaning(uint code) => code switch
    {
        0xC000027B => "an error inside WinUI that nothing caught",
        0xE0434352 => "a .NET exception that nothing caught",
        0xC0000005 => "access violation",
        0xC00000FD => "stack overflow",
        0xC0000409 => "the program ended itself on a fatal error",
        _ => null,
    };

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            // A path Windows wrote that cannot even be read is not this program.
            return false;
        }
    }

    /// <summary>
    /// Shared for writing: the radio's thread may be appending a line right
    /// now, and File.ReadAllText would refuse a file somebody holds open to write.
    /// </summary>
    private static string ReadIfThere(string path)
    {
        if (!File.Exists(path))
            return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
