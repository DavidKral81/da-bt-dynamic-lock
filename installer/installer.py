"""
Installer and uninstaller for Da BT Dynamic Lock.

One program for both roles:
  no arguments   -> install
  --uninstall    -> uninstall (during installation this copy is saved into
                    the program folder as odinstalovat.exe)

What the installation does:
  1. copies the program into  C:\\Program Files\\Da BT Dynamic Lock
  2. creates a shortcut in the Start menu
  3. registers itself so it shows up in Settings -> Apps
  4. optionally sets up the logon task
Settings and the log stay in the user profile (%APPDATA%), so neither an
uninstall nor a reinstall loses them.

A hand-written installer instead of a ready-made tool (Inno Setup) because
nothing has to be installed to build it and we keep full control over what
gets written into the system.
"""

import ctypes
import os
import shutil
import subprocess
import sys
import tempfile
import time
import tkinter as tk
import winreg
from pathlib import Path

APP_NAME = "Da BT Dynamic Lock"
REG_KEY = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DaBTDynamicLock"
TASK_NAME = APP_NAME
VERSION = "1.0"

# The mutex name the application holds. It MUST match the one in
# windows/dyn_lock.py - when the two drift apart, the installer stops
# noticing a running app and reinstalls over it.
MUTEX_NAME = "DaBTDynamicLock_single_instance"

BACKGROUND = "#151920"
CARD = "#1c222c"
TEXT = "#e6ebf2"
GREY = "#9aa4b2"
GREEN = "#22a050"
GREEN_LIGHT = "#4ade80"
RED = "#b91c1c"

TARGET_DIR = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / APP_NAME
STARTMENU = (Path(os.environ.get("ProgramData", r"C:\ProgramData"))
             / r"Microsoft\Windows\Start Menu\Programs" / f"{APP_NAME}.lnk")
# The desktop of all users - the installation is shared, so the shortcut
# belongs here, not only on the desktop of whoever happens to install it
DESKTOP = (Path(os.environ.get("PUBLIC", r"C:\Users\Public")) / "Desktop"
           / f"{APP_NAME}.lnk")
DATA = Path(os.environ.get("APPDATA", "")) / APP_NAME
# the fallback autostart mechanism the application sets up on its own
STARTUP = (Path(os.environ.get("APPDATA", ""))
           / r"Microsoft\Windows\Start Menu\Programs\Startup"
           / f"{APP_NAME}.lnk")


def icon_source():
    """Path to the icon - inside the packaged program, or next to the sources.

    From source the icons live in windows/icons/ (after the folder move they
    are no longer in the project root, where this used to look).
    """
    if getattr(sys, "frozen", False):
        candidates = [Path(sys._MEIPASS) / "dyn_lock_tray.ico"]
    else:
        root = Path(__file__).resolve().parent.parent
        candidates = [root / "windows" / "icons" / "dyn_lock_tray.ico"]
    return next((c for c in candidates if c.exists()), None)


def set_icon(window):
    """Without this the window gets the default Tk icon (a feather)."""
    icon = icon_source()
    if icon:
        try:
            window.iconbitmap(default=str(icon))
        except Exception:
            pass


def centre(window):
    """Place the window in the middle of the screen - otherwise Windows puts
    it in the top left corner."""
    window.update_idletasks()
    width, height = window.winfo_width(), window.winfo_height()
    x = (window.winfo_screenwidth() - width) // 2
    y = (window.winfo_screenheight() - height) // 2
    window.geometry(f"+{x}+{y}")


def program_source():
    """The folder with the program - inside the installer, or next to it while
    debugging."""
    if getattr(sys, "frozen", False):
        return Path(sys._MEIPASS) / "program"
    return Path(__file__).resolve().parent / "_build" / "dist" / "DaBTDynamicLock"


def is_admin():
    try:
        return ctypes.windll.shell32.IsUserAnAdmin() != 0
    except Exception:
        return False


def quiet(command):
    """Run a command without a console window and return (success, output)."""
    try:
        v = subprocess.run(command, shell=True, capture_output=True, text=True,
                           creationflags=0x08000000)   # CREATE_NO_WINDOW
        return v.returncode == 0, (v.stdout + v.stderr).strip()
    except Exception as e:
        return False, str(e)


def other_instance_running():
    """Is the app running from somewhere else (from source through pythonw)?

    taskkill will not stop such an instance (it is called pythonw.exe), so
    after the installation two would run and the new one would quit right
    away with "already running". It is recognised by the lock the app holds.
    """
    k32 = ctypes.windll.kernel32
    k32.OpenMutexW.restype = ctypes.c_void_p
    h = k32.OpenMutexW(0x00100000, False, MUTEX_NAME)
    if h:
        k32.CloseHandle(ctypes.c_void_p(h))
        return True
    return False


def stop_running_app():
    """Stop the running application and WAIT until it really ends.

    taskkill returns immediately, but the process holds the files for a while
    longer. Without waiting, deleting the old folder fails - and because that
    failure used to be ignored, the error surfaced a step later, while
    copying.
    """
    quiet('taskkill /F /IM DaBTDynamicLock.exe')

    # The app can also run from source - the process is then called
    # pythonw.exe and taskkill by name misses it. So it is looked up by the
    # command line, to hit only our script and no other Python.
    quiet('powershell -NoProfile -Command "Get-CimInstance Win32_Process '
          '-Filter \'Name=\'\'pythonw.exe\'\' or Name=\'\'python.exe\'\'\' | '
          'Where-Object { $_.CommandLine -like \'*dyn_lock.py*\' } | '
          'ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"')

    for _ in range(20):                      # at most 5 seconds
        _, output = quiet('tasklist /FI "IMAGENAME eq DaBTDynamicLock.exe" /NH')
        if "DaBTDynamicLock.exe" not in output and not other_instance_running():
            return True
        time.sleep(0.25)
    return False


def delete_folder(path):
    """Delete a folder including files marked read-only."""
    def on_error(function, name, _):
        try:
            os.chmod(name, 0o700)
            function(name)
        except OSError:
            pass
    shutil.rmtree(path, onerror=on_error)


def create_shortcut(where, target, icon):
    """Create a shortcut (.lnk).

    Paths go into PowerShell in SINGLE quotes - the whole command is already
    inside double ones, so another double quote would end it early and the
    shortcut would silently not be created.
    """
    ps = (f"$w=New-Object -ComObject WScript.Shell;"
          f"$s=$w.CreateShortcut('{where}');"
          f"$s.TargetPath='{target}';"
          f"$s.WorkingDirectory='{Path(target).parent}';"
          f"$s.IconLocation='{icon}';"
          f"$s.Description='Locks the laptop when the phone walks away';"
          f"$s.Save()")
    ok, output = quiet(f'powershell -NoProfile -ExecutionPolicy Bypass '
                       f'-Command "{ps}"')
    return ok and Path(where).exists()    # verify the file really appeared


def relaunch_from_temp():
    """Copy the uninstaller into a temp folder and run it from there.

    Without this the uninstaller runs FROM the folder it is supposed to
    delete, so it cannot delete it and would have to do it through a deferred
    command after it ends - which turned out to be unreliable (on 16.08.2026
    it left 977 files behind). This way it deletes a folder it is not running
    from, right away, and can VERIFY the result.

    Returns True when it has moved (the caller should end).
    """
    if not getattr(sys, "frozen", False) or "--from-temp" in sys.argv:
        return False
    exe = Path(sys.executable)
    if TARGET_DIR not in exe.parents:  # not running from the program folder
        return False
    try:
        copy = Path(tempfile.gettempdir()) / "DaBTDynamicLock-uninstall.exe"
        shutil.copy2(exe, copy)
        subprocess.Popen([str(copy), "--uninstall", "--from-temp"])
        return True
    except OSError:
        return False        # better to try uninstalling from here than not at all


# ---------------------------------------------------------------- install

def install(task, start_menu, desktop, report):
    report("Ukončuji běžící aplikaci…")
    stop_running_app()

    report("Kopíruji program…")
    if TARGET_DIR.exists():
        try:
            delete_folder(TARGET_DIR)
        except OSError:
            pass          # leftovers get overwritten by the copy below
    # dirs_exist_ok: should anything survive from an old installation (a
    # locked file, say), it is overwritten instead of failing the whole
    # installation
    shutil.copytree(program_source(), TARGET_DIR, dirs_exist_ok=True)

    # the uninstaller = a copy of this program
    if getattr(sys, "frozen", False):
        shutil.copy2(sys.executable, TARGET_DIR / "odinstalovat.exe")

    exe = TARGET_DIR / "DaBTDynamicLock.exe"
    problems = []
    if not exe.exists():
        raise RuntimeError("kopírování programu selhalo")
    if start_menu:
        report("Vytvářím zástupce v nabídce Start…")
        if not create_shortcut(STARTMENU, exe, exe):
            problems.append("zástupce do nabídky Start")
    else:
        try:
            STARTMENU.unlink(missing_ok=True)
        except OSError:
            pass

    if desktop:
        report("Vytvářím zástupce na ploše…")
        if not create_shortcut(DESKTOP, exe, exe):
            problems.append("zástupce na plochu")
    else:
        try:
            DESKTOP.unlink(missing_ok=True)
        except OSError:
            pass

    report("Zapisuji do seznamu aplikací…")
    size = sum(f.stat().st_size for f in TARGET_DIR.rglob("*") if f.is_file())
    with winreg.CreateKey(winreg.HKEY_LOCAL_MACHINE, REG_KEY) as k:
        for name, value in [
                ("DisplayName", APP_NAME),
                ("DisplayVersion", VERSION),
                ("Publisher", "David"),
                ("DisplayIcon", str(exe)),
                ("InstallLocation", str(TARGET_DIR)),
                ("UninstallString",
                 f'"{TARGET_DIR / "odinstalovat.exe"}" --uninstall'),
                ("URLInfoAbout", "")]:
            winreg.SetValueEx(k, name, 0, winreg.REG_SZ, value)
        winreg.SetValueEx(k, "EstimatedSize", 0, winreg.REG_DWORD, size // 1024)
        winreg.SetValueEx(k, "NoModify", 0, winreg.REG_DWORD, 1)
        winreg.SetValueEx(k, "NoRepair", 0, winreg.REG_DWORD, 1)

    # The task is created by the application itself (the --autostart-on
    # switch). Why: its tray menu can do the same, and if these were two
    # pieces of code they would drift apart sooner or later - one would set
    # up the restart after a crash and the other would not.
    report("Nastavuji spouštění při přihlášení…"
           if task else "Ruším spouštění při přihlášení…")
    ok, output = quiet(f'"{exe}" '
                       + ("--autostart-on" if task else "--autostart-off"))
    if not ok and task:
        report("Spouštění při přihlášení se nepodařilo nastavit.")

    # The final check - the REAL result is verified, not that the commands
    # finished. The installer must not report success when something is
    # missing.
    report("Kontroluji výsledek…")
    if not exe.exists():
        problems.append("program se nezkopíroval")
    if not (TARGET_DIR / "odinstalovat.exe").exists() \
            and getattr(sys, "frozen", False):
        problems.append("odinstalátor")
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, REG_KEY) as k:
            winreg.QueryValueEx(k, "DisplayName")
    except OSError:
        problems.append("záznam v Nastavení → Aplikace")
    if task:
        task_exists, _ = quiet(f'schtasks /Query /TN "{TASK_NAME}"')
        if not task_exists:
            problems.append("spouštění při přihlášení")

    if problems:
        report("Nepodařilo se: " + ", ".join(problems))
        return False
    report("Hotovo.")
    return True


def delete_after_exit(path, wait_for_pid):
    """Delete the folder as soon as the uninstaller ends.

    The uninstaller runs FROM that folder, so it cannot delete it itself.
    Originally it waited a fixed 3 seconds - but the window stays open until
    the user closes it, so the deletion ran while it was still going and the
    folder, odinstalovat.exe included, stayed behind. Now it waits for the
    REAL end of the process and the deletion is retried.
    """
    if wait_for_pid is None:
        # used by the tests - delete right away, but with retries: Windows
        # holds libraries for a while after the process that loaded them ends
        for attempt in range(6):
            try:
                delete_folder(path)
            except OSError:
                pass
            if not path.exists():
                return True
            time.sleep(1)
        return False
    ps = (f"Wait-Process -Id {wait_for_pid} -Timeout 120 "
          f"-ErrorAction SilentlyContinue; "
          f"foreach ($i in 1..5) {{ "
          f"Remove-Item -LiteralPath '{path}' -Recurse -Force "
          f"-ErrorAction SilentlyContinue; "
          f"if (-not (Test-Path -LiteralPath '{path}')) {{ break }}; "
          f"Start-Sleep -Seconds 2 }}")
    subprocess.Popen(f'powershell -NoProfile -WindowStyle Hidden -Command "{ps}"',
                     shell=True, creationflags=0x08000000)


# ---------------------------------------------------------------- uninstall

def uninstall(delete_data, report, wait_for_pid=None):
    # MIND the order: the task is removed BEFORE the app is stopped. The task
    # has "restart on failure" set, so if the app were killed first, the
    # scheduler would start it again a minute later - and it would recreate
    # the data folder we are deleting right now.
    report("Ruším spouštění při přihlášení…")
    quiet(f'schtasks /Delete /F /TN "{TASK_NAME}"')

    report("Ukončuji aplikaci…")
    stop_running_app()
    # Instead of the task the app may have a shortcut in the Startup folder
    # (it uses that when the user has no rights for the scheduler) - that has
    # to go too, otherwise something would keep starting after the uninstall
    try:
        STARTUP.unlink(missing_ok=True)
    except OSError:
        pass

    report("Odstraňuji zástupce…")
    for lnk in (STARTMENU, DESKTOP):
        try:
            lnk.unlink(missing_ok=True)
        except OSError:
            pass

    report("Mažu záznam ze seznamu aplikací…")
    try:
        winreg.DeleteKey(winreg.HKEY_LOCAL_MACHINE, REG_KEY)
    except FileNotFoundError:
        pass

    left = []
    if delete_data:
        report("Mažu nastavení a historii…")
        for attempt in range(3):
            try:
                delete_folder(DATA)
            except OSError:
                pass
            if not DATA.exists():
                break
            time.sleep(1)
        if DATA.exists():
            # must not fail silently - show what stayed and why
            left = [f.name for f in DATA.iterdir()]

    report("Odstraňuji soubory programu…")
    for attempt in range(6):
        try:
            delete_folder(TARGET_DIR)
        except OSError:
            pass
        if not TARGET_DIR.exists():
            break
        time.sleep(1)
    if TARGET_DIR.exists():
        left.append("soubory programu")
    if left:
        report("Hotovo, ale nešlo smazat: " + ", ".join(left[:4]))
        return False
    report("Hotovo.")
    return True


# ---------------------------------------------------------------- window

class Window:
    def __init__(self, uninstalling):
        self.uninstalling = uninstalling
        self.result = None        # None = the user closed it without acting
        self.clean = True
        self.r = tk.Tk()
        self.r.title(("Odinstalace " if uninstalling else "Instalace ")
                     + APP_NAME)
        self.r.configure(bg=BACKGROUND)
        self.r.resizable(False, False)

        frame = tk.Frame(self.r, bg=BACKGROUND, padx=26, pady=22)
        frame.pack()
        set_icon(self.r)

        self._text(frame, APP_NAME, 17, TEXT, True)
        self._text(frame,
                   "Zamkne notebook, když se od něj vzdálíš s telefonem.",
                   10, GREY, pady=(2, 14))

        card = tk.Frame(frame, bg=CARD, padx=16, pady=14)
        card.pack(fill="x")
        if uninstalling:
            self._text(card, f"Program {APP_NAME} se odinstaluje ze složky:",
                       10, TEXT)
        else:
            self._text(card, f"Program {APP_NAME} se nainstaluje do složky:",
                       10, TEXT)
        self._text(card, f"   {TARGET_DIR}", 10, GREEN_LIGHT, pady=(2, 0))

        # options
        self.start_menu = tk.BooleanVar(value=True)
        self.desktop = tk.BooleanVar(value=True)
        self.option = tk.BooleanVar(value=not uninstalling)

        self.options_frame = tk.Frame(frame, bg=BACKGROUND)
        self.options_frame.pack(fill="x")

        def switch(text, variable, parent=None):
            tk.Checkbutton(
                parent or self.options_frame, text=text, variable=variable,
                bg=BACKGROUND, fg=TEXT,
                selectcolor=CARD, activebackground=BACKGROUND,
                activeforeground=TEXT, font=("Segoe UI", 10),
                borderwidth=0, highlightthickness=0, anchor="w"
            ).pack(anchor="w", pady=(0, 2))

        # Note: a "pin to the taskbar" option is deliberately missing.
        # Windows 11 (build 26200) does not offer that verb at all - verified
        # by listing the verbs of both the shortcut and the .exe; only "Pin to
        # Start" is there. Microsoft blocked it so that installers cannot help
        # themselves to the taskbar.
        tk.Frame(self.options_frame, bg=BACKGROUND, height=4).pack()
        if uninstalling:
            switch("Smazat i nastavení a historii", self.option)
        else:
            switch("Přidat zástupce do nabídky Start", self.start_menu)
            switch("Přidat zástupce na plochu", self.desktop)
            switch("Spouštět automaticky při přihlášení", self.option)

        # wraplength: a long error message wraps instead of being cut off by
        # the window (WinError messages tend to be long)
        self.status = tk.Label(frame, text="", bg=BACKGROUND, fg=GREY,
                               font=("Segoe UI", 9), justify="left",
                               wraplength=440, anchor="w")
        self.status.pack(anchor="w", pady=(8, 10))

        row = tk.Frame(frame, bg=BACKGROUND)
        row.pack(fill="x")
        self.button = tk.Button(
            row, text="Odinstalovat" if uninstalling else "Instalovat",
            command=self.run, bg=RED if uninstalling else GREEN,
            fg="white", font=("Segoe UI", 11), relief="flat",
            padx=22, pady=8, cursor="hand2")
        self.button.pack(side="left")
        centre(self.r)
        tk.Button(row, text="Zavřít", command=self.r.destroy, bg=CARD,
                  fg=TEXT, font=("Segoe UI", 11), relief="flat",
                  padx=18, pady=8, cursor="hand2").pack(side="right")

    def _text(self, parent, s, size, color, bold=False, pady=(0, 0),
              anchor="w"):
        label = tk.Label(parent, text=s, bg=parent["bg"], fg=color,
                         justify="left", wraplength=430,
                         font=("Segoe UI", size,
                               "bold" if bold else "normal"))
        label.pack(anchor=anchor, pady=pady)
        return label

    def report(self, text):
        self.status.config(text=text)
        self.r.update()

    def run(self):
        self.button.config(state="disabled")
        try:
            if self.uninstalling:
                self.clean = uninstall(self.option.get(), self.report,
                                       wait_for_pid=os.getpid())
            else:
                ok = install(self.option.get(), self.start_menu.get(),
                             self.desktop.get(), self.report)
                if not ok:
                    self.status.config(fg="#f87171")
                    self.button.config(state="normal")
                    return
            self.result = True
            self.r.destroy()
        except Exception as e:
            self.status.config(text="Chyba: " + str(e), fg="#f87171")
            self.button.config(state="normal")


def is_uninstall(argv, file_name):
    """Should the program behave as the uninstaller?

    Either the argument OR the file name decides. The name is here because
    uninstalling from Windows Settings does pass the argument, but when the
    user runs odinstalovat.exe by double-clicking it, no argument arrives -
    and without this check the INSTALLATION would start (and fail, because it
    runs from the very folder it would be overwriting).

    Both switch spellings are accepted: an installation made by an older
    build has the Czech one stored in the registry under UninstallString.
    """
    return ("--uninstall" in argv or "--odinstalovat" in argv
            or Path(file_name).name.lower().startswith("odinstal"))


# ------------------------------------------------------- result window

class ResultWindow:
    """A second, SEPARATE window with the result and the next steps.

    A new window is opened on purpose instead of swapping the contents of the
    first one: a new window flashes in the taskbar, so the user notices it
    even while doing something else in the meantime (David 16.08.2026).
    """

    def __init__(self, uninstalling, all_ok):
        self.uninstalling = uninstalling
        self.r = tk.Tk()
        self.r.title(("Odinstalace " if uninstalling else "Instalace ")
                     + APP_NAME)
        self.r.configure(bg=BACKGROUND)
        self.r.resizable(False, False)

        frame = tk.Frame(self.r, bg=BACKGROUND, padx=26, pady=22)
        frame.pack()

        heading = "Odinstalováno" if uninstalling else "Nainstalováno"
        tk.Label(frame, text=heading, bg=BACKGROUND,
                 fg=GREEN_LIGHT if all_ok else "#fbbf24",
                 font=("Segoe UI", 17, "bold")).pack(anchor="w")

        if uninstalling:
            description = (f"{APP_NAME} byl odstraněn z počítače."
                           if all_ok else
                           f"{APP_NAME} byl odstraněn, ale některé soubory se "
                           f"nepodařilo smazat. Zkus to po restartu počítače.")
        else:
            description = ("Aby hlídání fungovalo, musí vysílat i telefon — "
                           "nainstaluj do něj přiloženou aplikaci. Postup "
                           "najdeš v návodu.")
        tk.Label(frame, text=description, bg=BACKGROUND, fg=GREY,
                 justify="left", wraplength=430,
                 font=("Segoe UI", 10)).pack(anchor="w", pady=(6, 14))

        self.launch = tk.BooleanVar(value=not uninstalling)
        self.manual = tk.BooleanVar(value=False)
        if not uninstalling:
            # No "open the phone app folder" option: the APK is not shipped
            # with the installer any more, it is downloaded from the GitHub
            # release through the link in the app.
            for text, variable in [("Spustit " + APP_NAME, self.launch),
                                   ("Otevřít návod", self.manual)]:
                tk.Checkbutton(frame, text=text, variable=variable,
                               bg=BACKGROUND, fg=TEXT, selectcolor=CARD,
                               anchor="w", activebackground=BACKGROUND,
                               activeforeground=TEXT, font=("Segoe UI", 10),
                               borderwidth=0,
                               highlightthickness=0).pack(anchor="w",
                                                          pady=(0, 2))

        tk.Button(frame, text="Dokončit", command=self.finish, bg=GREEN,
                  fg="white", font=("Segoe UI", 11), relief="flat",
                  padx=22, pady=8, cursor="hand2").pack(anchor="e",
                                                        pady=(16, 0))

        set_icon(self.r)
        centre(self.r)
        self.flash()

    def flash(self):
        """Flash the taskbar button when the window does not get focus."""
        try:
            hwnd = (ctypes.windll.user32.GetParent(self.r.winfo_id())
                    or self.r.winfo_id())
            struct = ctypes.c_uint * 5    # FLASHWINFO
            info = struct(ctypes.sizeof(struct), hwnd, 0x0000000C, 5, 0)
            ctypes.windll.user32.FlashWindowEx(ctypes.byref(info))
        except Exception:
            pass

    def finish(self):
        if not self.uninstalling:
            if self.manual.get():
                manual = next(TARGET_DIR.glob("*INFO*.txt"), None)
                if manual:
                    os.startfile(manual)
            if self.launch.get():
                if other_instance_running():
                    ctypes.windll.user32.MessageBoxW(
                        0, "Aplikace se nespustila, protože už běží jiná "
                           "kopie.\n\nUkonči ji (ikona v liště → Konec) "
                           "a spusť tuto.", APP_NAME, 0x40)
                else:
                    subprocess.Popen([str(TARGET_DIR / "DaBTDynamicLock.exe")])
        self.r.destroy()


def main():
    uninstalling = is_uninstall(sys.argv, sys.executable)
    if not is_admin():
        ctypes.windll.user32.MessageBoxW(
            0, "Spusť tento program jako správce.\n\n"
               "Zápis do složky Program Files to vyžaduje.",
            APP_NAME, 0x10)
        return 1
    if uninstalling and relaunch_from_temp():
        return 0            # the copy in the temp folder took over

    first = Window(uninstalling)
    first.r.mainloop()          # the first window closes once the work is done
    if first.result:
        ResultWindow(uninstalling, first.clean).r.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
