using DaBtDynamicLock.Installer;
using Microsoft.Win32;

// Runs a whole installation and removal against a harmless folder.
//
// The point is that the installer is RUN before it is shipped. The shipped one
// went out once without anybody having started it, and the faults that came
// back were all in paths nobody had walked: a folder that would not delete, a
// process that had not let go of its files yet, a shortcut that was never made.
//
// Nothing here touches Program Files, the Start menu, the desktop or HKLM: the
// paths are all pointed at a temporary folder, and the uninstall entry goes
// into the current user's own hive under a name of its own. The registry part
// is skipped out loud unless it is asked for by name.

internal static class InstallerChecks
{
    static readonly List<string> Failures = new();
    static int _skipped;

    static int Main(string[] argv)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        bool withRegistry = argv.Contains("--with-registry");

        string root = Path.Combine(Path.GetTempPath(),
            "ddl-setup-check-" + Guid.NewGuid().ToString("N")[..8]);
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "Program Files", "Da BT Dynamic Lock");

        try
        {
            MakeSource(source);
            CheckInstall(root, source, target, withRegistry);
            CheckUninstall(root, target, withRegistry);
            CheckSingleFileInstall(root, withRegistry);
            CheckUninstallerSparesItself(root);
            CheckFolderRemovedAfterExit(root);
        }
        finally
        {
            Setup.TryDeleteFolder(root);
        }

        Console.WriteLine();
        if (_skipped > 0)
            Console.WriteLine($"({_skipped} case(s) skipped - see above for why)");
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL OK");
            return 0;
        }
        Console.WriteLine($"FAILED: {Failures.Count}");
        foreach (var f in Failures) Console.WriteLine($"  - {f}");
        return 1;
    }

    static void Check(string what, object? expected, object? actual)
    {
        bool ok = Equals(expected, actual);
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {what}: {actual ?? "null"}"
            + (ok ? "" : $"  (expected {expected ?? "null"})"));
        if (!ok) Failures.Add(what);
    }

    static void Skip(string what, string why)
    {
        Console.WriteLine($"  SKIP  {what}: {why}");
        _skipped++;
    }

    /// <summary>
    /// A stand-in for the published app: the right file names, none of the
    /// behaviour. The program has to be something Windows will start and that
    /// ENDS BY ITSELF, because the installer runs it to set start at logon.
    ///
    /// ping.exe, and not cmd.exe: cmd with an argument it does not understand
    /// opens an interactive shell and waits for input. This check hung on that
    /// for seven minutes and left the shell running under the app's name.
    /// </summary>
    static void MakeSource(string source)
    {
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            Path.Combine(source, Setup.ProgramName));
        File.WriteAllText(Path.Combine(source, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(source, "sub", "nested.txt"), "in a subfolder");
    }

    static SetupPaths Where(string root, string target, bool withRegistry) => new(
        TargetDir: target,
        StartMenuLink: Path.Combine(root, "Start Menu", "Da BT Dynamic Lock.lnk"),
        DesktopLink: Path.Combine(root, "Desktop", "Da BT Dynamic Lock.lnk"),
        RegistryRoot: RegistryHive.CurrentUser,
        // null, not another key name: pointing it at a different NAME still
        // wrote to the registry, which is exactly what this run must not do.
        RegistryKey: withRegistry ? @"Software\DaBtDynamicLock-installer-check" : null,
        DataDir: Path.Combine(root, "AppData", "Da BT Dynamic Lock"));

    static void CheckInstall(string root, string source, string target, bool withRegistry)
    {
        Console.WriteLine("Installing:");
        var where = Where(root, target, withRegistry);
        Directory.CreateDirectory(where.DataDir);
        File.WriteAllText(Path.Combine(where.DataDir, "config.json"), "{}");

        // Something already in the way, as on a reinstall: a file that must be
        // gone afterwards rather than left mixed in with the new copy.
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "leftover.txt"), "from an older version");

        var steps = new List<string>();
        var report = Setup.Install(where, source,
            new SetupChoices(StartMenu: true, Desktop: true, Autostart: false),
            uninstallerSource: Path.Combine(source, "settings.json"),
            version: "2.0", language: "cs", report: steps.Add);

        if (!withRegistry)
        {
            // The uninstall entry is the one thing that cannot be checked
            // without writing to the registry, so it is said out loud rather
            // than passed over: run with --with-registry to include it.
            Skip("the entry in Installed apps",
                "this run writes nothing to the registry (pass --with-registry "
                + "to include it)");
            Check("the install reports no problems", 0, report.Problems.Count);
        }
        else
        {
            Check("the install reports no problems", 0, report.Problems.Count);
            Check("...and the entry in Installed apps is there", true,
                Setup.UninstallEntryExists(where));
            Check("...and it remembers the language for the uninstaller", "cs",
                Setup.StoredLanguage(where));
        }
        foreach (var problem in report.Problems)
            Console.WriteLine($"        ({problem})");

        Check("the program is in place", true,
            File.Exists(Path.Combine(target, Setup.ProgramName)));
        Check("...with the folders under it", true,
            File.Exists(Path.Combine(target, "sub", "nested.txt")));
        Check("...and the uninstaller beside it", true,
            File.Exists(Path.Combine(target, Setup.UninstallerName)));

        // An older installation must be REPLACED, not mixed with: a file left
        // from a previous version is how two versions end up running as one.
        Check("what an older version left is gone", false,
            File.Exists(Path.Combine(target, "leftover.txt")));

        Check("the Start menu shortcut is there", true, File.Exists(where.StartMenuLink));
        Check("the desktop shortcut is there", true, File.Exists(where.DesktopLink));

        // Unticking a shortcut removes one made earlier, so the answer to "do I
        // want a shortcut" means the same on a reinstall as on a first install.
        Setup.Install(where, source,
            new SetupChoices(StartMenu: false, Desktop: false, Autostart: false),
            uninstallerSource: null, version: "2.0", language: "cs", report: _ => { });
        Check("unticking the shortcuts removes them", false,
            File.Exists(where.StartMenuLink) || File.Exists(where.DesktopLink));

        Check("the steps are reported in order", "stopping,copying,shortcuts,"
            + "registry,autostart,checking", string.Join(",", steps));
    }

    /// <summary>
    /// Installing from ONE FILE, which is how 2.0 actually ships: the setup
    /// executable is the whole application, so there is no folder to copy from
    /// and the file has to land under the program's name.
    ///
    /// Checked separately because it is a different branch of the copy. The
    /// folder case above would go on passing with this one broken.
    /// </summary>
    static void CheckSingleFileInstall(string root, bool withRegistry)
    {
        Console.WriteLine("\nInstalling from a single file (how 2.0 ships):");
        string target = Path.Combine(root, "Program Files", "from-one-file");
        var where = Where(root, target, withRegistry) with
        {
            TargetDir = target,
            // A key of its own, so this does not overwrite what the first
            // install left behind and then check its own leftovers.
            RegistryKey = withRegistry
                ? @"Software\DaBtDynamicLock-installer-check-single" : null,
        };

        string oneFile = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var report = Setup.Install(where, oneFile,
            new SetupChoices(StartMenu: false, Desktop: false, Autostart: false),
            uninstallerSource: null, version: "2.0", language: "en", report: _ => { });

        Check("installing from one file reports no problems", 0, report.Problems.Count);
        foreach (var problem in report.Problems)
            Console.WriteLine($"        ({problem})");

        Check("...and the program is in place under its own name", true,
            File.Exists(Path.Combine(target, Setup.ProgramName)));
        // No second copy of a 69 MB program to remove itself with: the entry in
        // Installed apps has to point at the program itself.
        Check("...and no separate uninstaller was made", false,
            File.Exists(Path.Combine(target, Setup.UninstallerName)));

        if (withRegistry)
        {
            string uninstall;
            using (var key = Registry.CurrentUser.OpenSubKey(where.RegistryKey!))
                uninstall = key?.GetValue("UninstallString") as string ?? "";

            Check("...and Installed apps removes it with the program itself", true,
                uninstall.Contains(Setup.ProgramName) && uninstall.Contains("--uninstall"));

            // Put back exactly as it was found: this check is the only thing
            // that wrote there.
            Registry.CurrentUser.DeleteSubKeyTree(where.RegistryKey!, false);
        }
        else
        {
            Skip("what Installed apps would run to remove it",
                "this run writes nothing to the registry");
        }
    }

    static void CheckUninstall(string root, string target, bool withRegistry)
    {
        Console.WriteLine("\nUninstalling:");
        var where = Where(root, target, withRegistry);

        // Settings are kept unless removal is asked for: somebody reinstalling
        // should not lose the device they watch.
        var steps = new List<string>();
        var kept = Setup.Uninstall(where, deleteData: false, report: steps.Add);
        // The stand-in is ping.exe, which rejects --autostart-off with exit
        // code 1. That this is REPORTED is the point: the uninstaller used to
        // ignore the answer. Anything else going wrong still fails here.
        Check("removing without the data reports only the stand-in's refusal",
            "start at logon could not be switched off (exit code 1)",
            string.Join(" | ", kept.Problems));
        // Start at logon BEFORE stopping: the task restarts a killed app.
        Check("...and start at logon is switched off before the app is stopped",
            "autostart,stopping,shortcuts,registry,files", string.Join(",", steps));
        Check("...and the program folder is gone", false, Directory.Exists(target));
        foreach (var problem in kept.Problems)
            Console.WriteLine($"        ({problem})");
        Check("...and the settings folder is still there", true,
            Directory.Exists(where.DataDir));

        if (withRegistry)
            Check("...and the entry in Installed apps is gone", false,
                Setup.UninstallEntryExists(where));
        else
            Skip("removing the entry in Installed apps", "the registry is not "
                + "written in this run");

        var all = Setup.Uninstall(where, deleteData: true, report: _ => { });
        Check("removing with the data reports no problems", 0, all.Problems.Count);
        Check("...and the settings folder is gone", false, Directory.Exists(where.DataDir));

        Check("the shortcuts are gone", false,
            File.Exists(where.StartMenuLink) || File.Exists(where.DesktopLink));
    }

    /// <summary>
    /// The uninstaller of 2.0 is the installed program itself. It used to stop
    /// "every copy running from the program folder" - itself included - and the
    /// window closed with nothing removed.
    ///
    /// This process is not in that folder, so a running stand-in plays the
    /// uninstaller: it has to survive being spared and go when it is not.
    /// </summary>
    static void CheckUninstallerSparesItself(string root)
    {
        Console.WriteLine("\nThe uninstaller running from the program folder:");
        string folder = Path.Combine(root, "Program Files", "running");
        Directory.CreateDirectory(folder);
        string program = Path.Combine(folder, Setup.ProgramName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), program);

        using var uninstaller = StartQuietly(program, "-n 30 127.0.0.1");
        try
        {
            Setup.StopRunningApp(folder, spare: uninstaller.Id);
            Check("the process doing the setup is not stopped", false,
                uninstaller.WaitForExit(1500));

            Setup.StopRunningApp(folder);
            Check("...while any other copy from that folder is", true,
                uninstaller.WaitForExit(10_000));
        }
        finally
        {
            if (!uninstaller.HasExited)
                uninstaller.Kill();
        }
    }

    /// <summary>
    /// The folder a running program sits in can only go after it has ended. An
    /// apostrophe in the path, because a quote is what broke the PowerShell
    /// one-liners this project wrote before.
    /// </summary>
    static void CheckFolderRemovedAfterExit(string root)
    {
        Console.WriteLine("\nThe program folder once the uninstaller has quit:");
        string folder = Path.Combine(root, "Program Files", "it's still running");
        Directory.CreateDirectory(folder);
        string program = Path.Combine(folder, Setup.ProgramName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), program);

        using var uninstaller = StartQuietly(program, "-n 4 127.0.0.1");
        using var remover = Setup.RemoveFolderAfterExit(folder, uninstaller.Id);
        if (remover is null)
        {
            Check("the removal was started", true, false);
            return;
        }

        Check("the folder is not touched while the program still runs", true,
            Directory.Exists(folder) && !uninstaller.HasExited);
        Check("...the removal finishes", true, remover.WaitForExit(60_000));
        Check("...reporting success", 0, remover.HasExited ? remover.ExitCode : -1);
        Check("...and the folder is gone", false, Directory.Exists(folder));
    }

    static System.Diagnostics.Process StartQuietly(string program, string arguments) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = program,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
}
