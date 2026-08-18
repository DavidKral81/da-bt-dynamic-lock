"""
Da BT Dynamic Lock - evaluation of the measurement in track_log.csv.

It splits the recording into stretches following the walking scenario and
works out whether "at the desk" and "in the corridor" can be told apart by
RSSI - and with what threshold.

Run:     py tools/analyze.py [path to track_log.csv]
Output:  _output/analysis.txt (UTF-8)

Without an argument it evaluates the reference recording in tests/, the same
one test_logic.py replays. A fresh measurement from tools/track.py lands in
_output/, so pass that path to look at it.
"""

import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
LOG = (Path(sys.argv[1]) if len(sys.argv) > 1
       else HERE.parent / "tests" / "track_log.csv")
OUT = HERE.parent / "_output" / "analysis.txt"
OUT.parent.mkdir(exist_ok=True)

# (from_s, to_s, description, should_stay_unlocked)
PHASES = [
    (0, 60, "at the laptop (phone in a pocket)", True),
    (60, 120, "a few steps from the desk (~3 m)", True),
    (120, 180, "through the door, in the corridor", False),
    (180, 240, "further down the corridor", False),
    (240, 300, "back at the laptop", True),
]


def median(v):
    s = sorted(v)
    n = len(s)
    return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2


def main():
    rows, invalid = [], 0
    for line in LOG.read_text(encoding="utf-8").splitlines()[1:]:
        if not line.strip():
            continue
        t, r = line.split(";")
        # -127 dBm = "RSSI unknown" (a placeholder from the BLE stack), not a
        # measurement
        if int(r) <= -120:
            invalid += 1
            continue
        rows.append((float(t), int(r)))

    out = [f"Measurement evaluation - {len(rows)} valid advertisements"]
    if invalid:
        out.append(f"(dropped {invalid} records with RSSI -127 = unknown value)")
    out.append("")

    near, far = [], []
    for a, b, description, unlocked in PHASES:
        seg = [(t, r) for t, r in rows if a <= t < b]
        out.append(f"[{a:3}-{b:3}s] {description}")
        if not seg:
            out.append("    NO ADVERTISEMENT - the phone did not advertise "
                       "at all in this stretch")
            out.append("")
            continue
        rs = [r for _, r in seg]
        ts = [t for t, _ in seg]
        gaps = [ts[i] - ts[i - 1] for i in range(1, len(ts))]
        out.append(f"    advertisements: {len(seg)}   RSSI min {min(rs)} / "
                   f"median {median(rs):.0f} / max {max(rs)} dBm")
        if gaps:
            out.append(f"    gaps          : median {median(gaps):.1f}s   "
                       f"MAX {max(gaps):.1f}s")
        out.append("")
        (near if unlocked else far).extend(rs)

    out.append("=" * 55)
    if not near or not far:
        out.append("Data for one of the states is missing - no threshold can "
                   "be determined.")
    else:
        # the threshold is chosen so that as few samples as possible end up on
        # the wrong side
        best, best_err = None, 10 ** 9
        for threshold in range(min(near + far) - 1, max(near + far) + 2):
            err = (sum(1 for r in near if r < threshold)
                   + sum(1 for r in far if r >= threshold))
            if err < best_err:
                best, best_err = threshold, err
        fp = sum(1 for r in near if r < best)   # would lock at the desk
        fn = sum(1 for r in far if r >= best)   # would not lock in the corridor
        out.append(f"At the desk  : {len(near)} samples, median "
                   f"{median(near):.0f} dBm")
        out.append(f"In the corridor: {len(far)} samples, median "
                   f"{median(far):.0f} dBm")
        out.append(f"Separation   : {median(near) - median(far):.0f} dB")
        out.append("")
        out.append(f"BEST THRESHOLD: {best} dBm")
        out.append(f"  wrong lock at the desk    : {fp}/{len(near)} samples")
        out.append(f"  wrong no-lock in corridor : {fn}/{len(far)} samples")
        out.append("")
        if median(near) - median(far) < 8:
            out.append("WARNING: a separation below 8 dB is small - the "
                       "threshold will be unreliable,")
            out.append("a rolling average over more samples will be needed "
                       "(or plan B).")

    OUT.write_text("\n".join(out), encoding="utf-8")
    print("\n".join(out))
    print(f"\nSaved: {OUT}")


if __name__ == "__main__":
    main()
