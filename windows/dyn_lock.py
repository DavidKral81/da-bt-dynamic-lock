"""
Da BT Dynamic Lock - locks the laptop when the phone walks away.

How it works (derived from the measurements in CLAUDE.md):
  The phone broadcasts a BLE advertisement every 100 ms. An advertisement
  counts as "at the desk" while it keeps arriving and - if the user set a
  'rssi_threshold' - while the SMOOTHED strength stays above it (median of
  'threshold_window_s', because a raw reading jumps 8 dB even when the phone
  lies still). Once nothing has counted for 'silence_s' seconds, we lock.

  With no threshold set (the default) only disappearance locks the screen:
  in a pocket the signal is weak enough that a wall cuts it off completely,
  so the room boundary doubles as the signal boundary. A threshold draws the
  boundary tighter than a wall, which is what an open-plan room needs.

It never locks:
  - until the phone has been seen at least ONCE since the app started
    (otherwise the app would lock right after launch with the phone off)
  - twice in a row - after locking it waits for the phone to come back

It cannot UNLOCK - Windows does not allow that from third-party apps.
"""

import asyncio
import ctypes
import json
import os
import subprocess
import sys
import threading
import time
import tkinter as tk
from collections import deque
from ctypes import wintypes
from datetime import datetime
from pathlib import Path
from statistics import median
from xml.sax.saxutils import escape as xml_escape

from bleak import BleakScanner
from PIL import Image, ImageDraw
import pystray

import marks
import texts
from texts import t as tx
from version import VERSION, PROJECT_URL

APP_NAME = "Da BT Dynamic Lock"

HERE = Path(__file__).resolve().parent

# Where the data belongs (settings, log, history):
#   - run from source    -> next to the script, so everything stays together
#   - installed copy     -> into the user profile, because the program folder
#     is not a place to write into
if getattr(sys, "frozen", False):
    DATA = Path(os.environ.get("APPDATA", HERE)) / APP_NAME
    DATA.mkdir(parents=True, exist_ok=True)
    PROGRAM = Path(sys.executable).resolve().parent
else:
    DATA = HERE
    PROGRAM = HERE

CFG_PATH = DATA / "config.json"
LOG_PATH = DATA / "dyn_lock.log"
HIST_PATH = DATA / "history.json"
# History for the chart. A whole day at full resolution would be ~150 000
# samples (several MB), so older samples get thinned out: the last hour stays
# detailed, anything older is kept at ~10 s intervals.
HIST_LENGTH = 93600      # 26 h - so the "1 day" range is always fully filled
HIST_DETAILED = 3600     # last hour, kept without thinning
HIST_STEP = 10           # minimum spacing of older samples (s)

# Window icon. In the packaged build it sits next to the program, otherwise
# next to the sources.
_icon_file = ((Path(sys._MEIPASS) if getattr(sys, "frozen", False)
               else HERE / "icons") / "dyn_lock_tray.ico")
ICON_PATH = _icon_file if _icon_file.exists() else None

user32 = ctypes.windll.user32


# ---------------------------------------------------------------- settings

# The single source of truth for default settings. The shipped
# config.default.json is only a commented copy of these values, minus the
# keys the app writes for itself at runtime (window geometry). test_logic.py
# checks that the two never drift apart - it did NOT until 18.08.2026, and
# they had already drifted while this comment claimed otherwise.
DEFAULTS = {
    "target": "",                        # empty = user picks the phone first
    "silence_s": 45,                    # seconds of silence before locking
    "active": True,
    "countdown": True,
    "countdown_from_s": 15,
    "countdown_vertical": 0.33,           # 0.33 = one third from the top
    "rssi_threshold": None,                # None = lock only on signal loss
    "threshold_window_s": 6,
    "idle_guard": False,
    "idle_guard_s": 15,
    "scanner_restart_s": 120,
    "silence_watchdog_s": 45,
    "alert_no_signal_min": 10,
    "language": "cs",
    "window_geometry": "",             # remembered window size and position
    "window_maximised": True,
    "log": True,
}


def load_cfg():
    """Settings = defaults, overlaid with whatever the user has saved.

    Missing or damaged files must never stop the app: a fresh clone has no
    config.json at all, and a half-written file should not be fatal either.
    """
    cfg = dict(DEFAULTS)
    if not CFG_PATH.exists():
        # first run of an installed copy: start from the shipped template
        template = PROGRAM / "config.default.json"
        if template.exists():
            CFG_PATH.write_text(template.read_text(encoding="utf-8"),
                                encoding="utf-8")
    if CFG_PATH.exists():
        try:
            saved = json.loads(CFG_PATH.read_text(encoding="utf-8"))
            cfg.update({k: v for k, v in saved.items()
                        if not k.startswith("_")})
        except (OSError, ValueError) as error:
            # damaged settings must be visible, not silently ignored
            print(f"config.json unreadable ({error}) - using defaults")
    return cfg


def save_cfg(cfg):
    CFG_PATH.write_text(json.dumps(cfg, indent=2, ensure_ascii=False), encoding="utf-8")


CFG = load_cfg()
texts.set_language(CFG.get("language", "cs"))

# Command line switches (for testing):
#   --dry-run       everything runs, but the screen is never really locked
#   --quit-after N  quit on its own after N seconds
DRY_RUN = "--dry-run" in sys.argv
NO_DIALOG = "--no-dialog" in sys.argv     # for the automated test only
QUIT_AFTER = 0.0
if "--quit-after" in sys.argv:
    QUIT_AFTER = float(sys.argv[sys.argv.index("--quit-after") + 1])


LOG_MAX = 2_000_000      # 2 MB, then a new file is started


def log(msg):
    line = f"{datetime.now():%d.%m.%Y %H:%M:%S}  {msg}"
    print(line)
    if not CFG.get("log", True):
        return
    try:
        # Rotation: once the size is exceeded the file moves aside as .1
        # (overwriting the previous .1) - so at most two files are kept.
        if LOG_PATH.exists() and LOG_PATH.stat().st_size > LOG_MAX:
            previous = LOG_PATH.with_suffix(".log.1")
            previous.unlink(missing_ok=True)
            LOG_PATH.rename(previous)
    except OSError:
        pass
    with LOG_PATH.open("a", encoding="utf-8") as f:
        f.write(line + "\n")


# ------------------------------------------------------- start at logon

TASK_NAME = APP_NAME
STARTUP_LNK = (Path(os.environ.get("APPDATA", str(HERE)))
               / r"Microsoft\Windows\Start Menu\Programs\Startup"
               / f"{APP_NAME}.lnk")
_autostart = None        # remembered state, so schtasks is not run over and over
_autostart_at = 0.0


def _quiet(command):
    """Run a command without a console window flashing up."""
    try:
        v = subprocess.run(command, shell=True, capture_output=True, text=True,
                           creationflags=0x08000000)   # CREATE_NO_WINDOW
        return v.returncode == 0
    except Exception:
        return False


def _launch_parts():
    """Return (program, arguments) used to start the app.

    The packaged build starts directly; from source it goes through
    pythonw.exe so that no console window flashes up.
    """
    if getattr(sys, "frozen", False):
        return str(sys.executable), ""
    pyw = Path(sys.executable).with_name("pythonw.exe")
    return str(pyw if pyw.exists() else sys.executable), str(HERE / "dyn_lock.py")


def _create_task():
    """Register the logon task in Task Scheduler from XML.

    Why XML and not a plain schtasks command: that one only covers the
    basics. Here we also need a restart after a crash and, most importantly,
    the REMOVAL of the limit after which Windows kills the task on its own
    (3 days by default) - that can only be set this way.
    """
    program, arguments = _launch_parts()
    user = (os.environ.get("USERDOMAIN", "") + "\\"
            + os.environ.get("USERNAME", "")).strip("\\")
    e = xml_escape
    xml = f"""<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Locks the laptop when the phone walks away.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{e(user)}</UserId>
      <Delay>PT30S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{e(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{e(program)}</Command>
      <Arguments>{e('"' + arguments + '"') if arguments else ''}</Arguments>
      <WorkingDirectory>{e(str(HERE))}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>"""
    path = DATA / "_task.xml"
    try:
        # schtasks reads the XML as UTF-16, anything else is rejected
        path.write_text(xml, encoding="utf-16")
        return _quiet(f'schtasks /Create /F /TN "{TASK_NAME}" /XML "{path}"')
    except OSError as error:
        log(f"Could not prepare the task: {error}")
        return False
    finally:
        try:
            path.unlink(missing_ok=True)
        except OSError:
            pass


def autostart_enabled(refresh=False):
    """Does the app start at logon? Takes both mechanisms into account."""
    global _autostart, _autostart_at
    if not refresh and _autostart is not None \
            and time.monotonic() - _autostart_at < 30:
        return _autostart
    _autostart = _quiet(f'schtasks /Query /TN "{TASK_NAME}"') or STARTUP_LNK.exists()
    _autostart_at = time.monotonic()
    return _autostart


def autostart_set(enable):
    """Turn start-at-logon on or off.

    The scheduled task is tried first - it can delay the start (until
    bluetooth is up) and restart the app after a crash. When the user is not
    allowed to create one, a shortcut in the Startup folder is used instead,
    which always works.
    """
    global _autostart
    if enable:
        ok = _create_task()
        if ok:
            log("Start at logon enabled (scheduled task).")
        else:
            program, arguments = _launch_parts()
            ps = (f'$w=New-Object -ComObject WScript.Shell;'
                  f'$s=$w.CreateShortcut(\'{STARTUP_LNK}\');'
                  f'$s.TargetPath=\'{program}\';'
                  f'$s.Arguments=\'{arguments}\';'
                  f'$s.WorkingDirectory=\'{HERE}\';'
                  f'$s.Save()')
            STARTUP_LNK.parent.mkdir(parents=True, exist_ok=True)
            ok = _quiet(f'powershell -NoProfile -ExecutionPolicy Bypass -Command "{ps}"')
            log("Start at logon enabled (Startup folder)." if ok
                else "Start at logon could not be enabled.")
    else:
        _quiet(f'schtasks /Delete /F /TN "{TASK_NAME}"')
        try:
            STARTUP_LNK.unlink(missing_ok=True)
        except OSError:
            pass
        log("Start at logon disabled.")
    _autostart = None
    autostart_enabled(refresh=True)


# ---------------------------------------------------------------- state

class State:
    """State shared between the scanner thread and the main loop."""

    def __init__(self):
        self.seen_at = 0.0      # time of the last advertisement (monotonic)
        self.rssi = None        # last measured value
        self.rssi_median = None  # smoothed value (median of the window)
        self.samples = deque()  # (time, rssi) over the last window - smoothing
        self.history = deque()  # (time, rssi) over the last hour - for the chart
        self.locks = deque()    # lock times - drawn as vertical lines
        self.downtime = []      # (from, to) stretches when the app was not running
        self.nearby = {}        # what is audible now: key -> (name, rssi, time)
        self.near_at = 0.0      # last moment the phone counted as "at the desk"
        self.was_near = False
        self.armed = True       # False = already locked, waiting for the return
        self.paused_until = 0.0  # temporary pause from the tray icon
        self.lock = threading.Lock()

    def record(self, rssi):
        """An advertisement arrived. This decides whether it counts as
        "at the desk".

        When a sensitivity threshold (rssi_threshold) is set, hearing the signal is
        not enough - it also has to be strong enough. The SMOOTHED value
        (median of the window) is compared, because raw RSSI jumps by 8 dB
        even with the phone lying still.
        """
        threshold = CFG.get("rssi_threshold")
        window = float(CFG.get("threshold_window_s", 6))
        with self.lock:
            now = time.monotonic()
            if not self.was_near:
                message = f"Phone seen for the first time ({rssi} dBm) - watching."
            elif now - self.near_at > 5:
                message = (f"Phone back at the desk after {now - self.near_at:.0f} s "
                           f"of silence ({rssi} dBm).")
            else:
                message = None

            self.seen_at = now
            self.rssi = rssi
            # At most 20 samples per second go into the history. The phone
            # advertises 10x per second at most, so anything denser is a fault
            # (two running scanners, say) and would only bloat the file.
            if not self.history or now - self.history[-1][0] >= 0.05:
                self.history.append((now, rssi))
            while self.history and now - self.history[0][0] > HIST_LENGTH:
                self.history.popleft()
            self.samples.append((now, rssi))
            while self.samples and now - self.samples[0][0] > window:
                self.samples.popleft()
            self.rssi_median = int(median([r for _, r in self.samples]))

            near = threshold is None or self.rssi_median >= float(threshold)
            if near:
                self.near_at = now
                self.was_near = True
                self.armed = True
            else:
                message = None     # weak signal = as if absent, no "back" notice
        if message:
            log(message)

    def record_nearby(self, dev, adv):
        """Keep track of everything audible - for picking the target from the menu.

        Unnamed devices are never offered (a MAC would tell the user nothing
        and phones rotate theirs anyway), so only named ones are stored.
        """
        name = adv.local_name
        if not name:
            return
        with self.lock:
            self.nearby[name] = (name, adv.rssi, time.monotonic())

    def nearby_list(self, within_s=60):
        """Devices heard in the last within_s seconds, strongest first.

        Returns (name, rssi, age_s). The age matters so that a record which is
        only a memory can be told apart - otherwise the list would show the
        last known signal strength as if it had just arrived.
        """
        now = time.monotonic()
        with self.lock:
            items = [(n, r, now - t) for n, r, t in self.nearby.values()
                     if now - t <= within_s]
        return sorted(items, key=lambda x: -x[1])

    def target_changed(self):
        """Forget what we know about the previous device - otherwise readings
        from the old target would count for a while and the app could lock,
        or fail to lock, on them."""
        with self.lock:
            self.samples.clear()
            self.seen_at = 0.0
            self.near_at = 0.0
            self.was_near = False
            self.rssi = None
            self.rssi_median = None
            self.armed = True

    def silence(self):
        """How long the phone has not been "at the desk". None = never was."""
        with self.lock:
            if not self.was_near:
                return None
            return time.monotonic() - self.near_at


STATE = State()


# --- persistent history for the chart -----------------------------------
# Time is stored as "how many seconds ago" converted to wall clock time
# (time.time()). Monotonic time resets when the machine reboots, so stored
# points would end up somewhere completely different after a restart.

def thin_history():
    """Samples older than an hour are kept only every ~10 s.

    Without this a day of running would produce ~150 000 samples (several MB
    in the file and pointless drawing). It is invisible in the chart - with an
    8 h or 1 day range several samples fall on one pixel anyway.
    """
    now = time.monotonic()
    with STATE.lock:
        kept, last = [], None
        for t, r in STATE.history:
            if now - t <= HIST_DETAILED or last is None or t - last >= HIST_STEP:
                kept.append((t, r))
                if now - t > HIST_DETAILED:
                    last = t
        STATE.history.clear()
        STATE.history.extend(kept)


def save_history():
    try:
        thin_history()
        now_mono, now_wall = time.monotonic(), time.time()
        with STATE.lock:
            samples = [[round(now_wall - (now_mono - t), 2), r]
                       for t, r in STATE.history]
            locks = [round(now_wall - (now_mono - t), 2) for t in STATE.locks]
            downtime = [[round(now_wall - (now_mono - a), 2),
                         round(now_wall - (now_mono - b), 2)]
                        for a, b in STATE.downtime]
        HIST_PATH.write_text(json.dumps(
            {"until": now_wall, "samples": samples, "locks": locks,
             "downtime": downtime}),
            encoding="utf-8")
    except OSError as e:
        log(f"Could not save the history: {e}")


def load_history():
    """Load the history of the previous run and fill in the downtime gap."""
    if not HIST_PATH.exists():
        return
    try:
        d = json.loads(HIST_PATH.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        log(f"Could not load the history: {e}")
        return

    now_mono, now_wall = time.monotonic(), time.time()

    def to_mono(w):
        return now_mono - (now_wall - w)

    cutoff = now_wall - HIST_LENGTH
    with STATE.lock:
        for w, r in d.get("samples", []):
            if w >= cutoff:
                STATE.history.append((to_mono(w), r))
        for w in d.get("locks", []):
            if w >= cutoff:
                STATE.locks.append(to_mono(w))
        for a, b in d.get("downtime", []):
            if b >= cutoff:
                STATE.downtime.append((to_mono(a), to_mono(b)))
        # the stretch between the last write and this start = app was down
        end = d.get("until")
        if end and now_wall - end > 5:
            STATE.downtime.append((to_mono(end), now_mono))
            log(f"App was not running for {(now_wall - end) / 60:.0f} min "
                f"- marked in the chart.")


# ---------------------------------------------------------------- Windows

def lock_screen():
    user32.LockWorkStation()


_mutex = None
ERROR_ALREADY_EXISTS = 183


def already_running():
    """True when another instance is already running.

    A named mutex - the operating system holds it for the lifetime of the
    process, so no stale lock file is left behind after a crash. The name has
    no 'Global\\' prefix, so it applies within the logged-in user - which is
    exactly what we want (each user watches their own session).
    """
    global _mutex
    k32 = ctypes.windll.kernel32
    k32.CreateMutexW.restype = wintypes.HANDLE
    k32.CreateMutexW.argtypes = [wintypes.LPCVOID, wintypes.BOOL, wintypes.LPCWSTR]
    # A trial run (--dry-run) uses its own lock name so it can run alongside
    # the real app - otherwise nothing could be tested without shutting the
    # real one down.
    name = "DaBTDynamicLock_single_instance" + ("_dry_run" if DRY_RUN else "")
    _mutex = k32.CreateMutexW(None, False, name)
    return k32.GetLastError() == ERROR_ALREADY_EXISTS


class LASTINPUTINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.UINT), ("dwTime", wintypes.DWORD)]


def idle_seconds():
    """Seconds since the last key press / mouse movement.

    BEWARE of two traps, both around the 32bit tick counter:
      1) GetTickCount returns an UNSIGNED DWORD, but without a declared type
         ctypes reads it as a signed int - after 24.85 days of uptime it
         starts returning negative numbers. Hence GetTickCount64, truncated
         to 32 bits.
      2) dwTime from GetLastInputInfo is still 32bit and wraps every 49.7
         days. The difference is therefore computed in 32bit arithmetic
         (& 0xFFFFFFFF), so the wrap-around comes out right.
    """
    lii = LASTINPUTINFO()
    lii.cbSize = ctypes.sizeof(LASTINPUTINFO)
    if not user32.GetLastInputInfo(ctypes.byref(lii)):
        return 999.0
    k32 = ctypes.windll.kernel32
    k32.GetTickCount64.restype = ctypes.c_ulonglong
    now = k32.GetTickCount64() & 0xFFFFFFFF
    return ((now - lii.dwTime) & 0xFFFFFFFF) / 1000.0


def work_area():
    """Desktop size WITHOUT the taskbar - so the window sits above the clock."""
    r = wintypes.RECT()
    user32.SystemParametersInfoW(0x0030, 0, ctypes.byref(r), 0)  # SPI_GETWORKAREA
    return r.left, r.top, r.right, r.bottom


# ---------------------------------------------------------------- scanner

def matches(dev, adv, target):
    # No target picked yet = nothing counts. Without this an empty target
    # matched EVERYTHING ("" is a substring of every name), so any BLE device
    # around - a mouse, a TV, a neighbour's gadget - was taken for the phone.
    # The screen then stayed unlocked as long as anything at all was audible.
    if not target:
        return False
    if len(target) == 17 and target.count(":") == 5:
        return dev.address.upper() == target.upper()
    c = target.lower()
    if adv.local_name and c in adv.local_name.lower():
        return True
    return any(c in u.lower() for u in adv.service_uuids)


async def scanner_loop():

    def on_advertisement(dev, adv):
        if adv.rssi <= -120:      # -127 = "RSSI unknown", not a measurement
            return
        # The target is read on every advertisement, not once at the start -
        # it can be switched at runtime from the tray menu
        if matches(dev, adv, CFG.get("target", "")):
            STATE.record(adv.rssi)
        STATE.record_nearby(dev, adv)

    first_run = True
    futile = 0       # how many restarts in a row brought nothing
    while True:
        scanner = None
        try:
            scanner = BleakScanner(detection_callback=on_advertisement)
            await scanner.start()
            session_start = time.monotonic()
            if first_run:
                log(f"Scanner started, watching '{CFG.get('target')}'.")
                first_run = False

            # The scanner is restarted periodically. Why: during calibration
            # (5 minutes of a freshly started scanner) the longest gap was
            # 8.9 s, whereas after hours of running gaps of 30-200 s showed up
            # at the same signal strength. A long-running scanner on Windows
            # seems to go progressively deaf. A restart is cheap (~0.5 s) and
            # brings it back into shape.
            restart_after = float(CFG.get("scanner_restart_s", 120))
            watchdog = float(CFG.get("silence_watchdog_s", 45))
            while time.monotonic() - session_start < restart_after:
                await asyncio.sleep(1)
                # Safety net: when nothing has arrived for a long time, do not
                # wait for the period and try to bring the scanner back right
                # away - it may have stopped listening.
                #
                # BEWARE: a restart cannot conjure up a signal. When the phone
                # is simply gone (or not advertising), the condition stays true
                # and without a brake the scanner would restart every second
                # forever (on 15.08.2026 that produced 47 000 restarts
                # overnight and as many log lines). So after every futile
                # attempt the wait doubles, up to 10 minutes.
                wait = min(watchdog * (2 ** min(futile, 4)), 600)
                if STATE.was_near and time.monotonic() - STATE.seen_at > wait \
                        and time.monotonic() - session_start > wait:
                    futile += 1
                    log(f"No signal for {wait:.0f} s - restarting the scanner "
                        f"(futile attempt no. {futile}).")
                    break
            if STATE.seen_at > session_start:    # the session brought something
                futile = 0
        except Exception as e:
            log(f"Scanner failed ({e}) - trying again in 10 s.")
            await asyncio.sleep(10)
        finally:
            # The scanner MUST be stopped even on an exception. Left running
            # without an owner it keeps consuming advertisements - and with
            # repeated attempts dozens of them pile up, so one phone reports a
            # hundred times (on 15.08.2026 that produced 450 000 samples and
            # froze the chart).
            if scanner is not None:
                try:
                    await scanner.stop()
                except Exception:
                    pass


def scanner_thread():
    asyncio.new_event_loop().run_until_complete(scanner_loop())


# ---------------------------------------------------------------- countdown

class Countdown:
    """A small unobtrusive box above the clock in the bottom right corner.

    Deliberately WITHOUT a way to cancel - it only tells the time left. The
    window never steals focus (WS_EX_NOACTIVATE) and clicks go through it
    (WS_EX_TRANSPARENT) so that it is never in the way.
    """

    def __init__(self, root):
        self.root = root
        self.win = None
        self.lbl = None

    def _create(self):
        w = tk.Toplevel(self.root)
        w.overrideredirect(True)
        w.attributes("-topmost", True)
        w.configure(bg="#e8a33d")            # bright orange border
        frame = tk.Frame(w, bg="#241a10")
        frame.pack(padx=2, pady=2)
        self.lbl = tk.Label(frame, text="", font=("Segoe UI Semibold", 12),
                            fg="#ffc266", bg="#241a10", padx=14, pady=7)
        self.lbl.pack()
        w.update_idletasks()

        hwnd = ctypes.windll.user32.GetParent(w.winfo_id()) or w.winfo_id()
        GWL_EXSTYLE = -20
        WS_EX_NOACTIVATE, WS_EX_TRANSPARENT, WS_EX_TOOLWINDOW = 0x08000000, 0x20, 0x80
        st = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        user32.SetWindowLongW(hwnd, GWL_EXSTYLE,
                              st | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)
        self.win = w

    def show(self, remaining):
        if self.win is None:
            self._create()
        self.lbl.config(text=tx("countdown_text", s=remaining))
        self.win.update_idletasks()
        # Horizontally centred, vertically at 1/3 of the screen from the top -
        # the eye lands there on its own, unlike the corner by the clock.
        left, top, right, bottom = work_area()
        w, h = self.win.winfo_width(), self.win.winfo_height()
        ratio = float(CFG.get("countdown_vertical", 0.33))
        x = left + (right - left - w) // 2
        y = top + int((bottom - top) * ratio) - h // 2
        self.win.geometry(f"+{x}+{y}")
        self.win.deiconify()
        self.win.lift()

    def hide(self):
        if self.win is not None:
            self.win.withdraw()


# ---------------------------------------------------------------- chart

class Scroller(tk.Canvas):
    """A vertical scrollbar drawn by hand.

    The Tk scrollbar on Windows ignores colour settings (the system draws it),
    so a light grey bar would stay in the dark window where it does not belong.
    """
    WIDTH = 11
    MIN_THUMB = 34              # so that a small thumb can still be grabbed

    def __init__(self, parent, view):
        super().__init__(parent, width=self.WIDTH, bg="#151920",
                         highlightthickness=0, cursor="hand2")
        self.view = view                # yview of the area being scrolled
        self.first, self.last = 0.0, 1.0
        self.grab = None                # cursor offset from the top edge
        self.hovered = False
        self.bind("<Configure>", lambda e: self._draw())
        self.bind("<Button-1>", self._press)
        self.bind("<B1-Motion>", self._drag)
        self.bind("<ButtonRelease-1>", lambda e: setattr(self, "grab", None))
        self.bind("<Enter>", lambda e: self._hover(True))
        self.bind("<Leave>", lambda e: self._hover(False))

    def set_view(self, first, last):
        self.first, self.last = float(first), float(last)
        self._draw()

    def _hover(self, hovering):
        self.hovered = hovering
        self._draw()

    def _thumb(self):
        """Returns (from, to) in pixels."""
        v = self.winfo_height()
        start, end = self.first * v, self.last * v
        if end - start < self.MIN_THUMB:    # a short thumb cannot be hit
            mid = (start + end) / 2
            start = max(0, min(v - self.MIN_THUMB, mid - self.MIN_THUMB / 2))
            end = start + self.MIN_THUMB
        return start, end

    def _draw(self):
        self.delete("all")
        start, end = self._thumb()
        s = self.WIDTH
        self.create_rectangle(2, start + 1, s - 2, end - 1, width=0,
                              fill="#4d5866" if self.hovered else "#3a4350")

    def _press(self, event):
        start, end = self._thumb()
        if start <= event.y <= end:
            self.grab = event.y - start
        else:                            # click beside the thumb = page jump
            self.view("scroll", 1 if event.y > end else -1, "pages")

    def _drag(self, event):
        if self.grab is None:
            return
        v = max(1, self.winfo_height())
        self.view("moveto", max(0.0, min(1.0, (event.y - self.grab) / v)))


class Chart:
    """Live chart of the signal strength. Drawn into a tkinter Canvas so that
    the app does not need matplotlib (a few MB more for a single window)."""

    SETTINGS_WIDTH = 620  # width of the settings column; wider reads badly
    # labels are built at runtime - they change with the language
    TIME_RANGES = [("range_2min", 120), ("range_5min", 300), ("range_15min", 900),
                   ("range_1h", 3600), ("range_8h", 28800), ("range_1day", 86400)]
    # Range of the RSSI axis in dBm. It reaches higher than normal values (a
    # phone on the desk gives about -70) and lower than the sensitivity of the
    # receiver (~-100), so that extremes still fit in the chart and are
    # visibly outside the usual range.
    RSSI_TOP, RSSI_BOTTOM = -30, -110
    MARGIN_L, MARGIN_R, MARGIN_T, MARGIN_B = 52, 14, 34, 44

    def __init__(self, root):
        self.root = root
        self.win = None
        self.span = 300

    def toggle(self):
        if self.win is not None and self.win.winfo_exists():
            self._remember_window()
            self.win.destroy()
            self.win = None
            return
        self._create()

    def _remember_window(self):
        """Remember size, position and whether the window was maximised.

        Stored in the settings file, so it survives a restart of the app as
        well. Geometry is only read back from the window itself - never kept
        in a second variable that could go stale.
        """
        try:
            maximised = self.win.state() == "zoomed"
            if maximised:
                # geometry() of a maximised window reports the screen size,
                # which is useless once it is restored - keep the previous
                # windowed geometry instead
                geom = CFG.get("window_geometry", "")
            else:
                geom = self.win.geometry()
            if geom != CFG.get("window_geometry") or \
                    maximised != CFG.get("window_maximised"):
                CFG["window_geometry"] = geom
                CFG["window_maximised"] = maximised
                save_cfg(CFG)
        except tk.TclError:
            pass        # window already gone - nothing worth remembering

    def _create(self):
        w = tk.Toplevel(self.root)
        w.title(tx("window_title", app=APP_NAME))
        # The minimum width comes from the legend (it needs ~900 px) - a
        # narrower window would cut off the last item
        w.minsize(920, 300)
        # restore the size and position the window had when it was closed
        w.geometry(CFG.get("window_geometry") or "860x400")
        if CFG.get("window_maximised", True):
            w.state("zoomed")

        # Without this the window only flashed in the taskbar as minimised and
        # had to be clicked. The app has no main window, so Windows will not
        # hand it focus on its own - it has to ask.
        w.iconbitmap(default=str(ICON_PATH)) if ICON_PATH else None
        w.deiconify()
        w.lift()
        w.attributes("-topmost", True)
        w.after(300, lambda: w.attributes("-topmost", False))
        w.focus_force()
        w.configure(bg="#1b1f26")
        w.protocol("WM_DELETE_WINDOW", self.toggle)

        # --- Chart / Settings tabs -------------------------------------
        bar = tk.Frame(w, bg="#1b1f26")
        bar.pack(fill="x", padx=10, pady=(8, 0))
        self.tab_buttons = []
        for i, label in enumerate((tx("tab_chart"), tx("tab_settings"))):
            t = tk.Label(bar, text=label, font=("Segoe UI", 11), padx=18,
                         pady=7, cursor="hand2")
            t.pack(side="left", padx=(0, 4))
            t.bind("<Button-1>", lambda e, k=i: self._tab(k))
            self.tab_buttons.append(t)

        self.chart_frame = tk.Frame(w, bg="#1b1f26")
        self.settings_frame = tk.Frame(w, bg="#1b1f26")

        # --- contents of the Chart tab ---------------------------------
        bar = tk.Frame(self.chart_frame, bg="#1b1f26")
        bar.pack(fill="x", padx=10, pady=(8, 0))
        tk.Label(bar, text=tx("chart_show"), fg="#9aa4b2", bg="#1b1f26",
                 font=("Segoe UI", 9)).pack(side="left")
        self.span_var = tk.IntVar(value=self.span)
        for key, sec in self.TIME_RANGES:
            tk.Radiobutton(bar, text=tx(key), value=sec, variable=self.span_var,
                           command=self._change_span, fg="#d7dde5", bg="#1b1f26",
                           selectcolor="#2a3038", activebackground="#1b1f26",
                           activeforeground="#fff", font=("Segoe UI", 9),
                           borderwidth=0, highlightthickness=0).pack(side="left", padx=2)
        self.info = tk.Label(bar, text="", fg="#9aa4b2", bg="#1b1f26",
                             font=("Segoe UI", 9))
        self.info.pack(side="right")

        self.canvas = tk.Canvas(self.chart_frame, bg="#151920",
                                highlightthickness=0)
        self.canvas.pack(fill="both", expand=True, padx=10, pady=(10, 4))
        self._legend(self.chart_frame)

        self._create_settings(self.settings_frame)
        self.win = w
        self._tab(0)
        self._refresh_settings()
        self._set_minimum(w)
        self._draw()

    def _set_minimum(self, w):
        """The CONTENT decides the smallest window size, not a guess.

        The minimum used to be hard-coded and shrinking the window cut the
        settings off without a scrollbar. Now we measure what the content
        really needs (the larger of the two tabs) and the window will not go
        below that.

        The HEIGHT, however, is no longer dictated by the settings content -
        that one scrolls. When the content drove it, one device more in the
        list was enough for the window to want to be taller than the screen ->
        the bottom (the Quit button) became unreachable."""
        w.update_idletasks()
        # width = a single column (narrower makes no sense)
        need_settings = self.SETTINGS_WIDTH + 60
        # the Chart tab needs the width of the legend and a sane curve height
        need_chart = (940, 320)
        width = min(max(need_settings + 40, need_chart[0]),
                    w.winfo_screenwidth() - 80)
        height = min(max(need_chart[1], 420), w.winfo_screenheight() - 160)
        w.minsize(width, height)
        # a window that is already taller than the screen (from before) gets
        # brought back into line
        if w.winfo_height() > w.winfo_screenheight() - 80:
            w.geometry(f"{w.winfo_width()}x{w.winfo_screenheight() - 120}")

    def _tab(self, which):
        """Switch between the chart and the settings."""
        self.active_tab = which
        for i, t in enumerate(self.tab_buttons):
            selected = i == which
            t.configure(bg="#151920" if selected else "#1b1f26",
                        fg="#e6ebf2" if selected else "#8b95a3")
        self.chart_frame.pack_forget()
        self.settings_frame.pack_forget()
        (self.chart_frame if which == 0 else self.settings_frame).pack(
            fill="both", expand=True)

    # ---------------------------------------------------------- settings

    def _create_settings(self, parent):
        """The Settings tab.

        A single column with a fixed maximum width in the middle - on a wide
        monitor content stretched across the whole screen looks bad and reads
        badly. Every setting is a drop-down list, so that the controls look
        and behave the same.
        """
        # The content lives in a scrollable area. Without it every growth of
        # the content (another device showing up in the list, say) cut off the
        # bottom of the window with no way to reach it - the window cannot go
        # past the screen size.
        self.settings_canvas = tk.Canvas(parent, bg="#1b1f26",
                                         highlightthickness=0)
        self.settings_scroller = Scroller(parent, self.settings_canvas.yview)
        self.scroller_shown = False
        self.settings_canvas.configure(yscrollcommand=self._scroller_state)
        self.settings_canvas.pack(side="left", fill="both", expand=True)

        inner = tk.Frame(self.settings_canvas, bg="#1b1f26")
        self.settings_window = self.settings_canvas.create_window(
            (0, 0), window=inner, anchor="nw")
        self.grid_frame = tk.Frame(inner, bg="#1b1f26")
        self.grid_frame.pack(anchor="n", pady=(14, 10))

        def measured(_=None):
            content = self.settings_canvas.bbox("all")
            if not content:
                return
            # The scrolled region must be at least as tall as the area itself.
            # When it is smaller, Tk CENTRES it while scrolling - the settings
            # content then jumps to the middle of the window on a wheel turn.
            height = max(content[3], self.settings_canvas.winfo_height())
            self.settings_canvas.configure(scrollregion=(0, 0, content[2], height))
            # the inner frame has to be as wide as the area, otherwise the
            # content would not be centred and the column relayout would
            # compute with the wrong width
            self.settings_canvas.itemconfigure(
                self.settings_window, width=self.settings_canvas.winfo_width())

        inner.bind("<Configure>", measured)
        self.settings_canvas.bind("<Configure>", lambda e: (measured(),
                                                            self._relayout()))
        # the mouse wheel only works while the cursor is over the settings
        self.settings_canvas.bind_all("<MouseWheel>", self._wheel, add="+")
        self.cards = []
        self.language_var = tk.StringVar(
            value=dict(texts.LANGUAGES)[texts.language()])
        column = self.grid_frame
        self.columns = 0
        parent.bind("<Configure>", self._relayout)

        # --- 1. phone --------------------------------------------------
        card = self._card(column, tx("card_phone"), tx("card_phone_desc"))
        self.target_var = tk.StringVar(value=CFG.get("target", ""))
        self.devices_frame = tk.Frame(card, bg="#151920")
        self.devices_frame.pack(fill="x", pady=(2, 0))

        # --- 2. when to lock -------------------------------------------
        card = self._card(column, tx("card_when"),
                          tx("card_when_desc"))
        self.sw_active = self._switch(
            card, tx("sw_active"), "active")
        self.sw_idle_guard = self._switch(
            card, tx("sw_idle_guard"), "idle_guard")
        self._select(card, tx("lbl_lock_after"), "silence_s",
                     [(tx("opt_seconds", s=s), s) for s in
                      (12, 15, 20, 30, 45, 60, 90, 120)])
        self._select(card, tx("lbl_range"), "rssi_threshold",
                     [(tx("opt_range_max"), None)]
                     + [(f"{v} dBm", v) for v in
                        (-100, -95, -90, -85, -80, -75, -70, -65, -60)])
        # The countdown is one menu, not a checkbox plus a menu: "do not show"
        # is just another option in the same list, same as with the alerts.
        self.countdown_var = tk.StringVar()
        self.countdown_pairs = ([(tx("opt_countdown_off"), 0)]
                                + [(tx("opt_countdown_from", s=s), s)
                                   for s in (5, 10, 15, 20, 30)])
        self.countdown_var.set(self._countdown_label())
        self._select(card, tx("sw_countdown"), None, self.countdown_pairs,
                     var=self.countdown_var, action=self._set_countdown)

        # --- 3. behaviour ----------------------------------------------
        card = self._card(column, tx("card_behaviour"), None)
        self.sw_autostart = self._switch(card, tx("sw_autostart"),
                                         None, autostart_enabled, autostart_set)
        self._select(card, tx("lbl_language"), None,
                     [(name, code) for code, name in texts.LANGUAGES],
                     var=self.language_var, action=self._change_language)

        card = self._card(
            column, tx("card_gone"), tx("card_gone_desc"))
        self._select(card, tx("lbl_warn"),
                     "alert_no_signal_min",
                     [(tx("opt_no_warning"), 0)]
                     + [(tx("opt_after_minutes", m=m), m) for m in
                        (1, 2, 5, 10, 20, 30, 60)])

        # --- 4. pause --------------------------------------------------
        card = self._card(column, tx("card_pause"),
                          tx("card_pause_desc"))
        self.pause_var = tk.StringVar()
        self._select(card, None, None,
                     [(tx("opt_not_paused"), 0), (tx("opt_5_min"), 5),
                      (tx("opt_15_min"), 15), (tx("opt_30_min"), 30),
                      (tx("opt_1_hour"), 60), (tx("opt_2_hours"), 120),
                      (tx("opt_4_hours"), 240), (tx("opt_12_hours"), 720),
                      (tx("opt_1_day"), 1440), (tx("opt_2_days"), 2880)],
                     var=self.pause_var, action=self._pause)

        # --- 5. links --------------------------------------------------
        self.links_frame = tk.Frame(self.grid_frame, bg="#1b1f26")
        links = self.links_frame
        row = tk.Frame(links, bg="#1b1f26")
        row.pack(anchor="w", pady=(0, 14))
        self._link(row, tx("link_phone_app"),
                   self._open_phone).pack(side="left")
        tk.Label(row, text="   ·   ", bg="#1b1f26", fg="#8b95a3",
                 font=("Segoe UI", 10)).pack(side="left")
        self._link(row, tx("link_manual"), self._open_manual).pack(side="left")
        tk.Label(row, text="   ·   ", bg="#1b1f26", fg="#8b95a3",
                 font=("Segoe UI", 10)).pack(side="left")
        self._link(row, tx("link_project"),
                   self._open_project).pack(side="left")
        tk.Label(row, text="   ·   ", bg="#1b1f26", fg="#8b95a3",
                 font=("Segoe UI", 10)).pack(side="left")
        # The version belongs somewhere a user can find it when reporting a
        # problem. Same constant the installer and the file properties use,
        # so the three can never disagree. No translation key needed - "v1.0"
        # reads the same in both languages.
        tk.Label(row, text=f"v{VERSION}", bg="#1b1f26", fg="#a9b4c2",
                 font=("Segoe UI", 10)).pack(side="left")
        self._button(links, f"  {tx('btn_quit')}  ",
                     self._quit).pack(anchor="w")

    def _scroller_state(self, first, last):
        """Show the scrollbar only when the content does not fit.

        We keep our own record of whether the scrollbar is shown.
        `winfo_ismapped()` only tells the truth after the window has been
        redrawn, so with two changes in quick succession it still answered
        according to the old state and the scrollbar stayed hovering over
        content that did fit.
        """
        self.settings_scroller.set_view(first, last)
        fits = float(first) <= 0.0 and float(last) >= 1.0
        if fits and self.scroller_shown:
            self.settings_scroller.pack_forget()
            self.scroller_shown = False
        elif not fits and not self.scroller_shown:
            self.settings_scroller.pack(side="right", fill="y")
            self.scroller_shown = True

    def _wheel(self, event):
        """The mouse wheel scrolls the settings - only when there is room."""
        if self.active_tab != 1 or not self.scroller_shown:
            return
        self.settings_canvas.yview_scroll(-1 if event.delta > 0 else 1, "units")

    def _card(self, parent, title, description):
        """One settings section - heading, short explanation, content.

        The card is not packed - _relayout places it according to the window
        width.
        """
        outer = tk.Frame(self.grid_frame, bg="#1b1f26")
        self.cards.append(outer)
        tk.Label(outer, text=title, bg="#1b1f26", fg="#e6ebf2",
                 font=("Segoe UI", 12, "bold")).pack(anchor="w", pady=(0, 1))
        if description:
            tk.Label(outer, text=description, bg="#1b1f26", fg="#8b95a3",
                     justify="left", wraplength=self.SETTINGS_WIDTH - 20,
                     font=("Segoe UI", 9)).pack(anchor="w", pady=(0, 5))
        inner = tk.Frame(outer, bg="#151920", padx=16, pady=12)
        inner.pack(fill="both", expand=True)
        return inner

    def _relayout(self, event=None):
        """Lay the cards out into one or two columns by the window width.

        On a wide monitor a single narrow column wastes space, in a narrow
        window two columns do not fit - so let it sort itself out.
        """
        width = self.settings_canvas.winfo_width()
        wanted = 2 if width >= 2 * self.SETTINGS_WIDTH + 100 else 1
        if wanted == self.columns:
            return
        self.columns = wanted

        # All columns equally wide and fixed - otherwise every card sets its
        # width by its own content and the blocks come out uneven
        for i in range(2):
            self.grid_frame.grid_columnconfigure(
                i, minsize=(self.SETTINGS_WIDTH + 10) if i < wanted else 0,
                weight=1 if i < wanted else 0,
                uniform="cards" if i < wanted else "")

        per_column = -(-len(self.cards) // wanted)     # round up
        for index, card in enumerate(self.cards):
            column = index // per_column
            row = index % per_column
            # the padding has to be the same on BOTH columns, otherwise one
            # column is narrower by that gap (it was 600 vs 620 px)
            gap = 10 if wanted == 2 else 0
            card.grid(row=row, column=column, sticky="new",
                      padx=(0, gap) if column == 0 else (gap, 0),
                      pady=(0, 14))
        # a gap has to remain at the bottom, otherwise the buttons are glued
        # to the edge of the window
        self.links_frame.grid(row=per_column, column=0, columnspan=wanted,
                              sticky="ew", pady=(2, 22))
        self._set_minimum(self.win) if self.win else None

    def _quit(self):
        log("Quit by the user.")
        self.root.quit()

    def _link(self, parent, text, action):
        """Clickable text - not a button. Underlined only on hover."""
        t = tk.Label(parent, text=text, bg=parent["bg"], fg="#7cc0ff",
                     font=("Segoe UI", 10), cursor="hand2")
        t.bind("<Button-1>", lambda e=None: action())
        t.bind("<Enter>", lambda e=None: t.configure(
            font=("Segoe UI", 10, "underline")))
        t.bind("<Leave>", lambda e=None: t.configure(font=("Segoe UI", 10)))
        return t

    def _button(self, parent, text, action):
        t = tk.Label(parent, text=text, bg="#232b36", fg="#e6ebf2", padx=12,
                     pady=7, font=("Segoe UI", 10), cursor="hand2")
        t.bind("<Button-1>", lambda e=None: action())
        return t

    # The drawing lives in marks.py because the INSTALLER draws the same
    # marks - one place to change, so the two cannot end up looking
    # different.
    MARK_SIZE = marks.SIZE

    def _mark(self, parent, round_mark=False):
        """A square (checkbox) or a circle (radio) - drawn by hand."""
        return tk.Canvas(parent, width=self.MARK_SIZE, height=self.MARK_SIZE,
                         bg=parent["bg"], highlightthickness=0, cursor="hand2")

    def _draw_mark(self, c, on, round_mark=False, hovered=False):
        marks.draw(c, on, round_mark, hovered)

    def _option(self, parent, text, on, action, round_mark=False,
                color="#d7dde5"):
        """A row with a mark and a label - the whole row is clickable."""
        row = tk.Frame(parent, bg=parent["bg"], cursor="hand2")
        row.pack(anchor="w", pady=2, fill="x")
        c = self._mark(row, round_mark)
        c.pack(side="left", padx=(0, 9))
        p = tk.Label(row, text=text, bg=parent["bg"], fg=color,
                     font=("Segoe UI", 10), cursor="hand2", justify="left")
        p.pack(side="left")
        row.mark, row.label, row.round_mark = c, p, round_mark
        row.on, row.hovered = on, False

        def redraw():
            self._draw_mark(row.mark, row.on, round_mark, row.hovered)

        def hover(hovering):
            row.hovered = hovering
            redraw()

        # the handlers tolerate being called without an event (e=None): while
        # a row is being destroyed an event can still reach a widget that is
        # going away, and the error would then pop up in the console with no
        # way to tell where it came from
        for w in (row, c, p):
            w.bind("<Button-1>", lambda e=None: action())
            w.bind("<Enter>", lambda e=None: hover(True))
            w.bind("<Leave>", lambda e=None: hover(False))
        row.redraw = redraw
        redraw()
        return row

    def _switch(self, parent, text, key, read=None, write=None):
        """A checkbox bound either to the config, or to a pair of functions."""
        v = tk.BooleanVar(value=read() if read else bool(CFG.get(key, False)))

        def changed():
            v.set(not v.get())
            if write:
                write(v.get())
            else:
                CFG[key] = v.get()
                save_cfg(CFG)
                log(f"Setting '{key}' = {CFG[key]}")

        t = self._option(parent, text, v.get(), changed)
        # the variable can also change from the tray menu - the mark redraws
        # itself, so the two paths cannot drift apart
        def sync(*_):
            t.on = v.get()
            t.redraw()
        v.trace_add("write", sync)
        # the key is stored on the widget; it used to be looked up by the
        # LABEL, and renaming a label broke that
        t.var, t.read, t.key = v, read, key
        return t

    def _select(self, parent, label, key, pairs, var=None, action=None):
        """A drop-down list - either bound to a settings key, or with its own
        variable and action (the pause, for example)."""
        if label:
            tk.Label(parent, text=label, bg=parent["bg"], fg="#9aa4b2",
                     font=("Segoe UI", 9)).pack(anchor="w", pady=(6, 0))
        labels = [p for p, _ in pairs]
        current = CFG.get(key) if key else None
        selected = next((p for p, h in pairs if h == current), labels[0])
        v = var or tk.StringVar()
        # When the caller brings its own variable with a valid value (the
        # language just set, say), keep it - otherwise the list would always
        # jump to the first item and show something other than what is
        # actually configured.
        if not (var is not None and v.get() in labels):
            v.set(selected)

        def changed(*_):
            for p, h in pairs:
                if p == v.get():
                    if action:
                        action(h)
                    else:
                        CFG[key] = h
                        save_cfg(CFG)
                        log(f"Setting '{key}' = {h}")
                    break

        m = tk.OptionMenu(parent, v, *labels, command=changed)
        # indicatoron=0 hides the default Tk rectangle, which refuses to be
        # styled; an arrow is placed on the right instead
        m.configure(bg="#232b36", fg="#e6ebf2", activebackground="#2f3743",
                    activeforeground="#fff", highlightthickness=0,
                    borderwidth=0, font=("Segoe UI", 10), anchor="w",
                    pady=6, padx=10, indicatoron=0)
        m["menu"].configure(bg="#232b36", fg="#e6ebf2", borderwidth=0,
                            activebackground="#2f3743", activeforeground="#fff",
                            font=("Segoe UI", 10))
        m.pack(anchor="w", fill="x", pady=(1, 4))
        arrow = tk.Label(m, text="▾", bg="#232b36", fg="#c3ccd8",
                         font=("Segoe UI", 14))
        arrow.place(relx=1.0, rely=0.5, anchor="e", x=-12)
        # clicking the arrow opens the menu directly - more reliable than
        # sending a synthetic event to the menubutton
        arrow.bind("<Button-1>", lambda e: m["menu"].post(
            m.winfo_rootx(), m.winfo_rooty() + m.winfo_height()))

    def _countdown_label(self):
        """The label in the countdown menu, following what is really set."""
        seconds = int(CFG.get("countdown_from_s", 10)) if CFG.get("countdown") else 0
        for label, value in self.countdown_pairs:
            if value == seconds:
                return label
        return self.countdown_pairs[0][0]

    def _set_countdown(self, seconds):
        CFG["countdown"] = seconds > 0
        if seconds:
            CFG["countdown_from_s"] = seconds
        save_cfg(CFG)
        log(f"Countdown: {seconds or 'do not show'}")

    def _change_language(self, code):
        """Switch the language and build the window again.

        Redrawing the individual labels would mean keeping a reference to
        every one of them, and one would get forgotten. Rebuilding the window
        is simpler and cannot leave half the text in the old language.
        """
        if code == texts.language():
            return
        CFG["language"] = code
        save_cfg(CFG)
        texts.set_language(code)
        log(f"Language switched to '{code}'.")
        span, tab = self.span, self.active_tab
        self.toggle()                      # close
        self.toggle()                      # and build again, now in the new language
        self.span = span
        self.span_var.set(span)
        self._tab(tab)

    def _pause(self, minutes):
        STATE.paused_until = time.monotonic() + minutes * 60 if minutes else 0.0
        log(f"Paused for {minutes} minutes." if minutes
            else "Pause cancelled.")

    def _open_phone(self):
        """Open the release page where the phone app can be downloaded.

        The installer used to ship the APK in a subfolder, but a link that
        always points at the latest release is more useful: the phone app
        gets updated separately from the desktop one.
        """
        os.startfile(PROJECT_URL + "/releases/latest")

    def _open_project(self):
        os.startfile(PROJECT_URL)

    def _open_manual(self):
        # in English open the English manual; when it is missing, Czech is
        # better than nothing. Looked up by pattern rather than by an exact
        # name, so a rename of the manual cannot silently break the link.
        patterns = (["*INFO-READ*.txt", "*INFO*.txt"] if texts.language() == "en"
                    else ["*INFO-CTI*.txt", "*INFO*.txt"])
        # installed: manuals sit next to the program; from source they
        # live in docs/ one level up
        for pattern in patterns:
            for folder in (PROGRAM, HERE.parent / "docs", HERE):
                manual = next(folder.glob(pattern), None)
                if manual:
                    os.startfile(manual)
                    return
        # Found nothing. Doing nothing at all is the worst answer to a click -
        # the same manuals are in the repository, so open that and write down
        # why, instead of leaving the user tapping a dead link.
        log("No manual found next to the program or in docs/ - "
            "opening the project page instead.")
        os.startfile(PROJECT_URL)

    def _refresh_settings(self):
        """Bring the tab in line with reality - settings can also be changed
        from the tray menu.

        The whole handler sits in try/except and reschedules itself EVEN after
        an error. Without that a single exception killed the loop and the
        device list was never refreshed again, even though the chart (a
        separate loop) kept drawing.
        """
        if self.win is None or not self.win.winfo_exists():
            return
        try:
            self._refresh_content()
        except Exception as error:
            log(f"Error while refreshing the settings: {error}")
        self.win.after(2000, self._refresh_settings)

    def _refresh_content(self):
        label = self._countdown_label()
        if self.countdown_var.get() != label:
            self.countdown_var.set(label)      # changed from the tray menu

        for t in (self.sw_active, self.sw_autostart, self.sw_idle_guard):
            actual = t.read() if t.read else bool(CFG.get(t.key, False))
            if t.var.get() != actual:
                t.var.set(actual)

        remaining = STATE.paused_until - time.monotonic()
        not_paused = tx("opt_not_paused")
        if remaining <= 0 and self.pause_var.get() != not_paused:
            self.pause_var.set(not_paused)     # the pause ran out on its own

        # The device list is only redrawn when it has changed. The signature
        # of what is drawn is kept DIRECTLY ON THE FRAME holding the list -
        # not in a property of the window. Closing the window destroys the
        # frame and the signature with it, so a new window starts with a clean
        # slate. When the signature was kept separately, it survived closing
        # the window: the new (empty) list was considered current and was
        # never drawn again.
        seen = STATE.nearby_list()
        # whether a record is fresh belongs in the signature too - otherwise
        # the greying out of "last seen X s ago" would only kick in on some
        # other change
        signature = (tuple((n, int(age) // 5) for n, _, age in seen)
                     + (CFG.get("target"),))
        if signature != getattr(self.devices_frame, "signature", None):
            self.devices_frame.signature = signature
            for w in self.devices_frame.winfo_children():
                w.destroy()
            current = CFG.get("target", "")
            if not seen:
                tk.Label(self.devices_frame,
                         text=tx("dev_none_heard"),
                         bg="#151920", fg="#8b95a3",
                         font=("Segoe UI", 9)).pack(anchor="w")
            for name, rssi, age in seen[:12]:
                is_target = bool(current) and current.lower() in name.lower()
                # A device stays in the list for a minute after it went quiet
                # (otherwise rarely advertising ones would flicker). But the
                # old value must not pretend to be current - that would look
                # as if the phone were advertising while it is switched off.
                fresh = age <= 10
                label = (tx("dev_dbm", name=name, rssi=rssi) if fresh
                         else tx("dev_last_heard", name=name, s=int(age)))
                color = ("#4ade80" if is_target else "#d7dde5") if fresh \
                    else "#79828f"
                self._option(self.devices_frame, label, is_target,
                             lambda n=name: self._select_target(n),
                             round_mark=True, color=color)
            if current and not any(current.lower() in n.lower()
                                   for n, _, _ in seen):
                tk.Label(self.devices_frame,
                         text=tx("dev_not_heard", name=current),
                         bg="#151920", fg="#fbbf24",
                         font=("Segoe UI", 10)).pack(anchor="w")

    def _select_target(self, name):
        if CFG.get("target") == name:
            return
        CFG["target"] = name
        save_cfg(CFG)
        STATE.target_changed()
        log(f"Watched device changed to '{name}'.")
        # so the selected row gets recoloured right away
        self.devices_frame.signature = None

    def _legend(self, parent):
        """The key under the chart - so the window explains itself."""
        bar = tk.Frame(parent, bg="#1b1f26")
        bar.pack(fill="x", padx=10, pady=(0, 9))

        def item(kind, color, label):
            frame = tk.Frame(bar, bg="#1b1f26")
            frame.pack(side="left", padx=(0, 16))
            swatch = tk.Canvas(frame, width=26, height=14, bg="#1b1f26",
                               highlightthickness=0)
            swatch.pack(side="left", padx=(0, 5))
            if kind == "band":
                swatch.create_rectangle(2, 2, 24, 12, fill=color, outline="")
            elif kind == "curve":
                swatch.create_line(2, 10, 9, 5, 17, 8, 24, 3, fill=color,
                                   width=2)
            elif kind == "vertical":
                swatch.create_line(13, 1, 13, 13, fill=color, width=2)
            elif kind == "dashed":
                swatch.create_line(2, 7, 24, 7, fill=color, dash=(4, 3))
            tk.Label(frame, text=label, fg="#9aa4b2", bg="#1b1f26",
                     font=("Segoe UI", 8)).pack(side="left")

        item("curve", "#7cc0ff", tx("leg_signal"))
        item("curve", "#79828f", tx("leg_weak"))
        item("band", "#2e2a1c", tx("leg_gap"))
        item("band", "#4a1f22", tx("leg_gap_lock"))
        item("band", "#2b3038", tx("leg_downtime"))
        item("vertical", "#ff5d5d", tx("leg_locked"))
        item("dashed", "#e8a33d", tx("leg_threshold"))

    def _change_span(self):
        self.span = self.span_var.get()
        self._draw()

    def _y(self, rssi, height):
        ratio = (rssi - self.RSSI_BOTTOM) / (self.RSSI_TOP - self.RSSI_BOTTOM)
        ratio = min(max(ratio, 0.0), 1.0)
        return self.MARGIN_T + (1 - ratio) * (height - self.MARGIN_T
                                              - self.MARGIN_B)

    def _draw(self):
        if self.win is None or not self.win.winfo_exists():
            return
        c = self.canvas
        c.delete("all")
        width, height = c.winfo_width(), c.winfo_height()
        if width < 50 or height < 50:
            self.win.after(500, self._draw)
            return

        now = time.monotonic()
        since = now - self.span
        with STATE.lock:
            points = [(t, r) for t, r in STATE.history if t >= since]
            locks = [t for t in STATE.locks if t >= since]
            downtime = [(max(a, since), b) for a, b in STATE.downtime
                        if b >= since]
        threshold = CFG.get("rssi_threshold")
        limit = float(CFG.get("silence_s", 20))

        left, right = self.MARGIN_L, width - self.MARGIN_R

        def x_at(t):
            return left + (t - since) / self.span * (right - left)

        # horizontal grid every 10 dBm
        for dbm in range(self.RSSI_TOP, self.RSSI_BOTTOM - 1, -10):
            y = self._y(dbm, height)
            c.create_line(left, y, right, y, fill="#242a33")
            c.create_text(left - 8, y, text=f"{dbm}", anchor="e",
                          fill="#6b7684", font=("Segoe UI", 8))
        c.create_text(14, self.MARGIN_T - 16, text="dBm", anchor="w",
                      fill="#6b7684", font=("Segoe UI", 8))

        # vertical grid every minute (every 30 s on a short range)
        step = (30 if self.span <= 120 else 60 if self.span <= 900 else
                300 if self.span <= 3600 else
                3600 if self.span <= 28800 else 10800)
        t = now - (now % step)
        while t > since:
            x = x_at(t)
            c.create_line(x, self.MARGIN_T, x, height - self.MARGIN_B,
                          fill="#242a33")
            ago = int(round(now - t))
            label = tx("chart_now") if ago < step / 2 else (
                f"−{ago // 3600} h" if ago >= 3600 else
                f"−{ago // 60} min" if ago >= 60 else f"−{ago} s")
            c.create_text(x, height - self.MARGIN_B + 14, text=label,
                          fill="#6b7684", font=("Segoe UI", 8))
            t -= step

        # Silence bands longer than 5 s. The start of the range only counts
        # when we really do have older records - otherwise the time before the
        # app was started would be drawn as a loss of signal.
        #
        # IMPORTANT: for the app "the signal is audible" is not the same as
        # "the phone is at the desk". With a threshold set, a weak packet does
        # not count - so the same rule as in State.record is applied here
        # (median of the window >= threshold), otherwise the chart would show
        # a continuous curve where the app counts silence.
        # Samples are merged per PIXEL. With the daily range there are tens of
        # thousands of them and drawing each one meant hundreds of thousands
        # of canvas objects on every refresh - that froze the chart and ate
        # hundreds of MB of memory. Several samples fall on one pixel anyway.
        # The bucket boundaries are anchored to ABSOLUTE time, not to the edge
        # of the window. When they were counted from "now", they shifted with
        # every refresh and samples hopped between neighbouring buckets - the
        # curve then flickered.
        span_px = max(1, int(right - left))
        step = self.span / span_px          # seconds per pixel
        base = int(since / step)
        buckets = [None] * span_px       # [min, max, time]
        for t, r in points:
            i = int(t / step) - base
            i = 0 if i < 0 else (span_px - 1 if i >= span_px else i)
            k = buckets[i]
            if k is None:
                buckets[i] = [r, r, t]
            else:
                if r < k[0]:
                    k[0] = r
                if r > k[1]:
                    k[1] = r
                k[2] = t

        # When the phone counts as "at the desk": with a threshold the
        # strongest sample in the pixel is enough (the app itself does the
        # exact evaluation, this is about showing it)
        strong = [k[2] for k in buckets if k is not None
                  and (threshold is None or k[1] >= float(threshold))]

        have_older = bool(STATE.history) and STATE.history[0][0] < since
        edges = ([since] if have_older else []) + strong + [now]

        def without_downtime(a, b):
            """Cut the stretches when the app was not running out of (a,b).
            Without this a stopped app would masquerade as the longest loss of
            signal."""
            parts = [(a, b)]
            for off_a, off_b in downtime:
                kept = []
                for x, y in parts:
                    if off_b <= x or off_a >= y:
                        kept.append((x, y))
                        continue
                    if x < off_a:
                        kept.append((x, off_a))
                    if off_b < y:
                        kept.append((off_b, y))
                parts = kept
            return parts

        def gap_threshold(a):
            """What still counts as a "normal" spacing of samples. Older data
            is thinned to ~10 s (see thin_history), so with a 5 s threshold it
            would look like one continuous dropout."""
            return 5 if now - a <= HIST_DETAILED else HIST_STEP * 2

        self.longest_gap = 0.0
        for i in range(1, len(edges)):
            for a, b in without_downtime(edges[i - 1], edges[i]):
                if b - a > gap_threshold(a):
                    color = "#4a1f22" if b - a >= limit else "#2e2a1c"
                    c.create_rectangle(x_at(a), self.MARGIN_T, x_at(b),
                                       height - self.MARGIN_B, fill=color,
                                       outline="")
                if b - a > gap_threshold(a):
                    self.longest_gap = max(self.longest_gap, b - a)

        # the time the app was not running - drawn OVER the silence bands
        for a, b in downtime:
            c.create_rectangle(x_at(a), self.MARGIN_T, x_at(b),
                               height - self.MARGIN_B, fill="#2b3038",
                               outline="")
            if x_at(b) - x_at(a) > 80:
                c.create_text((x_at(a) + x_at(b)) / 2,
                              (self.MARGIN_T + height - self.MARGIN_B) / 2,
                              text=tx("chart_downtime"), fill="#8b95a3",
                              font=("Segoe UI", 9))

        # sensitivity threshold
        if threshold is not None:
            y = self._y(float(threshold), height)
            c.create_line(left, y, right, y, fill="#e8a33d", dash=(5, 4))
            c.create_text(right - 4, y - 9, text=tx("chart_threshold", v=threshold),
                          anchor="e", fill="#e8a33d", font=("Segoe UI", 8))

        # The signal itself - one vertical stroke per pixel (from the weakest
        # to the strongest sample). Neighbouring pixels are joined unless
        # there is a hole between them. Grey = below the threshold, so it does
        # not count as "at the desk".
        prev = None
        for i, k in enumerate(buckets):
            if k is None:
                continue          # an empty pixel does not break the line
            x = left + i
            ymin, ymax = self._y(k[1], height), self._y(k[0], height)
            counts = threshold is None or k[1] >= float(threshold)
            color = "#7cc0ff" if counts else "#79828f"
            if ymax - ymin >= 1:
                c.create_line(x, ymin, x, ymax, fill=color)
            else:
                c.create_oval(x - 1, ymin - 1, x + 1, ymin + 1,
                              fill=color, outline="")
            mid = (ymin + ymax) / 2
            # Whether the curve breaks is decided by the TIME between samples,
            # not by whether the pixels are adjacent. On a short range a pixel
            # covers a fraction of a second, so perhaps only every fifth one
            # is occupied - without this the curve would be left as scattered
            # dots.
            if prev is not None and k[2] - prev[2] <= gap_threshold(prev[2]):
                c.create_line(prev[0], prev[1], x, mid,
                              fill="#4ea3ff" if counts else "#5c6470")
            prev = (x, mid, k[2])

        for t in locks:
            x = x_at(t)
            c.create_line(x, self.MARGIN_T, x, height - self.MARGIN_B,
                          fill="#ff5d5d", width=2)
            c.create_text(x, self.MARGIN_T - 8, text=tx("chart_lock"),
                          fill="#ff5d5d", font=("Segoe UI", 8))

        if points:
            rs = [r for _, r in points]  # only min/max/median, no drawing
            self.info.config(text=tx(
                "chart_summary", count=len(points),
                per_min=f"{len(points) / self.span * 60:.0f}",
                median=int(median(rs)), silence=f"{self.longest_gap:.0f}"))
        else:
            self.info.config(text=tx("chart_no_signal"))

        # the longer the range, the less often - in the daily view nothing
        # visible changes in a second anyway
        self.win.after(1000 if self.span <= 900 else
                       3000 if self.span <= 3600 else 10000, self._draw)


# ---------------------------------------------------------------- tray icon

def icon_image(color):
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse((6, 6, 58, 58), fill=color)
    d.rectangle((24, 30, 40, 46), fill="white")
    d.arc((24, 18, 40, 38), 180, 360, fill="white", width=5)
    return img


COLORS = {"ok": (34, 160, 80), "countdown": (230, 140, 20),
          "off": (120, 120, 120)}


class TrayIcon:
    def __init__(self, root, countdown, chart):
        self.root = root
        self.countdown = countdown
        self.chart = chart
        self.state = "ok"
        self._signature = None
        self._menu_at = 0.0
        self.icon = pystray.Icon("dyn_lock", icon_image(COLORS["ok"]), APP_NAME,
                                 menu=self._menu())

    def _menu(self):
        def toggle_key(key):
            def f(icon, item):
                CFG[key] = not CFG.get(key, False)
                save_cfg(CFG)
                log(f"Setting '{key}' = {CFG[key]}")
                if key == "countdown" and not CFG[key]:
                    self.root.after(0, self.countdown.hide)
            return f

        def is_on(key):
            return lambda item: bool(CFG.get(key, False))

        def pause_for(minutes, label):
            def f(icon, item):
                STATE.paused_until = time.monotonic() + minutes * 60
                log(f"Paused for {label}.")
            return f

        # The item texts are functions, not finished strings: pystray calls
        # them when the menu is opened, so a language switch takes effect on
        # its own and half the menu cannot stay in the original language.
        pause_menu = pystray.Menu(*[
            pystray.MenuItem((lambda k: lambda i: tx(k))(key),
                             pause_for(minutes, tx(key)))
            for key, minutes in [("opt_5_min", 5), ("opt_15_min", 15),
                                 ("opt_30_min", 30), ("opt_1_hour", 60),
                                 ("opt_2_hours", 120), ("opt_4_hours", 240),
                                 ("opt_12_hours", 720), ("opt_1_day", 1440),
                                 ("opt_2_days", 2880)]])

        def clear_pause(icon, item):
            STATE.paused_until = 0.0
            log("Pause cancelled.")

        def quit_app(icon, item):
            log("Quit by the user.")
            icon.stop()
            self.root.after(0, self.root.destroy)

        def set_value(key, value):
            def f(icon, item):
                CFG[key] = value
                save_cfg(CFG)
                log(f"Setting '{key}' = {value}")
            return f

        def is_value(key, value):
            return lambda item: CFG.get(key) == value

        time_menu = pystray.Menu(*[
            pystray.MenuItem((lambda v: lambda i: tx("tray_seconds", s=v))(s),
                             set_value("silence_s", s),
                             checked=is_value("silence_s", s), radio=True)
            for s in (12, 15, 20, 30, 45, 60, 90, 120)])

        # How long before locking the countdown box appears.
        def set_countdown(seconds):
            def f(icon, item):
                CFG["countdown"] = seconds > 0
                if seconds:
                    CFG["countdown_from_s"] = seconds
                save_cfg(CFG)
                log(f"Countdown: {seconds or 'do not show'}")
            return f

        def countdown_is(seconds):
            return lambda item: (bool(CFG.get("countdown")) == bool(seconds)
                                 and (not seconds
                                      or CFG.get("countdown_from_s") == seconds))

        countdown_menu = pystray.Menu(
            pystray.MenuItem(lambda i: tx("opt_countdown_off"), set_countdown(0),
                             checked=countdown_is(0), radio=True),
            pystray.Menu.SEPARATOR,
            *[pystray.MenuItem((lambda v: lambda i: tx("tray_seconds_ahead", s=v))(s),
                               set_countdown(s), checked=countdown_is(s),
                               radio=True)
              for s in (5, 10, 15, 20, 30)])

        # Threshold of the smoothed RSSI in 5 dBm steps. A lower number means
        # even a weak signal still counts as "at the desk" = a longer reach.
        # Measured: phone on the desk -73, in a pocket at the desk -81 to
        # -83 dBm.
        values = [-100, -95, -90, -85, -80, -75, -70, -65, -60]
        current = CFG.get("rssi_threshold")
        if current is not None and current not in values:
            values = sorted(values + [current])   # show a hand-entered one too

        def threshold_label(v):
            if v == values[0]:
                return tx("tray_threshold_max", v=v)
            if v == values[-1]:
                return tx("tray_threshold_min", v=v)
            if v == -80:
                return tx("tray_threshold_edge", v=v)
            return tx("tray_threshold_plain", v=v)

        # After how long without a signal to warn with a balloon that the app
        # is no longer locking
        alert_menu = pystray.Menu(
            pystray.MenuItem(lambda i: tx("opt_no_warning"),
                             set_value("alert_no_signal_min", 0),
                             checked=is_value("alert_no_signal_min", 0),
                             radio=True),
            pystray.Menu.SEPARATOR,
            *[pystray.MenuItem((lambda v: lambda i: tx("tray_minutes", m=v))(m),
                               set_value("alert_no_signal_min", m),
                               checked=is_value("alert_no_signal_min", m),
                               radio=True)
              for m in (1, 2, 5, 10, 20, 30, 60)])

        sensitivity_menu = pystray.Menu(
            pystray.MenuItem(lambda i: tx("tray_threshold_off"),
                             set_value("rssi_threshold", None),
                             checked=is_value("rssi_threshold", None), radio=True),
            pystray.Menu.SEPARATOR,
            *[pystray.MenuItem(
                  (lambda h: lambda i: threshold_label(h).replace("-", "−"))(v),
                  set_value("rssi_threshold", v),
                  checked=is_value("rssi_threshold", v), radio=True)
              for v in values])

        def toggle_autostart(icon, item):
            autostart_set(not autostart_enabled())

        def open_chart(icon, item):
            # The window has to be created on the main thread, the tray icon
            # runs on its own
            self.root.after(0, self.chart.toggle)

        # Picking the watched phone from the devices currently audible.
        # Typing the name by hand is not needed - and cannot be mistyped.
        def pick_target(name):
            def f(icon, item):
                if CFG.get("target") == name:
                    return
                CFG["target"] = name
                save_cfg(CFG)
                STATE.target_changed()
                log(f"Watched device changed to '{name}'.")
            return f

        items = []
        current_target = CFG.get("target", "")
        seen = [(n, r) for n, r, age in STATE.nearby_list() if age <= 10]

        def matches_target(name):
            """The same rule as when searching: the target is a PART of the
            name, not the whole name. Without that a target of 'DaKing' would
            not match 'Xiaomi 15 DaKing' and the menu would claim the phone is
            not audible."""
            return bool(current_target) and current_target.lower() in name.lower()

        if current_target and not any(matches_target(n) for n, _ in seen):
            # the current target is out of range right now - keep it in the menu
            items.append(pystray.MenuItem(
                lambda i: tx("dev_not_heard", name=current_target),
                pick_target(current_target), checked=lambda i: True, radio=True))
        for name, rssi in seen[:12]:
            items.append(pystray.MenuItem(
                (lambda n, r: lambda i: tx("dev_dbm", name=n, rssi=r))(name, rssi),
                pick_target(name),
                checked=(lambda n: lambda i: matches_target(n))(name), radio=True))
        if not items:
            items.append(pystray.MenuItem(lambda i: tx("tray_none_heard"), None,
                                          enabled=False))
        devices_menu = pystray.Menu(*items)

        return pystray.Menu(
            # default=True => a left click on the tray icon opens the chart
            pystray.MenuItem(lambda i: tx("tray_chart"), open_chart, default=True),
            pystray.MenuItem(lambda i: tx("tray_phone"), devices_menu),
            pystray.Menu.SEPARATOR,
            # the switch labels are the same as in the settings window (same
            # key) - otherwise each place would call the same thing differently
            pystray.MenuItem(lambda i: tx("sw_active"), toggle_key("active"),
                             checked=is_on("active")),
            pystray.MenuItem(lambda i: tx("sw_idle_guard"),
                             toggle_key("idle_guard"),
                             checked=is_on("idle_guard")),
            pystray.MenuItem(lambda i: tx("sw_autostart"), toggle_autostart,
                             checked=lambda item: autostart_enabled()),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem(lambda i: tx("tray_time"), time_menu),
            pystray.MenuItem(lambda i: tx("tray_countdown"), countdown_menu),
            pystray.MenuItem(lambda i: tx("tray_sensitivity"), sensitivity_menu),
            pystray.MenuItem(lambda i: tx("tray_alerts"), alert_menu),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem(lambda i: tx("tray_pause"), pause_menu),
            pystray.MenuItem(lambda i: tx("tray_clear_pause"), clear_pause,
                             visible=lambda item:
                                 STATE.paused_until > time.monotonic()),
            pystray.MenuItem(lambda i: tx("tray_quit"), quit_app),
        )

    def refresh_menu(self):
        """Rebuild the menu so the device list matches reality.

        This happens when the set of devices changes, otherwise at most once
        every 10 s - the signal strength changes all the time and rebuilding
        the menu every second would be pointless.
        """
        signature = (tuple(n for n, _, age in STATE.nearby_list() if age <= 10)
                     + (CFG.get("target"),))
        now = time.monotonic()
        if signature == self._signature and now - self._menu_at < 10:
            return
        self._signature, self._menu_at = signature, now
        try:
            self.icon.menu = self._menu()
            self.icon.update_menu()
        except Exception as e:
            log(f"Could not refresh the menu: {e}")

    def notify(self, text):
        try:
            self.icon.notify(text, APP_NAME)
        except Exception as e:          # some systems cannot do balloons
            log(f"Could not show the notification: {e}")

    def set_state(self, state, label):
        if state != self.state:
            self.state = state
            self.icon.icon = icon_image(COLORS[state])
        self.icon.title = f"{APP_NAME} — {label}"

    def start(self):
        threading.Thread(target=self.icon.run, daemon=True).start()


# ---------------------------------------------------------------- main loop

def decide(cfg, silence, armed, pause_left, idle):
    """Pure decision function - no GUI, no locking.

    Returns (action, label, remaining_s, reason), where action is one of:
      'none'      - all quiet, nothing is happening
      'countdown' - locking is near, show the countdown
      'lock'      - lock now
      'stop'      - the app is off / paused / waiting for a signal

    `reason` is a KEY (e.g. "idle_guard", "at_desk") - callers decide by it.
    Never by `label`, that one gets translated.

    Kept apart from the loop on purpose, so it can be tested without locking
    the screen (see tests/test_logic.py).
    """
    if not cfg.get("active", True):
        return "stop", tx("st_off"), 0, "off"
    if pause_left > 0:
        return "stop", tx("st_paused", minutes=int(pause_left) // 60 + 1), 0, "paused"
    if silence is None:
        return "stop", tx("st_waiting"), 0, "waiting"
    if not armed:
        return "stop", tx("st_locked_wait"), 0, "locked"

    limit = float(cfg.get("silence_s", 20))
    remaining = int(round(limit - silence))

    # The idle guard is evaluated BEFORE the countdown. When we already know
    # there will be no locking, there is no point in scaring the user with a
    # countdown that ends in nothing.
    idle_guard = (cfg.get("idle_guard", False)
                  and idle < float(cfg.get("idle_guard_s", 15)))

    if silence >= limit:
        if idle_guard:
            return "none", tx("st_guard"), 0, "idle_guard"
        return "lock", tx("st_locking"), 0, "locking"

    if cfg.get("countdown", False) and remaining <= int(cfg.get("countdown_from_s", 10)):
        if idle_guard:
            return "none", tx("st_guard_countdown"), 0, "idle_guard"
        return "countdown", tx("st_countdown", s=remaining), max(remaining, 0), \
            "countdown"

    return "none", tx("st_at_desk"), remaining, "at_desk"


_previous_action = None
_saved_at = 0.0
_alerted = False         # we have already reported a long silence


def watch_signal_loss(tray):
    """Warn when the phone has stopped advertising altogether.

    Why: after locking, the app waits for the phone to come back. When the
    phone never speaks up again (typically flight mode overnight - it turns
    bluetooth off and nRF Connect does not restart the advertiser on its own),
    the laptop is no longer locked and the user has no idea. Better to warn
    once than to silently stop guarding.
    """
    global _alerted
    minutes = float(CFG.get("alert_no_signal_min", 10))
    silence = STATE.silence()
    if silence is None or minutes <= 0:
        return
    if silence > minutes * 60:
        if not _alerted:
            _alerted = True
            log(f"Phone has not advertised for {silence / 60:.0f} min "
                f"- warning the user.")
            tray.notify(tx("msg_no_signal", minutes=f"{silence / 60:.0f}"))
    elif _alerted:
        _alerted = False
        log("Phone is advertising again - watching resumed.")


def main_loop(root, countdown, tray):
    global _previous_action
    try:
        pause = max(0.0, STATE.paused_until - time.monotonic())
        action, label, remaining, reason = decide(
            CFG, STATE.silence(), STATE.armed, pause, idle_seconds())

        # The start and the cancellation of a countdown are logged, so it can
        # be verified afterwards that a lock really was preceded by a warning.
        if action == "countdown" and _previous_action != "countdown":
            log(f"Countdown started - {remaining} s left "
                f"(last RSSI {STATE.rssi} dBm).")
        elif _previous_action == "countdown" and action == "none":
            log("Countdown cancelled - the phone is back at the desk.")
        _previous_action = action

        if CFG.get("active", True):
            watch_signal_loss(tray)
        tray.refresh_menu()

        # The history is saved as we go, so it survives a restart of the app
        # and of the machine
        global _saved_at
        if time.monotonic() - _saved_at > 30:
            _saved_at = time.monotonic()
            save_history()

        if action == "lock":
            silence = STATE.silence()
            log(f"Locking (silence {silence:.0f} s, "
                f"last RSSI {STATE.rssi} dBm).")
            with STATE.lock:
                STATE.armed = False
                STATE.locks.append(time.monotonic())
            countdown.hide()
            tray.set_state("off", tx("st_locked"))
            if DRY_RUN:
                log("  (dry run - the screen is not really locked)")
            else:
                lock_screen()
        elif action == "countdown":
            countdown.show(remaining)
            tray.set_state("countdown", label)
        else:
            countdown.hide()
            # BEWARE: never decide by the text of the label - that gets
            # translated. It used to say `"pojistka" not in popis`, which in
            # English was never true, so the icon stayed green even while
            # locking was blocked.
            guard_blocks = reason == "idle_guard"
            state = "ok" if action == "none" and not guard_blocks else "off"
            if action == "none" and STATE.rssi is not None and not guard_blocks:
                label = f"{label} ({STATE.rssi} dBm)"
            tray.set_state(state, label)
    except Exception as e:
        log(f"Error in the main loop: {e}")
    root.after(500, main_loop, root, countdown, tray)


def main():
    # This is how the installer asks for the task to be created - so that ONE
    # piece of code registers it and the two cannot drift apart
    if "--autostart-on" in sys.argv or "--autostart-off" in sys.argv:
        autostart_set("--autostart-on" in sys.argv)
        return 0

    if already_running():
        log("The app is already running - not starting a second instance.")
        if not NO_DIALOG:
            user32.MessageBoxW(
                0,
                tx("msg_already_running"),
                APP_NAME, 0x40)
        return 0

    log("=" * 50)
    mode = "  [DRY RUN - does not lock]" if DRY_RUN else ""
    log(f"Start. Target '{CFG['target']}', lock after {CFG['silence_s']} s "
        f"of silence.{mode}")

    load_history()
    threading.Thread(target=scanner_thread, daemon=True).start()

    root = tk.Tk()
    root.withdraw()
    countdown = Countdown(root)
    chart = Chart(root)
    tray = TrayIcon(root, countdown, chart)
    tray.start()
    root.after(500, main_loop, root, countdown, tray)
    if QUIT_AFTER:
        root.after(int(QUIT_AFTER * 1000), root.destroy)
    root.mainloop()
    save_history()           # so it is visible when the app ended
    log("Ended.")


if __name__ == "__main__":
    sys.exit(main())
