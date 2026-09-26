package cz.david.dabtdynamiclock;

import android.content.Context;

import java.util.HashMap;
import java.util.Map;

/**
 * All application texts in Czech and English.
 *
 * The same principle as texts.py in the Windows app: both languages side by
 * side in one file, so a missing translation is obvious at a glance.
 * res/values-en is not used, because the app is built without Gradle and the
 * language is switched inside the app, not by the phone settings.
 */
public final class Texts {

    public static final String CS = "cs";
    public static final String EN = "en";

    private static final Map<String, String> CZECH = new HashMap<>();
    private static final Map<String, String> ENGLISH = new HashMap<>();
    private static String language = CS;

    private Texts() {
    }

    /** Load the language from the settings; called when the activity and the
     *  service start. */
    public static void load(Context c) {
        language = Prefs.language(c);
    }

    public static void set(String code) {
        language = EN.equals(code) ? EN : CS;
    }

    public static String language() {
        return language;
    }

    /** Text in the current language; a missing translation falls back to
     *  Czech. */
    public static String t(String key) {
        String s = EN.equals(language) ? ENGLISH.get(key) : CZECH.get(key);
        if (s == null) {
            s = CZECH.get(key);
        }
        return s == null ? key : s;
    }

    public static String t(String key, Object... values) {
        return String.format(t(key), values);
    }

    static {
        // --- main screen ---
        // "Computer", never "notebook": the Windows app runs on a desktop
        // just as well. Impersonal wording, like every label in the project.
        CZECH.put("subtitle", "vysílač pro počítač");
        ENGLISH.put("subtitle", "transmitter for the computer");

        CZECH.put("intro", "Vysílá signál Bluetooth, podle kterého počítač pozná, "
                + "že je telefon nablízku. Po restartu telefonu i po vypnutí "
                + "režimu letadla se spustí sám.");
        ENGLISH.put("intro", "Broadcasts a Bluetooth signal that tells the computer "
                + "the phone is nearby. It starts again by itself after the "
                + "phone restarts or aeroplane mode is turned off.");

        CZECH.put("btn_start", "Zapnout vysílání");
        ENGLISH.put("btn_start", "Turn broadcasting on");
        CZECH.put("btn_stop", "Vypnout vysílání");
        ENGLISH.put("btn_stop", "Turn broadcasting off");

        // "Interval", not "frequency": the choices are the time between two
        // signals (100 ms, 250 ms, 1 s), and a frequency would be given in Hz.
        // It is also the term Bluetooth itself uses (advertising interval).
        CZECH.put("interval_title", "Interval vysílání");
        ENGLISH.put("interval_title", "Broadcast interval");
        // The battery: measured in everyday use, the difference between the
        // three is negligible - the manual says so, and this line used to
        // claim the opposite.
        CZECH.put("interval_desc", "Kratší interval znamená rychlejší reakci "
                + "počítače. Rozdíl ve spotřebě baterie je zanedbatelný.");
        ENGLISH.put("interval_desc", "A shorter interval lets the computer react "
                + "faster. The difference in battery use is negligible.");

        CZECH.put("i_fast", "Rychlý — 100 ms");
        ENGLISH.put("i_fast", "Fast — 100 ms");
        CZECH.put("i_medium", "Střední — 250 ms");
        ENGLISH.put("i_medium", "Medium — 250 ms");
        CZECH.put("i_saving", "Úsporný — 1 s");
        ENGLISH.put("i_saving", "Saving — 1 s");

        CZECH.put("link_project", "Projekt na GitHubu");
        ENGLISH.put("link_project", "Project on GitHub");

        CZECH.put("version", "Verze %s");
        ENGLISH.put("version", "Version %s");
        CZECH.put("link_update", "Stáhnout nejnovější verzi");
        ENGLISH.put("link_update", "Download the newest version");

        CZECH.put("language_title", "Jazyk");
        ENGLISH.put("language_title", "Language");

        CZECH.put("note_name", "Vysílá se jméno telefonu z nastavení Bluetooth. "
                + "Hlídaný telefon se vybírá v aplikaci v počítači.");
        ENGLISH.put("note_name", "The phone's Bluetooth name is what gets "
                + "broadcast. The watched phone is chosen in the app on the "
                + "computer.");

        CZECH.put("note_internet", "Aplikace nemá přístup k internetu — nemůže nic "
                + "nikam odeslat.");
        ENGLISH.put("note_internet", "The app has no internet access — it cannot "
                + "send anything anywhere.");

        // --- states ---
        CZECH.put("st_starting", "Vysílání se spouští…");
        ENGLISH.put("st_starting", "Broadcasting is starting…");
        CZECH.put("st_bt_off", "Bluetooth je vypnutý — čeká se na zapnutí");
        ENGLISH.put("st_bt_off", "Bluetooth is off — waiting for it");
        CZECH.put("st_no_permission", "Chybí oprávnění „Zařízení v okolí“");
        ENGLISH.put("st_no_permission", "The \"Nearby devices\" permission is missing");
        CZECH.put("st_unsupported", "Telefon neumí BLE vysílání");
        ENGLISH.put("st_unsupported", "This phone cannot broadcast BLE");
        CZECH.put("st_broadcasting", "Vysílá — %s");
        ENGLISH.put("st_broadcasting", "Broadcasting — %s");
        CZECH.put("st_broadcasting_response",
                "Vysílá (jméno v odpovědi — méně přesné měření)");
        ENGLISH.put("st_broadcasting_response",
                "Broadcasting (name in the response — less accurate measurement)");
        CZECH.put("st_error", "Nepodařilo se spustit (kód %s)");
        ENGLISH.put("st_error", "Could not start (code %s)");
        CZECH.put("st_not_started", "nespuštěno");
        ENGLISH.put("st_not_started", "not started");
        CZECH.put("st_off", "vypnuto");
        ENGLISH.put("st_off", "off");
        CZECH.put("st_starting_up", "spouští se…");
        ENGLISH.put("st_starting_up", "starting…");

        // --- other ---
        CZECH.put("channel", "Vysílání");
        ENGLISH.put("channel", "Broadcasting");
        CZECH.put("unknown", "(nezjištěno)");
        ENGLISH.put("unknown", "(unknown)");
        CZECH.put("int_fast", "rychlý (100 ms)");
        ENGLISH.put("int_fast", "fast (100 ms)");
        CZECH.put("int_medium", "střední (250 ms)");
        ENGLISH.put("int_medium", "medium (250 ms)");
        CZECH.put("int_saving", "úsporný (1 s)");
        ENGLISH.put("int_saving", "saving (1 s)");
    }

    // There used to be a missing() here, listing keys present in one language
    // only. Nothing ever called it, so it hid the gap instead of closing it:
    // the dictionary looked guarded while a key falling out of ENGLISH would
    // still show up as a Czech sentence in the English interface. The check
    // now lives in tests/test_logic.py, which reads the keys straight out of
    // this file and DOES run before every release.
}
