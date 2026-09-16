using System.Globalization;

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

    /// <summary>
    /// How numbers and dates inside a text are written. The INTERFACE language
    /// decides it, never the machine's: on Czech Windows the English chart
    /// summary came out as "27,2/min", because formatting fell back to whatever
    /// culture the thread happened to carry.
    /// </summary>
    public static CultureInfo Culture =>
        Language == "en" ? EnglishCulture : CzechCulture;

    private static readonly CultureInfo CzechCulture = new("cs-CZ");
    private static readonly CultureInfo EnglishCulture = new("en-GB");

    /// <summary>
    /// The languages on offer, each written IN ITSELF - never translated, so
    /// somebody who opened the app in a language they cannot read still
    /// recognises their own.
    ///
    /// Named rather than flagged. A flag is a country, not a language: English
    /// has no flag that is not also a claim about whose English it is, and the
    /// choice between a British and an American one has no right answer. The
    /// common practice in software is to write the language out.
    ///
    /// One list, used by the settings window and by the installer.
    /// </summary>
    /// <summary>One language on offer. A record, not a tuple: a drop-down binds
    /// to a real property name, and a tuple has none at run time.</summary>
    public sealed record LanguageOption(string Code, string Name);

    public static readonly LanguageOption[] Languages =
    {
        new("cs", "Čeština"),
        new("en", "English"),
    };

    /// <summary>
    /// The language to start in when nothing has been chosen yet: the one
    /// Windows is in, falling back to English.
    ///
    /// Never Czech by default. An English-speaking user installing this must
    /// not be handed a Czech interface because of where it was written.
    /// </summary>
    public static string SystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "cs" ? "cs" : "en";

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
            return string.Format(Culture, text, values);
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
        ["act_settings"] = "Nastavení",

        ["net_this"] = "Tato síť",
        ["net_hint"] = "nezamykat, když jsem na ní",
        ["net_none"] = "žádná Wi-Fi",

        ["foot_last_lock"] = "Naposledy zamčeno {0}",
        ["foot_never_locked"] = "zatím nikdy",
        ["foot_log"] = "Záznam",

        ["msg_no_signal"] = "Telefon se {0} min neozval – hlídání nefunguje.",
        ["msg_already_running"] = "Da BT Dynamic Lock už běží.\n\nAplikace je "
                                + "dostupná jako ikona v oznamovací oblasti vpravo "
                                + "dole u hodin, případně pod šipkou skrytých ikon.",

        // --- settings window: navigation ---------------------------------
        ["app_version"] = "verze {0}",
        ["nav_overview"] = "Přehled",
        ["nav_signal"] = "Signál",
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

        ["why_off"] = "Hlídání je vypnuté. Počítač se sám nezamkne.",
        ["why_screen_locked"] = "Za zamčenou obrazovkou se nic nerozhoduje. "
                              + "Hlídání pokračuje po odemknutí.",
        ["why_paused"] = "Až pozastavení skončí, hlídání se samo zapne.",
        ["why_trusted_network"] = "Tato síť je uložená mezi sítěmi bez zamykání.",
        ["why_waiting"] = "Telefon ještě nebyl slyšet. V telefonu musí běžet "
                        + "aplikace Da BT Dynamic Lock.",
        ["why_locked"] = "Zamčeno. Hlídání začne znovu, až bude telefon zase slyšet.",
        ["why_idle_guard"] = "Na počítači se právě pracuje, takže se nezamyká, "
                           + "i když telefon není slyšet.",
        ["why_watching"] = "Počítač se zamkne {0} s poté, co telefon přestane být slyšet.",

        // --- settings window: the chart ----------------------------------
        ["chart_title"] = "Síla signálu telefonu",
        ["range_2min"] = "2 min",
        ["range_5min"] = "5 min",
        ["range_15min"] = "15 min",
        ["range_1h"] = "1 h",
        ["range_8h"] = "8 h",
        ["range_1day"] = "1 den",
        // The numbers are rounded HERE, in the text, not by the caller: a
        // caller that hands over a ready-made string formats it with the
        // machine's culture and the decimal mark stops following the language.
        ["chart_summary"] = "{0} signálů · {1:F1}/min · medián {2} dBm · "
                          + "nejdelší ticho {3:F0} s",
        ["chart_no_signal"] = "V tomto úseku není žádný signál.",
        ["chart_threshold"] = "práh {0} dBm",
        ["leg_signal"] = "signál (výš = lepší)",
        ["leg_weak"] = "slabý — pod prahem, nepočítá se",
        ["leg_gap"] = "telefon nebyl slyšet",
        ["leg_gap_lock"] = "ticho dost dlouhé na zamknutí",
        ["leg_downtime"] = "aplikace neběžela",
        ["leg_locked"] = "uzamknuto",
        ["leg_threshold"] = "práh citlivosti",

        // --- settings window: phone --------------------------------------
        ["card_phone"] = "Které zařízení hlídat",
        ["card_phone_hint"] = "Podle tohoto zařízení aplikace pozná přítomnost "
                            + "u počítače. Seznam se plní tím, co je právě slyšet.",
        ["dev_none_heard"] = "Zatím není slyšet žádné zařízení. Chvíli to trvá.",
        ["dev_not_heard"] = "{0} (není slyšet)",
        ["dev_dbm"] = "{0} ({1} dBm)",
        ["dev_last_heard"] = "{0} (naposledy před {1} s)",
        ["card_gone"] = "Když telefon zmizí",
        ["card_gone_hint"] = "Vybitý nebo vypnutý telefon není slyšet, takže se "
                           + "počítač jednou zamkne a dál už nehlídá. Tato volba "
                           + "na takový stav upozorní.",
        ["opt_no_warning"] = "Neupozorňovat",
        ["opt_after_minutes"] = "Po {0} minutách bez signálu",

        // --- settings window: locking ------------------------------------
        ["sw_active"] = "Zamykat, když se vzdálím s telefonem",
        ["sw_active_hint"] = "Hlavní vypínač celé aplikace.",
        ["group_when"] = "Kdy zamknout",
        ["lbl_silence"] = "Ticho, po kterém se zamyká",
        ["opt_seconds"] = "{0} sekund",
        ["lbl_range"] = "Dosah",
        ["lbl_range_hint"] = "Slabší signál, než je zvolený, se považuje "
                           + "za neslyšitelný.",
        // Short on purpose: it shares a fixed width with the other entries in
        // the list, and the card's own line underneath says what it means.
        ["opt_range_max"] = "Bez omezení",
        ["opt_range_longest"] = "{0} dBm – největší dosah",
        ["opt_range_shortest"] = "{0} dBm – nejmenší dosah",
        // Short because it has to fit the box. The longer wording came out cut
        // off as "z kaps" - the same way the English one once read "a pocket
        // meas". Czech is the longer language, so it sets the limit.
        ["opt_range_edge"] = "{0} dBm – telefon v kapse",
        ["opt_range_plain"] = "{0} dBm",
        ["sw_idle_guard"] = "Nezamykat, když právě píšu nebo hýbu myší",
        ["sw_idle_guard_hint"] = "Pojistka pro případ, že telefon zmlkne "
                               + "během práce na počítači.",
        ["group_countdown"] = "Odpočet před zamknutím",
        ["lbl_countdown"] = "Kdy ukázat odpočet",
        ["lbl_countdown_hint"] = "Kolik času zbývá na návrat ke stolu.",
        ["opt_countdown_off"] = "Nezobrazovat",
        ["opt_countdown_from"] = "{0} sekund předem",
        ["lbl_position"] = "Kde se odpočet ukáže",
        ["opt_from_top"] = "{0} % odshora",
        ["sw_primary_only"] = "Jen na hlavním monitoru",
        ["sw_primary_only_hint"] = "Jinak se odpočet ukáže na každé obrazovce.",

        ["card_pause"] = "Dočasně pozastavit",
        ["card_pause_hint"] = "Hlídání se na zvolenou dobu vypne a poté se "
                            + "samo zapne.",
        ["lbl_pause_left"] = "Zbývá {0} min",
        ["lbl_pause_last"] = "Zbývá necelá minuta",
        ["opt_not_paused"] = "Nepozastaveno",
        ["opt_5_min"] = "5 minut",
        ["opt_15_min"] = "15 minut",
        ["opt_30_min"] = "30 minut",
        ["opt_1_hour"] = "1 hodinu",
        ["opt_2_hours"] = "2 hodiny",
        ["opt_4_hours"] = "4 hodiny",
        ["opt_12_hours"] = "12 hodin",
        ["opt_1_day"] = "1 den",
        ["opt_2_days"] = "2 dny",

        // --- settings window: networks -----------------------------------
        ["sw_trusted"] = "Nezamykat na vybraných Wi-Fi sítích",
        ["sw_trusted_hint"] = "Například doma nebo v kanceláři, kde zamykání "
                            + "není potřeba.",
        ["card_networks"] = "Sítě bez zamykání",
        ["card_networks_hint"] = "Zaškrtnutá síť se uloží, odškrtnutá se zapomene. "
                               + "Každý přístupový bod má vlastní řádek.",
        ["net_none_connected"] = "Počítač není připojen k Wi-Fi a žádná síť "
                               + "není uložená.",
        ["net_row"] = "{0} · {1}",
        ["net_here"] = "{0} · {1} – právě připojeno",

        // --- settings window: application --------------------------------
        ["sw_autostart"] = "Spouštět po přihlášení do Windows",
        ["sw_autostart_hint"] = "Bez toho se hlídání po restartu samo nezapne.",
        ["sw_autostart_failed"] = "Spuštění po přihlášení se nepodařilo nastavit "
                                + "— podrobnosti jsou v záznamu o běhu.",
        ["sw_log"] = "Zapisovat záznam o běhu",
        ["sw_log_hint"] = "Podle záznamu lze zjistit, proč se počítač zamkl. "
                        + "Vhodné přiložit k hlášení chyby.",
        // Back after being dropped for the flags. A drop-down needs saying what
        // it is; a flag did not.
        ["lbl_language"] = "Jazyk",
        ["lbl_folder"] = "Kde záznam leží",
        ["act_open_folder"] = "Otevřít složku",
        ["lbl_version"] = "Verze {0}",
        ["link_updates"] = "Stránka vydání",
        ["link_project"] = "Projekt na GitHubu",
        ["link_phone_app"] = "Stáhnout aplikaci pro telefon",
        ["btn_quit"] = "Ukončit aplikaci",

        // --- installer ---------------------------------------------------
        // Taken from the shipped 1.5 (windows/texts.py, the ins_ and uni_
        // keys), with the wording made impersonal: the installer speaks about
        // what happens, not to the reader.
        ["ins_title_install"] = "Instalace – {0}",
        ["ins_title_uninstall"] = "Odinstalace – {0}",
        ["ins_subtitle"] = "Automatické uzamčení počítače při vzdálení s telefonem.",
        ["ins_to_folder"] = "Program se nainstaluje do složky:",
        ["ins_from_folder"] = "Program se odinstaluje ze složky:",
        ["ins_opt_startmenu"] = "Přidat zástupce do nabídky Start",
        ["ins_opt_desktop"] = "Přidat zástupce na plochu",
        ["ins_opt_autostart"] = "Spouštět automaticky při přihlášení",
        ["uni_opt_data"] = "Odstranit i nastavení a historii",
        ["ins_btn_install"] = "Instalovat",
        ["ins_btn_uninstall"] = "Odinstalovat",
        ["ins_btn_cancel"] = "Zrušit",
        ["ins_btn_close"] = "Zavřít",

        // The steps Setup reports while it works. The keys are built from the
        // word Setup passes, so a step added there without a text here shows up
        // as a missing key rather than as silence.
        ["ins_step_stopping"] = "Ukončuje se běžící aplikace…",
        ["ins_step_copying"] = "Kopíruje se program…",
        ["ins_step_shortcuts"] = "Vytvářejí se zástupci…",
        ["ins_step_registry"] = "Zapisuje se záznam do seznamu aplikací…",
        ["ins_step_autostart"] = "Nastavuje se spouštění při přihlášení…",
        ["ins_step_checking"] = "Kontroluje se výsledek…",
        ["ins_step_data"] = "Odstraňuje se nastavení a historie…",
        ["ins_step_files"] = "Odstraňuje se program…",

        // The outcome goes into the window's own heading, so it is written as a
        // whole sentence. There is no second heading under it any more.
        ["ins_head_installed"] = "Aplikace {0} byla úspěšně nainstalována.",
        ["ins_head_uninstalled"] = "Aplikace {0} byla odinstalována.",
        ["ins_head_problems"] = "Dokončeno s výhradami",
        // Without an app broadcasting from the phone there is nothing to watch
        // for and the computer never locks, so this is the one thing the result
        // screen has to make impossible to miss.
        ["ins_phone_needed"] = "Ke správnému běhu aplikace je potřeba "
                             + "nainstalovat mobilní aplikaci.",
        // A heading that says something went wrong must not sit above a line
        // claiming it all worked. Found by looking at the picture.
        ["ins_partial_desc"] = "Program je nainstalovaný, ale některé kroky "
                             + "se nepodařily.",
        ["uni_partial_desc"] = "Program byl odstraněn, ale některé kroky "
                             + "se nepodařily.",
        ["ins_problems_desc"] = "Následující kroky se nepodařily:",
        ["ins_opt_launch"] = "Spustit aplikaci",
        ["ins_opt_phone"] = "Otevřít stránku se stažením aplikace pro telefon",

        ["ins_admin_refused"] = "Instalace vyžaduje oprávnění správce "
                              + "a bez nich nemůže pokračovat.",
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
        ["act_settings"] = "Settings",

        ["net_this"] = "This network",
        ["net_hint"] = "do not lock while I am on it",
        ["net_none"] = "no Wi-Fi",

        ["foot_last_lock"] = "Last locked at {0}",
        ["foot_never_locked"] = "not yet",
        ["foot_log"] = "Log",

        ["msg_no_signal"] = "The phone has not been heard for {0} min - watching is not working.",
        ["msg_already_running"] = "Da BT Dynamic Lock is already running.\n\nThe "
                                + "application is available as an icon in the "
                                + "notification area next to the clock, possibly "
                                + "hidden under the arrow.",

        // --- settings window: navigation ---------------------------------
        ["app_version"] = "version {0}",
        ["nav_overview"] = "Overview",
        ["nav_signal"] = "Signal",
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
                              + "Watching resumes once the screen is unlocked.",
        ["why_paused"] = "When the pause ends, watching switches itself back on.",
        ["why_trusted_network"] = "This network is saved among the networks "
                                + "without locking.",
        ["why_waiting"] = "The phone has not been heard yet. The Da BT Dynamic "
                        + "Lock app has to be running on it.",
        ["why_locked"] = "Locked. Watching starts again once the phone is heard.",
        ["why_idle_guard"] = "The computer is in use, so it will not lock even "
                           + "though the phone cannot be heard.",
        ["why_watching"] = "The computer locks {0} s after the phone stops being heard.",

        // --- settings window: the chart ----------------------------------
        ["chart_title"] = "The phone's signal strength",
        ["range_2min"] = "2 min",
        ["range_5min"] = "5 min",
        ["range_15min"] = "15 min",
        ["range_1h"] = "1 h",
        ["range_8h"] = "8 h",
        ["range_1day"] = "1 day",
        ["chart_summary"] = "{0} signals · {1:F1}/min · median {2} dBm · "
                          + "longest silence {3:F0} s",
        ["chart_no_signal"] = "No signal at all in this range.",
        ["chart_threshold"] = "threshold {0} dBm",
        ["leg_signal"] = "signal (higher is better)",
        ["leg_weak"] = "weak - below the threshold, not counted",
        ["leg_gap"] = "the phone was not heard",
        ["leg_gap_lock"] = "silence long enough to lock",
        ["leg_downtime"] = "the app was not running",
        ["leg_locked"] = "locked",
        ["leg_threshold"] = "sensitivity threshold",

        // --- settings window: phone --------------------------------------
        ["card_phone"] = "Which device to watch",
        ["card_phone_hint"] = "This device is how the application knows somebody "
                            + "is nearby. The list fills up with whatever is heard.",
        ["dev_none_heard"] = "No device heard yet. This takes a moment.",
        ["dev_not_heard"] = "{0} (not heard)",
        ["dev_dbm"] = "{0} ({1} dBm)",
        ["dev_last_heard"] = "{0} (last heard {1} s ago)",
        ["card_gone"] = "When the phone disappears",
        ["card_gone_hint"] = "A flat or switched-off phone cannot be heard, so the "
                           + "computer locks once and then stops watching. This "
                           + "option gives warning of that.",
        ["opt_no_warning"] = "Do not warn",
        ["opt_after_minutes"] = "After {0} minutes without a signal",

        // --- settings window: locking ------------------------------------
        ["sw_active"] = "Lock when I walk away with my phone",
        ["sw_active_hint"] = "The main switch for the whole app.",
        ["group_when"] = "When to lock",
        ["lbl_silence"] = "Silence before locking",
        ["opt_seconds"] = "{0} seconds",
        ["lbl_range"] = "Range",
        ["lbl_range_hint"] = "A signal weaker than the one chosen counts as the "
                           + "phone not being heard.",
        ["opt_range_max"] = "No limit",
        ["opt_range_longest"] = "{0} dBm - longest range",
        ["opt_range_shortest"] = "{0} dBm - shortest range",
        // Kept short because it has to fit the box: the longer wording
        // ("about what a pocket measures") came out cut off as "a pocket meas".
        ["opt_range_edge"] = "{0} dBm - a phone in a pocket",
        ["opt_range_plain"] = "{0} dBm",
        ["sw_idle_guard"] = "Do not lock while I am typing or moving the mouse",
        ["sw_idle_guard_hint"] = "A safeguard for when the phone goes quiet "
                               + "during work at the computer.",
        ["group_countdown"] = "The countdown before locking",
        ["lbl_countdown"] = "When to show the countdown",
        ["lbl_countdown_hint"] = "How much time is left to get back to the desk.",
        ["opt_countdown_off"] = "Do not show",
        ["opt_countdown_from"] = "{0} seconds ahead",
        ["lbl_position"] = "Where the countdown appears",
        ["opt_from_top"] = "{0} % from the top",
        ["sw_primary_only"] = "On the main monitor only",
        ["sw_primary_only_hint"] = "Otherwise the countdown appears on every screen.",

        ["card_pause"] = "Pause for a while",
        ["card_pause_hint"] = "Watching turns off for the chosen time and then "
                            + "turns itself back on.",
        ["lbl_pause_left"] = "{0} min left",
        ["lbl_pause_last"] = "Less than a minute left",
        ["opt_not_paused"] = "Not paused",
        ["opt_5_min"] = "5 minutes",
        ["opt_15_min"] = "15 minutes",
        ["opt_30_min"] = "30 minutes",
        ["opt_1_hour"] = "1 hour",
        ["opt_2_hours"] = "2 hours",
        ["opt_4_hours"] = "4 hours",
        ["opt_12_hours"] = "12 hours",
        ["opt_1_day"] = "1 day",
        ["opt_2_days"] = "2 days",

        // --- settings window: networks -----------------------------------
        ["sw_trusted"] = "Do not lock on the selected Wi-Fi networks",
        ["sw_trusted_hint"] = "For example at home or at the office, where "
                            + "locking is not needed.",
        ["card_networks"] = "Networks without locking",
        ["card_networks_hint"] = "Ticking a network saves it, unticking forgets it. "
                               + "Every access point has its own row.",
        ["net_none_connected"] = "The computer is not on Wi-Fi and no network "
                               + "is saved.",
        ["net_row"] = "{0} · {1}",
        ["net_here"] = "{0} · {1} - connected now",

        // --- settings window: application --------------------------------
        ["sw_autostart"] = "Start after signing in to Windows",
        ["sw_autostart_hint"] = "Without this, watching does not come back after "
                              + "a restart.",
        ["sw_autostart_failed"] = "Start at logon could not be set - the log says "
                                + "what went wrong.",
        ["sw_log"] = "Keep a log of what happens",
        ["sw_log_hint"] = "The log is what shows why the computer locked. "
                        + "Worth attaching to a fault report.",
        ["lbl_language"] = "Language",
        ["lbl_folder"] = "Where the log is kept",
        ["act_open_folder"] = "Open the folder",
        ["lbl_version"] = "Version {0}",
        ["link_updates"] = "Releases page",
        ["link_project"] = "Project on GitHub",
        ["link_phone_app"] = "Download the phone app",
        ["btn_quit"] = "Quit the application",

        // --- installer ---------------------------------------------------
        ["ins_title_install"] = "Setup – {0}",
        ["ins_title_uninstall"] = "Uninstall – {0}",
        ["ins_subtitle"] = "Locks the computer automatically when the phone moves away.",
        ["ins_to_folder"] = "The program will be installed into:",
        ["ins_from_folder"] = "The program will be removed from:",
        ["ins_opt_startmenu"] = "Add a shortcut to the Start menu",
        ["ins_opt_desktop"] = "Add a shortcut to the desktop",
        ["ins_opt_autostart"] = "Start automatically on sign-in",
        ["uni_opt_data"] = "Remove the settings and history as well",
        ["ins_btn_install"] = "Install",
        ["ins_btn_uninstall"] = "Uninstall",
        ["ins_btn_cancel"] = "Cancel",
        ["ins_btn_close"] = "Close",

        ["ins_step_stopping"] = "Stopping the running application…",
        ["ins_step_copying"] = "Copying the program…",
        ["ins_step_shortcuts"] = "Creating the shortcuts…",
        ["ins_step_registry"] = "Writing the entry in the list of apps…",
        ["ins_step_autostart"] = "Setting up the start on sign-in…",
        ["ins_step_checking"] = "Checking the result…",
        ["ins_step_data"] = "Removing the settings and history…",
        ["ins_step_files"] = "Removing the program…",

        ["ins_head_installed"] = "{0} has been installed successfully.",
        ["ins_head_uninstalled"] = "{0} has been uninstalled.",
        ["ins_head_problems"] = "Finished with problems",
        ["ins_phone_needed"] = "The phone app has to be installed as well "
                             + "for this to work.",
        ["ins_partial_desc"] = "The program is installed, but some steps "
                             + "did not succeed.",
        ["uni_partial_desc"] = "The program has been removed, but some steps "
                             + "did not succeed.",
        ["ins_problems_desc"] = "These steps did not succeed:",
        ["ins_opt_launch"] = "Run the application",
        ["ins_opt_phone"] = "Open the download page for the phone app",

        ["ins_admin_refused"] = "Setup needs administrator rights "
                              + "and cannot continue without them.",
    };
}
