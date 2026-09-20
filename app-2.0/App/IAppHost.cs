using DaBtDynamicLock.Core;
using DaBtDynamicLock.Engine;
using DaBtDynamicLock.Platform;

namespace DaBtDynamicLock.App;

/// <summary>
/// What the settings window needs from the running app, and what it asks it
/// to do.
/// </summary>
public interface IAppHost
{
    Settings Settings { get; }
    PhoneWatch Watch { get; }
    Decision? Latest { get; }
    DateTime? LastLockedAt { get; }

    /// <summary>
    /// Now, on the clock the readings carry and on the wall. Asked for rather
    /// than read from the system, so the picture run can stop time and every
    /// picture of an unchanged build comes out the same.
    /// </summary>
    double NowMonotonic();
    double NowWall();

    /// <summary>
    /// Where THIS run keeps its settings and log. Asked for rather than taken
    /// from AppInfo, so a dry run opens its own folder and not the installed
    /// app's - the same split the settings file has to make.
    /// </summary>
    string DataFolder { get; }

    /// <summary>
    /// This run exists to photograph the windows.
    ///
    /// ⚠ It must not remember the window's size, and must not open at a
    /// remembered one. Measured on 20.09.2026: a picture run stored that its
    /// window had been maximised, the next one therefore opened maximised, and
    /// PrintWindow drew all nine screenshots black. It is the rule this project
    /// already has for made-up data, in another shape - a run that produces
    /// pictures stands on nothing it saved earlier and leaves nothing behind,
    /// or the pictures stop being comparable.
    ///
    /// The self-check is deliberately NOT included: it has to be able to check
    /// that the size is remembered, and it clears what it wrote when it ends.
    /// </summary>
    bool TakingPictures { get; }

    void SaveSettings();
    void PauseFor(TimeSpan how);
    void ResumePausing();
    void OpenSettingsWindow();

    /// <summary>Redraw everything that carries text - the tray tip, the window.</summary>
    void LanguageChanged();

    /// <summary>Puts something in the log from a window that has no log of its own.</summary>
    void Report(string problem);

    /// <summary>Stops everything and quits.</summary>
    void QuitApp();

    /// <summary>The network in use, or null when there is none or it cannot be read.</summary>
    WifiConnection? CurrentNetwork();

    /// <summary>
    /// The devices heard recently. Asked of the host rather than read straight
    /// off PhoneWatch so that a picture run can hand over made-up ones: a
    /// screenshot is shared far more easily than a log, and somebody's actual
    /// phone has no business being in one.
    /// </summary>
    IReadOnlyList<NearbyDevice> NearbyDevices();

    /// <summary>What the chart is drawn from.</summary>
    SignalHistory History { get; }

    /// <summary>Does the app start when the user signs in to Windows?</summary>
    bool AutostartOn();

    /// <summary>
    /// Turns start at logon on or off and says what it REALLY is afterwards.
    /// Asked of the host because a dry run must not touch Task Scheduler: the
    /// scheduled task belongs to the installed copy, and a test run that leaves
    /// a logon task behind would start the wrong build every morning.
    /// </summary>
    Reading<bool> SetAutostart(bool on);
}
