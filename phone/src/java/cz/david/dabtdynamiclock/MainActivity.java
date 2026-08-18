package cz.david.dabtdynamiclock;

import android.app.Activity;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;

/**
 * The only screen of the app: status, on/off, broadcasting interval.
 *
 * The interface is built in code (no XML and no extra libraries), INCLUDING
 * the look of the buttons. System buttons are light and blend into the dark
 * background - unselected options would then be invisible.
 */
public class MainActivity extends Activity {

    private static final int BACKGROUND = Color.parseColor("#151920");
    private static final int CARD = Color.parseColor("#1c222c");
    private static final int TEXT = Color.parseColor("#e6ebf2");
    private static final int TEXT_GREY = Color.parseColor("#9aa4b2");
    private static final int GREEN = Color.parseColor("#22a050");
    private static final int GREEN_LIGHT = Color.parseColor("#4ade80");
    private static final int BORDER = Color.parseColor("#2f3743");
    private static final String PROJECT_URL =
            "https://github.com/DavidKral81/da-bt-dynamic-lock";

    private TextView statusText;
    private TextView mainButton;
    private final TextView[] options = new TextView[3];
    private final FlagView[] flags = new FlagView[2];

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable refresh = new Runnable() {
        @Override
        public void run() {
            showStatus();
            handler.postDelayed(this, 1000);
        }
    };

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        Texts.load(this);
        requestWindowFeature(Window.FEATURE_NO_TITLE);   // our own header
        getWindow().setStatusBarColor(BACKGROUND);
        getWindow().setNavigationBarColor(BACKGROUND);

        LinearLayout l = new LinearLayout(this);
        l.setOrientation(LinearLayout.VERTICAL);
        l.setPadding(dp(20), dp(28), dp(20), dp(28));
        l.setBackgroundColor(BACKGROUND);

        // --- language flags (top right) ---------------------------------
        // At the top because down at the end of the page they would need
        // scrolling to and would be easily missed.
        LinearLayout languageRow = new LinearLayout(this);
        languageRow.setOrientation(LinearLayout.HORIZONTAL);
        languageRow.setGravity(Gravity.END);
        final String[] codes = {Texts.CS, Texts.EN};
        for (int i = 0; i < 2; i++) {
            final String code = codes[i];
            FlagView v = new FlagView(this, i == 0);
            LinearLayout.LayoutParams lp =
                    new LinearLayout.LayoutParams(dp(24), dp(16));
            lp.leftMargin = dp(8);
            v.setLayoutParams(lp);
            v.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View w) {
                    if (code.equals(Texts.language())) {
                        return;
                    }
                    Prefs.setLanguage(MainActivity.this, code);
                    AdvertiserService.refreshNotification();
                    // the screen is built again, already in the new language -
                    // redrawing the individual labels would mean forgetting
                    // one of them
                    recreate();
                }
            });
            flags[i] = v;
            languageRow.addView(v);
        }
        l.addView(languageRow);

        // --- header with the logo ---------------------------------------
        ImageView logo = new ImageView(this);
        logo.setImageResource(R.mipmap.ic_launcher);
        LinearLayout.LayoutParams lpLogo =
                new LinearLayout.LayoutParams(dp(76), dp(76));
        lpLogo.gravity = Gravity.CENTER_HORIZONTAL;
        lpLogo.bottomMargin = dp(12);
        logo.setLayoutParams(lpLogo);
        l.addView(logo);

        TextView title = text("Da BT Dynamic Lock", 23, TEXT, 0, 0);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        title.setGravity(Gravity.CENTER);
        l.addView(title);

        TextView subtitle = text(Texts.t("subtitle"), 13, GREEN_LIGHT,
                                 dp(2), dp(18));
        subtitle.setGravity(Gravity.CENTER);
        l.addView(subtitle);

        l.addView(text(Texts.t("intro"), 13, TEXT_GREY, 0, dp(18)));

        // --- status + the main switch -----------------------------------
        LinearLayout card = card();
        statusText = text("", 17, TEXT, dp(4), dp(4));
        statusText.setGravity(Gravity.CENTER);
        statusText.setTypeface(Typeface.DEFAULT_BOLD);
        card.addView(statusText);

        mainButton = button("", true);
        mainButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                boolean turnOn = !Prefs.isEnabled(MainActivity.this);
                Prefs.setEnabled(MainActivity.this, turnOn);
                if (turnOn) {
                    requestNeededPermissions();
                    startForegroundService(new Intent(MainActivity.this,
                                                      AdvertiserService.class));
                } else {
                    stopService(new Intent(MainActivity.this,
                                           AdvertiserService.class));
                }
                showStatus();
            }
        });
        card.addView(mainButton);
        l.addView(card);

        // --- interval ---------------------------------------------------
        l.addView(text(Texts.t("interval_title"), 15, TEXT, dp(24), dp(2)));
        l.addView(text(Texts.t("interval_desc"), 12, TEXT_GREY, 0, dp(10)));

        LinearLayout card2 = card();
        String[] labels = {Texts.t("i_fast"), Texts.t("i_medium"),
                           Texts.t("i_saving")};
        for (int i = 0; i < 3; i++) {
            final int option = i;
            TextView t = button(labels[i], false);
            t.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    Prefs.setInterval(MainActivity.this, option);
                    markSelected();
                    if (Prefs.isEnabled(MainActivity.this)) {
                        // restart the service so the new setting takes effect
                        // right away
                        stopService(new Intent(MainActivity.this,
                                               AdvertiserService.class));
                        startForegroundService(new Intent(MainActivity.this,
                                                          AdvertiserService.class));
                    }
                }
            });
            options[i] = t;
            card2.addView(t);
        }
        l.addView(card2);

        l.addView(text(Texts.t("note_name"), 12, TEXT_GREY, dp(22), 0));
        l.addView(text(Texts.t("note_internet"), 12,
                Color.parseColor("#6b7684"), dp(10), 0));

        // a link to the project - so it is visible from the app where it comes
        // from and where the source is
        TextView link = text(Texts.t("link_project"), 12, GREEN_LIGHT,
                             dp(14), 0);
        link.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                startActivity(new Intent(Intent.ACTION_VIEW,
                        android.net.Uri.parse(PROJECT_URL)));
            }
        });
        l.addView(link);

        ScrollView s = new ScrollView(this);
        s.setBackgroundColor(BACKGROUND);
        s.addView(l);
        setContentView(s);

        markSelected();
        showStatus();
        requestNeededPermissions();
    }

    @Override
    protected void onResume() {
        super.onResume();
        ensureServiceRunning();
        handler.post(refresh);
    }

    /**
     * When broadcasting should be on but the service is not running, start it.
     *
     * This happens after an app update (the system stops it, but the stored
     * "enabled" stays) or when the system kills the service. Without this the
     * app showed "starting…" and nothing happened until the user turned
     * broadcasting off and on by hand.
     */
    private void ensureServiceRunning() {
        if (Prefs.isEnabled(this) && !AdvertiserService.running) {
            startForegroundService(new Intent(this, AdvertiserService.class));
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        handler.removeCallbacks(refresh);
    }

    // ------------------------------------------------------------- look

    private int dp(int v) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v,
                getResources().getDisplayMetrics());
    }

    private TextView text(String s, int size, int color, int top, int bottom) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(size);
        t.setTextColor(color);
        t.setPadding(0, top, 0, bottom);
        t.setLineSpacing(dp(2), 1f);
        return t;
    }

    private LinearLayout card() {
        LinearLayout k = new LinearLayout(this);
        k.setOrientation(LinearLayout.VERTICAL);
        k.setPadding(dp(14), dp(14), dp(14), dp(14));
        GradientDrawable g = new GradientDrawable();
        g.setColor(CARD);
        g.setCornerRadius(dp(14));
        k.setBackground(g);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = dp(4);
        k.setLayoutParams(lp);
        return k;
    }

    /** A hand-drawn button - the system one is light and blends into the dark
     *  background. */
    private TextView button(String label, boolean main) {
        TextView t = new TextView(this);
        t.setText(label);
        t.setTextSize(main ? 16 : 15);
        t.setGravity(Gravity.CENTER);
        t.setPadding(dp(12), dp(14), dp(12), dp(14));
        t.setClickable(true);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = dp(8);
        t.setLayoutParams(lp);
        colorise(t, false);
        return t;
    }

    private void colorise(TextView t, boolean selected) {
        GradientDrawable g = new GradientDrawable();
        g.setCornerRadius(dp(11));
        g.setColor(selected ? GREEN : Color.parseColor("#232b36"));
        g.setStroke(dp(1), selected ? GREEN : BORDER);
        t.setBackground(g);
        t.setTextColor(selected ? Color.WHITE : TEXT);
    }

    // ------------------------------------------------------------ status

    private void markSelected() {
        int a = Prefs.interval(this);
        for (int i = 0; i < options.length; i++) {
            colorise(options[i], i == a);
        }
        boolean en = Texts.EN.equals(Texts.language());
        if (flags[0] != null) {
            flags[0].setActive(!en);
            flags[1].setActive(en);
        }
    }

    private void showStatus() {
        boolean enabled = Prefs.isEnabled(this);
        String s = AdvertiserService.running ? AdvertiserService.stateText()
                : (enabled ? Texts.t("st_starting_up") : Texts.t("st_off"));
        boolean ok = AdvertiserService.running && AdvertiserService.broadcasting;
        statusText.setText(s);
        statusText.setTextColor(ok ? GREEN_LIGHT
                : (enabled ? Color.parseColor("#fbbf24") : TEXT_GREY));

        mainButton.setText(Texts.t(enabled ? "btn_stop" : "btn_start"));
        colorise(mainButton, !enabled);   // green = an invitation to turn on
    }

    /** Ask for the permissions Android requires at runtime since 12 and 13. */
    private void requestNeededPermissions() {
        List<String> missing = new ArrayList<>();
        if (Build.VERSION.SDK_INT >= 31
                && checkSelfPermission(android.Manifest.permission.BLUETOOTH_ADVERTISE)
                != PackageManager.PERMISSION_GRANTED) {
            missing.add(android.Manifest.permission.BLUETOOTH_ADVERTISE);
        }
        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED) {
            missing.add(android.Manifest.permission.POST_NOTIFICATIONS);
        }
        if (!missing.isEmpty()) {
            requestPermissions(missing.toArray(new String[0]), 1);
        }

        BluetoothManager bm =
                (BluetoothManager) getSystemService(Context.BLUETOOTH_SERVICE);
        BluetoothAdapter a = bm == null ? null : bm.getAdapter();
        if (a != null && !a.isEnabled()) {
            startActivity(new Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE));
        }
    }
}
