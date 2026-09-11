"""Window tests - they catch bugs that are only visible while the UI runs.

Written after a bug where the device list stayed permanently empty on the
SECOND opening of the window: what was drawn was remembered outside the
window, so a new window considered itself finished. Neither the logic nor the
measured dimensions showed it.
"""
import time
import tkinter as tk
import traceback

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


def _radio_hears_the_room():
    """The radio is picking up other devices - so a silence is the phone's.

    Without this the app rightly holds a lock off (deaf_radio_holds_off_lock),
    and every test that expects a lock would be testing the hold-off instead.
    """
    with D.STATE.lock:
        D.STATE.adverts.clear()
    for address in ("AA:1", "BB:2", "CC:3", "DD:4", "EE:5", "FF:6"):
        D.STATE.record_heard(address)
    D._deaf_hold_offs = 0
    D.SCANNER_RESTART.clear()


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

    # --- the list of networks without locking ----------------------------
    # The mesh case is why this is tested at all: same name, another access
    # point. That row has to be there, has to start unticked, and has to be
    # tellable apart from the saved one - otherwise the user faces two
    # identical lines and watching switches back on with no explanation.
    _saved_networks = D.CFG.get("trusted_networks")
    _live_network = D.current_network
    try:
        D.CFG["trusted_networks"] = []
        D.current_network = lambda: None
        report("nothing saved and no Wi-Fi: the list is empty",
               chart._network_rows() == [])
        D.current_network = lambda: ("Hotel", "11:22:33:44:55:66")
        report("on an unsaved network: one row, unticked, marked connected",
               chart._network_rows()
               == [("Hotel", "11:22:33:44:55:66", False, True)])
        D.CFG["trusted_networks"] = [{"ssid": "Kancelar",
                                      "bssid": "AA:BB:CC:DD:EE:FF"}]
        D.current_network = lambda: ("Kancelar", "AA:BB:CC:DD:EE:FF")
        report("on a selected network: one row, ticked",
               chart._network_rows()
               == [("Kancelar", "AA:BB:CC:DD:EE:FF", True, True)])
        # Out of range it must stay listed, or the only way to remove it would
        # be to travel back to it.
        D.current_network = lambda: None
        report("a selected network out of range stays on the list",
               chart._network_rows()
               == [("Kancelar", "AA:BB:CC:DD:EE:FF", True, False)])
        D.current_network = lambda: ("Kancelar", "11:22:33:44:55:66")
        rows = chart._network_rows()
        report("mesh: the new access point is a row of its own, unticked",
               rows == [("Kancelar", "11:22:33:44:55:66", False, True),
                        ("Kancelar", "AA:BB:CC:DD:EE:FF", True, False)])
        labels = [chart._network_label(s, b, c) for s, b, _, c in rows]
        report("mesh: the two rows do not read the same",
               labels[0] != labels[1])
        report("...and each row names its own access point",
               "11:22:33:44:55:66" in labels[0]
               and "AA:BB:CC:DD:EE:FF" in labels[1])
        # The address is half of what a network is recognised by, so every row
        # shows it - not only the rows that would otherwise look alike.
        D.CFG["trusted_networks"] = []
        D.current_network = lambda: ("Hotel", "11:22:33:44:55:66")
        report("every row carries the access point address",
               "11:22:33:44:55:66" in chart._network_label(
                   "Hotel", "11:22:33:44:55:66", True))
        # Ticking and unticking a row.
        chart._toggle_network("Hotel", "11:22:33:44:55:66")
        report("ticking a row puts the network on the list",
               D.CFG["trusted_networks"]
               == [{"ssid": "Hotel", "bssid": "11:22:33:44:55:66"}])
        chart._toggle_network("Hotel", "11:22:33:44:55:66")
        report("unticking takes it off again",
               D.CFG["trusted_networks"] == [])
    finally:
        D.current_network = _live_network
        D.CFG["trusted_networks"] = _saved_networks
        # _toggle_network writes to disk, so putting the value back in memory
        # is not enough - the file would keep a network nobody chose.
        D.save_cfg(D.CFG)

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
        root.after(400, settings_columns)

    def settings_columns():
        """Cards in one column have to be a FIXED gap apart - no holes.

        This is the layout defect that came back three times, last on
        10.09.2026 right above "Pause for a while". The cards used to sit in
        one shared grid, so a card on the left and a card on the right took
        the same row, the row was as tall as the TALLER of them, and under
        the shorter one a hole opened up. Every card that was added or grew
        re-paired the cards and moved the hole somewhere else, which is why
        it kept looking like a brand new bug.

        It was always found by eye on a preview - and by eye it was always
        found late. So it is measured instead, in BOTH widths: the pairing
        (and with it the hole) changed with the window.

        Measured relative to grid_frame, not on the screen: the lower cards
        can be scrolled out of view and their screen coordinates would then
        say nothing about the spacing.
        """
        chart.toggle()
        chart._tab(1)
        chart.win.update()
        left, right = chart.column_frames

        def cards_of(frame):
            """Top edge and height of each card, in the order they appear."""
            chart.win.update_idletasks()
            return [(frame.winfo_y() + c.winfo_y(), c.winfo_height())
                    for c in frame.winfo_children()]

        def holes(cards):
            """The gap between each card and the next one below it."""
            return [below - (top + height)
                    for (top, height), (below, _) in zip(cards, cards[1:])]

        gap = D.Chart.CARD_GAP
        # --- wide window: two columns next to each other -------------------
        needed = 2 * D.Chart.SETTINGS_WIDTH + 140
        if root.winfo_screenwidth() < needed + 80:
            print(f"  SKIP  two columns: the screen is only "
                  f"{root.winfo_screenwidth()} px wide, {needed + 80} needed")
        else:
            chart.win.geometry(f"{needed}x760")
            chart.win.update()
            report(f"a wide window uses two columns ({chart.columns})",
                   chart.columns == 2)
            report("...and the columns really stand side by side",
                   right.winfo_x() > left.winfo_x()
                   and right.winfo_y() == left.winfo_y())
            for name, frame in (("left", left), ("right", right)):
                found = holes(cards_of(frame))
                report(f"the {name} column has no hole in it "
                       f"(gaps {found}, all should be {gap})",
                       bool(found) and all(g == gap for g in found))

        # --- narrow window: the two frames stack ---------------------------
        # The gap ACROSS the seam is the interesting one: that is where a
        # column of cards is handed over from one frame to the other, and a
        # reader must not be able to tell there is a seam at all.
        chart.win.geometry("960x760")
        chart.win.update()
        report(f"a narrow window uses one column ({chart.columns})",
               chart.columns == 1)
        report("...and the second frame sits under the first, not beside it",
               right.winfo_x() == left.winfo_x()
               and right.winfo_y() > left.winfo_y())
        found = holes(cards_of(left) + cards_of(right))
        report(f"one column: every card the same gap apart "
               f"(gaps {found}, all should be {gap})",
               bool(found) and all(g == gap for g in found))

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
        root.after(400, chart_time_axis)

    def chart_time_axis():
        """The X axis shows the time of day - and it has to be the real one.

        The samples carry time.monotonic() (seconds since boot), so a grid
        anchored to it lands on a random second and the labels would read
        09:23:37, 09:24:37... Only a running window shows what was drawn, so
        the labels are read straight off the canvas.
        """
        chart.toggle()
        chart._tab(0)
        chart.win.update()
        time.sleep(0.4)
        chart._draw()
        chart.win.update()

        c, bottom = chart.canvas, chart.canvas.winfo_height() - D.Chart.MARGIN_B
        labels = [c.itemcget(i, "text") for i in c.find_all()
                  if c.type(i) == "text" and c.coords(i)[1] > bottom]
        report(f"the chart has labels under the X axis ({len(labels)})",
               len(labels) >= 3)

        shape = all(len(s) == 5 and s[2] == ":" and s.replace(":", "").isdigit()
                    for s in labels)
        report(f"they are times of day, not '-x min' ({', '.join(labels)})",
               shape and not any("min" in s for s in labels))

        if shape and labels:
            secs = [int(s[:2]) * 3600 + int(s[3:]) * 60 for s in labels]
            # the 5 min range steps by a minute, and every line has to sit on
            # a whole minute of the clock (that is what "anchored" means)
            gaps = {(a - b) % 86400 for a, b in zip(secs, secs[1:])}
            report(f"the lines sit a minute apart ({sorted(gaps)})",
                   gaps == {60})
            lt = time.localtime()
            now_s = lt.tm_hour * 3600 + lt.tm_min * 60 + lt.tm_sec
            age = (now_s - secs[0]) % 86400
            report(f"the newest label is the current minute ({labels[0]}, "
                   f"{age} s old)", 0 <= age < 60)

        # The 2 min range labels seconds too - and that is the only place
        # where the anchoring is provable: whole minutes hide the seconds, so
        # a grid drifted off the clock would still print a plausible HH:MM.
        chart.span_var.set(120)
        chart._change_span()
        chart.win.update()
        short = [c.itemcget(i, "text") for i in c.find_all()
                 if c.type(i) == "text" and c.coords(i)[1] > bottom]
        fine = all(len(s) == 8 and s.count(":") == 2 for s in short)
        report(f"the 2 min range labels seconds ({', '.join(short[:3])}…)",
               fine and len(short) >= 3)
        if fine and short:
            secs = [int(s[:2]) * 3600 + int(s[3:5]) * 60 + int(s[6:])
                    for s in short]
            report("every line sits on a whole half minute of the clock",
                   all(s % 30 == 0 for s in secs))
            lt = time.localtime()
            now_s = lt.tm_hour * 3600 + lt.tm_min * 60 + lt.tm_sec
            age = (now_s - secs[0]) % 86400
            report(f"...and the newest one is at most half a minute old "
                   f"({short[0]}, {age} s)", 0 <= age < 30)
        chart.span_var.set(300)
        chart._change_span()

        chart.toggle()
        root.after(400, countdown_position)

    def countdown_position():
        """The countdown box must LAND where the setting says.

        Measured on the real window, not on the setting: the menu and the box
        read the same function, and this is the only place that proves the
        result of it ever reaches the screen.
        """
        box = D.Countdown(root)
        left, top, right, bottom = D.work_area()
        original = D.CFG.get("countdown_vertical")

        # 0.33 is there because that is what shipped in 1.0 - it is not one of
        # the offered steps and has to snap to the nearest one, 30 %.
        for stored, percent in ((0.10, 10), (0.50, 50), (0.90, 90), (0.33, 30)):
            D.CFG["countdown_vertical"] = stored
            box.show(9)
            root.update()
            first = box.boxes[0].win            # the primary monitor comes first
            h = first.winfo_height()
            wanted = top + int((bottom - top) * percent / 100) - h // 2
            got = first.winfo_rooty()
            report(f"countdown sits at {percent} % from the top "
                   f"(y {got}, wanted {wanted})", abs(got - wanted) <= 2)
            # ...and the whole box stays on the desktop, ends of the range
            # included - half a warning is a warning wasted
            report(f"the whole box is on the desktop at {percent} %",
                   got >= top and got + h <= bottom)
            # the position is computed from the REQUESTED size before the box is
            # shown; if the two ever differed, everything above would be off
            report(f"the box is as wide as it asked to be at {percent} %",
                   first.winfo_width() == first.winfo_reqwidth())

        box.hide()
        D.CFG["countdown_vertical"] = original
        root.after(200, countdown_monitors)

    def countdown_monitors():
        """A box on every monitor - and only one when the setting says so.

        The second monitor is faked, because the machine running the test has
        whatever it has. Its work area deliberately sits at NEGATIVE x, the way
        Windows numbers a monitor to the left of the primary one: Tk reads a
        bare "-100" in a geometry string as "from the right edge", so that is
        the case which would silently put the box on the wrong screen.
        """
        box = D.Countdown(root)
        real = D.monitor_work_areas()
        primary = real[0]
        # 900x600 of desktop starting 1000 px left of the primary monitor
        second = (primary[0] - 1000, primary[1] + 40,
                  primary[0] - 100, primary[1] + 640)
        original_areas = D.monitor_work_areas
        original_only = D.CFG.get("countdown_primary_only", False)
        D.monitor_work_areas = lambda: [primary, second]
        try:
            D.CFG["countdown_primary_only"] = False
            box.show(9)
            root.update()
            report(f"two monitors get two boxes ({len(box.boxes)})",
                   len(box.boxes) == 2)
            for i, area in enumerate((primary, second)):
                if i >= len(box.boxes):     # already reported as a failure -
                    continue                # measuring it would raise instead
                b = box.boxes[i]
                w, h = b.win.winfo_width(), b.win.winfo_height()
                wanted_x = area[0] + (area[2] - area[0] - w) // 2
                wanted_y = (area[1] + int((area[3] - area[1])
                                          * D.countdown_percent() / 100) - h // 2)
                got = (b.win.winfo_rootx(), b.win.winfo_rooty())
                report(f"box {i + 1} sits on its own monitor "
                       f"(at {got}, wanted {(wanted_x, wanted_y)})",
                       abs(got[0] - wanted_x) <= 2 and abs(got[1] - wanted_y) <= 2)
                report(f"...and box {i + 1} is on screen",
                       bool(b.win.winfo_ismapped()))

            # ...and the setting really takes the second one away, without
            # leaving it hanging on the screen it was drawn on
            D.CFG["countdown_primary_only"] = True
            box.show(8)
            root.update()
            report(f"'primary monitor only' leaves one box ({len(box.boxes)})",
                   len(box.boxes) == 1)
            report("...and it is the primary one",
                   bool(box.boxes) and box.boxes[0].area == primary)

            D.CFG["countdown_primary_only"] = False
            box.show(7)
            root.update()
            report("switching it back brings the second box again",
                   len(box.boxes) == 2)
            box.hide()
            root.update()
            report("...and hiding takes down every box",
                   all(not b.win.winfo_ismapped() for b in box.boxes))
        finally:
            # The boxes have to come down even when something above raises:
            # a failing test that leaves a "Locking in 9 s" box sitting on the
            # desktop is a test that broke the machine it was checking.
            D.monitor_work_areas = original_areas
            D.CFG["countdown_primary_only"] = original_only
            box.hide()
            for b in box.boxes:
                b.win.destroy()
            root.update()
        root.after(200, countdown_report)

    def countdown_report():
        """The box has to say in the log where it really landed.

        On 22.08.2026 two locks a minute apart both had their "Countdown
        started" line and one box was never seen. The log proved the countdown
        had been DECIDED on and nothing more, so a box on the wrong monitor, a
        box under a full-screen window and a box simply missed all looked
        identical. One line per appearance, not per tick.
        """
        box = D.Countdown(root)
        lines = []
        original_log = D.log
        said = lambda: [l for l in lines if l.startswith("Countdown box ")]
        D.log = lambda message: lines.append(message)
        try:
            box.show(9)
            root.update()
            # one line per box, however many monitors this machine has
            screens = len(box.boxes)
            first = said()
            report(f"every box reports where it landed "
                   f"({len(first)} of {screens})", len(first) == screens)
            report("...with a position, a size and what is in front of it",
                   bool(first) and "work area" in first[0]
                   and "in front" in first[0])
            report("...and it says the box is visible",
                   bool(first) and "NOT VISIBLE" not in first[0]
                   and "visible" in first[0])
            report("...and which box of how many it is",
                   bool(first) and first[0].startswith(f"Countdown box 1/{screens} at"))

            box.show(8)
            box.show(7)
            root.update()
            report("...once per appearance, not once per tick",
                   len(said()) == screens)

            box.hide()
            box.show(6)
            root.update()
            report("...and again the next time it appears",
                   len(said()) == 2 * screens)
        finally:
            D.log = original_log
        box.hide()
        root.after(200, loop_after_sleep)

    def loop_after_sleep():
        """main_loop from end to end: a gap must not lock, and a decision must
        not be carried out once it has gone stale.

        decide() alone cannot show either of these - it is a pure function and
        knows nothing about gaps or about how long the loop took. This is the
        only place that proves both guards are actually wired into the loop.
        """
        D.DRY_RUN = True                 # never really lock the screen

        class FakeTray:
            def refresh_menu(self):
                pass

            def set_state(self, colour, label):
                pass

            def notify(self, message):
                pass

        tray = FakeTray()
        box = D.Countdown(root)
        before = len(D.STATE.locks)

        # The screen state is answered here, not asked of Windows. These cases
        # are about the loop, and the loop refuses to lock behind a lock screen
        # - correctly so. Run with the workstation actually locked (the tester
        # walked away, a screensaver kicked in), the three cases below that
        # REQUIRE a lock would fail and read exactly like a regression. Telling
        # a locked screen apart has its own tests in test_logic.py.
        _screen_state = D.session_locked
        D.session_locked = lambda: False

        # --- A: the loop stood still (sleep, hibernation). Silence piled up
        # while nobody was measuring, so it must not be used to lock.
        D.STATE.was_near, D.STATE.armed = True, True
        D.STATE.near_at = time.monotonic() - 700
        D._last_tick = time.monotonic() - 700       # the previous tick is ancient
        D._alerted = True              # "the phone has gone missing" was warned
        D.main_loop(root, box, tray)
        report("a gap in the loop does not lock the screen",
               len(D.STATE.locks) == before)
        silence = D.STATE.silence()
        report("...and the silence is measured from now on",
               silence is not None and silence < 5)
        # The clock going back to zero must not be announced as the phone
        # coming back - nothing was heard from it.
        report("...and no good news is invented about the phone",
               D._alerted is False)
        report("a forgotten reading reads as unknown, not as None",
               D.rssi_text() == "unknown")

        # --- B: an honest lock decision that goes stale while it is being
        # carried out. watch_signal_loss() is where the Windows notification
        # appears, so that is exactly where the phone gets its chance to speak.
        D.STATE.near_at = time.monotonic() - 700
        D._last_tick = time.monotonic()             # no gap this time
        original_watch = D.watch_signal_loss
        D.watch_signal_loss = lambda t: D.STATE.record(-50)
        try:
            D.main_loop(root, box, tray)
        finally:
            D.watch_signal_loss = original_watch
        report("a decision that went stale is not carried out",
               len(D.STATE.locks) == before)

        # ...and the very same situation without the phone coming back DOES
        # lock. Without this the two checks above would pass on an app that
        # never locks anything at all.
        _radio_hears_the_room()
        D.STATE.near_at = time.monotonic() - 700
        D.STATE.armed = True
        D._last_tick = time.monotonic()
        D.main_loop(root, box, tray)
        report("without the phone coming back it still locks",
               len(D.STATE.locks) == before + 1)

        # --- C: the same silence, but the radio hears nothing from anyone.
        # That is a deaf scanner, so the lock waits and the scanner is told to
        # restart. Only visible in the real loop: decide() knows nothing about
        # the radio.
        with D.STATE.lock:
            D.STATE.adverts.clear()
        D._deaf_hold_offs = 0
        D.SCANNER_RESTART.clear()
        held = len(D.STATE.locks)
        D.STATE.near_at = time.monotonic() - 700
        D.STATE.armed = True
        D._last_tick = time.monotonic()
        D.main_loop(root, box, tray)
        report("a lock the radio cannot corroborate is held off",
               len(D.STATE.locks) == held)
        report("...and the scanner is asked to restart",
               D.SCANNER_RESTART.is_set())
        report("...and the countdown box is not left on screen",
               all(not b.win.winfo_ismapped() for b in box.boxes))
        # The allowance is finite: keep asking and the screen does lock.
        for _ in range(D.DEAF_HOLD_OFF_MAX + 1):
            D.STATE.near_at = time.monotonic() - 700
            D.STATE.armed = True
            D._last_tick = time.monotonic()
            D.main_loop(root, box, tray)
        report("...but a quiet room cannot switch the guard off for good",
               len(D.STATE.locks) > held)

        # --- D: coming back from a state that watched nothing. Silence that
        # piled up meanwhile must not lock at once - it is measured again, so
        # the countdown gets its turn. Three ways in, one remedy: leaving (or
        # unticking) a network without locking, a pause running out, and the
        # switch turned back on.
        _real_trusted = D.on_trusted_network

        def comes_back(what, enter, leave):
            _radio_hears_the_room()
            D._not_watching = None
            D.STATE.was_near, D.STATE.armed = True, True
            enter()
            D.STATE.near_at = time.monotonic() - 700
            D._last_tick = time.monotonic()
            start = len(D.STATE.locks)
            D.main_loop(root, box, tray)             # watching nothing
            leave()
            D._last_tick = time.monotonic()
            D.main_loop(root, box, tray)             # back to watching
            silence = D.STATE.silence()
            report(f"{what}: no lock straight away",
                   len(D.STATE.locks) == start)
            report("...and the silence is measured again from now",
                   silence is not None and silence < 5)
            # The twin: real silence afterwards DOES lock. Without it the two
            # checks above would pass on an app that never locks anything.
            _radio_hears_the_room()
            D.STATE.near_at = time.monotonic() - 700
            D._last_tick = time.monotonic()
            D.main_loop(root, box, tray)
            report("...but real silence afterwards still locks",
                   len(D.STATE.locks) == start + 1)

        try:
            comes_back("leaving a network without locking",
                       enter=lambda: setattr(D, "on_trusted_network",
                                             lambda: True),
                       leave=lambda: setattr(D, "on_trusted_network",
                                             lambda: False))
            comes_back("a pause running out",
                       enter=lambda: setattr(D.STATE, "paused_until",
                                             time.monotonic() + 600),
                       leave=lambda: setattr(D.STATE, "paused_until", 0.0))
            comes_back("switching watching back on",
                       enter=lambda: D.CFG.__setitem__("active", False),
                       leave=lambda: D.CFG.__setitem__("active", True))
        finally:
            D.on_trusted_network = _real_trusted
            D.STATE.paused_until = 0.0
            D._not_watching = None

        D.session_locked = _screen_state     # the next case asks for itself
        D.CFG["active"] = False          # stop the ticks main_loop scheduled
        box.hide()
        root.after(200, loop_while_locked)

    def loop_while_locked():
        """Nothing may happen behind the lock screen - and everything must
        start again cleanly once it goes away.

        decide() alone cannot show this: it is handed the answer, it does not
        go and ask. Only the loop wires session_locked() in, remembers the
        change and restarts the measurement on the way out.
        """
        D.CFG["active"] = True
        D.DRY_RUN = True

        class FakeTray:
            def refresh_menu(self):
                pass

            def set_state(self, colour, label):
                self.label = label

            def notify(self, message):
                pass

        tray = FakeTray()
        box = D.Countdown(root)
        original = D.session_locked
        before = len(D.STATE.locks)

        try:
            # --- behind the lock screen: silence long past the limit, and
            # still nothing happens
            D.session_locked = lambda: True
            D.STATE.was_near, D.STATE.armed = True, True
            D.STATE.near_at = time.monotonic() - 700
            D._last_tick = time.monotonic()
            D._screen_locked = False           # the change has to be noticed
            D.main_loop(root, box, tray)
            report("a locked screen is not locked all over again",
                   len(D.STATE.locks) == before)
            report("...and the loop knows it is locked", D._screen_locked is True)
            report("...and the tray says so", tray.label == D.texts.t("st_screen_locked"))

            # --- unlocking: the silence collected behind the lock screen says
            # nothing about where the phone is now. Without the restart the
            # screen would lock again the moment the PIN was typed.
            D.session_locked = lambda: False
            D._last_tick = time.monotonic()
            D.main_loop(root, box, tray)
            silence = D.STATE.silence()
            report("unlocking restarts the measurement",
                   silence is not None and silence < 5)
            report("...without locking on the way out",
                   len(D.STATE.locks) == before)
            report("...and the phone stays known, so watching goes on",
                   D.STATE.silence() is not None)

            # ...and the very same tick, unlocked and with the phone really
            # gone, DOES lock. Without this the three checks above would pass
            # on an app that never locks anything.
            _radio_hears_the_room()
            D.STATE.near_at = time.monotonic() - 700
            D.STATE.armed = True
            D._last_tick = time.monotonic()
            D.main_loop(root, box, tray)
            report("unlocked, with the phone gone, it still locks",
                   len(D.STATE.locks) == before + 1)
        finally:
            D.session_locked = original
            D._screen_locked = False

        D.CFG["active"] = False
        box.hide()
        root.after(200, tray_menu)

    def tray_menu():
        """The tray menu is built out of lambdas and is only ever seen when
        somebody right-clicks the icon - a broken item would show up in front
        of the user, not here. So it gets built and read once.
        """
        D.CFG["active"] = True
        icon = D.TrayIcon(root, D.Countdown(root), chart)
        labels = [item.text for item in icon.icon.menu.items]
        report("the tray menu builds at all", len(labels) > 5)
        report("...and offers Settings",
               D.texts.t("tab_settings") in labels)
        report("...right above Quit",
               labels.index(D.texts.t("tab_settings"))
               == labels.index(D.texts.t("tray_quit")) - 1)
        # Labels must be read when the menu opens, not baked in when it is
        # built - a plain string would freeze in whatever language was current
        # at build time. Asked behaviourally: switch the language and see
        # whether the SAME menu object now says something else. (Asking pystray
        # whether the label is a callable does not work: it wraps plain strings
        # too, so that check passed even on a hardcoded label.)
        was = D.texts.language()
        D.texts.set_language("en")
        english = [item.text for item in icon.icon.menu.items]
        D.texts.set_language(was)
        czech = [item.text for item in icon.icon.menu.items]
        report("switching the language reaches the menu too",
               "Settings" in english and D.texts.t("tab_settings") in czech
               and english != czech)
        root.after(200, finish)

    def finish():
        root.destroy()

    def crashed(kind, value, tb):
        """A step that raises must not leave the test hanging - or a box up.

        Tk swallows exceptions from after() callbacks, so the next step was
        never scheduled: mainloop ran for ever with a "Locking in 9 s" box
        sitting on the desktop and nothing in the output to say why. Found on
        04.09.2026 by deliberately breaking the multi-monitor code to prove the
        test could see it - the sabotage was caught by a person, not by this.
        """
        traceback.print_exception(kind, value, tb)
        report(f"a step of the test crashed: {value!r}", False)
        root.destroy()

    root.report_callback_exception = crashed
    round_trip(1, lambda: round_trip(2, gone_quiet))
    root.mainloop()

    print("\nALL OK" if not failures
          else "\nFAILED:\n  " + "\n  ".join(failures))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(run())
