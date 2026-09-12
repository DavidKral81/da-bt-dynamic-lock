namespace DaBtDynamicLock.App;

/// <summary>
/// Every word the user sees, Czech and English side by side in one file - so a
/// missing or drifted translation is visible at a glance rather than found by
/// somebody switching languages.
///
/// The log is NOT in here. It stays English whatever the interface says,
/// because it is what faults get diagnosed from and what gets attached to a
/// report.
/// </summary>
public static class Texts
{
    /// <summary>
    /// Which language is in use. Set from the settings at startup and when the
    /// user switches; everything is translated at the moment of display, never
    /// stored translated. The Python phone app was caught twice by the other
    /// habit - a remembered status stayed in the old language until the state
    /// happened to change.
    /// </summary>
    public static string Language { get; set; } = "cs";

    /// <summary>The text for a key, with {0} filled in when a value is given.</summary>
    public static string Get(string key, object? value = null)
    {
        var table = Language == "en" ? English : Czech;
        if (!table.TryGetValue(key, out string? text))
            // Never silently show nothing: a key with no text is a fault in
            // this file, and it has to be visible to whoever sees the screen.
            return $"[{key}]";
        return value is null ? text : string.Format(text, value);
    }

    /// <summary>
    /// Keys that exist in one language only. Checked by the tests before a
    /// release - a check nobody runs is worse than none, so it has a caller.
    /// </summary>
    public static IEnumerable<string> Missing() =>
        Czech.Keys.Except(English.Keys).Concat(English.Keys.Except(Czech.Keys));

    // The state keys are the ones Core hands over as Decision.LabelKey. They
    // are listed in the order Decide() weighs them.
    private static readonly Dictionary<string, string> Czech = new()
    {
        ["st_off"] = "Vypnuto",
        ["st_screen_locked"] = "Obrazovka je zamčená",
        ["st_paused"] = "Pozastaveno, zbývá {0} min",
        ["st_trusted_network"] = "Uložená Wi-Fi – nezamyká se",
        ["st_waiting"] = "Čeká na telefon",
        ["st_locked_wait"] = "Zamčeno, čeká na návrat telefonu",
        ["st_guard"] = "Pracuje se – nezamyká se",
        ["st_guard_countdown"] = "Pracuje se – nezamyká se",
        ["st_locking"] = "Zamyká se",
        ["st_countdown"] = "Zamknutí za {0} s",
        ["st_at_desk"] = "Hlídá",
        ["st_locked"] = "Zamčeno",
        ["st_lock_failed"] = "Zamknout se nepodařilo",

        ["detail_at_desk"] = "telefon u stolu, {0} dBm",
        ["detail_no_reading"] = "telefon zatím není slyšet",

        ["act_pause"] = "Pauza 15 min",
        ["act_resume"] = "Pokračovat",
        ["act_lock_now"] = "Zamknout teď",
        ["act_settings"] = "Nastavení",

        ["net_this"] = "Tato síť",
        ["net_hint"] = "nezamykat, když jsem na ní",
        ["net_none"] = "žádná Wi-Fi",

        ["foot_last_lock"] = "Naposledy zamčeno {0}",
        ["foot_never_locked"] = "Zatím se nezamykalo",
        ["foot_log"] = "Záznam",

        ["msg_no_signal"] = "Telefon se {0} min neozval – hlídání nefunguje.",
    };

    private static readonly Dictionary<string, string> English = new()
    {
        ["st_off"] = "Off",
        ["st_screen_locked"] = "The screen is locked",
        ["st_paused"] = "Paused, {0} min left",
        ["st_trusted_network"] = "Saved Wi-Fi - not locking",
        ["st_waiting"] = "Waiting for the phone",
        ["st_locked_wait"] = "Locked, waiting for the phone",
        ["st_guard"] = "In use - not locking",
        ["st_guard_countdown"] = "In use - not locking",
        ["st_locking"] = "Locking",
        ["st_countdown"] = "Locking in {0} s",
        ["st_at_desk"] = "Watching",
        ["st_locked"] = "Locked",
        ["st_lock_failed"] = "Locking failed",

        ["detail_at_desk"] = "phone at the desk, {0} dBm",
        ["detail_no_reading"] = "the phone cannot be heard yet",

        ["act_pause"] = "Pause 15 min",
        ["act_resume"] = "Resume",
        ["act_lock_now"] = "Lock now",
        ["act_settings"] = "Settings",

        ["net_this"] = "This network",
        ["net_hint"] = "do not lock while I am on it",
        ["net_none"] = "no Wi-Fi",

        ["foot_last_lock"] = "Last locked at {0}",
        ["foot_never_locked"] = "Nothing locked yet",
        ["foot_log"] = "Log",

        ["msg_no_signal"] = "The phone has not been heard for {0} min - watching is not working.",
    };
}
