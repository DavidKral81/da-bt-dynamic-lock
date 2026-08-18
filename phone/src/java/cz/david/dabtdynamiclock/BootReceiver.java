package cz.david.dabtdynamiclock;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/**
 * Starts broadcasting after the phone is switched on and after the app is
 * updated.
 *
 * This is exactly what nRF Connect cannot do, which is why the advertiser had
 * to be turned on by hand every morning. Updating the app stops the service,
 * but the stored "enabled" stays - without this the phone would silently stop
 * broadcasting after an update.
 */
public class BootReceiver extends BroadcastReceiver {

    @Override
    public void onReceive(Context context, Intent intent) {
        String a = intent.getAction();
        if (!Intent.ACTION_BOOT_COMPLETED.equals(a)
                && !"android.intent.action.LOCKED_BOOT_COMPLETED".equals(a)
                && !Intent.ACTION_MY_PACKAGE_REPLACED.equals(a)) {
            return;
        }
        // What the user turned off stays off - starting after boot follows the
        // last state, it is not forced
        if (!Prefs.isEnabled(context)) {
            return;
        }
        context.startForegroundService(new Intent(context,
                                                  AdvertiserService.class));
    }
}
