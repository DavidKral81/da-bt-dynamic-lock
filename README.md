# Da BT Dynamic Lock

Locks your Windows computer when you walk away with your phone — reliably,
and within seconds rather than minutes.

A replacement for the **Dynamic Lock** feature built into Windows, which locks
late, cannot be configured, and gives no indication of whether it is working
at all.

---

## Why it needs a phone app

Windows locks when it stops hearing your phone. The catch is that **Android
barely broadcasts anything on its own** — measured on a stock phone lying
still: **1.6 advertisements per minute**, with gaps up to **244 seconds**.
That is far too little to decide anything.

So this project has two parts:

| Part | What it does |
|---|---|
| **Windows app** | listens for the phone, locks the screen when it goes quiet |
| **Android app** | broadcasts a small Bluetooth LE beacon, restarts itself after reboot or aeroplane mode |

The phone app turns those 1.6 signals per minute into a steady stream, which
makes the decision trustworthy.

**It can only lock, never unlock.** Windows does not allow third-party
programs to unlock a session — you come back and sign in with your PIN or
fingerprint as usual.

---

## How the decision is made

Distance **cannot** be measured over Bluetooth. Measured at a desk: phone on
the desk −73 dBm, phone in a pocket −81 dBm, phone three metres away
−83 dBm — the difference between "here" and "across the room" is smaller than
the natural fluctuation of the signal.

What does work is that a phone in a pocket is weak enough that **a wall cuts
it off completely**. The edge of the room becomes the edge of the signal, so
the app locks when the phone stops being heard at all — not when it estimates
some distance.

The default of 45 seconds is derived from measurements, not guessed:

| Situation | Signals per minute | Longest gap |
|---|---|---|
| Phone on the desk, Bluetooth mouse on | 177 | 3 s |
| Phone in a pocket, mouse off | 84 | 9 s |
| Phone in a pocket, mouse on | 20 | **20 s** |

One Bluetooth radio serves the mouse, the keyboard and this app at the same
time; a mouse alone eats roughly three quarters of the packets that get
through. Locking after 20 seconds would produce false locks while you sit
still — hence 45.

---

## What it looks like

The app runs as an icon near the clock. Clicking it opens a window with a live
signal chart and all the settings:

- **which phone to watch** — picked from whatever is currently heard
- **when to lock** — 12 to 120 seconds of silence
- **range (sensitivity)** — optionally require a minimum signal strength
- **countdown** before locking (information only, it cannot be cancelled)
- **do not lock while typing or moving the mouse** (off by default — anyone at
  the desk could otherwise postpone locking)
- **warning when the phone disappears** for good, so a silent failure does not
  go unnoticed
- **pause** for a chosen period, then resume by itself
- **Czech / English** interface

The chart is there to tell two different problems apart: a curve that sits low
means a weak signal (distance, pocket, body), while a curve that is high but
full of gaps means interference — usually a Bluetooth mouse.

---

## Installation

**Windows** — run `DaBTDynamicLock-setup.exe` from the latest release. It
offers a Start menu shortcut, a desktop shortcut and automatic start after
sign-in. The program goes to `C:\Program Files\Da BT Dynamic Lock`; settings
and history live in `%APPDATA%\Da BT Dynamic Lock`, per user.

**Android** — install `DaBTDynamicLock.apk` from the same release, open it,
tap *Turn broadcasting on*, and allow the *Nearby devices* and *Notifications*
permissions. Set the battery mode to unrestricted and enable autostart,
otherwise the system will eventually stop the service.

Full instructions: [English manual](docs/___INFO-READ.txt) ·
[česky](docs/___INFO-CTI.txt)

---

## Building from source

```
windows\      Python 3.14 + bleak, pystray, Pillow, tkinter
phone\        Java, no Gradle (aapt2 + javac + d8 + apksigner)
installer\    PyInstaller
```

```powershell
powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1   # setup.exe
powershell -ExecutionPolicy Bypass -File phone\build.ps1                 # APK
```

The Android build needs no Android Studio and no Gradle, but the tools
(a JDK and the Android SDK build-tools) have to be present in an
`_android-build/` folder next to the project — the script does **not**
download them, it stops with an error when they are missing. Once they are
there, the build works offline.

Tests, to be run before any release:

```
py tests\test_logic.py        the locking decision
py tests\test_window.py       window behaviour, device list, language switch
py tests\test_installer.py    a full install and uninstall cycle
py tests\preview.py           renders the window to PNG so the look can be checked
```

---

## Known limitations

- **Locking only.** Unlocking is not possible for third-party software.
- **No distance setting.** See the measurements above — it is not a shortcoming
  of the implementation but of what Bluetooth signal strength can tell you.
- **The Windows binary is unsigned.** SmartScreen will warn on first run; a
  code-signing certificate costs more per year than this project is worth.
- **The phone app must keep running.** Aggressive battery optimisation
  (Xiaomi, Samsung, Huawei) will stop it unless you allow it to run freely.

---

## License

Apache License 2.0 — see [LICENSE](LICENSE).
