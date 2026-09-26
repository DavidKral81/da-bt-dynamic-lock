using System.Diagnostics;
using DaBtDynamicLock.Installer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.Graphics;
using WinRT.Interop;

namespace DaBtDynamicLock.App;

/// <summary>
/// The installer's window: what is about to happen, what to include, and how it
/// went.
///
/// The window only asks and reports. Everything it actually does is in
/// Installer.Setup, which knows nothing about windows and is therefore run end
/// to end against a harmless folder by Installer.Tests. A window cannot be
/// tested that way, so as little as possible lives in here.
/// </summary>
public sealed partial class InstallerWindow : Window
{
    private const int WidthDip = 580;

    /// <summary>
    /// Never shorter than this, whatever the measurement says - a window that
    /// collapsed onto its buttons would look broken.
    /// </summary>
    private const int MinHeightDip = 240;

    /// <summary>
    /// What the title bar takes, in DIPs. MoveAndResize sizes the WHOLE window,
    /// so without this the bar eats the bottom of the content - which is the
    /// same trap the Python preview tool fell into from the other side, when it
    /// measured without the title bar and cut the picture short.
    /// </summary>
    private const int TitleBarDip = 32;

    private readonly bool _uninstall;
    private readonly Action<string> _log;
    private readonly DispatcherQueue _ui;

    /// <summary>True once the work has run and the window shows the outcome.</summary>
    private bool _finished;

    public nint Handle { get; }

    /// <summary>Stops the log writing into the settings folder once it is removed.</summary>
    private readonly Action? _stopLogFile;

    public InstallerWindow(bool uninstall, Action<string> log, Action? stopLogFile = null)
    {
        _uninstall = uninstall;
        _log = log;
        _stopLogFile = stopLogFile;

        InitializeComponent();
        _ui = DispatcherQueue.GetForCurrentThread();
        Handle = WindowNative.GetWindowHandle(this);

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        WindowLayout.PaintTitleBar(AppWindow, Root);
        WindowLayout.SetWindowIcon(Handle);

        // Closing the window ends the process. There is no tray icon and no
        // loop behind this one - leaving it would keep an invisible copy of the
        // program alive holding the very files it just wrote.
        AppWindow.Closing += (_, _) => Quit();

        LanguageChoice.ItemsSource = Texts.Languages;

        ApplyTexts();
        FillChoices();
    }

    /// <summary>Shows the window, sized to its content and centred.</summary>
    public void ShowWindow()
    {
        FitToContent();
        AppWindow.Show(true);
        // Without this it can open behind whatever had focus - every window in
        // this app needs the same push.
        Native.SetForegroundWindow(Handle);
    }

    /// <summary>
    /// Makes the window exactly as tall as what it holds.
    ///
    /// Measured, not picked: a height written as a number left a third of the
    /// window empty under the buttons - the same fault the chart page had, and
    /// one no amount of measuring widths would have shown. Called again when
    /// the content changes, because the outcome screen is a different height
    /// from the choices.
    /// </summary>
    private void FitToContent()
    {
        Root.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        int heightDip = Math.Max(MinHeightDip,
            (int)Math.Ceiling(Root.DesiredSize.Height) + TitleBarDip);

        var (x, y, w, h) = WindowLayout.Centred(WidthDip, heightDip);
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    // ------------------------------------------------------------ where it goes

    /// <summary>
    /// Where an installation puts things on this machine.
    ///
    /// Setup itself holds no paths on purpose, so that a test can point the
    /// whole of it somewhere harmless. The real answers therefore live here.
    ///
    /// ⚠ DataDir is the settings folder of whoever the installer is RUNNING AS.
    /// Elevation normally keeps the same user, but an install elevated with a
    /// different administrator account would name that account's folder - so
    /// "remove the settings as well" can only promise to clear the profile it
    /// is running in.
    /// </summary>
    public static SetupPaths Where() => new(
        TargetDir: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.Name),
        StartMenuLink: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            AppInfo.Name + ".lnk"),
        DesktopLink: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            AppInfo.Name + ".lnk"),
        RegistryRoot: RegistryHive.LocalMachine,
        RegistryKey: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DaBtDynamicLock",
        DataDir: AppInfo.DataFolder);

    private static string ProgramPath => Path.Combine(Where().TargetDir, Setup.ProgramName);

    // ----------------------------------------------------------------- texts

    private void ApplyTexts()
    {
        Title = Texts.Get(_uninstall ? "ins_title_uninstall" : "ins_title_install",
            AppInfo.Name);

        // The heading carries the outcome once there is one, so it is not
        // written here twice. Before that it names what is about to happen.
        if (!_finished)
        {
            Heading.Text = Title;
            Subtitle.Text = Texts.Get("ins_subtitle");
        }

        // The same wording as the settings window. When removing, this names
        // the version being removed: the uninstaller is the installed program.
        VersionText.Text = Texts.Get("lbl_version", AppInfo.Version);

        FolderLabel.Text = Texts.Get(_uninstall ? "ins_from_folder" : "ins_to_folder");
        FolderPath.Text = Where().TargetDir;

        OptStartMenu.Content = Texts.Get("ins_opt_startmenu");
        OptDesktop.Content = Texts.Get("ins_opt_desktop");
        OptAutostart.Content = Texts.Get("ins_opt_autostart");
        OptData.Content = Texts.Get("uni_opt_data");

        OptLaunch.Content = Texts.Get("ins_opt_launch");
        OptPhone.Content = Texts.Get("ins_opt_phone");

        ProblemsHead.Text = Texts.Get("ins_problems_desc");

        CancelButton.Content = Texts.Get(_finished ? "ins_btn_close" : "ins_btn_cancel");
        GoButton.Content = Texts.Get(_uninstall ? "ins_btn_uninstall" : "ins_btn_install");

        LanguageChoice.SelectedItem =
            Texts.Languages.FirstOrDefault(l => l.Code == Texts.Language)
            ?? Texts.Languages[0];

        // The outcome is written from a key too, so switching language on the
        // result screen translates it rather than leaving the old wording.
        if (_finished)
            ApplyResultTexts();
    }

    private void OnLanguageChosen(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageChoice.SelectedItem is not Texts.LanguageOption picked
            || picked.Code == Texts.Language)
            return;

        Texts.Language = picked.Code;
        ApplyTexts();
        // The two languages do not wrap to the same number of lines, so the
        // height is worked out again rather than left at what the other one
        // needed. Czech is the longer of the two and usually sets it.
        FitToContent();
    }

    /// <summary>Shows only the choices that belong to this role.</summary>
    private void FillChoices()
    {
        OptStartMenu.Visibility = Shown(!_uninstall);
        OptDesktop.Visibility = Shown(!_uninstall);
        OptAutostart.Visibility = Shown(!_uninstall);
        OptData.Visibility = Shown(_uninstall);

        // What a fresh install should do unless told otherwise. Start at logon
        // is on by default because an app that locks an unattended screen is of
        // no use if somebody has to remember to start it.
        OptStartMenu.IsChecked = true;
        OptDesktop.IsChecked = false;
        OptAutostart.IsChecked = true;
        OptData.IsChecked = false;

        // Both ticked. The phone app is not optional for this to work at all,
        // so offering it unticked would be offering to leave the job half done.
        OptPhone.IsChecked = true;
        OptLaunch.IsChecked = true;
    }

    private static Visibility Shown(bool yes) =>
        yes ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Takes the window off the screen WITHOUT ending the process - for the
    /// picture run, which photographs both roles one after the other. Closing
    /// it would quit, which is right for a person and wrong here.
    /// </summary>
    internal void HideWindow() => AppWindow.Hide();

    /// <summary>
    /// Does the window say which version it is? Checked by the picture run in
    /// both states, because the rewrite already lost it once and nobody
    /// noticed until the installer was in use.
    /// </summary>
    internal bool ShowsVersion =>
        VersionText.Visibility == Visibility.Visible
        && VersionText.Text.Contains(AppInfo.Version, StringComparison.Ordinal);

    /// <summary>
    /// Puts the outcome screen up with made-up content, so the picture run
    /// shows it too.
    ///
    /// Without this, half of what the installer displays would never be looked
    /// at - which is exactly what happened to 1.5, where the result window went
    /// unphotographed and was where the version number had just been added.
    /// </summary>
    internal void ShowSampleResult(bool withProblems) =>
        ShowResult(new SetupReport(withProblems
            // A problem that fits the role being photographed. Made-up data
            // that contradicts itself sends whoever looks at the picture
            // hunting for a drawing fault that is not there.
            ? new[]
            {
                _uninstall
                    ? "the settings folder could not be removed (a file is in use)"
                    : "the desktop shortcut could not be created (access denied)",
            }
            : Array.Empty<string>()));

    // ------------------------------------------------------------------ doing

    private void OnCancel(object sender, RoutedEventArgs e) => Quit();

    private async void OnGo(object sender, RoutedEventArgs e)
    {
        GoButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        Busy.IsActive = true;

        var what = new SetupChoices(
            StartMenu: OptStartMenu.IsChecked == true,
            Desktop: OptDesktop.IsChecked == true,
            Autostart: OptAutostart.IsChecked == true);
        bool alsoData = OptData.IsChecked == true;
        string language = Texts.Language;

        _log($"{(_uninstall ? "Uninstalling" : "Installing")} {AppInfo.Version} "
            + $"into {Where().TargetDir}.");

        // Off the UI thread: copying 69 MB and waiting for a running copy to go
        // takes seconds, and a window that stops repainting looks like one that
        // has crashed.
        SetupReport report = await Task.Run(() =>
        {
            try
            {
                return _uninstall
                    ? Setup.Uninstall(Where(), alsoData, Step)
                    : Setup.Install(Where(), Environment.ProcessPath ?? "", what,
                        uninstallerSource: null, AppInfo.Version, language, Step);
            }
            catch (Exception ex)
            {
                // Never swallowed: a setup that stops halfway and says nothing
                // leaves a half-installed program behind and no way to tell.
                return new SetupReport(new[] { ex.Message });
            }
        });

        // The settings folder is where the log lives. Once it is really gone,
        // the lines that follow go to the console only - written to the file,
        // each would make the folder again.
        if (_uninstall && alsoData && !Directory.Exists(Where().DataDir))
            _stopLogFile?.Invoke();

        Busy.IsActive = false;
        Busy.Visibility = Visibility.Collapsed;
        ShowResult(report);
    }

    /// <summary>
    /// A step Setup is on. Called from the worker thread, so it hops back onto
    /// the UI one - touching a control from anywhere else throws.
    /// </summary>
    private void Step(string step) =>
        _ui.TryEnqueue(() => StepText.Text = Texts.Get("ins_step_" + step));

    private SetupReport? _report;

    private void ShowResult(SetupReport report)
    {
        _report = report;
        _finished = true;

        foreach (string problem in report.Problems)
            _log("Setup: " + problem);
        _log(report.Ok
            ? $"{(_uninstall ? "Uninstall" : "Install")} finished cleanly."
            : $"{(_uninstall ? "Uninstall" : "Install")} finished with "
                + $"{report.Problems.Count} problem(s).");

        PageChoices.Visibility = Visibility.Collapsed;
        PageResult.Visibility = Visibility.Visible;
        StepText.Text = "";

        // Nothing left to start once the program is gone.
        AfterCard.Visibility = Shown(!_uninstall);
        GoButton.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        CancelButton.Content = Texts.Get("ins_btn_close");

        ApplyResultTexts();
        // The outcome is a different height from the choices, and a list of
        // problems is different again.
        FitToContent();
    }

    private void ApplyResultTexts()
    {
        var report = _report;
        if (report is null)
            return;

        // Into the window's own heading, as a whole sentence. A second heading
        // under the first meant the window named itself twice.
        Heading.Text = report.Ok
            ? Texts.Get(_uninstall ? "ins_head_uninstalled" : "ins_head_installed",
                AppInfo.Name)
            : Texts.Get("ins_head_problems");

        // The line under it follows the outcome too. A heading saying something
        // went wrong above a line saying it all worked is the window
        // contradicting itself, which is what the picture showed. When it all
        // worked, the heading has said everything and the line goes away.
        Subtitle.Text = report.Ok ? "" :
            Texts.Get(_uninstall ? "uni_partial_desc" : "ins_partial_desc");
        Subtitle.Visibility = Shown(!report.Ok);

        // Only after installing, and only when there is something to run: after
        // a removal there is no phone app to go with anything.
        PhoneNeeded.Text = Texts.Get("ins_phone_needed");
        PhoneNeededCard.Visibility = Shown(!_uninstall);

        ProblemsCard.Visibility = Shown(!report.Ok);
        // The problems are Setup's own English sentences, not translated keys:
        // they name a file or a Windows message, and that is what has to be
        // readable when somebody reports the fault.
        ProblemsList.Text = string.Join("\n", report.Problems.Select(p => "• " + p));
    }

    // ------------------------------------------------------------------- exit

    private void Quit()
    {
        if (_finished && !_uninstall)
        {
            if (OptLaunch.IsChecked == true)
                StartAsUser(ProgramPath);
            if (OptPhone.IsChecked == true)
                StartAsUser(AppInfo.ProjectUrl + "/releases/latest");
        }

        _log("Setup closed.");

        // Last, right before quitting: the uninstaller runs from inside the
        // program folder, so its own file can only go once it has ended.
        if (_finished && _uninstall)
        {
            try
            {
                Setup.RemoveProgramFolderAfterExit(Where().TargetDir);
            }
            catch (Exception e)
            {
                _log($"The program folder could not be handed over for removal ({e.Message}).");
            }
        }

        Environment.Exit(_report is null || _report.Ok ? 0 : 1);
    }

    /// <summary>
    /// Starts something as the ordinary user, not as the administrator this
    /// installer is running as.
    ///
    /// Handed to Explorer on purpose: a child process inherits the elevated
    /// token, so starting the app directly would leave it running with
    /// administrator rights for the rest of the session - and it would then
    /// write its settings into the administrator's profile instead of the
    /// user's. Explorer is already running as the user, so what it starts is
    /// the user's too.
    /// </summary>
    private void StartAsUser(string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{what}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            // Said out loud rather than passed over: the person ticked a box and
            // is entitled to know nothing happened.
            _log($"{what} could not be started ({e.Message}).");
        }
    }
}
