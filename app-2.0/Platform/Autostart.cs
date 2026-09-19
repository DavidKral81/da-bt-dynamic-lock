using System.Diagnostics;
using System.Security;
using System.Text;

namespace DaBtDynamicLock.Platform;

/// <summary>Where an autostart entry should point, and what to call it.</summary>
/// <param name="TaskName">The scheduled task's name.</param>
/// <param name="Program">What gets started.</param>
/// <param name="Arguments">What it is started with; empty for none.</param>
/// <param name="WorkingDirectory">Where it starts from.</param>
/// <param name="ShortcutPath">The fallback shortcut, in the Startup folder.</param>
/// <param name="ScratchFolder">Where the task's XML may be written while it is
/// handed to Windows. It is deleted again straight away.</param>
public sealed record AutostartTarget(
    string TaskName,
    string Program,
    string Arguments,
    string WorkingDirectory,
    string ShortcutPath,
    string ScratchFolder);

/// <summary>
/// Starting the app when the user signs in.
///
/// Two mechanisms, in this order: a scheduled task, and a shortcut in the
/// Startup folder if a task cannot be created. The task is preferred because it
/// can wait for Bluetooth to come up and can restart the app after a crash;
/// the shortcut always works, even without the rights for Task Scheduler.
///
/// The shipped version learned both halves the hard way and this keeps what it
/// learned: the task is registered FROM XML rather than by a plain
/// schtasks command, and whether it worked is decided by looking, never by a
/// return code.
/// </summary>
public static class Autostart
{
    /// <summary>
    /// How long an answer is reused. Asking Windows means starting a process,
    /// and the settings window refreshes twice a second - without this, opening
    /// it would run schtasks a hundred times a minute. Any change made from
    /// here clears it at once, so the only staleness possible is somebody
    /// editing the task in Task Scheduler while the window is open.
    /// </summary>
    private const double RememberSeconds = 30;

    private static bool? _known;
    private static double _knownAt;

    /// <summary>Does the app start at logon? Both mechanisms count.</summary>
    public static bool Enabled(AutostartTarget target, bool refresh = false)
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (!refresh && _known is bool remembered && now - _knownAt < RememberSeconds)
            return remembered;

        bool found = Run($"schtasks /Query /TN \"{target.TaskName}\"").Ok
            || File.Exists(target.ShortcutPath);
        _known = found;
        _knownAt = now;
        return found;
    }

    /// <summary>
    /// Turns start-at-logon on or off, and reports what it REALLY is afterwards.
    ///
    /// Both halves of the shipped version used to trust the return code and
    /// announce "enabled": the user got a confirmation and after a restart
    /// nothing came up. The last word therefore belongs to a fresh
    /// <see cref="Enabled"/>, which goes and looks.
    /// </summary>
    public static Reading<bool> Set(AutostartTarget target, bool enable,
        Action<string> log)
    {
        if (enable)
            Enable(target, log);
        else
            Disable(target, log);

        _known = null;
        bool actual = Enabled(target, refresh: true);
        return actual == enable
            ? new Reading<bool>(actual)
            : Reading<bool>.Failed(actual,
                $"start at logon was asked to be {(enable ? "ON" : "OFF")} "
                + $"but the check says it is {(actual ? "ON" : "OFF")}");
    }

    private static void Enable(AutostartTarget target, Action<string> log)
    {
        var task = CreateTask(target);
        if (task.Ok)
        {
            log("Start at logon enabled (scheduled task).");
            return;
        }

        log($"The scheduled task could not be created ({task.Problem}) "
            + "- falling back to the Startup folder.");

        string? shortcut = WriteShortcut(target);
        log(shortcut is null
            ? "Start at logon enabled (Startup folder)."
            : $"Start at logon could not be enabled: {shortcut}");
    }

    private static void Disable(AutostartTarget target, Action<string> log)
    {
        // A missing task is not worth shouting about - the user may have been
        // running from the Startup folder, or from neither. The check at the
        // end of Set() is what decides whether this worked.
        Run($"schtasks /Delete /F /TN \"{target.TaskName}\"");
        try
        {
            File.Delete(target.ShortcutPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"The Startup shortcut could not be removed: {e.Message}");
        }
        log("Start at logon disabled.");
    }

    /// <summary>
    /// Registers the logon task from XML.
    ///
    /// Why XML and not a plain schtasks command: that one only covers the
    /// basics. This also needs a restart after a crash and, above all, the
    /// REMOVAL of the limit after which Windows stops the task on its own
    /// (three days by default) - which can only be set this way.
    /// </summary>
    private static Reading<bool> CreateTask(AutostartTarget target)
    {
        string path = Path.Combine(target.ScratchFolder, "_task.xml");
        try
        {
            Directory.CreateDirectory(target.ScratchFolder);
            // schtasks reads the XML as UTF-16 and rejects anything else.
            File.WriteAllText(path, TaskXml(target, CurrentUser()), Encoding.Unicode);

            var made = Run($"schtasks /Create /F /TN \"{target.TaskName}\" "
                + $"/XML \"{path}\"");
            return made.Ok
                ? new Reading<bool>(true)
                : Reading<bool>.Failed(false, made.Problem ?? "no detail");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Reading<bool>.Failed(false, $"the task file could not be written "
                + $"({e.Message})");
        }
        finally
        {
            // In a finally, not after the call: a copy left behind would sit
            // there until something happened to overwrite it, which is how a
            // signing key once stayed in TEMP for days.
            try { File.Delete(path); } catch (IOException) { /* gone is fine */ }
        }
    }

    /// <summary>
    /// The task, as Windows wants to read it. Kept apart from the registering
    /// so it can be checked without touching Task Scheduler at all.
    /// </summary>
    public static string TaskXml(AutostartTarget target, string user)
    {
        string arguments = target.Arguments.Length == 0
            ? string.Empty
            : SecurityElement.Escape($"\"{target.Arguments}\"");

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Locks the laptop when the phone walks away.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT30S</Delay>
                  <Repetition>
                    <Interval>PT5M</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <!-- Carries the repeat above: a five minute repeat while a copy
                     is already running must do nothing at all. Without this,
                     every repeat would start a second watcher. -->
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(target.Program)}</Command>
                  <Arguments>{arguments}</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(target.WorkingDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>DOMAIN\user, the way Task Scheduler writes it.</summary>
    public static string CurrentUser()
    {
        string domain = Environment.GetEnvironmentVariable("USERDOMAIN") ?? "";
        string user = Environment.UserName;
        return domain.Length == 0 ? user : $"{domain}\\{user}";
    }

    /// <summary>
    /// Writes the Startup shortcut. Returns what went wrong, or null.
    ///
    /// Through the shell's own object rather than by handing a PowerShell
    /// command a path: this project has quotes inside a -Command string on its
    /// list of repeated faults, and a path with an apostrophe in it would break
    /// exactly that way.
    /// </summary>
    public static string? WriteShortcut(AutostartTarget target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target.ShortcutPath)!);

            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
                return "WScript.Shell is not available on this machine";

            object? shell = Activator.CreateInstance(type);
            if (shell is null)
                return "WScript.Shell could not be started";

            object? link = type.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell,
                new object[] { target.ShortcutPath });
            if (link is null)
                return "the shortcut object could not be created";

            Assign(link, "TargetPath", target.Program);
            Assign(link, "Arguments", target.Arguments);
            Assign(link, "WorkingDirectory", target.WorkingDirectory);
            link.GetType().InvokeMember("Save",
                System.Reflection.BindingFlags.InvokeMethod, null, link, null);

            // Checked, not assumed: Save() reports nothing at all when the
            // folder is write-protected.
            return File.Exists(target.ShortcutPath)
                ? null
                : "the shortcut was saved but is not there";
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
    /// Runs a command with no console window flashing up, and keeps what it
    /// said. The shipped version threw the output away at first, so whenever
    /// schtasks refused to do something the reason went with it.
    /// </summary>
    private static Reading<string> Run(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return Reading<string>.Failed("", $"{command} did not start");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            string detail = (error.Length > 0 ? error : output).Trim();
            return process.ExitCode == 0
                ? new Reading<string>(detail)
                : Reading<string>.Failed(detail,
                    detail.Length > 0 ? detail : $"exit code {process.ExitCode}");
        }
        catch (Exception e)
        {
            return Reading<string>.Failed("", e.Message);
        }
    }
}
