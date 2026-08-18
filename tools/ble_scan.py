"""
Da BT Dynamic Lock - diagnostic scan of the BLE surroundings.

Purpose: find out what is visible nearby, whether the phone is among it (and
under what address/name), how stable the address is and how much the RSSI
jumps around.

Run:     py tools/ble_scan.py [seconds]
Output:  _output/ble-scan-report.txt (UTF-8)
"""

import asyncio
import sys
import time
from collections import defaultdict
from pathlib import Path

from bleak import BleakScanner

HERE = Path(__file__).resolve().parent
REPORT = HERE.parent / "_output" / "ble-scan-report.txt"
REPORT.parent.mkdir(exist_ok=True)

seen = defaultdict(lambda: {"name": "", "rssi": [], "first": 0.0, "last": 0.0,
                            "mfg": set(), "svc": set(), "ibeacon": ""})
t0 = time.time()


def parse_ibeacon(mfg_data):
    """iBeacon = vendor 0x004C, payload starts with 02 15, then a 16B UUID +
    major + minor."""
    raw = mfg_data.get(0x004C)
    if not raw or len(raw) < 23 or raw[0] != 0x02 or raw[1] != 0x15:
        return ""
    u = raw[2:18].hex()
    uuid = f"{u[0:8]}-{u[8:12]}-{u[12:16]}-{u[16:20]}-{u[20:32]}"
    major = int.from_bytes(raw[18:20], "big")
    minor = int.from_bytes(raw[20:22], "big")
    return f"{uuid} major={major} minor={minor}"


def on_adv(dev, adv):
    now = time.time() - t0
    # An iBeacon is recognised by its CONTENT, not by the MAC - so the key
    # uses the beacon when available (a phone MAC rotates, the content does not)
    ib = parse_ibeacon(adv.manufacturer_data)
    d = seen[ib or dev.address]
    if not d["first"]:
        d["first"] = now
    d["last"] = now
    d["rssi"].append(adv.rssi)
    d["ibeacon"] = ib
    if adv.local_name:
        d["name"] = adv.local_name
    for cid in adv.manufacturer_data:
        d["mfg"].add(cid)
    for u in adv.service_uuids:
        d["svc"].add(u)


async def main(secs):
    print(f"Scanning for {secs} s... (walk down the corridor with the phone/fob "
          f"so the RSSI change is visible)")
    scanner = BleakScanner(detection_callback=on_adv)
    await scanner.start()
    for i in range(secs):
        await asyncio.sleep(1)
        if (i + 1) % 5 == 0:
            print(f"  {i+1}/{secs} s, devices: {len(seen)}")
    await scanner.stop()

    # company IDs (0x004C = Apple, 0x0075 = Samsung, 0x00E0 = Google)
    VENDOR = {0x004C: "Apple", 0x0075: "Samsung", 0x00E0: "Google",
              0x0006: "Microsoft", 0x0087: "Garmin", 0x0157: "Huawei",
              0x038F: "Xiaomi"}

    rows = sorted(seen.items(), key=lambda kv: -len(kv[1]["rssi"]))
    out = []
    out.append(f"Scan of {secs} s, found {len(rows)} devices")
    out.append("(sorted by the number of advertisements caught - the most "
               "frequent one on top)")
    out.append("")
    for addr, d in rows:
        r = d["rssi"]
        vend = ", ".join(VENDOR.get(c, f"0x{c:04X}")
                         for c in sorted(d["mfg"])) or "-"
        if d["ibeacon"]:
            out.append(">>> iBEACON (token candidate - identity independent "
                       "of the MAC)")
            out.append(f"  {d['ibeacon']}")
        else:
            out.append(f"MAC: {addr}")
        out.append(f"  Name           : {d['name'] or '(no name)'}")
        out.append(f"  Vendor (mfg)   : {vend}")
        out.append(f"  Advertisements : {len(r)}  (from {d['first']:.0f}s to "
                   f"{d['last']:.0f}s)")
        out.append(f"  RSSI           : min {min(r)}  max {max(r)}  "
                   f"average {sum(r)/len(r):.1f} dBm")
        if d["svc"]:
            out.append(f"  Services       : {', '.join(sorted(d['svc'])[:4])}")
        out.append("")

    REPORT.write_text("\n".join(out), encoding="utf-8")
    print(f"\nDone. Report: {REPORT}")


if __name__ == "__main__":
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 60
    asyncio.run(main(n))
