# Da BT Dynamic Lock

A replacement for the **Dynamic Lock** feature built into Windows, which is
unreliable and gives you nothing to set up.

Da BT Dynamic Lock locks your Windows computer when you walk away with your
phone — or with another Bluetooth LE device you pick — reliably, and on
conditions you choose yourself.

---

## Which device it can watch

Nothing is paired and nothing is connected: the computer only **listens for
Bluetooth LE advertisements** in the air, and you pick one of the devices it
can hear. That leaves two conditions for anything you might want to watch:

- **It has to be Bluetooth LE.** Classic Bluetooth accessories never show up
  in the list, no matter how they are paired with Windows.
- **It has to keep broadcasting.** Most accessories go quiet the moment they
  connect to something, and a phone on its own is nearly silent — see below.

A beacon, a fitness band or a watch that advertises continuously works
without anything installed. **A phone does not**, which is what the phone app
is for.

---

## Why a phone needs the phone app

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
makes the decision trustworthy. It costs almost nothing in battery: an
advertisement is a short radio signal, not a data transfer, and normal daily
use shows no drain worth mentioning.

**The phone app is deliberately tiny, and it cannot reach the network.** It
has no `INTERNET` permission at all, so there is nowhere for anything to be
sent even in principle — the only permissions it asks for are Bluetooth
advertising, running in the foreground, starting after a reboot and showing
its notification, and you can check that yourself in the app's permission
list. The whole thing is a handful of Java files in `phone/src/` with no
third-party libraries, so there is not much to audit.

**It can only lock, never unlock.** Windows does not allow third-party
programs to unlock a session — you come back and sign in with your PIN or
fingerprint as usual.

---

## How the decision is made

The real advantage here is that **the app shows you the signal instead of
hiding it, and then lets you set the rules**. The window carries a live chart
of the strength your own phone and your own room actually produce, and every
threshold below is something you set against what you can see there. Nobody
else decides for you what "away" means.

**You choose which event locks the screen:**

- **Signal disappears** (the default). A phone in a pocket is weak enough that
  a wall cuts it off completely, so the edge of the room becomes the edge of
  the signal. Robust, and it needs no tuning.
- **Signal drops below a strength you pick** (−100 to −60 dBm). The phone
  counts as "at the desk" only while it stays above that level — this is how
  you draw the boundary tighter than a wall, for an open-plan room or a desk
  near a doorway.

Either way the lock happens only after a delay you choose (12–120 seconds of
not counting), so a brief dropout never locks your screen.

**Why the app smooths the signal.** A single reading jumps by several dB with
the phone lying perfectly still, and the absolute values shift with the
phone, the pocket, a body in the way, and whatever else shares the Bluetooth
radio — an active mouse alone swallows a large share of the packets. So the
threshold is compared against the **median of a short window**, not the last
reading. For the same reason the app never converts dBm into metres: the
difference between "at the desk" and "across the room" is smaller than the
signal's own fluctuation, so any distance in metres would be a guess dressed
up as a number.

The chart also tells the two failure modes apart: a curve that sits low means
a weak signal, one that is high but full of gaps means interference. Our own
measurements behind the defaults are in [`docs/DESIGN.md`](docs/DESIGN.md) —
yours will differ, which is exactly why the settings exist.

---

## What it looks like

The app runs as an icon near the clock. Clicking it opens a window with a live
signal chart and all the settings:

- **which device to watch** — picked from whatever is currently heard
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
`_android-build/` folder in the project root — the script does **not**
download them, it stops with an error when they are missing. Once they are
there, the build works offline.

Tests, to be run before any release:

```
py tests\test_logic.py        the locking decision
py tests\test_window.py       window behaviour, device list, language switch
py tests\test_installer.py    a full install and uninstall cycle
py tests\preview.py           renders the window to PNG so the look can be checked
py tests\preview.py installer the same for every installer window
py tools\check_docs.py        checks the documentation against the code
```

---

## Known limitations

- **Locking only.** Unlocking is not possible for third-party software.
- **No distance in metres.** You can require a minimum signal strength, but
  not "lock at three metres" — signal strength cannot be turned into a
  trustworthy distance. That is a limit of the radio, not of this program.
- **The Windows binary is unsigned.** SmartScreen will warn on first run; a
  code-signing certificate costs more per year than this project is worth.
- **The phone app must keep running.** Aggressive battery optimisation
  (Xiaomi, Samsung, Huawei) will stop it unless you allow it to run freely.

---

## License

Apache License 2.0 — see [LICENSE](LICENSE).
