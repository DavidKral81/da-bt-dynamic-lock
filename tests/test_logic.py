"""
Test of the decision logic - no GUI and no screen locking.

The last test replays REAL measured data from track_log.csv (a walk down the
corridor at 12:48) and verifies that the app would lock exactly once, and only
once out in the corridor.

Run:  py tests/test_logic.py
"""

import sys
from pathlib import Path

# the app lives one level up in windows/
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "windows"))

from dyn_lock import decide

HERE = Path(__file__).resolve().parent
CFG = {"active": True, "silence_s": 20, "countdown": False,
       "countdown_from_s": 10, "idle_guard": False, "idle_guard_s": 15}

failures = []


def check(description, expected, actual):
    ok = expected == actual
    print(f"  {'OK ' if ok else 'FAIL'}  {description}: {actual}"
          + ("" if ok else f"  (expected {expected})"))
    if not ok:
        failures.append(description)


print("Basic situations:")
check("all quiet, phone audible", "none", decide(CFG, 2, True, 0, 99)[0])
check("silence 19 s (not yet)", "none", decide(CFG, 19, True, 0, 99)[0])
check("silence 20 s (now yes)", "lock", decide(CFG, 20, True, 0, 99)[0])
check("app switched off", "stop",
      decide({**CFG, "active": False}, 60, True, 0, 99)[0])
check("paused from the tray", "stop", decide(CFG, 60, True, 300, 99)[0])
check("phone never seen yet", "stop", decide(CFG, None, True, 0, 99)[0])
check("already locked, waiting for the return", "stop",
      decide(CFG, 300, False, 0, 99)[0])

print("\nCountdown (when enabled):")
C = {**CFG, "countdown": True}
check("silence 9 s - do not show yet", "none", decide(C, 9, True, 0, 99)[0])
check("silence 11 s - show", "countdown", decide(C, 11, True, 0, 99)[0])
check("correct number of seconds left", 5, decide(C, 15, True, 0, 99)[2])
check("countdown disabled = nothing", "none", decide(CFG, 15, True, 0, 99)[0])

print("\nIdle guard:")
P = {**CFG, "idle_guard": True}
check("typing right now - do not lock", "none", decide(P, 60, True, 0, 3)[0])
check("nothing happening at the PC - lock", "lock",
      decide(P, 60, True, 0, 60)[0])
check("guard disabled - lock even while working", "lock",
      decide(CFG, 60, True, 0, 3)[0])
# When the guard prevents locking anyway, the countdown must not show either
PO = {**CFG, "idle_guard": True, "countdown": True}
check("while working do NOT show the countdown", "none",
      decide(PO, 15, True, 0, 3)[0])
check("without work show the countdown", "countdown",
      decide(PO, 15, True, 0, 60)[0])
check("countdown hides while working even just before locking", "none",
      decide(PO, 19.5, True, 0, 1)[0])

print("\nBehind the lock screen:")
# 19.-22.08.2026: 41 of 68 "Locking" lines in the log belonged to a screen that
# was already locked (cross-checked against the Winlogon event log). Every one
# of them also drew a countdown box behind the lock screen, where nobody could
# see it.
check("locked screen: no countdown, no locking", "stop",
      decide(C, 300, True, 0, 99, True)[0])
check("...and it says why", "screen_locked",
      decide(C, 300, True, 0, 99, True)[3])
check("locked screen beats an otherwise certain lock", "stop",
      decide(CFG, 300, True, 0, 99, True)[0])
# ...and the same situation unlocked MUST lock, or the check above proves
# nothing at all - it would pass just as well on an app that never locks.
check("the same moment unlocked does lock", "lock",
      decide(CFG, 300, True, 0, 99, False)[0])
check("switched off still wins over the lock screen", "off",
      decide({**CFG, "active": False}, 300, True, 0, 99, True)[3])

print("\nTelling a locked screen apart (OpenInputDesktop):")
import dyn_lock as _D

# The real call cannot be exercised from a test - it would need the screen
# actually locked. What CAN be tested is the reading of the answer, which is
# the part that can be wrong. ERROR_ACCESS_DENIED is the refusal Windows gives
# while the secure desktop is in front.
_original = _D._input_desktop_error
try:
    _D._input_desktop_error = lambda: 0
    check("the desktop is ours = not locked", False, _D.session_locked())
    _D._input_desktop_error = lambda: 5          # ERROR_ACCESS_DENIED
    check("access denied = locked", True, _D.session_locked())
    # Any other failure must NOT be read as "locked": that would switch the
    # watching off for good and the app would never lock anything again.
    _D._input_desktop_error = lambda: 6          # ERROR_INVALID_HANDLE
    check("an unrelated error keeps watching alive", False, _D.session_locked())
finally:
    _D._input_desktop_error = _original
check("...and the real check answers on a live desktop", False,
      _D.session_locked())

print("\nAfter a gap in the loop (sleep, hibernation):")
import time as _time
from dyn_lock import STATE, tick_gap, STALL_S

# Telling an ordinary late tick from a machine that was not running at all.
tick_gap(1000.0)                       # the first tick only sets the clock
check("an ordinary tick is not a gap", False, tick_gap(1000.5) > STALL_S)
check("a tick 11 minutes late IS a gap", True, tick_gap(1690.5) > STALL_S)

# The point of it: silence that piled up while nobody was measuring must not
# lock the screen the moment the lid opens. Measured on 20.08.2026 - an 11.5
# minute sleep ended with "Locking (silence 0 s)" and no countdown at all.
STATE.was_near = True
STATE.near_at = _time.monotonic() - 700
check("stale silence from the sleep would lock", "lock",
      decide(CFG, STATE.silence(), STATE.armed, 0, 99)[0])
STATE.restart_measurement()
check("...and restarting the measurement stops it", "none",
      decide(CFG, STATE.silence(), STATE.armed, 0, 99)[0])
check("the phone stays known, so guarding does not stop", False,
      STATE.silence() is None)
# ...but a phone that really is gone still locks, after the full delay
STATE.near_at = _time.monotonic() - 60
check("a phone that really is gone still locks", "lock",
      decide(CFG, STATE.silence(), STATE.armed, 0, 99)[0])
# a phone never seen at all stays never seen - no pretending it was here
STATE.was_near = False
STATE.restart_measurement()
check("a phone never seen stays 'never seen'", None, STATE.silence())

# The measured data is not versioned (several MB of raw samples), so a clean
# clone of the project does not have it. The test has to survive that - it
# used to fail on FileNotFoundError before it even got to its own message
# about skipping.
MEASUREMENTS = HERE / "track_log.csv"


def _lines():
    if not MEASUREMENTS.exists():
        return []
    return [l for l in MEASUREMENTS.read_text(encoding="utf-8").splitlines()[1:]
            if l.strip()]


print("\nReplay of the real measurement (track_log.csv):")
times = [float(l.split(";")[0]) for l in _lines()]
if len(times) < 100:
    print("  SKIPPED - track_log.csv is missing or holds no walking measurement")
else:
    locks, armed, seen, i = [], True, None, 0
    for t in [x / 10 for x in range(0, 3001)]:      # simulation every 0.1 s
        while i < len(times) and times[i] <= t:
            seen, armed, i = times[i], True, i + 1
        silence = None if seen is None else t - seen
        if decide(CFG, silence, armed, 0, 99)[0] == "lock":
            locks.append(t)
            armed = False
    print(f"  locks in total: {len(locks)}  at: {[round(z) for z in locks]}")
    check("locked exactly once", 1, len(locks))
    if locks:
        check("locked out in the corridor (between 130 and 240 s)", True,
              130 < locks[0] < 240)

print("\nIdle measurement (trap: the 32bit tick counter wraps after 24.85 days):")
from dyn_lock import idle_seconds
n = idle_seconds()
print(f"  idle_seconds() = {n:.1f} s")
check("not negative", True, n >= 0)
check("within a sane range (< 30 days)", True, n < 30 * 24 * 3600)

print("\nSensitivity (RSSI threshold) on real data:")
samples = [(float(l.split(';')[0]), int(l.split(';')[1])) for l in _lines()]
if len(samples) < 100:
    print("  SKIPPED - track_log.csv is missing or holds no walking measurement")
else:
    from statistics import median as med

    def replay(threshold, silence_s=20, window=6):
        """An exact copy of the State.record + decide logic, just without
        threads."""
        cfg = {**CFG, "silence_s": silence_s}
        locks, near, was_near, armed, i, buf = [], 0.0, False, True, 0, []
        for step in range(3001):
            t = step / 10
            while i < len(samples) and samples[i][0] <= t:
                buf.append(samples[i])
                buf[:] = [x for x in buf if t - x[0] <= window]
                if threshold is None or med([r for _, r in buf]) >= threshold:
                    near, was_near, armed = samples[i][0], True, True
                i += 1
            silence = None if not was_near else t - near
            if decide(cfg, silence, armed, 0, 99)[0] == "lock":
                locks.append(t)
                armed = False
        return locks

    for threshold in (None, -90, -86, -83):
        z = replay(threshold)
        label = "off" if threshold is None else f"{threshold} dBm"
        print(f"  threshold {label:>9}: locks {len(z):2}x  at {[round(x) for x in z]}")

    check("no threshold: 1 lock", 1, len(replay(None)))
    check("threshold -90 (long reach): still 1 lock", 1, len(replay(-90)))
    check("threshold -83 (short reach): locks sooner or the same",
          True, replay(-83)[0] <= replay(None)[0])

print("\nMatching the watched device:")
from dyn_lock import matches


class _Dev:
    address = "AA:BB:CC:DD:EE:FF"


class _Adv:
    def __init__(self, name="Neighbour's fridge", uuids=()):
        self.local_name = name
        self.service_uuids = list(uuids)


# Found 18.08.2026: an empty target matched EVERYTHING, because "" is a
# substring of every name. Any BLE device around then counted as the phone -
# the chart recorded it and the screen would not lock while anything was
# audible.
check("no target picked = nothing counts", False,
      matches(_Dev(), _Adv(), ""))
check("part of the name matches", True,
      matches(_Dev(), _Adv("Xiaomi 15 DaKing"), "DaKing"))
check("a different name does not match", False,
      matches(_Dev(), _Adv("Xiaomi 15 DaKing"), "Pixel"))
check("a MAC address matches", True,
      matches(_Dev(), _Adv(), "AA:BB:CC:DD:EE:FF"))
check("an unnamed device does not match a name target", False,
      matches(_Dev(), _Adv(None), "DaKing"))

print("\nTranslations:")
import re
import texts
check("no key is missing in Czech or English", [], texts.missing())

# The phone carries its own dictionary with both languages side by side. It
# had a missing() of its own, but nothing ever called it - so a key that fell
# out of English would surface as a Czech sentence in the English interface
# and nobody would find out. Read straight out of the source: every entry is
# one put("key", ...), so this needs no Java runtime.
JAVA_TEXTS = (HERE.parent / "phone" / "src" / "java" / "cz" / "david"
              / "dabtdynamiclock" / "Texts.java")
java = JAVA_TEXTS.read_text(encoding="utf-8")
phone = {lang: set(re.findall(lang + r'\.put\("([^"]+)"', java))
         for lang in ("CZECH", "ENGLISH")}
# A typo in the parsing would leave both sets empty and the check would pass
# on nothing at all - the hollow test this project has been bitten by before.
check("phone: the dictionary was read at all", True, len(phone["CZECH"]) > 20)
check("phone: no key is missing in Czech or English", set(),
      phone["CZECH"] ^ phone["ENGLISH"])

print("\nShipped default settings:")
# dyn_lock.py claimed in a comment that a test kept DEFAULTS and
# config.default.json in step. No such test existed, and the two had already
# drifted (window_geometry, window_maximised). Now the claim is true.
import json
from dyn_lock import DEFAULTS

# Runtime state the app writes for itself - it has no place in the template
# handed to a new user, so it is excluded on purpose.
RUNTIME_ONLY = {"window_geometry", "window_maximised"}

shipped = json.loads(
    (HERE.parent / "windows" / "config.default.json").read_text(encoding="utf-8"))
# Keys starting with "_" are the comments inside the template, not settings.
shipped_keys = {k for k in shipped if not k.startswith("_")}

check("template has every default", set(),
      set(DEFAULTS) - RUNTIME_ONLY - shipped_keys)
check("template invents nothing extra", set(),
      shipped_keys - set(DEFAULTS))
check("template values match the defaults", [],
      [k for k in shipped_keys if shipped[k] != DEFAULTS[k]])

print("\n" + ("ALL OK" if not failures else f"FAILURES: {failures}"))
# Without this the test ended with code 0 even when there were findings - a
# failure would slip through the build unnoticed.
raise SystemExit(1 if failures else 0)
