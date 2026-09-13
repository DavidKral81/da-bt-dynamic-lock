namespace DaBtDynamicLock.App;

/// <summary>What this run is: the app itself, or setup putting it in or taking it out.</summary>
public enum SetupRole
{
    Install,
    Uninstall,
}

/// <summary>
/// Command line switches. The same three the Python version has, for the same
/// reason: the app has to be runnable next to the installed one without
/// touching it or locking anybody's screen.
/// </summary>
public sealed record Options
{
    /// <summary>
    /// Set when this run is an installation or a removal rather than the app.
    ///
    /// There is no separate installer program in 2.0: the application IS the
    /// installer, published as one file under another name. Measured on
    /// 13.09.2026 - a second program would have had to carry its own copy of
    /// .NET, which is 72 MB, more than the whole application takes.
    /// </summary>
    public SetupRole? Setup { get; init; }

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

    /// <summary>
    /// Work the settings window's controls the way a person would and check
    /// that each one lands in the settings file. What a picture cannot show -
    /// a screenshot proves a switch is drawn, never that flipping it saves
    /// anything.
    /// </summary>
    public bool SelfCheck { get; init; }

    /// <summary>
    /// Turn start at logon on or off and quit, with the exit code saying
    /// whether it worked. This is how the installer sets it up: the app owns
    /// that job, the way it owns the switch in its own window, so the two
    /// cannot drift into registering different tasks.
    /// </summary>
    public bool? Autostart { get; init; }

    /// <param name="processPath">The file this is running from. Handed in
    /// rather than read here, so the rule that decides the role can be checked
    /// without moving or renaming an executable.</param>
    /// <param name="installedDir">Where an installation puts the program.
    /// Running from anywhere else means this copy was downloaded, not
    /// installed.</param>
    public static Options Parse(string[] argv, string? processPath = null,
        string? installedDir = null)
    {
        processPath ??= Environment.ProcessPath;

        bool dryRun = argv.Contains("--dry-run");
        bool selfCheck = argv.Contains("--self-check");
        string? shots = ValueAfter(argv, "--screenshot");
        double quitAfter = double.TryParse(ValueAfter(argv, "--quit-after"),
            System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 0;

        bool? autostart = argv.Contains("--autostart-on") ? true
            : argv.Contains("--autostart-off") ? false
            : null;

        // Setup, but never instead of a run that was asked for explicitly: a
        // picture or self-check run of a file that happens to be named setup
        // still has to take pictures.
        //
        // A dry run and a timed run count as "asked for" too. Without that,
        // "setup.exe --dry-run" would ask for administrator rights and offer to
        // install - the opposite of what a dry run is for.
        bool otherJob = dryRun || selfCheck || shots is not null
            || autostart is not null || quitAfter > 0;
        SetupRole? role = otherJob ? null
            : argv.Contains("--uninstall") ? SetupRole.Uninstall
            : argv.Contains("--install") || NotInstalledYet(processPath, installedDir)
                ? SetupRole.Install
                : null;

        return new Options
        {
            Setup = role,
            Autostart = autostart,
            DryRun = dryRun || selfCheck || shots is not null,
            QuitAfterSeconds = quitAfter,
            ScreenshotFolder = shots,
            SelfCheck = selfCheck,
            // A test run must never write into the installed app's settings or
            // log: the shipped copy is somebody's working setup, and its config
            // holds the device they actually watch.
            //
            // Beside the executable rather than in TEMP - the same split the
            // Python version makes, where a packaged build writes to APPDATA
            // and one run from source writes next to itself. It also keeps a
            // test run's files somewhere the person running it expects to find
            // them, instead of scattering a named folder through TEMP.
            DataFolder = dryRun || selfCheck || shots is not null
                ? Path.Combine(AppInfo.ProgramFolder, "dry-run-data")
                : AppInfo.DataFolder,
        };
    }

    /// <summary>
    /// The mutex name for this run. A dry run gets its own, so it can be
    /// started beside the installed app - which is the whole point of it.
    /// </summary>
    public string MutexName => DryRun ? AppInfo.MutexName + ".DryRun" : AppInfo.MutexName;

    /// <summary>
    /// A run with nobody at the keyboard: pictures, the self-check, or one that
    /// quits on a timer. Such a run must never put up a modal dialog - it would
    /// sit there waiting for a click that is never coming.
    /// </summary>
    public bool Batch => SelfCheck || ScreenshotFolder is not null
        || QuitAfterSeconds > 0 || Autostart is not null;

    public string SettingsPath => Path.Combine(DataFolder, "config.json");
    public string LogPath => Path.Combine(DataFolder, "dyn_lock.log");

    private static string? ValueAfter(string[] argv, string name)
    {
        int at = Array.IndexOf(argv, name);
        return at >= 0 && at + 1 < argv.Length ? argv[at + 1] : null;
    }

    /// <summary>
    /// Whether this copy is running from somewhere other than where an
    /// installation puts it - which means it was downloaded, and a double-click
    /// on it means "install me".
    ///
    /// It has to be decided WITHOUT a switch, because what a person does with a
    /// downloaded file is double-click it.
    ///
    /// 🔴 And it is decided by LOCATION rather than by the file's name, which
    /// was the first attempt. A WinUI 3 executable CANNOT BE RENAMED: it finds
    /// its own XAML through resources keyed to the executable's name, so a
    /// renamed copy dies on a XamlParseException before it draws anything.
    /// Measured 13.09.2026 - a copy called DaBtDynamicLock-kopie.exe failed the
    /// same way a copy called ...-setup.exe did, in both a single-file and a
    /// multi-file publish, and copying the .deps.json and .runtimeconfig.json
    /// across under the new name changed nothing.
    ///
    /// That matters far beyond the name on the download: installing means
    /// putting THIS FILE in Program Files, so if it had to be renamed on the
    /// way, the installed application would not start either.
    /// </summary>
    private static bool NotInstalledYet(string? processPath, string? installedDir)
    {
        if (processPath is null || installedDir is null)
            return false;       // nothing to compare - assume the app, not setup

        try
        {
            string here = Path.GetFullPath(Path.GetDirectoryName(processPath) ?? "")
                .TrimEnd(Path.DirectorySeparatorChar);
            string installed = Path.GetFullPath(installedDir)
                .TrimEnd(Path.DirectorySeparatorChar);
            return !string.Equals(here, installed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            // An unreadable path is no reason to offer to install over
            // somebody's working copy.
            return false;
        }
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
