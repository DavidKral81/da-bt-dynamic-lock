namespace DaBtDynamicLock.App;

/// <summary>
/// Facts about the application that more than one place needs. One constant
/// each, so nothing can quietly go out of step - the Python version learned
/// this when a renamed mutex left the installer unable to spot the running app.
/// </summary>
public static class AppInfo
{
    public const string Name = "Da BT Dynamic Lock";

    /// <summary>
    /// The version, and the ONLY place it is written down.
    ///
    /// ⚠ 1.5 keeps this in windows/version.py, which the build scripts read.
    /// When 2.0 first gets built into an installer, that file stops being the
    /// source and this one takes over - and whatever generates the file
    /// properties has to read it from here, or two numbers will drift apart.
    /// </summary>
    public const string Version = "2.0";

    public const string ProjectUrl = "https://github.com/DavidKral81/da-bt-dynamic-lock";

    /// <summary>
    /// Agreed with the shipped version and NOT to be renamed lightly: the
    /// installer looks for this to tell whether the app is running.
    /// </summary>
    public const string MutexName = @"Global\DaBTDynamicLock";

    /// <summary>
    /// The folder the running .exe actually sits in.
    ///
    /// ⚠ NOT AppContext.BaseDirectory. In a single-file publish - which is how
    /// 2.0 ships - that points at the temporary folder the program unpacks
    /// itself into: measured 13.09.2026 as
    /// %TEMP%\.net\DaBtDynamicLock\&lt;random&gt;. Writing there scatters files
    /// somewhere nobody would look, and registering it as a logon task's
    /// working directory names a folder that will not exist next time.
    /// </summary>
    public static string ProgramFolder =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>Where settings, the log and the chart history live.</summary>
    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    public static string SettingsPath => Path.Combine(DataFolder, "config.json");

    public static string LogPath => Path.Combine(DataFolder, "dyn_lock.log");
}
