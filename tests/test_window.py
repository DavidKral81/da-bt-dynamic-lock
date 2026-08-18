"""Window tests - they catch bugs that are only visible while the UI runs.

Written after a bug where the device list stayed permanently empty on the
SECOND opening of the window: what was drawn was remembered outside the
window, so a new window considered itself finished. Neither the logic nor the
measured dimensions showed it.
"""
import tkinter as tk

import sys
from pathlib import Path

# the app lives one level up in windows/
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "windows"))

import dyn_lock as D


class _Advertisement:
    def __init__(self, name, rssi):
        self.local_name = name
        self.rssi = rssi
        self.service_uuids = []


def _add_devices():
    for name, rssi in (("Test Phone", -52), ("TV", -76)):
        D.STATE.record_nearby(None, _Advertisement(name, rssi))


def run():
    failures = []
    # the test must not depend on the developer's personal settings - it picks
    # the watched device itself, otherwise a "not audible" row appears and the
    # counts do not match
    D.CFG["target"] = "Test Phone"
    _add_devices()
    root = tk.Tk()
    root.withdraw()
    chart = D.Chart(root)
    results = {}

    def report(what, ok):
        print(f"  {'OK  ' if ok else 'FAIL '} {what}")
        if not ok:
            failures.append(what)

    def round_trip(number, then):
        chart.toggle()                      # open (as if from the tray)
        chart._tab(1)
        chart.win.update()
        root.after(2600, lambda: measure(number, then))

    def measure(number, then):
        chart.win.update()
        count = len(chart.devices_frame.winfo_children())
        results[number] = count
        report(f"opening the window #{number}: device list has {count} items",
               count == 2)

        sees_end = chart.settings_canvas.yview()[1] <= 1.0001
        report("the settings content can be scrolled to the end", sees_end)

        # large window: the content fits -> no scrollbar and the wheel does
        # not move it
        chart.win.state("zoomed")
        chart.win.update()
        fits = chart.settings_canvas.yview() == (0.0, 1.0)
        report("the content fits in a maximised window",
               fits or not chart.scroller_shown)
        if fits:
            report("...and so there is no scrollbar", not chart.scroller_shown)
        before = chart.grid_frame.winfo_rooty()
        chart.settings_canvas.yview_scroll(3, "units")
        chart.settings_canvas.yview_moveto(0.5)
        chart.win.update()
        report("the content does not jump to the middle when scrolled",
               not fits or chart.grid_frame.winfo_rooty() == before)
        chart.win.state("normal")
        chart.win.update()

        chart.toggle()                      # close
        root.after(500, then)

    def gone_quiet():
        """A phone that stopped advertising must not pretend to be audible.

        The list holds a device for another minute (otherwise rarely
        advertising ones would flicker), so disappearing from the list is not
        enough on its own - it has to be visible that the value is old.
        """
        chart.toggle()
        chart._tab(1)
        chart.win.update()

        # move the last-heard time 30 s back = the phone went quiet
        with D.STATE.lock:
            for key, (name, rssi, when) in list(D.STATE.nearby.items()):
                D.STATE.nearby[key] = (name, rssi, when - 30)
        chart.devices_frame.signature = None
        chart._refresh_content()
        chart.win.update()

        row_texts = []
        for row in chart.devices_frame.winfo_children():
            for d in row.winfo_children():
                if isinstance(d, tk.Label):
                    row_texts.append(d.cget("text"))
        everything = " | ".join(row_texts)
        report("a quiet phone no longer shows its signal strength as current",
               "dBm" not in everything)
        report("instead it shows how long ago it was heard",
               "naposledy" in everything or "last heard" in everything)
        # the tray menu does not offer quiet devices at all
        report("the tray menu does not offer quiet devices",
               all(age > 10 for _, _, age in D.STATE.nearby_list()))

        chart.toggle()
        root.after(400, language)

    def language():
        """Switching the language has to translate EVERYTHING, the tray too."""
        original = D.texts.language()
        chart.toggle()
        chart._tab(1)
        chart.win.update()
        chart._change_language("en")
        chart.win.update()

        report("the window title is in English",
               "signal strength" in chart.win.title())
        headings = []

        def walk(w):
            for d in w.winfo_children():
                if isinstance(d, tk.Label):
                    headings.append(d.cget("text"))
                walk(d)

        walk(chart.settings_canvas)
        everything = " | ".join(headings)
        report("the settings are in English", "When to lock" in everything)
        report("no Czech text is left in the settings",
               "Kdy zamknout" not in everything
               and "Dočasně pozastavit" not in everything)
        report("the app state is in English",
               D.decide({"active": True, "silence_s": 20}, 1, True, 0, 99)[1]
               == "phone at the desk")

        chart._change_language(original)
        chart.win.update()
        report("switching back to Czech works",
               "síla signálu" in chart.win.title())
        chart.toggle()
        root.after(400, finish)

    def finish():
        root.destroy()

    round_trip(1, lambda: round_trip(2, gone_quiet))
    root.mainloop()

    print("\nALL OK" if not failures
          else "\nFAILED:\n  " + "\n  ".join(failures))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(run())
