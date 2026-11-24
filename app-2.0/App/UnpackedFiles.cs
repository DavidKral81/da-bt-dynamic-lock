namespace DaBtDynamicLock.App;

/// <summary>
/// Keeps the files the single-file build unpacked at start from being deleted
/// while the app runs.
///
/// The program unpacks itself into %TEMP%\.net\DaBtDynamicLock\&lt;random&gt;
/// (AppContext.BaseDirectory, see AppInfo.ProgramFolder). Temp cleanup - Storage
/// Sense, Disk Cleanup - may delete anything there that no process holds. The
/// app runs for weeks, and most of those files (the compiled XAML, the resource
/// indexes, libraries loaded only when a window opens) are not held until they
/// are needed. Measured 24.11.2026: after that, the next click on the tray icon
/// killed the app inside Microsoft.UI.Xaml.dll (0xC000027B, file not found),
/// with nothing in the log. A fresh start unpacks the missing files again, so
/// only the deletion DURING a run does harm.
///
/// Holding a read handle without FileShare.Delete makes such a deletion fail;
/// reading and loading the files stays possible. tools\check_extracted_files.ps1
/// plays the deletion against a built program.
/// </summary>
public static class UnpackedFiles
{
    /// <summary>Open for as long as the process lives; never closed on purpose.</summary>
    private static readonly List<FileStream> Held = [];

    /// <summary>
    /// Opens every unpacked file. Returns a line for the log, or null when the
    /// program is not running unpacked (a plain build has nothing to protect).
    /// Never throws: a file that cannot be held is reported, not fatal.
    /// </summary>
    public static string? Hold()
    {
        string unpacked = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/');
        string program = Path.GetFullPath(AppInfo.ProgramFolder).TrimEnd('\\', '/');
        if (string.Equals(unpacked, program, StringComparison.OrdinalIgnoreCase))
            return null;

        int failed = 0;
        string? firstProblem = null;
        try
        {
            foreach (string path in Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories))
            {
                try
                {
                    Held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    firstProblem ??= $"{Path.GetFileName(path)}: {e.Message}";
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The unpacked program files could not be listed, so temp cleanup may "
                + $"delete them while the app runs ({e.Message}).";
        }

        return failed == 0
            ? $"Holding {Held.Count} unpacked program files, so temp cleanup cannot delete them."
            : $"Holding {Held.Count} unpacked program files; {failed} could not be held and "
              + $"temp cleanup may delete them while the app runs (first: {firstProblem}).";
    }
}
