package cz.david.dabtdynamiclock;

import android.bluetooth.le.AdvertiseSettings;
import android.content.Context;
import android.content.SharedPreferences;
import android.provider.Settings;

/**
 * Persistent application settings - they survive a phone restart.
 *
 * Named Prefs, not Settings: android.provider.Settings is used right below
 * and two classes of the same name in one file would be a trap.
 */
public final class Prefs {

    // The stored keys are English as well. Nothing has to be migrated: the
    // new signing key means the app gets installed fresh anyway.
    private static final String FILE = "ddl";
    private static final String KEY_ENABLED = "enabled";
    private static final String KEY_INTERVAL = "interval";
    private static final String KEY_LANGUAGE = "language";

    public static final int FAST = 0;       // 100 ms
    public static final int MEDIUM = 1;     // 250 ms
    public static final int SAVING = 2;     // 1 s

    private Prefs() {
    }

    private static SharedPreferences p(Context c) {
        return c.getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    public static boolean isEnabled(Context c) {
        return p(c).getBoolean(KEY_ENABLED, false);
    }

    public static void setEnabled(Context c, boolean state) {
        p(c).edit().putBoolean(KEY_ENABLED, state).apply();
    }

    public static int interval(Context c) {
        return p(c).getInt(KEY_INTERVAL, FAST);
    }

    public static void setInterval(Context c, int option) {
        p(c).edit().putInt(KEY_INTERVAL, option).apply();
    }

    /**
     * Interface language. Until the user picks one, it follows the phone:
     * Czech and Slovak -> the Czech version, anything else -> English.
     * (A Slovak speaker understands Czech better than English, hence the
     * Czech version.)
     */
    public static String language(Context c) {
        String saved = p(c).getString(KEY_LANGUAGE, null);
        if (saved != null) {
            return saved;
        }
        String phone = java.util.Locale.getDefault().getLanguage();
        return ("cs".equals(phone) || "sk".equals(phone)) ? Texts.CS : Texts.EN;
    }

    public static void setLanguage(Context c, String code) {
        p(c).edit().putString(KEY_LANGUAGE, code).apply();
        Texts.set(code);
    }

    /** Convert the option into the advertising mode Android expects. */
    public static int mode(int option) {
        switch (option) {
            case SAVING:
                return AdvertiseSettings.ADVERTISE_MODE_LOW_POWER;      // ~1 s
            case MEDIUM:
                return AdvertiseSettings.ADVERTISE_MODE_BALANCED;       // ~250 ms
            default:
                return AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY;    // ~100 ms
        }
    }

    /**
     * A translation key, not finished text.
     *
     * When finished text is stored (in the service state, say), it stays in
     * the original language after a language switch - everything around it
     * gets translated, but it does not.
     */
    public static String intervalKey(int option) {
        switch (option) {
            case SAVING:
                return "int_saving";
            case MEDIUM:
                return "int_medium";
            default:
                return "int_fast";
        }
    }

    /**
     * The name the phone broadcasts.
     *
     * Read from the system settings, not through BluetoothAdapter.getName() -
     * since Android 12 that method would need the BLUETOOTH_CONNECT
     * permission on top, and that is not worth it for one label.
     */
    public static String broadcastName(Context c) {
        try {
            String s = Settings.Global.getString(c.getContentResolver(),
                                                 "bluetooth_name");
            if (s == null || s.isEmpty()) {
                s = Settings.Secure.getString(c.getContentResolver(),
                                              "bluetooth_name");
            }
            return (s == null || s.isEmpty()) ? Texts.t("unknown") : s;
        } catch (Exception e) {
            return Texts.t("unknown");
        }
    }
}
