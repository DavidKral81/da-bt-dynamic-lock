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
import texts
check("no key is missing in Czech or English", [], texts.missing())

print("\n" + ("ALL OK" if not failures else f"FAILURES: {failures}"))
# Without this the test ended with code 0 even when there were findings - a
# failure would slip through the build unnoticed.
raise SystemExit(1 if failures else 0)
