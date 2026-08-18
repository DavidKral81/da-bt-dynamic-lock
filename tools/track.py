"""
Da BT Dynamic Lock - tracking a single device over time.

It establishes what the whole app stands on:
  - how often the device advertises (the interval between advertisements)
  - whether it goes quiet once the phone screen is off
  - how the RSSI changes with distance

Run:     py tools/track.py [seconds] [MAC]
Output:  track_log.csv (time, rssi) + a running printout in the console
"""

import asyncio
import os
import sys
import time
from pathlib import Path

from bleak import BleakScanner

HERE = Path(__file__).resolve().parent
# A NEW measurement goes into _output/ - never over tests/track_log.csv, which
# is the reference recording replayed by test_logic.py. To make a new
# measurement the reference one, copy it there by hand.
LOG = HERE.parent / "_output" / "track_log.csv"
LOG.parent.mkdir(exist_ok=True)

# What to track. Two options:
#   - a MAC address   ("4C:4C:1C:FE:16:E3") - for devices with a fixed address
#   - a piece of the advertisement ("nrf", "180d", ...) - for the Android app,
#     which does not choose its address and rotates it; looked up both in the
#     name and in the service UUIDs
TARGET = os.environ.get("DDL_TARGET", "")   # name fragment or MAC address


def is_mac(s):
    return len(s) == 17 and s.count(":") == 5


def matches(dev, adv):
    if is_mac(TARGET):
        return dev.address.upper() == TARGET.upper()
    t = TARGET.lower()
    if adv.local_name and t in adv.local_name.lower():
        return True
    return any(t in u.lower() for u in adv.service_uuids)

hits = []          # (time_since_start, rssi)
t0 = time.time()
last_print = 0.0
logf = None        # written AS WE GO - even if not a single advertisement
                   # arrives, the file exists and shows the run happened


def on_adv(dev, adv):
    global last_print
    if not matches(dev, adv):
        return
    if adv.rssi <= -120:      # -127 = "RSSI unknown", not a measurement
        return
    now = time.time() - t0
    gap = now - hits[-1][0] if hits else 0.0
    hits.append((now, adv.rssi))
    if logf:
        logf.write(f"{now:.2f};{adv.rssi}\n")
        logf.flush()
    # print at most once a second, so the console is not flooded
    if now - last_print >= 1.0:
        last_print = now
        print(f"  {now:6.1f}s   RSSI {adv.rssi:5} dBm   (gap {gap:.1f}s)")


async def main(secs):
    global logf
    print(f"Tracking {TARGET} for {secs} s.")
    print("Tip: turn the phone screen off and walk away - so it is visible "
          "whether it keeps advertising.\n")
    logf = LOG.open("w", encoding="utf-8")
    logf.write("cas_s;rssi_dbm\n")     # header kept as-is: analyze.py reads it
    logf.flush()
    scanner = BleakScanner(detection_callback=on_adv)
    await scanner.start()
    try:
        for i in range(secs):
            await asyncio.sleep(1)
            if (i + 1) % 30 == 0:
                print(f"  ... {i+1}s, advertisements so far: {len(hits)}")
    finally:
        await scanner.stop()
        logf.close()
        logf = None

    print("\n" + "=" * 55)
    if not hits:
        print("NOT A SINGLE advertisement the whole time.")
        print("In this mode the phone does not advertise over BLE at all.")
        print(f"(the empty log was saved anyway: {LOG})")
        return

    gaps = [hits[i][0] - hits[i - 1][0] for i in range(1, len(hits))]
    rs = [r for _, r in hits]
    print(f"Advertisements: {len(hits)} in {secs} s  (~{len(hits)/secs*60:.0f}/min)")
    if gaps:
        gaps_s = sorted(gaps)
        print(f"Gaps between  : min {min(gaps):.1f}s  median "
              f"{gaps_s[len(gaps_s)//2]:.1f}s  MAX {max(gaps):.1f}s")
        print(f"                (MAX = the longest blind spot; it decides how")
        print(f"                 fast we can tell the phone is gone at all)")
    print(f"RSSI          : min {min(rs)}  max {max(rs)}  "
          f"average {sum(rs)/len(rs):.1f} dBm")
    print(f"\nLog: {LOG}")


if __name__ == "__main__":
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 120
    if len(sys.argv) > 2:
        TARGET = sys.argv[2]
    asyncio.run(main(n))
