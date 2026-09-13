using System.Diagnostics;
using Microsoft.Win32;

namespace DaBtDynamicLock.Installer;

/// <summary>Everywhere an installation touches. Nothing here is a constant so
/// that a test can point the whole thing somewhere harmless.</summary>
/// <param name="TargetDir">Where the program is copied to.</param>
/// <param name="StartMenuLink">The Start menu shortcut (.lnk).</param>
/// <param name="DesktopLink">The desktop shortcut (.lnk).</param>
/// <param name="RegistryRoot">Which hive the uninstall entry goes in.</param>
/// <param name="RegistryKey">The key path under that hive, or null for an
/// installation that writes no uninstall entry at all. Null really means
/// nothing is written: a check once pointed the key at another NAME to avoid
/// touching the registry, and of course wrote to the registry anyway.</param>
/// <param name="DataDir">Where the app keeps settings, log and history.</param>
public sealed record SetupPaths(
    string TargetDir,
    string StartMenuLink,
    string DesktopLink,
    RegistryHive RegistryRoot,
    string? RegistryKey,
    string DataDir);

/// <summary>What the person ticked in the installer.</summary>
public sealed record SetupChoices(bool StartMenu, bool Desktop, bool Autostart);

/// <summary>How it went: what failed, and whether anything did.</summary>
public sealed record SetupReport(IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Installing and removing the app.
///
/// Kept apart from any window so it can be run end to end against a harmless
/// folder. The shipped installer learned most of these lessons the hard way,
/// and each one is written where it applies: a running app holds its own files,
/// deleting a folder is not instant, a return code is not a result.
/// </summary>
public static class Setup
{
    /// <summary>The program's own file name inside the target folder.</summary>
    public const string ProgramName = "DaBtDynamicLock.exe";

    /// <summary>
    /// The uninstaller is a COPY of the installer under this name. The name is
    /// a constant because three places have to agree on it: what gets copied,
    /// what UninstallString points at, and how the program recognises that it
    /// was started to uninstall.
    /// </summary>
    public const string UninstallerName = "uninstall.exe";

    /// <summary>
    /// Copies the program in, makes the shortcuts, writes the uninstall entry
    /// and switches start-at-logon on. Reports what did not work rather than
    /// stopping at the first thing: a shortcut that could not be made is not a
    /// reason to leave a half-installed program behind.
    /// </summary>
    public static SetupReport Install(SetupPaths where, string sourceDir,
        SetupChoices what, string? uninstallerSource, string version,
        string language, Action<string> report)
    {
        var problems = new List<string>();

        report("stopping");
        StopRunningApp(where.TargetDir);

        report("copying");
        // An old installation is cleared out first, but a leftover that cannot
        // be deleted (a locked file) is not fatal - the copy overwrites it.
        if (Directory.Exists(where.TargetDir))
            TryDeleteFolder(where.TargetDir);
        CopyFolder(sourceDir, where.TargetDir);

        string program = Path.Combine(where.TargetDir, ProgramName);
        if (!File.Exists(program))
            return new SetupReport(new[] { $"{ProgramName} is not in the target folder" });

        // The uninstaller is this very installer, copied alongside the program.
        if (uninstallerSource is not null)
        {
            try
            {
                File.Copy(uninstallerSource,
                    Path.Combine(where.TargetDir, UninstallerName), true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"the uninstaller could not be put in place ({e.Message})");
            }
        }

        report("shortcuts");
        Shortcut(where.StartMenuLink, program, what.StartMenu, problems);
        Shortcut(where.DesktopLink, program, what.Desktop, problems);

        report("registry");
        if (where.RegistryKey is not null)
        {
            string? registry = WriteUninstallEntry(where, program, version, language);
            if (registry is not null)
                problems.Add(registry);
        }

        // Start at logon is set BY THE APP, not from here: the app's own window
        // can switch it too, and two pieces of code registering logon tasks
        // would sooner or later register different ones.
        report("autostart");
        var autostart = Run(program, what.Autostart ? "--autostart-on" : "--autostart-off");
        if (what.Autostart && !autostart.Ok)
            problems.Add($"start at logon could not be set ({autostart.Detail})");

        // The last word is what is actually there, not what the steps returned.
        report("checking");
        if (!File.Exists(program))
            problems.Add("the program is missing after the copy");
        if (uninstallerSource is not null
            && !File.Exists(Path.Combine(where.TargetDir, UninstallerName)))
            problems.Add("the uninstaller is missing");
        if (what.StartMenu && !File.Exists(where.StartMenuLink))
            problems.Add("the Start menu shortcut is missing");
        if (what.Desktop && !File.Exists(where.DesktopLink))
            problems.Add("the desktop shortcut is missing");
        if (where.RegistryKey is not null && !UninstallEntryExists(where))
            problems.Add("the entry in Installed apps is missing");

        return new SetupReport(problems);
    }

    /// <summary>
    /// Takes it all out again. The program folder goes last and may still be
    /// held by the uninstaller running from inside it - the caller says when
    /// that is the case by passing its own process id.
    /// </summary>
    public static SetupReport Uninstall(SetupPaths where, bool deleteData,
        Action<string> report)
    {
        var problems = new List<string>();

        report("stopping");
        StopRunningApp(where.TargetDir);

        report("autostart");
        string program = Path.Combine(where.TargetDir, ProgramName);
        if (File.Exists(program))
            Run(program, "--autostart-off");

        report("shortcuts");
        TryDelete(where.StartMenuLink, problems);
        TryDelete(where.DesktopLink, problems);

        report("registry");
        if (where.RegistryKey is not null)
        {
            try
            {
                using var root = RegistryKeyFor(where);
                root.DeleteSubKeyTree(where.RegistryKey, throwOnMissingSubKey: false);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                problems.Add("the entry in Installed apps could not be removed "
                    + $"({e.Message})");
            }
        }

        if (deleteData && Directory.Exists(where.DataDir))
        {
            report("data");
            if (!TryDeleteFolder(where.DataDir))
                problems.Add("the settings folder could not be removed");
        }

        return new SetupReport(problems);
    }

    /// <summary>
    /// Ends a running copy of the app and waits for it to really be gone.
    ///
    /// By path, not by name: a dry run or a copy somebody is testing from
    /// another folder is none of the installer's business. The shipped one
    /// killed every process of that name on the machine.
    /// </summary>
    public static void StopRunningApp(string targetDir)
    {
        string program = Path.Combine(targetDir, ProgramName);
        foreach (var process in Processes(program))
        {
            try
            {
                process.Kill();
                // Waited for, not assumed: taskkill reports success long before
                // the files are let go, and the copy that follows then fails on
                // a file still in use.
                process.WaitForExit(10_000);
            }
            catch (Exception e) when (e is InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                // Already gone between listing and killing - which is fine.
            }
        }
    }

    /// <summary>Running copies started from this folder.</summary>
    private static List<Process> Processes(string programPath)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName(
            Path.GetFileNameWithoutExtension(ProgramName)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, programPath,
                    StringComparison.OrdinalIgnoreCase))
                    found.Add(process);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception
                or InvalidOperationException)
            {
                // A process we may not look inside is not ours to stop.
            }
        }
        return found;
    }

    private static void Shortcut(string path, string program, bool wanted,
        List<string> problems)
    {
        if (!wanted)
        {
            // Unticking removes one made earlier, so the answer to "do I want a
            // shortcut" is the same before and after a reinstall.
            TryDelete(path, problems);
            return;
        }

        string? problem = WriteShortcut(path, program);
        if (problem is not null)
            problems.Add(problem);
    }

    /// <summary>
    /// Writes a .lnk through the shell's own object. Returns what went wrong,
    /// or null. Deliberately not a PowerShell one-liner: a path with a quote or
    /// an apostrophe in it breaks that, and this project has the same class of
    /// fault written down twice already.
    /// </summary>
    public static string? WriteShortcut(string path, string program)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
                return "WScript.Shell is not available on this machine";

            object? shell = Activator.CreateInstance(type);
            object? link = type.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell,
                new object[] { path });
            if (link is null)
                return "the shortcut object could not be created";

            Assign(link, "TargetPath", program);
            Assign(link, "WorkingDirectory", Path.GetDirectoryName(program)!);
            Assign(link, "IconLocation", program);
            link.GetType().InvokeMember("Save",
                System.Reflection.BindingFlags.InvokeMethod, null, link, null);

            return File.Exists(path) ? null : $"the shortcut {path} was not created";
        }
        catch (Exception e)
        {
            return $"the shortcut could not be written ({e.Message})";
        }

        static void Assign(object link, string property, string value) =>
            link.GetType().InvokeMember(property,
                System.Reflection.BindingFlags.SetProperty, null, link,
                new object[] { value });
    }

    /// <summary>
    /// The entry under Installed apps. Returns what went wrong, or null.
    /// InstallerLanguage is not a Windows value: it is how the uninstaller
    /// knows which language to speak a year from now, when it runs elevated and
    /// cannot read the user's settings file.
    /// </summary>
    private static string? WriteUninstallEntry(SetupPaths where, string program,
        string version, string language)
    {
        if (where.RegistryKey is null)
            return null;
        try
        {
            using var root = RegistryKeyFor(where);
            using var key = root.CreateSubKey(where.RegistryKey, true);
            if (key is null)
                return "the entry in Installed apps could not be created";

            long size = 0;
            foreach (var file in new DirectoryInfo(where.TargetDir)
                .EnumerateFiles("*", SearchOption.AllDirectories))
                size += file.Length;

            key.SetValue("DisplayName", "Da BT Dynamic Lock");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "David");
            key.SetValue("DisplayIcon", program);
            key.SetValue("InstallLocation", where.TargetDir);
            key.SetValue("UninstallString",
                $"\"{Path.Combine(where.TargetDir, UninstallerName)}\" --uninstall");
            key.SetValue("InstallerLanguage", language);
            key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
            or System.Security.SecurityException)
        {
            return $"the entry in Installed apps could not be written ({e.Message})";
        }
    }

    public static bool UninstallEntryExists(SetupPaths where)
    {
        if (where.RegistryKey is null)
            return false;
        try
        {
            using var root = RegistryKeyFor(where);
            using var key = root.OpenSubKey(where.RegistryKey);
            return key?.GetValue("DisplayName") is not null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>The language the uninstaller should speak, or null when unknown.</summary>
    public static string? StoredLanguage(SetupPaths where)
    {
        if (where.RegistryKey is null)
            return null;
        try
        {
            using var root = RegistryKeyFor(where);
            using var key = root.OpenSubKey(where.RegistryKey);
            return key?.GetValue("InstallerLanguage") as string;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static RegistryKey RegistryKeyFor(SetupPaths where) =>
        RegistryKey.OpenBaseKey(where.RegistryRoot, RegistryView.Default);

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(from, file);
            string target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    /// <summary>
    /// Deletes a folder, with retries. Never silently: the caller is told
    /// whether it worked, because "ignore the errors" once hid three failed
    /// removals in a single day.
    /// </summary>
    public static bool TryDeleteFolder(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                    return true;
                Directory.Delete(path, true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Something still holds a file - a process on its way out, or
                // an antivirus reading what was just written.
                Thread.Sleep(300);
            }
        }
        return !Directory.Exists(path);
    }

    private static void TryDelete(string path, List<string> problems)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(path)} could not be removed ({e.Message})");
        }
    }

    /// <summary>Runs the app with a switch and waits. No console window.</summary>
    private static (bool Ok, string Detail) Run(string program, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = program,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return (false, "it did not start");
            process.WaitForExit(30_000);
            return (process.HasExited && process.ExitCode == 0,
                process.HasExited ? $"exit code {process.ExitCode}" : "it did not finish");
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }
}
