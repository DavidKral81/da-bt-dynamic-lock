package cz.david.dabtdynamiclock;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothManager;
import android.bluetooth.le.AdvertiseCallback;
import android.bluetooth.le.AdvertiseData;
import android.bluetooth.le.AdvertiseSettings;
import android.bluetooth.le.BluetoothLeAdvertiser;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;

/**
 * The service that keeps BLE broadcasting alive.
 *
 * Why a foreground service: only that way does Android guarantee it will not
 * be killed when the system goes to sleep. The permanent notification is the
 * price the system demands for it.
 *
 * The key advantage over nRF Connect: it watches bluetooth being turned on
 * (ACTION_STATE_CHANGED) and as soon as the radio comes back - after
 * aeroplane mode, after a phone restart - it brings broadcasting back by
 * itself.
 */
public class AdvertiserService extends Service {

    private static final String CHANNEL = "broadcasting";
    private static final int NOTIFICATION_ID = 1;

    public static volatile boolean running = false;
    /** Really broadcasting. Do not read this from the status text - that gets
     *  translated. */
    public static volatile boolean broadcasting = false;
    /** The state as a translation key + parameter - NOT finished text.
     *  Finished text would stay in the original language after a switch. */
    public static volatile String stateKey = "st_not_started";
    public static volatile String stateParam = null;
    /** True when stateParam is a translation KEY, not finished text. */
    public static volatile boolean stateParamIsKey = false;
    private static AdvertiserService instance;

    /** The state translated into the currently selected language. */
    public static String stateText() {
        if (stateParam == null) {
            return Texts.t(stateKey);
        }
        return Texts.t(stateKey,
                       stateParamIsKey ? Texts.t(stateParam) : stateParam);
    }

    /** Redraw the notification after a language change (if the service runs). */
    public static void refreshNotification() {
        if (instance != null) {
            // the channel name is stored text too - otherwise it would stay in
            // the original language in the phone settings (same channel, new
            // name = overwrite)
            instance.createChannel();
            instance.showState();
        }
    }

    private BluetoothLeAdvertiser advertiser;
    private AdvertiseCallback callback;
    private BroadcastReceiver btReceiver;
    private final Handler handler = new Handler(Looper.getMainLooper());
    // deliberately without a lambda - Android has no full support and we do
    // not want to deal with desugaring over two lines
    private final Runnable restart = new Runnable() {
        @Override
        public void run() {
            startBroadcasting();
        }
    };

    @Override
    public void onCreate() {
        super.onCreate();
        Texts.load(this);       // the service runs even with the app closed
        instance = this;
        createChannel();
        startInForeground(Texts.t("st_starting"));

        btReceiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context c, Intent i) {
                int s = i.getIntExtra(BluetoothAdapter.EXTRA_STATE, -1);
                if (s == BluetoothAdapter.STATE_ON) {
                    // bluetooth came back (aeroplane mode, restart) - bring
                    // broadcasting back up
                    handler.postDelayed(restart, 1500);
                } else if (s == BluetoothAdapter.STATE_OFF) {
                    advertiser = null;
                    callback = null;
                    broadcasting = false;
                    setState("st_bt_off");
                }
            }
        };
        IntentFilter f = new IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED);
        if (Build.VERSION.SDK_INT >= 34) {
            registerReceiver(btReceiver, f, Context.RECEIVER_EXPORTED);
        } else {
            registerReceiver(btReceiver, f);
        }

        startBroadcasting();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        running = true;
        return START_STICKY;    // should the system kill us anyway, bring us back
    }

    @Override
    public void onDestroy() {
        running = false;
        broadcasting = false;
        instance = null;
        stopBroadcasting();
        try {
            unregisterReceiver(btReceiver);
        } catch (IllegalArgumentException ignored) {
        }
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    // -------------------------------------------------------- broadcasting

    private boolean hasPermission() {
        if (Build.VERSION.SDK_INT < 31) return true;
        return checkSelfPermission(android.Manifest.permission.BLUETOOTH_ADVERTISE)
                == PackageManager.PERMISSION_GRANTED;
    }

    private void startBroadcasting() {
        stopBroadcasting();

        if (!hasPermission()) {
            broadcasting = false;
            setState("st_no_permission");
            return;
        }
        BluetoothManager bm =
                (BluetoothManager) getSystemService(Context.BLUETOOTH_SERVICE);
        BluetoothAdapter adapter = bm == null ? null : bm.getAdapter();
        if (adapter == null || !adapter.isEnabled()) {
            broadcasting = false;
            setState("st_bt_off");
            return;
        }
        advertiser = adapter.getBluetoothLeAdvertiser();
        if (advertiser == null) {
            broadcasting = false;
            setState("st_unsupported");
            return;
        }

        // 100 ms between advertisements and the highest power - measured as
        // the minimum needed for the laptop to hear a signal from a pocket
        // reliably.
        //
        // A NON-CONNECTABLE beacon (setConnectable false) is deliberate. The
        // first version was connectable and sent the name in the scan
        // response as well - the laptop then got two packets instead of one
        // (427 samples/min instead of 205) and the signal strength readings
        // were twice as jittery (8.5 dB instead of 5.2) with dips down to
        // -110 dBm, because strength is measured unreliably on a scan
        // response.
        int option = Prefs.interval(this);
        AdvertiseSettings settings = new AdvertiseSettings.Builder()
                .setAdvertiseMode(Prefs.mode(option))
                .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
                .setConnectable(false)
                .setTimeout(0)                 // 0 = no time limit
                .build();

        // The phone name in the advertisement - the laptop finds us by it.
        // It fits: 3 B flags + 2 B header + the name (the limit is 31 B).
        AdvertiseData data = new AdvertiseData.Builder()
                .setIncludeDeviceName(true)
                .build();

        callback = new AdvertiseCallback() {
            @Override
            public void onStartSuccess(AdvertiseSettings s) {
                broadcasting = true;
                setState("st_broadcasting", Prefs.intervalKey(option), true);
            }

            @Override
            public void onStartFailure(int error) {
                if (error == ADVERTISE_FAILED_DATA_TOO_LARGE) {
                    tryNameInResponse();
                    return;
                }
                broadcasting = false;
                setState("st_error", String.valueOf(error));
                handler.postDelayed(restart, 30000);   // try again
            }
        };
        try {
            // three parameters = no scan response (see the comment above)
            advertiser.startAdvertising(settings, data, callback);
        } catch (SecurityException e) {
            broadcasting = false;
            setState("st_no_permission");
        }
    }

    /**
     * A fallback in case the name would not fit into the advertisement (the
     * 31 B limit). Only here do we reach for the scan response - because of
     * the jittery signal strength readings it is the second choice, not the
     * default.
     */
    private void tryNameInResponse() {
        try {
            AdvertiseSettings s = new AdvertiseSettings.Builder()
                    .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
                    .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
                    .setConnectable(true).setTimeout(0).build();
            advertiser.startAdvertising(s,
                    new AdvertiseData.Builder().build(),
                    new AdvertiseData.Builder().setIncludeDeviceName(true).build(),
                    callback);
            broadcasting = true;
            setState("st_broadcasting_response");
        } catch (SecurityException e) {
            broadcasting = false;
            setState("st_no_permission");
        }
    }

    private void stopBroadcasting() {
        if (advertiser != null && callback != null) {
            try {
                advertiser.stopAdvertising(callback);
            } catch (SecurityException ignored) {
            }
        }
        callback = null;
    }

    // ------------------------------------------------------- notification

    private void createChannel() {
        NotificationManager nm = getSystemService(NotificationManager.class);
        NotificationChannel k = new NotificationChannel(CHANNEL,
                Texts.t("channel"), NotificationManager.IMPORTANCE_LOW);
        k.setShowBadge(false);                       // LOW = no sound
        nm.createNotificationChannel(k);
    }

    private Notification notification(String text) {
        Intent i = new Intent(this, MainActivity.class);
        PendingIntent pi = PendingIntent.getActivity(this, 0, i,
                PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL)
                .setContentTitle("Da BT Dynamic Lock")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.ic_lock_idle_lock)
                .setContentIntent(pi)
                .setOngoing(true)
                .build();
    }

    private void startInForeground(String text) {
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, notification(text),
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
        } else {
            startForeground(NOTIFICATION_ID, notification(text));
        }
    }

    private void setState(String key) {
        setState(key, null);
    }

    private void setState(String key, String parameter) {
        setState(key, parameter, false);
    }

    private void setState(String key, String parameter, boolean parameterIsKey) {
        stateKey = key;
        stateParam = parameter;
        stateParamIsKey = parameterIsKey;
        showState();
    }

    private void showState() {
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.notify(NOTIFICATION_ID, notification(stateText()));
    }
}
