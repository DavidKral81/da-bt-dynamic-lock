"""Render the app window into a PNG so it can be SEEN how it looks.

Why this exists: measuring (width, padding, number of widgets) cannot reveal
that something looks sloppy - a tiny arrow, a small checkbox, poor contrast.
This script produces an image that can be looked at before a release.

It draws through PrintWindow (the window renders itself into memory), so the
shot comes out even when the screen is locked or something covers the window -
an ordinary screenshot would capture the lock screen in that case.

Usage:
    py tests/preview.py              # both tabs, default width 1400
    py tests/preview.py 900          # narrow window - checks the
                                     # single-column layout
    py tests/preview.py 1400 en      # the English version
    py tests/preview.py installer    # the installer and uninstaller windows,
                                     # both languages - they are the FIRST
                                     # thing a user ever sees
"""
import ctypes
import pathlib
import sys
import time
import tkinter as tk
from ctypes import wintypes

from PIL import Image

import sys
from pathlib import Path

# the app lives one level up in windows/
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "windows"))

import dyn_lock as D

PW_RENDERFULLCONTENT = 0x00000002


class _Advertisement:
    """Stand-in for a discovered device - only what State.record_nearby reads."""

    def __init__(self, name, rssi):
        self.local_name = name
        self.rssi = rssi
        self.service_uuids = []


def _capture(window, path):
    """Save the window contents into a PNG. Returns (width, height)."""
    window.update_idletasks()
    window.update()
    hwnd = int(window.frame(), 16)
    # the size MUST come from the window frame (GetWindowRect), not from
    # winfo_width/height - those return the interior without the title bar,
    # whereas PrintWindow draws the whole window including the bar. With the
    # wrong size the bottom of the image is cut off by the height of the bar
    # and it looks as if the app content were cut off.
    rect = wintypes.RECT()
    ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(rect))
    width, height = rect.right - rect.left, rect.bottom - rect.top

    u32, g32 = ctypes.windll.user32, ctypes.windll.gdi32
    window_dc = u32.GetWindowDC(hwnd)
    dc = g32.CreateCompatibleDC(window_dc)
    bmp = g32.CreateCompatibleBitmap(window_dc, width, height)
    g32.SelectObject(dc, bmp)
    if not u32.PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT):
        raise RuntimeError("PrintWindow failed - the window was not rendered")

    class BMI(ctypes.Structure):
        _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
                    ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
                    ("biBitCount", wintypes.WORD),
                    ("biCompression", wintypes.DWORD),
                    ("biSizeImage", wintypes.DWORD),
                    ("biXPelsPerMeter", wintypes.LONG),
                    ("biYPelsPerMeter", wintypes.LONG),
                    ("biClrUsed", wintypes.DWORD),
                    ("biClrImportant", wintypes.DWORD)]

    header = BMI()
    header.biSize = ctypes.sizeof(BMI)
    header.biWidth = width
    header.biHeight = -height           # negative = top down
    header.biPlanes = 1
    header.biBitCount = 32
    data = ctypes.create_string_buffer(width * height * 4)
    g32.GetDIBits(dc, bmp, 0, height, data, ctypes.byref(header), 0)

    img = Image.frombuffer("RGBA", (width, height), data, "raw", "BGRA", 0, 1)
    img.convert("RGB").save(path)

    g32.DeleteObject(bmp)
    g32.DeleteDC(dc)
    u32.ReleaseDC(hwnd, window_dc)
    return width, height


OUTPUT = pathlib.Path(__file__).resolve().parent.parent / "_output"


def preview_installer():
    """Shoot every installer window: both roles, both languages, and the
    result window that follows each of them.

    The installer is the first thing anyone sees of this project, so it gets
    looked at the same way the app does - and that has to include the result
    window. It used to be left out, so half of what the installer shows could
    not be checked at all. Windows are built one at a time and destroyed - two
    live tk.Tk instances in one process fight over the event loop.
    """
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "installer"))
    import installer

    for language in ("cs", "en"):
        for uninstalling, role in ((False, "install"), (True, "uninstall")):
            installer.texts.set_language(language)
            for stage, build in (
                    ("", lambda: installer.Window(uninstalling)),
                    # all_ok=True: the ordinary ending. The partial one differs
                    # only in the colour of the heading and in one sentence.
                    ("-done",
                     lambda: installer.ResultWindow(uninstalling, True))):
                window = build()
                window.r.update()
                time.sleep(0.4)
                name = f"preview-{role}{stage}-{language}.png"
                print(f"{name}: {_capture(window.r, OUTPUT / name)}")
                window.r.destroy()


def main():
    OUTPUT.mkdir(exist_ok=True)
    if len(sys.argv) > 1 and sys.argv[1] == "installer":
        preview_installer()
        return
    width = int(sys.argv[1]) if len(sys.argv) > 1 else 1400
    if len(sys.argv) > 2:                    # second parameter = language
        D.texts.set_language(sys.argv[2])
    # put something in the device list so it is not empty
    for name, rssi in (("Test Phone", -65), ("Living room TV", -90)):
        D.STATE.record_nearby(None, _Advertisement(name, rssi))
    # a stretch of signal for the chart - written straight into the history so
    # the shot does not depend on how record() evaluates the sensitivity
    now = time.monotonic()
    D.STATE.history.extend((now - (60 - i) * 2, -60 - (i % 15))
                           for i in range(60))
    D.STATE.seen_at = now

    root = tk.Tk()
    root.withdraw()
    chart = D.Chart(root)
    chart.toggle()

    def save():
        window = chart.win
        window.state("normal")
        window.geometry(f"{width}x900+40+40")
        window.update()
        time.sleep(0.8)              # let the redraw after the resize finish
        for number, name in ((1, "preview-settings.png"),
                             (0, "preview-chart.png")):
            chart._tab(number)
            window.update()
            time.sleep(0.4)
            print(f"{name}: {_capture(window, OUTPUT / name)}")
        # the bottom of the settings - that is where the content used to be
        # cut off and unreachable. Scroll only AFTER the layout has settled:
        # if the scrolled area still grows, the view stays a bit above the end.
        chart._tab(1)
        window.update()
        time.sleep(0.5)
        window.update()
        chart.settings_canvas.yview_moveto(1.0)
        window.update()
        time.sleep(0.3)
        print("preview-settings-bottom.png: "
              f"{_capture(window, OUTPUT / 'preview-settings-bottom.png')}")
        chart.toggle()
        root.destroy()

    root.after(1500, save)
    root.mainloop()


if __name__ == "__main__":
    main()
