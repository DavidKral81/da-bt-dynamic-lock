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
        CZECH.put("subtitle", "vysílač pro notebook");
        ENGLISH.put("subtitle", "transmitter for the computer");

        CZECH.put("intro", "Vysílá bluetooth signál, podle kterého notebook pozná, "
                + "že jsi u stolu. Po restartu telefonu i po vypnutí režimu "
                + "letadla se spustí sám.");
        ENGLISH.put("intro", "Broadcasts a bluetooth signal that tells your computer "
                + "you are at the desk. It starts again by itself after the "
                + "phone restarts or aeroplane mode is turned off.");

        CZECH.put("btn_start", "Zapnout vysílání");
        ENGLISH.put("btn_start", "Turn broadcasting on");
        CZECH.put("btn_stop", "Vypnout vysílání");
        ENGLISH.put("btn_stop", "Turn broadcasting off");

        CZECH.put("interval_title", "Jak často vysílat");
        ENGLISH.put("interval_title", "How often to broadcast");
        CZECH.put("interval_desc", "Častěji = rychlejší reakce notebooku, ale "
                + "vyšší spotřeba baterie.");
        ENGLISH.put("interval_desc", "More often = the computer reacts faster, but "
                + "the battery drains quicker.");

        CZECH.put("i_fast", "Rychlý — 100 ms");
        ENGLISH.put("i_fast", "Fast — 100 ms");
        CZECH.put("i_medium", "Střední — 250 ms");
        ENGLISH.put("i_medium", "Medium — 250 ms");
        CZECH.put("i_saving", "Úsporný — 1 s");
        ENGLISH.put("i_saving", "Saving — 1 s");

        CZECH.put("link_project", "Projekt na GitHubu");
        ENGLISH.put("link_project", "Project on GitHub");

        CZECH.put("language_title", "Jazyk");
        ENGLISH.put("language_title", "Language");

        CZECH.put("note_name", "Vysílá se jméno telefonu z nastavení Bluetooth. "
                + "V notebooku se sledovaný telefon vybírá v nastavení "
                + "aplikace.");
        ENGLISH.put("note_name", "The phone's Bluetooth name is what gets "
                + "broadcast. On the computer you pick the watched phone in "
                + "the app settings.");

        CZECH.put("note_internet", "Aplikace nemá přístup k internetu — nemůže nic "
                + "nikam odeslat.");
        ENGLISH.put("note_internet", "The app has no internet access — it cannot "
                + "send anything anywhere.");

        // --- states ---
        CZECH.put("st_starting", "Vysílání se spouští…");
        ENGLISH.put("st_starting", "Broadcasting is starting…");
        CZECH.put("st_bt_off", "Bluetooth vypnutý — čekám");
        ENGLISH.put("st_bt_off", "Bluetooth is off — waiting");
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

    /** Keys present in one language only - in case one ever falls out. */
    public static String missing() {
        StringBuilder sb = new StringBuilder();
        for (String k : CZECH.keySet()) {
            if (!ENGLISH.containsKey(k)) {
                sb.append(k).append(" (missing en) ");
            }
        }
        for (String k : ENGLISH.keySet()) {
            if (!CZECH.containsKey(k)) {
                sb.append(k).append(" (missing cs) ");
            }
        }
        return sb.toString();
    }
}
