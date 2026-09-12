namespace DaBtDynamicLock.App;

/// <summary>
/// Command line switches. The same three the Python version has, for the same
/// reason: the app has to be runnable next to the installed one without
/// touching it or locking anybody's screen.
/// </summary>
public sealed record Options
{
    /// <summary>Everything runs, but the screen is never really locked.</summary>
    public bool DryRun { get; init; }

    /// <summary>Quit on its own after this many seconds. 0 = run until closed.</summary>
    public double QuitAfterSeconds { get; init; }

    /// <summary>
    /// Open the panel, save a picture of it here, and quit. This is how the
    /// look gets checked - by LOOKING at it. Measuring widths and counting
    /// controls is what let a cramped panel and faint grey text through before.
    /// </summary>
    public string? ScreenshotFolder { get; init; }

    /// <summary>Where settings, the log and the history live for this run.</summary>
    public string DataFolder { get; init; } = AppInfo.DataFolder;

    public static Options Parse(string[] argv)
    {
        bool dryRun = argv.Contains("--dry-run");
        string? shots = ValueAfter(argv, "--screenshot");

        return new Options
        {
            DryRun = dryRun || shots is not null,
            QuitAfterSeconds = double.TryParse(ValueAfter(argv, "--quit-after"),
                System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 0,
            ScreenshotFolder = shots,
            // A test run must never write into the installed app's settings or
            // log: the shipped copy is somebody's working setup, and its config
            // holds the device they actually watch.
            //
            // Beside the executable rather than in TEMP - the same split the
            // Python version makes, where a packaged build writes to APPDATA
            // and one run from source writes next to itself. It also keeps a
            // test run's files somewhere the person running it expects to find
            // them, instead of scattering a named folder through TEMP.
            DataFolder = dryRun || shots is not null
                ? Path.Combine(AppContext.BaseDirectory, "dry-run-data")
                : AppInfo.DataFolder,
        };
    }

    /// <summary>
    /// The mutex name for this run. A dry run gets its own, so it can be
    /// started beside the installed app - which is the whole point of it.
    /// </summary>
    public string MutexName => DryRun ? AppInfo.MutexName + ".DryRun" : AppInfo.MutexName;

    public string SettingsPath => Path.Combine(DataFolder, "config.json");
    public string LogPath => Path.Combine(DataFolder, "dyn_lock.log");

    private static string? ValueAfter(string[] argv, string name)
    {
        int at = Array.IndexOf(argv, name);
        return at >= 0 && at + 1 < argv.Length ? argv[at + 1] : null;
    }

    /// <summary>
    /// Why the screenshot folder cannot be used, or null when it can.
    ///
    /// Checked rather than trusted, because a path handed in from outside can
    /// arrive cut in half: a caller that forgets to quote a path with spaces
    /// leaves only the first word of it, and writing to that means writing
    /// somewhere nobody meant. Measured on 13.09.2026, when exactly that
    /// created a folder two levels up from the intended one.
    /// </summary>
    public string? WhyScreenshotFolderIsUnusable()
    {
        if (ScreenshotFolder is null)
            return null;
        if (!Path.IsPathFullyQualified(ScreenshotFolder))
            return $"--screenshot needs a full path, and \"{ScreenshotFolder}\" is not one "
                + "(a path with spaces has to be quoted)";

        string? parent = Path.GetDirectoryName(ScreenshotFolder.TrimEnd('\\'));
        if (parent is not null && parent.Length > 0 && !Directory.Exists(parent))
            return $"--screenshot points into \"{parent}\", which does not exist "
                + "- the path looks cut short, which is what happens when it is unquoted";

        return null;
    }
}
