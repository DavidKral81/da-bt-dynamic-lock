"""All application texts in Czech and English.

Both languages sit side by side on purpose: a missing or drifted translation
is then obvious at a glance, and `missing()` turns that into a test.

Usage:
    from texts import t
    t("btn_quit")                  ->  "Ukončit aplikaci" / "Quit application"
    t("opt_seconds", s=60)          ->  "60 sekundách" / "60 seconds"

The language is set through `set_language("cs"|"en")` - never by assigning to
the variable directly, so there is exactly one place where it changes.

Key naming follows where the text appears: card_* / lbl_* / opt_* / sw_* in
the settings, chart_* and leg_* in the chart, tray_* in the notification area,
st_* for the icon tooltip, msg_* for message boxes.
"""

LANGUAGES = [("cs", "Čeština"), ("en", "English")]
_language = "cs"


def set_language(code):
    global _language
    _language = code if code in dict(LANGUAGES) else "cs"


def language():
    return _language


def t(key, **kw):
    """Text in the current language.

    A missing translation falls back to Czech, so the app never shows an
    empty string or a raw key to the user.
    """
    words = EN if _language == "en" else CS
    text = words.get(key) or CS.get(key) or key
    return text.format(**kw) if kw else text


CS = {
    # --- window -------------------------------------------------------
    "window_title": "{app} — síla signálu",
    "tab_chart": "Graf",
    "tab_settings": "Nastavení",

    # --- settings: which phone ----------------------------------------
    "card_phone": "Který telefon hlídat",
    "card_phone_desc": "Vyber zařízení, podle kterého se pozná, že jsi "
                       "u počítače. Seznam se plní tím, co je právě slyšet.",
    "dev_none_heard": "(zatím nic není slyšet — chvíli to trvá)",
    "dev_not_heard": "{name}   (není slyšet)",
    "dev_dbm": "{name}   ({rssi} dBm)",
    "dev_last_heard": "{name}   (naposledy před {s} s)",

    # --- settings: when to lock ---------------------------------------
    "card_when": "Kdy zamknout",
    "card_when_desc": "Notebook se zamkne, když telefon není slyšet "
                   "nastavenou dobu.",
    "sw_active": "Automatické zamykání aktivní",
    "lbl_lock_after": "Zamknout po",
    "opt_seconds": "{s} sekundách",
    "lbl_range": "Dosah (citlivost)",
    "opt_range_max": "Co nejdál — dokud je signál slyšet",
    "sw_countdown": "Před zamknutím ukázat odpočet",
    "opt_countdown_from": "{s} sekund předem",
    "opt_countdown_off": "Nezobrazovat",
    "sw_idle_guard": "Nezamykat při aktivitě uživatele (myš, klávesnice)",

    # --- settings: app behaviour --------------------------------------
    "card_behaviour": "Chování aplikace",
    "sw_autostart": "Spouštět po přihlášení do Windows",
    "lbl_language": "Jazyk",

    # --- settings: phone disappears -----------------------------------
    "card_gone": "Když telefon zmizí",
    "card_gone_desc": "Když telefon přestane vysílat (vybije se, vypne "
                     "bluetooth), notebook se jednou zamkne a pak už "
                     "nehlídá — a nepozná se to. Tohle na takový stav "
                     "upozorní.",
    "lbl_warn": "Upozornit, že hlídání přestalo fungovat",
    "opt_no_warning": "Neupozorňovat",
    "opt_after_minutes": "po {m} minutách bez signálu",

    # --- settings: pause ----------------------------------------------
    "card_pause": "Dočasně pozastavit",
    "card_pause_desc": "Hlídání se na zvolenou dobu vypne a pak se samo "
                     "zase zapne.",
    "opt_not_paused": "Nepozastaveno",
    "opt_5_min": "5 minut",
    "opt_15_min": "15 minut",
    "opt_30_min": "30 minut",
    "opt_1_hour": "1 hodinu",
    "opt_2_hours": "2 hodiny",
    "opt_4_hours": "4 hodiny",
    "opt_12_hours": "12 hodin",
    "opt_1_day": "1 den",
    "opt_2_days": "2 dny",

    # --- links and buttons --------------------------------------------
    "link_phone_app": "Stáhnout aplikaci do telefonu",
    "link_manual": "Návod",
    "link_project": "Projekt na GitHubu",
    "btn_quit": "Ukončit aplikaci",

    # --- chart --------------------------------------------------------
    "chart_show": "Zobrazit:",
    "range_2min": "2 min", "range_5min": "5 min", "range_15min": "15 min",
    "range_1h": "1 h", "range_8h": "8 h", "range_1day": "1 den",
    "leg_signal": "signál (výš = lepší)",
    "leg_weak": "slabý — pod prahem, nepočítá se",
    "leg_gap": "výpadek nad 5 s",
    "leg_gap_lock": "výpadek na uzamknutí",
    "leg_downtime": "apka neběžela",
    "leg_threshold": "práh citlivosti",
    "leg_locked": "uzamknuto",
    "chart_now": "teď",
    "chart_threshold": "práh {v} dBm",
    "chart_lock": "zámek",
    "chart_downtime": "apka neběžela",
    "chart_summary": "{count} signálů  ·  {per_min}/min  ·  medián {median} dBm  "
                "·  nejdelší ticho {silence} s",
    "chart_no_signal": "žádný signál v tomto úseku",

    # --- countdown ----------------------------------------------------
    "countdown_text": "Uzamknutí za {s} s",

    # --- notification area --------------------------------------------
    "tray_chart": "Graf signálu",
    "tray_phone": "Sledovaný telefon …",
    "tray_time": "Uzamknout po … bez signálu",
    "tray_countdown": "Odpočet se ukáže …",
    "tray_sensitivity": "Citlivost (dosah)",
    "tray_alerts": "Upozornit, když telefon zmizí …",
    "tray_pause": "Pozastavit hlídání …",
    "tray_clear_pause": "Zrušit pozastavení",
    "tray_quit": "Ukončit",
    "tray_seconds": "{s} s",
    "tray_seconds_ahead": "{s} s předem",
    "tray_minutes": "{m} min",
    "tray_threshold_off": "Vypnuto (jen ztráta signálu)",
    "tray_threshold_max": "{v} dBm — největší dosah",
    "tray_threshold_min": "{v} dBm — nejmenší dosah",
    "tray_threshold_edge": "{v} dBm — na hranici (v kapse bývá −81)",
    "tray_threshold_plain": "{v} dBm",
    "tray_none_heard": "(zatím nic není slyšet)",

    # --- states (icon tooltip) ----------------------------------------
    "st_waiting": "čekám na první signál z telefonu",
    "st_locked_wait": "zamčeno — čekám na návrat telefonu",
    "st_guard": "pojistka: pracuje se, nezamykám",
    "st_locking": "zamykám",
    "st_guard_countdown": "pojistka: pracuje se, odpočet nezobrazuji",
    "st_countdown": "uzamknutí za {s} s",
    "st_at_desk": "telefon u stolu",
    "st_off": "vypnuto",
    "st_paused": "pozastaveno ({minutes} min)",
    "st_locked": "zamčeno",

    # --- messages -----------------------------------------------------
    "msg_no_signal": "Telefon se neozval {minutes} minut — notebook se "
                      "nezamyká.\nZkontroluj, jestli v telefonu běží "
                      "aplikace Da BT Dynamic Lock.",
    "msg_already_running": "Da BT Dynamic Lock už běží.\n\nNajdeš ho jako ikonu "
               "v oznamovací oblasti vpravo dole u hodin (případně pod "
               "šipkou skrytých ikon).",

    # --- installer and uninstaller (installer/installer.py) -------------
    # One program serves both roles, so ins_* is shared and uni_* is only
    # for what the uninstaller says on its own.
    "ins_title_install": "Instalace {app}",
    "ins_title_uninstall": "Odinstalace {app}",
    "ins_subtitle": "Zamkne notebook, když se od něj vzdálíš s telefonem.",
    "ins_lbl_language": "Jazyk / Language",
    "ins_to_folder": "Program {app} se nainstaluje do složky:",
    "ins_from_folder": "Program {app} se odinstaluje ze složky:",

    # options
    "ins_opt_startmenu": "Přidat zástupce do nabídky Start",
    "ins_opt_desktop": "Přidat zástupce na plochu",
    "ins_opt_autostart": "Spouštět automaticky při přihlášení",
    "uni_opt_data": "Smazat i nastavení a historii",

    # buttons
    "ins_btn_install": "Instalovat",
    "ins_btn_uninstall": "Odinstalovat",
    "ins_btn_close": "Zavřít",
    "ins_btn_finish": "Dokončit",

    # progress
    "ins_stopping": "Ukončuji běžící aplikaci…",
    "ins_copying": "Kopíruji program…",
    "ins_startmenu": "Vytvářím zástupce v nabídce Start…",
    "ins_desktop": "Vytvářím zástupce na ploše…",
    "ins_registry": "Zapisuji do seznamu aplikací…",
    "ins_task_on": "Nastavuji spouštění při přihlášení…",
    "ins_task_off": "Ruším spouštění při přihlášení…",
    "ins_task_failed": "Spouštění při přihlášení se nepodařilo nastavit.",
    "ins_checking": "Kontroluji výsledek…",
    "ins_done": "Hotovo.",
    "uni_shortcuts": "Odstraňuji zástupce…",
    "uni_registry": "Mažu záznam ze seznamu aplikací…",
    "uni_data": "Mažu nastavení a historii…",
    "uni_files": "Odstraňuji soubory programu…",

    # what did not work - the installer never reports success while
    # something is missing
    "ins_failed": "Nepodařilo se: {what}",
    "uni_partial": "Hotovo, ale nešlo smazat: {what}",
    "ins_error": "Chyba: {error}",
    "ins_err_copy": "kopírování programu selhalo",
    "ins_prob_startmenu": "zástupce do nabídky Start",
    "ins_prob_desktop": "zástupce na plochu",
    "ins_prob_program": "program se nezkopíroval",
    "ins_prob_uninstaller": "odinstalátor",
    "ins_prob_registry": "záznam v Nastavení → Aplikace",
    "ins_prob_task": "spouštění při přihlášení",
    "uni_prob_files": "soubory programu",

    # result window
    "ins_head_installed": "Nainstalováno",
    "ins_head_uninstalled": "Odinstalováno",
    "ins_ok_desc": "Aby hlídání fungovalo, musí vysílat i telefon — "
                   "nainstaluj do něj aplikaci pro Android. Odkaz na "
                   "stažení je v okně programu, postup v návodu.",
    "uni_ok_desc": "{app} byl odstraněn z počítače.",
    "uni_partial_desc": "{app} byl odstraněn, ale některé soubory se "
                        "nepodařilo smazat. Zkus to po restartu počítače.",
    "ins_opt_launch": "Spustit {app}",
    "ins_opt_phone": "Otevřít stránku se stažením aplikace do telefonu",
    "ins_opt_manual": "Otevřít návod",

    # message boxes
    "ins_msg_admin": "Spusť tento program jako správce.\n\n"
                     "Zápis do složky Program Files to vyžaduje.",
    "ins_msg_running": "Aplikace se nespustila, protože už běží jiná "
                       "kopie.\n\nUkonči ji (ikona v liště → Konec) "
                       "a spusť tuto.",
}

EN = {
    # --- window -------------------------------------------------------
    "window_title": "{app} — signal strength",
    "tab_chart": "Chart",
    "tab_settings": "Settings",

    # --- settings: phone ----------------------------------------------
    "card_phone": "Which phone to watch",
    "card_phone_desc": "Pick the device that tells the computer you are "
                       "nearby. The list fills up with whatever is heard.",
    "dev_none_heard": "(nothing heard yet — give it a moment)",
    "dev_not_heard": "{name}   (not heard)",
    "dev_dbm": "{name}   ({rssi} dBm)",
    "dev_last_heard": "{name}   (last heard {s} s ago)",

    # --- settings: when to lock ---------------------------------------
    "card_when": "When to lock",
    "card_when_desc": "The computer locks when the phone has not been heard "
                   "for the time set below.",
    "sw_active": "Automatic locking on",
    "lbl_lock_after": "Lock after",
    "opt_seconds": "{s} seconds",
    "lbl_range": "Range (sensitivity)",
    "opt_range_max": "As far as possible — while the signal is heard",
    "sw_countdown": "Show a countdown before locking",
    "opt_countdown_from": "{s} seconds ahead",
    "opt_countdown_off": "Do not show",
    "sw_idle_guard": "Do not lock while the user is active (mouse, keyboard)",

    # --- settings: application behaviour ------------------------------
    "card_behaviour": "Application behaviour",
    "sw_autostart": "Start after signing in to Windows",
    "lbl_language": "Language",

    # --- settings: when the phone disappears --------------------------
    "card_gone": "When the phone disappears",
    "card_gone_desc": "If the phone stops broadcasting (flat battery, "
                     "bluetooth turned off), the computer locks once and "
                     "then stops watching — with nothing to show for it. "
                     "This warns you when that happens.",
    "lbl_warn": "Warn me that watching has stopped working",
    "opt_no_warning": "Do not warn",
    "opt_after_minutes": "after {m} minutes without a signal",

    # --- settings: pause ----------------------------------------------
    "card_pause": "Pause for a while",
    "card_pause_desc": "Watching turns off for the chosen time and then "
                     "turns itself back on.",
    "opt_not_paused": "Not paused",
    "opt_5_min": "5 minutes",
    "opt_15_min": "15 minutes",
    "opt_30_min": "30 minutes",
    "opt_1_hour": "1 hour",
    "opt_2_hours": "2 hours",
    "opt_4_hours": "4 hours",
    "opt_12_hours": "12 hours",
    "opt_1_day": "1 day",
    "opt_2_days": "2 days",

    # --- links and buttons --------------------------------------------
    "link_phone_app": "Download the phone app",
    "link_manual": "Manual",
    "link_project": "Project on GitHub",
    "btn_quit": "Quit application",

    # --- chart --------------------------------------------------------
    "chart_show": "Show:",
    "range_2min": "2 min", "range_5min": "5 min", "range_15min": "15 min",
    "range_1h": "1 h", "range_8h": "8 h", "range_1day": "1 day",
    "leg_signal": "signal (higher = better)",
    "leg_weak": "weak — below the threshold, not counted",
    "leg_gap": "gap over 5 s",
    "leg_gap_lock": "gap long enough to lock",
    "leg_downtime": "app was not running",
    "leg_threshold": "sensitivity threshold",
    "leg_locked": "locked",
    "chart_now": "now",
    "chart_threshold": "threshold {v} dBm",
    "chart_lock": "lock",
    "chart_downtime": "app was not running",
    "chart_summary": "{count} signals  ·  {per_min}/min  ·  median {median} dBm  "
                "·  longest silence {silence} s",
    "chart_no_signal": "no signal in this range",

    # --- countdown ----------------------------------------------------
    "countdown_text": "Locking in {s} s",

    # --- notification area --------------------------------------------
    "tray_chart": "Signal chart",
    "tray_phone": "Watched phone …",
    "tray_time": "Lock after … without a signal",
    "tray_countdown": "Countdown shows …",
    "tray_sensitivity": "Sensitivity (range)",
    "tray_alerts": "Warn when the phone disappears …",
    "tray_pause": "Pause watching …",
    "tray_clear_pause": "Cancel the pause",
    "tray_quit": "Quit",
    "tray_seconds": "{s} s",
    "tray_seconds_ahead": "{s} s ahead",
    "tray_minutes": "{m} min",
    "tray_threshold_off": "Off (signal loss only)",
    "tray_threshold_max": "{v} dBm — longest range",
    "tray_threshold_min": "{v} dBm — shortest range",
    "tray_threshold_edge": "{v} dBm — borderline (a pocket is about −81)",
    "tray_threshold_plain": "{v} dBm",
    "tray_none_heard": "(nothing heard yet)",

    # --- states (icon tooltip) ----------------------------------------
    "st_waiting": "waiting for the first signal from the phone",
    "st_locked_wait": "locked — waiting for the phone to come back",
    "st_guard": "guard: you are working, not locking",
    "st_locking": "locking",
    "st_guard_countdown": "guard: you are working, no countdown",
    "st_countdown": "locking in {s} s",
    "st_at_desk": "phone at the desk",
    "st_off": "off",
    "st_paused": "paused ({minutes} min)",
    "st_locked": "locked",

    # --- messages -----------------------------------------------------
    "msg_no_signal": "The phone has not been heard for {minutes} minutes — "
                      "the computer is not locking.\nCheck that the Da BT "
                      "Dynamic Lock app is running on the phone.",
    "msg_already_running": "Da BT Dynamic Lock is already running.\n\nLook for its "
               "icon in the notification area next to the clock (it may "
               "be hidden under the arrow).",

    # --- installer and uninstaller (installer/installer.py) -------------
    # One program serves both roles, so ins_* is shared and uni_* is only
    # for what the uninstaller says on its own.
    "ins_title_install": "{app} setup",
    "ins_title_uninstall": "Uninstall {app}",
    "ins_subtitle": "Locks the laptop when you walk away with your phone.",
    "ins_lbl_language": "Jazyk / Language",
    "ins_to_folder": "{app} will be installed into:",
    "ins_from_folder": "{app} will be removed from:",

    # options
    "ins_opt_startmenu": "Add a shortcut to the Start menu",
    "ins_opt_desktop": "Add a shortcut to the desktop",
    "ins_opt_autostart": "Start automatically on sign-in",
    "uni_opt_data": "Delete the settings and history as well",

    # buttons
    "ins_btn_install": "Install",
    "ins_btn_uninstall": "Uninstall",
    "ins_btn_close": "Close",
    "ins_btn_finish": "Finish",

    # progress
    "ins_stopping": "Stopping the running app…",
    "ins_copying": "Copying the program…",
    "ins_startmenu": "Creating the Start menu shortcut…",
    "ins_desktop": "Creating the desktop shortcut…",
    "ins_registry": "Registering in the list of apps…",
    "ins_task_on": "Setting up the start on sign-in…",
    "ins_task_off": "Removing the start on sign-in…",
    "ins_task_failed": "The start on sign-in could not be set up.",
    "ins_checking": "Checking the result…",
    "ins_done": "Done.",
    "uni_shortcuts": "Removing the shortcuts…",
    "uni_registry": "Removing the entry from the list of apps…",
    "uni_data": "Deleting the settings and history…",
    "uni_files": "Removing the program files…",

    # what did not work - the installer never reports success while
    # something is missing
    "ins_failed": "Could not do: {what}",
    "uni_partial": "Done, but these could not be deleted: {what}",
    "ins_error": "Error: {error}",
    "ins_err_copy": "copying the program failed",
    "ins_prob_startmenu": "the Start menu shortcut",
    "ins_prob_desktop": "the desktop shortcut",
    "ins_prob_program": "the program did not get copied",
    "ins_prob_uninstaller": "the uninstaller",
    "ins_prob_registry": "the entry in Settings → Apps",
    "ins_prob_task": "the start on sign-in",
    "uni_prob_files": "the program files",

    # result window
    "ins_head_installed": "Installed",
    "ins_head_uninstalled": "Uninstalled",
    "ins_ok_desc": "For the watching to work, the phone has to broadcast "
                   "too — install the companion app on it. The manual "
                   "explains how.",
    "uni_ok_desc": "{app} has been removed from the computer.",
    "uni_partial_desc": "{app} has been removed, but some files could not "
                        "be deleted. Try again after restarting the "
                        "computer.",
    "ins_opt_launch": "Run {app}",
    "ins_opt_phone": "Open the download page for the phone app",
    "ins_opt_manual": "Open the manual",

    # message boxes
    "ins_msg_admin": "Run this program as an administrator.\n\n"
                     "Writing into Program Files requires it.",
    "ins_msg_running": "The app did not start because another copy is "
                       "already running.\n\nQuit it (tray icon → Quit) "
                       "and start this one.",
}


def missing():
    """Keys present in one language only - a test fails on anything here."""
    return sorted(set(CS) ^ set(EN))
