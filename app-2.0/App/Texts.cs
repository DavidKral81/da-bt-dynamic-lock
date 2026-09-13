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

    /// <summary>The text for a key, with {0}, {1}… filled in when values are given.</summary>
    public static string Get(string key, params object?[] values)
    {
        var table = Language == "en" ? English : Czech;
        if (!table.TryGetValue(key, out string? text))
            // Never silently show nothing: a key with no text is a fault in
            // this file, and it has to be visible to whoever sees the screen.
            return $"[{key}]";
        // "values is null" is not paranoia: a caller passing a single object?
        // that happens to BE null - as the status line does when a state has
        // neither seconds nor minutes - hands this method a null array rather
        // than an array holding null.
        if (values is null || values.Length == 0)
            return text;
        try
        {
            return string.Format(text, values);
        }
        catch (FormatException)
        {
            // A text whose placeholders do not match what the caller passes is
            // also a fault in this file. Shown as such rather than crashing the
            // window it was going into.
            return $"[{key}!]";
        }
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
        ["foot_never_locked"] = "zatím nikdy",
        ["foot_log"] = "Záznam",

        ["msg_no_signal"] = "Telefon se {0} min neozval – hlídání nefunguje.",

        // --- settings window: navigation ---------------------------------
        ["app_version"] = "verze {0}",
        ["nav_overview"] = "Přehled",
        ["nav_phone"] = "Telefon",
        ["nav_locking"] = "Zamykání",
        ["nav_networks"] = "Wi-Fi sítě",
        ["nav_app"] = "Aplikace",

        // --- settings window: overview -----------------------------------
        ["ring_seconds"] = "{0} s",
        ["ring_of"] = "ticho z {0}",
        ["key_phone"] = "Telefon",
        ["key_signal"] = "Síla signálu",
        ["key_network"] = "Síť",
        ["key_last_lock"] = "Naposledy zamčeno",
        ["val_no_phone"] = "není vybraný",
        ["val_dbm"] = "{0} dBm",
        ["val_no_reading"] = "není slyšet",
        ["val_net_trusted"] = "{0} – nezamyká se",
        ["val_net_locking"] = "{0} – zamyká se",
        ["act_pause_15"] = "Pozastavit 15 min",
        ["act_pause_60"] = "Pozastavit 1 hodinu",
        ["act_end_pause"] = "Ukončit pozastavení",
        ["act_switch_off"] = "Vypnout hlídání",
        ["act_switch_on"] = "Zapnout hlídání",

        ["why_off"] = "Hlídání je vypnuté. Notebook se sám nezamkne.",
        ["why_screen_locked"] = "Za zamčenou obrazovkou se nic nerozhoduje. "
                              + "Hlídá se zase po odemknutí.",
        ["why_paused"] = "Až pozastavení skončí, hlídání se samo zapne.",
        ["why_trusted_network"] = "Na téhle Wi-Fi se zamykat nemá, takže se nezamyká.",
        ["why_waiting"] = "Telefon ještě nebyl slyšet. Zkontroluj, jestli v něm "
                        + "běží aplikace Da BT Dynamic Lock.",
        ["why_locked"] = "Zamčeno. Hlídat se začne znovu, až bude telefon zase slyšet.",
        ["why_idle_guard"] = "Pracuje se na počítači, takže se nezamyká, i když "
                           + "telefon není slyšet.",
        ["why_watching"] = "Notebook se zamkne {0} s poté, co telefon přestane být slyšet.",

        // --- settings window: phone --------------------------------------
        ["card_phone"] = "Které zařízení hlídat",
        ["card_phone_hint"] = "Podle tohohle zařízení se pozná, že jsi u počítače. "
                            + "Seznam se plní tím, co je právě slyšet.",
        ["dev_none_heard"] = "Zatím není nic slyšet – chvíli to trvá.",
        ["dev_not_heard"] = "{0} (není slyšet)",
        ["dev_dbm"] = "{0} ({1} dBm)",
        ["dev_last_heard"] = "{0} (naposledy před {1} s)",
        ["card_gone"] = "Když telefon zmizí",
        ["card_gone_hint"] = "Vybitý nebo vypnutý telefon není slyšet, takže se "
                           + "notebook jednou zamkne a pak už nehlídá. Tohle na to upozorní.",
        ["opt_no_warning"] = "Neupozorňovat",
        ["opt_after_minutes"] = "Po {0} minutách bez signálu",

        // --- settings window: locking ------------------------------------
        ["sw_active"] = "Zamykat, když se vzdálím s telefonem",
        ["sw_active_hint"] = "Hlavní vypínač celé aplikace.",
        ["group_when"] = "Kdy zamknout",
        ["lbl_silence"] = "Ticho, po kterém se zamyká",
        ["lbl_silence_hint"] = "Jak dlouho telefon nesmí být slyšet.",
        ["opt_seconds"] = "{0} sekund",
        ["lbl_range"] = "Dosah",
        ["lbl_range_hint"] = "Slabší signál než tenhle se bere, jako by telefon "
                           + "nebyl slyšet.",
        // Short on purpose: it shares a fixed width with the other entries in
        // the list, and the card's own line underneath says what it means.
        ["opt_range_max"] = "Bez omezení",
        ["opt_range_longest"] = "{0} dBm – největší dosah",
        ["opt_range_shortest"] = "{0} dBm – nejmenší dosah",
        ["opt_range_edge"] = "{0} dBm – v kapse bývá právě tolik",
        ["opt_range_plain"] = "{0} dBm",
        ["sw_idle_guard"] = "Nezamykat, když zrovna píšu nebo hýbu myší",
        ["sw_idle_guard_hint"] = "Pojistka pro případ, že telefon zmlkne, "
                               + "zatímco sedíš u počítače.",
        ["group_countdown"] = "Odpočet před zamknutím",
        ["lbl_countdown"] = "Kdy ukázat odpočet",
        ["lbl_countdown_hint"] = "Kolik času zbývá na to vrátit se ke stolu.",
        ["opt_countdown_off"] = "Nezobrazovat",
        ["opt_countdown_from"] = "{0} sekund předem",
        ["lbl_position"] = "Kde se odpočet ukáže",
        ["lbl_position_hint"] = "Měřeno od horního okraje obrazovky.",
        ["opt_from_top"] = "{0} % odshora",
        ["sw_primary_only"] = "Jen na hlavním monitoru",
        ["sw_primary_only_hint"] = "Jinak se odpočet ukáže na každé obrazovce.",

        // --- settings window: networks -----------------------------------
        ["sw_trusted"] = "Nezamykat na vybraných Wi-Fi sítích",
        ["sw_trusted_hint"] = "Třeba doma nebo v kanceláři, kde zamykat nepotřebuješ.",
        ["card_networks"] = "Sítě bez zamykání",
        ["card_networks_hint"] = "Zaškrtnutá síť se uloží, odškrtnutá se zapomene. "
                               + "Každý přístupový bod má vlastní řádek.",
        ["net_none_connected"] = "Nejsi připojený k Wi-Fi a žádná uložená síť tu není.",
        ["net_row"] = "{0} · {1}",
        ["net_here"] = "{0} · {1} – právě připojeno",

        // --- settings window: application --------------------------------
        ["sw_log"] = "Zapisovat záznam o běhu",
        ["sw_log_hint"] = "Podle něj se pozná, proč se notebook zamkl. "
                        + "Hodí se přiložit k hlášení chyby.",
        ["lbl_folder"] = "Kde záznam leží",
        ["act_open_folder"] = "Otevřít složku",
        ["lbl_version"] = "Verze {0}",
        ["lbl_version_hint"] = "Porovnej ji s tou na stránce vydání.",
        ["link_updates"] = "Stránka vydání",
        ["link_project"] = "Projekt na GitHubu",
        ["link_phone_app"] = "Stáhnout aplikaci pro telefon",
        ["btn_quit"] = "Ukončit aplikaci",
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
        ["foot_never_locked"] = "not yet",
        ["foot_log"] = "Log",

        ["msg_no_signal"] = "The phone has not been heard for {0} min - watching is not working.",

        // --- settings window: navigation ---------------------------------
        ["app_version"] = "version {0}",
        ["nav_overview"] = "Overview",
        ["nav_phone"] = "Phone",
        ["nav_locking"] = "Locking",
        ["nav_networks"] = "Wi-Fi networks",
        ["nav_app"] = "Application",

        // --- settings window: overview -----------------------------------
        ["ring_seconds"] = "{0} s",
        ["ring_of"] = "silence of {0}",
        ["key_phone"] = "Phone",
        ["key_signal"] = "Signal",
        ["key_network"] = "Network",
        ["key_last_lock"] = "Last locked",
        ["val_no_phone"] = "none chosen",
        ["val_dbm"] = "{0} dBm",
        ["val_no_reading"] = "not heard",
        ["val_net_trusted"] = "{0} - not locking",
        ["val_net_locking"] = "{0} - locking",
        ["act_pause_15"] = "Pause for 15 min",
        ["act_pause_60"] = "Pause for 1 hour",
        ["act_end_pause"] = "End the pause",
        ["act_switch_off"] = "Switch watching off",
        ["act_switch_on"] = "Switch watching on",

        ["why_off"] = "Watching is off. The computer will not lock itself.",
        ["why_screen_locked"] = "Nothing is decided behind a locked screen. "
                              + "Watching resumes after you unlock.",
        ["why_paused"] = "When the pause ends, watching switches itself back on.",
        ["why_trusted_network"] = "This Wi-Fi is one where locking is not wanted, "
                                + "so nothing locks.",
        ["why_waiting"] = "The phone has not been heard yet. Check that the "
                        + "Da BT Dynamic Lock app is running on it.",
        ["why_locked"] = "Locked. Watching starts again once the phone is heard.",
        ["why_idle_guard"] = "You are using the computer, so it will not lock even "
                           + "though the phone cannot be heard.",
        ["why_watching"] = "The computer locks {0} s after the phone stops being heard.",

        // --- settings window: phone --------------------------------------
        ["card_phone"] = "Which device to watch",
        ["card_phone_hint"] = "This device is what tells the computer you are nearby. "
                            + "The list fills up with whatever is heard.",
        ["dev_none_heard"] = "Nothing heard yet - give it a moment.",
        ["dev_not_heard"] = "{0} (not heard)",
        ["dev_dbm"] = "{0} ({1} dBm)",
        ["dev_last_heard"] = "{0} (last heard {1} s ago)",
        ["card_gone"] = "When the phone disappears",
        ["card_gone_hint"] = "A flat or switched-off phone cannot be heard, so the "
                           + "computer locks once and then stops watching. This warns you.",
        ["opt_no_warning"] = "Do not warn",
        ["opt_after_minutes"] = "After {0} minutes without a signal",

        // --- settings window: locking ------------------------------------
        ["sw_active"] = "Lock when I walk away with my phone",
        ["sw_active_hint"] = "The main switch for the whole app.",
        ["group_when"] = "When to lock",
        ["lbl_silence"] = "Silence before locking",
        ["lbl_silence_hint"] = "How long the phone has to go unheard.",
        ["opt_seconds"] = "{0} seconds",
        ["lbl_range"] = "Range",
        ["lbl_range_hint"] = "A signal weaker than this counts as the phone not "
                           + "being heard.",
        ["opt_range_max"] = "No limit",
        ["opt_range_longest"] = "{0} dBm - longest range",
        ["opt_range_shortest"] = "{0} dBm - shortest range",
        ["opt_range_edge"] = "{0} dBm - about what a pocket measures",
        ["opt_range_plain"] = "{0} dBm",
        ["sw_idle_guard"] = "Do not lock while I am typing or moving the mouse",
        ["sw_idle_guard_hint"] = "A safeguard for when the phone goes quiet while "
                               + "you are sitting at the computer.",
        ["group_countdown"] = "The countdown before locking",
        ["lbl_countdown"] = "When to show the countdown",
        ["lbl_countdown_hint"] = "How much time is left to get back to the desk.",
        ["opt_countdown_off"] = "Do not show",
        ["opt_countdown_from"] = "{0} seconds ahead",
        ["lbl_position"] = "Where the countdown appears",
        ["lbl_position_hint"] = "Measured from the top of the screen.",
        ["opt_from_top"] = "{0} % from the top",
        ["sw_primary_only"] = "On the main monitor only",
        ["sw_primary_only_hint"] = "Otherwise the countdown appears on every screen.",

        // --- settings window: networks -----------------------------------
        ["sw_trusted"] = "Do not lock on the selected Wi-Fi networks",
        ["sw_trusted_hint"] = "At home or at the office, say, where locking is not needed.",
        ["card_networks"] = "Networks without locking",
        ["card_networks_hint"] = "Ticking a network saves it, unticking forgets it. "
                               + "Every access point has its own row.",
        ["net_none_connected"] = "You are not on Wi-Fi and no network is saved.",
        ["net_row"] = "{0} · {1}",
        ["net_here"] = "{0} · {1} - connected now",

        // --- settings window: application --------------------------------
        ["sw_log"] = "Keep a log of what happens",
        ["sw_log_hint"] = "It is what tells you why the computer locked. "
                        + "Worth attaching to a fault report.",
        ["lbl_folder"] = "Where the log is kept",
        ["act_open_folder"] = "Open the folder",
        ["lbl_version"] = "Version {0}",
        ["lbl_version_hint"] = "Compare it with the one on the releases page.",
        ["link_updates"] = "Releases page",
        ["link_project"] = "Project on GitHub",
        ["link_phone_app"] = "Download the phone app",
        ["btn_quit"] = "Quit the application",
    };
}
