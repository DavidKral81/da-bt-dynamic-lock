"""
Installer and uninstaller for Da BT Dynamic Lock.

One program for both roles:
  no arguments   -> install
  --uninstall    -> uninstall (during installation this copy is saved into
                    the program folder as uninstall.exe)

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

Both languages come from windows/texts.py - the SAME dictionary the app uses,
not a copy of it. Two dictionaries would drift apart, and one shared
`missing()` test then covers the installer too. PyInstaller finds the module
through --paths in build_installer.ps1.
"""

import ctypes
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import tkinter as tk
import winreg
from pathlib import Path

# Running from source the module sits in a sibling folder; inside the packaged
# .exe PyInstaller has already put it next to this one.
if not getattr(sys, "frozen", False):
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "windows"))
import marks
import texts
from texts import t as tx
from version import VERSION

APP_NAME = "Da BT Dynamic Lock"
REG_KEY = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DaBTDynamicLock"
TASK_NAME = APP_NAME

# The mutex name the application holds. It MUST match the one in
# windows/dyn_lock.py - when the two drift apart, the installer stops
# noticing a running app and reinstalls over it.
MUTEX_NAME = "DaBTDynamicLock_single_instance"

# The uninstaller is a copy of this program left in the program folder. The
# name is a constant because three places have to agree on it: what gets
# copied, what UninstallString points at, and what is_uninstall recognises.
UNINSTALLER_NAME = "uninstall.exe"

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


# ---------------------------------------------------------------- language

def system_language():
    """"cs" when Windows itself runs in Czech, otherwise English.

    Only the primary language matters (0x05 = Czech), so cs-CZ and any other
    Czech variant both count.
    """
    try:
        langid = ctypes.windll.kernel32.GetUserDefaultUILanguage()
        return "cs" if (langid & 0x3FF) == 0x05 else "en"
    except OSError:
        # A missing UI language must not stop an installation - English is
        # the safer guess for a machine that cannot answer.
        return "en"


def stored_language():
    """The language picked during installation, or None.

    Kept in the registry rather than in the user's config.json: the
    uninstaller runs elevated, so %APPDATA% points at the ADMINISTRATOR's
    profile, not at the profile of whoever uses the app. The registry key is
    the same one the uninstall entry lives in, so it disappears with it.
    """
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, REG_KEY) as key:
            value, _ = winreg.QueryValueEx(key, "InstallerLanguage")
            return value if value in dict(texts.LANGUAGES) else None
    except OSError:
        return None


def ship_language(code):
    """Hand the chosen language over to the application itself.

    The app builds its config.json out of config.default.json on first run
    (see load_cfg in dyn_lock.py), so writing it there is what makes the app
    come up in the language picked here - no second mechanism needed.

    Returns False when it did not work, so the caller can report it instead
    of pretending everything went fine.
    """
    template = TARGET_DIR / "config.default.json"
    if not template.exists():
        return False
    try:
        settings = json.loads(template.read_text(encoding="utf-8"))
        settings["language"] = code
        template.write_text(json.dumps(settings, indent=2, ensure_ascii=False),
                            encoding="utf-8")
        return True
    except (OSError, ValueError):
        return False


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
    report(tx("ins_stopping"))
    stop_running_app()

    report(tx("ins_copying"))
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
        shutil.copy2(sys.executable, TARGET_DIR / UNINSTALLER_NAME)

    exe = TARGET_DIR / "DaBTDynamicLock.exe"
    problems = []
    if not exe.exists():
        raise RuntimeError(tx("ins_err_copy"))

    # The app should come up in the language picked here, not in the one the
    # template happens to carry.
    ship_language(texts.language())

    if start_menu:
        report(tx("ins_startmenu"))
        if not create_shortcut(STARTMENU, exe, exe):
            problems.append(tx("ins_prob_startmenu"))
    else:
        try:
            STARTMENU.unlink(missing_ok=True)
        except OSError:
            pass

    if desktop:
        report(tx("ins_desktop"))
        if not create_shortcut(DESKTOP, exe, exe):
            problems.append(tx("ins_prob_desktop"))
    else:
        try:
            DESKTOP.unlink(missing_ok=True)
        except OSError:
            pass

    report(tx("ins_registry"))
    size = sum(f.stat().st_size for f in TARGET_DIR.rglob("*") if f.is_file())
    with winreg.CreateKey(winreg.HKEY_LOCAL_MACHINE, REG_KEY) as k:
        for name, value in [
                ("DisplayName", APP_NAME),
                ("DisplayVersion", VERSION),
                ("Publisher", "David"),
                ("DisplayIcon", str(exe)),
                ("InstallLocation", str(TARGET_DIR)),
                ("UninstallString",
                 f'"{TARGET_DIR / UNINSTALLER_NAME}" --uninstall'),
                ("URLInfoAbout", ""),
                # not a Windows value - it is how the uninstaller knows which
                # language to speak when it runs a year from now
                ("InstallerLanguage", texts.language())]:
            winreg.SetValueEx(k, name, 0, winreg.REG_SZ, value)
        winreg.SetValueEx(k, "EstimatedSize", 0, winreg.REG_DWORD, size // 1024)
        winreg.SetValueEx(k, "NoModify", 0, winreg.REG_DWORD, 1)
        winreg.SetValueEx(k, "NoRepair", 0, winreg.REG_DWORD, 1)

    # The task is created by the application itself (the --autostart-on
    # switch). Why: its tray menu can do the same, and if these were two
    # pieces of code they would drift apart sooner or later - one would set
    # up the restart after a crash and the other would not.
    report(tx("ins_task_on") if task else tx("ins_task_off"))
    ok, output = quiet(f'"{exe}" '
                       + ("--autostart-on" if task else "--autostart-off"))
    if not ok and task:
        report(tx("ins_task_failed"))

    # The final check - the REAL result is verified, not that the commands
    # finished. The installer must not report success when something is
    # missing.
    report(tx("ins_checking"))
    if not exe.exists():
        problems.append(tx("ins_prob_program"))
    if not (TARGET_DIR / UNINSTALLER_NAME).exists() \
            and getattr(sys, "frozen", False):
        problems.append(tx("ins_prob_uninstaller"))
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, REG_KEY) as k:
            winreg.QueryValueEx(k, "DisplayName")
    except OSError:
        problems.append(tx("ins_prob_registry"))
    if task:
        task_exists, _ = quiet(f'schtasks /Query /TN "{TASK_NAME}"')
        if not task_exists:
            problems.append(tx("ins_prob_task"))

    if problems:
        report(tx("ins_failed", what=", ".join(problems)))
        return False
    report(tx("ins_done"))
    return True


def delete_after_exit(path, wait_for_pid):
    """Delete the folder as soon as the uninstaller ends.

    The uninstaller runs FROM that folder, so it cannot delete it itself.
    Originally it waited a fixed 3 seconds - but the window stays open until
    the user closes it, so the deletion ran while it was still going and the
    folder, uninstall.exe included, stayed behind. Now it waits for the
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
    report(tx("ins_task_off"))
    quiet(f'schtasks /Delete /F /TN "{TASK_NAME}"')

    report(tx("ins_stopping"))
    stop_running_app()
    # Instead of the task the app may have a shortcut in the Startup folder
    # (it uses that when the user has no rights for the scheduler) - that has
    # to go too, otherwise something would keep starting after the uninstall
    try:
        STARTUP.unlink(missing_ok=True)
    except OSError:
        pass

    report(tx("uni_shortcuts"))
    for lnk in (STARTMENU, DESKTOP):
        try:
            lnk.unlink(missing_ok=True)
        except OSError:
            pass

    report(tx("uni_registry"))
    try:
        winreg.DeleteKey(winreg.HKEY_LOCAL_MACHINE, REG_KEY)
    except FileNotFoundError:
        pass

    left = []
    if delete_data:
        report(tx("uni_data"))
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

    report(tx("uni_files"))
    for attempt in range(6):
        try:
            delete_folder(TARGET_DIR)
        except OSError:
            pass
        if not TARGET_DIR.exists():
            break
        time.sleep(1)
    if TARGET_DIR.exists():
        left.append(tx("uni_prob_files"))
    if left:
        report(tx("uni_partial", what=", ".join(left[:4])))
        return False
    report(tx("ins_done"))
    return True


# ---------------------------------------------------------------- window

def switch_row(parent, text, variable):
    """A checkbox row - the mark is DRAWN, not left to Tk.

    Tk's own checkbox is a few pixels of white and looks sloppy next to the
    app, which draws its marks by hand. Both now use the same drawing
    (marks.py), so the installer and the app cannot end up looking different.

    Used by BOTH windows - fixing the mark in one and leaving the other with
    Tk's default is exactly the mistake this replaces.

    The whole row is clickable, not just the little square.
    """
    row = tk.Frame(parent, bg=BACKGROUND, cursor="hand2")
    row.pack(anchor="w", pady=2, fill="x")
    canvas = tk.Canvas(row, width=marks.SIZE, height=marks.SIZE,
                       bg=BACKGROUND, highlightthickness=0, cursor="hand2")
    canvas.pack(side="left", padx=(0, 9))
    label = tk.Label(row, text=text, bg=BACKGROUND, fg=TEXT,
                     font=("Segoe UI", 10), cursor="hand2", justify="left")
    label.pack(side="left")

    # kept on the row, so it dies with the row - a language switch throws
    # these widgets away and rebuilds them
    row.hovered = False

    def redraw():
        marks.draw(canvas, variable.get(), False, row.hovered)

    def toggle(_=None):
        variable.set(not variable.get())
        redraw()

    def hover(state):
        row.hovered = state
        redraw()

    for widget in (row, canvas, label):
        widget.bind("<Button-1>", toggle)
        widget.bind("<Enter>", lambda e=None: hover(True))
        widget.bind("<Leave>", lambda e=None: hover(False))
    redraw()
    return row


class Window:
    def __init__(self, uninstalling):
        self.uninstalling = uninstalling
        self.result = None        # None = the user closed it without acting
        self.clean = True
        self.r = tk.Tk()
        self.r.configure(bg=BACKGROUND)
        self.r.resizable(False, False)
        set_icon(self.r)

        # These live on the window, NOT inside _build: the contents are
        # thrown away on a language switch, and a choice made before it must
        # not be thrown away with them.
        self.start_menu = tk.BooleanVar(value=True)
        self.desktop = tk.BooleanVar(value=True)
        self.option = tk.BooleanVar(value=not uninstalling)

        self._build()

    def _build(self):
        """Build the contents - and build them AGAIN after a language switch.

        Rebuilding beats relabelling for the same reason the app does it
        (dyn_lock.py): relabelling means keeping a reference to every widget,
        and sooner or later one is forgotten and stays in the old language.
        """
        for child in self.r.winfo_children():
            child.destroy()
        self.r.title(tx("ins_title_uninstall" if self.uninstalling
                        else "ins_title_install", app=APP_NAME))

        frame = tk.Frame(self.r, bg=BACKGROUND, padx=26, pady=22)
        frame.pack()

        header = tk.Frame(frame, bg=BACKGROUND)
        header.pack(fill="x")
        tk.Label(header, text=APP_NAME, bg=BACKGROUND, fg=TEXT,
                 font=("Segoe UI", 17, "bold")).pack(side="left")
        self._language_picker(header)

        self._text(frame, tx("ins_subtitle"), 10, GREY, pady=(2, 14))

        card = tk.Frame(frame, bg=CARD, padx=16, pady=14)
        card.pack(fill="x")
        self._text(card, tx("ins_from_folder" if self.uninstalling
                            else "ins_to_folder", app=APP_NAME), 10, TEXT)
        self._text(card, f"   {TARGET_DIR}", 10, GREEN_LIGHT, pady=(2, 0))

        self.options_frame = tk.Frame(frame, bg=BACKGROUND)
        self.options_frame.pack(fill="x")

        # Note: a "pin to the taskbar" option is deliberately missing.
        # Windows 11 (build 26200) does not offer that verb at all - verified
        # by listing the verbs of both the shortcut and the .exe; only "Pin to
        # Start" is there. Microsoft blocked it so that installers cannot help
        # themselves to the taskbar.
        tk.Frame(self.options_frame, bg=BACKGROUND, height=4).pack()
        if self.uninstalling:
            self._switch(tx("uni_opt_data"), self.option)
        else:
            self._switch(tx("ins_opt_startmenu"), self.start_menu)
            self._switch(tx("ins_opt_desktop"), self.desktop)
            self._switch(tx("ins_opt_autostart"), self.option)

        # wraplength: a long error message wraps instead of being cut off by
        # the window (WinError messages tend to be long)
        self.status = tk.Label(frame, text="", bg=BACKGROUND, fg=GREY,
                               font=("Segoe UI", 9), justify="left",
                               wraplength=440, anchor="w")
        self.status.pack(anchor="w", pady=(8, 10))

        row = tk.Frame(frame, bg=BACKGROUND)
        row.pack(fill="x")
        self.button = tk.Button(
            row, text=tx("ins_btn_uninstall") if self.uninstalling
            else tx("ins_btn_install"),
            command=self.run, bg=RED if self.uninstalling else GREEN,
            fg="white", font=("Segoe UI", 11), relief="flat",
            padx=22, pady=8, cursor="hand2")
        self.button.pack(side="left")
        tk.Button(row, text=tx("ins_btn_close"), command=self.r.destroy,
                  bg=CARD, fg=TEXT, font=("Segoe UI", 11), relief="flat",
                  padx=18, pady=8, cursor="hand2").pack(side="right")
        centre(self.r)

    def _switch(self, text, variable):
        return switch_row(self.options_frame, text, variable)

    def _language_picker(self, parent):
        """The Czech/English chooser.

        It is in BOTH roles on purpose: someone who installed the app in a
        language they cannot read has to be able to switch before uninstalling
        it too.

        A Menubutton rather than an OptionMenu so the arrow can be a readable
        character instead of the default few-pixel one, and so the choice
        arrives as a language CODE - the displayed name is never a data value.
        """
        current = dict(texts.LANGUAGES)[texts.language()]
        button = tk.Menubutton(parent, text=f"{current}  ▾", bg=CARD, fg=TEXT,
                               activebackground=CARD, activeforeground=TEXT,
                               font=("Segoe UI", 10), relief="flat",
                               borderwidth=0, highlightthickness=0,
                               padx=12, pady=5, cursor="hand2")
        menu = tk.Menu(button, tearoff=0, bg=CARD, fg=TEXT,
                       activebackground=GREEN, activeforeground="white",
                       font=("Segoe UI", 10), borderwidth=0)
        for code, name in texts.LANGUAGES:
            menu.add_command(label=name,
                             command=lambda c=code: self._change_language(c))
        button.config(menu=menu)
        button.pack(side="right")

    def _change_language(self, code):
        if code == texts.language():
            return
        texts.set_language(code)
        self._build()

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
            self.status.config(text=tx("ins_error", error=e), fg="#f87171")
            self.button.config(state="normal")


def is_uninstall(argv, file_name):
    """Should the program behave as the uninstaller?

    Either the argument OR the file name decides. The name is here because
    uninstalling from Windows Settings does pass the argument, but when the
    user runs uninstall.exe by double-clicking it, no argument arrives - and
    without this check the INSTALLATION would start (and fail, because it runs
    from the very folder it would be overwriting).

    The old Czech name and switch are still recognised: an installation made
    by an earlier build has "odinstalovat.exe" on disk and the Czech switch in
    the registry, and it still has to be possible to uninstall it.
    """
    name = Path(file_name).name.lower()
    return ("--uninstall" in argv or "--odinstalovat" in argv
            or name.startswith("uninstall") or name.startswith("odinstal"))


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
        self.r.title(tx("ins_title_uninstall" if uninstalling
                        else "ins_title_install", app=APP_NAME))
        self.r.configure(bg=BACKGROUND)
        self.r.resizable(False, False)

        frame = tk.Frame(self.r, bg=BACKGROUND, padx=26, pady=22)
        frame.pack()

        # No language chooser here on purpose - by this point the work is
        # done and the language was settled in the first window.
        heading = tx("ins_head_uninstalled" if uninstalling
                     else "ins_head_installed")
        tk.Label(frame, text=heading, bg=BACKGROUND,
                 fg=GREEN_LIGHT if all_ok else "#fbbf24",
                 font=("Segoe UI", 17, "bold")).pack(anchor="w")

        if uninstalling:
            description = tx("uni_ok_desc" if all_ok else "uni_partial_desc",
                             app=APP_NAME)
        else:
            description = tx("ins_ok_desc")
        tk.Label(frame, text=description, bg=BACKGROUND, fg=GREY,
                 justify="left", wraplength=430,
                 font=("Segoe UI", 10)).pack(anchor="w", pady=(6, 14))

        self.launch = tk.BooleanVar(value=not uninstalling)
        self.manual = tk.BooleanVar(value=False)
        if not uninstalling:
            # No "open the phone app folder" option: the APK is not shipped
            # with the installer any more, it is downloaded from the GitHub
            # release through the link in the app.
            for text, variable in [(tx("ins_opt_launch", app=APP_NAME),
                                    self.launch),
                                   (tx("ins_opt_manual"), self.manual)]:
                switch_row(frame, text, variable)

        tk.Button(frame, text=tx("ins_btn_finish"), command=self.finish,
                  bg=GREEN,
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
                # Both manuals are shipped, so the language decides which one
                # opens - the SAME patterns _open_manual uses in dyn_lock.py.
                # A bare *INFO*.txt would be a coin toss between the two.
                patterns = (["*INFO-READ*.txt", "*INFO*.txt"]
                            if texts.language() == "en"
                            else ["*INFO-CTI*.txt", "*INFO*.txt"])
                manual = next((m for p in patterns
                               for m in TARGET_DIR.glob(p)), None)
                if manual:
                    os.startfile(manual)
            if self.launch.get():
                if other_instance_running():
                    ctypes.windll.user32.MessageBoxW(
                        0, tx("ins_msg_running"), APP_NAME, 0x40)
                else:
                    subprocess.Popen([str(TARGET_DIR / "DaBTDynamicLock.exe")])
        self.r.destroy()


def main():
    uninstalling = is_uninstall(sys.argv, sys.executable)

    # What language to open in: the one chosen when installing (the
    # uninstaller runs long after and nobody picks it again), otherwise the
    # language of Windows itself. Either way the chooser in the window wins.
    texts.set_language(stored_language() or system_language())

    if not is_admin():
        ctypes.windll.user32.MessageBoxW(
            0, tx("ins_msg_admin"), APP_NAME, 0x10)
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
