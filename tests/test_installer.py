"""
Test of installing and uninstalling - it goes through the WHOLE cycle.

All targets are redirected into a temp folder and into an HKCU key, so the
test needs no administrator rights and never touches the real installation.
The code under test is the same one though - copying, shortcuts, the registry
entry, the final check and the cleanup during uninstallation.

Run:  py tests/test_installer.py
"""

import os
import tempfile
import shutil
import sys
import winreg
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "installer"))
import installer as I

TEMP_DIR = Path(os.environ.get("TEMP", tempfile.gettempdir())) / "ddl-install-test"
failures = []


def check(description, expected, actual):
    ok = expected == actual
    print(f"  {'OK ' if ok else 'FAIL'}  {description}: {actual}"
          + ("" if ok else f"   (expected {expected})"))
    if not ok:
        failures.append(description)


def prepare():
    """Redirect every target so the test never touches a real installation.

    The APPDATA variable is redirected as well - the packaged application uses
    it to decide where to store its settings and log. Without that the tests
    would write into the real user profile (and make a mess there).
    """
    # Leftovers from a previous run may not delete right away (Windows holds
    # the libraries for a while), so we do not rely on it and only top the
    # folder up - exist_ok, not a hard mkdir.
    shutil.rmtree(TEMP_DIR, ignore_errors=True)
    TEMP_DIR.mkdir(parents=True, exist_ok=True)
    I.TARGET_DIR = TEMP_DIR / "Program Files" / "Da BT Dynamic Lock"
    I.STARTMENU = TEMP_DIR / "Start Menu" / f"{I.APP_NAME}.lnk"
    I.DESKTOP = TEMP_DIR / "Desktop" / f"{I.APP_NAME}.lnk"
    I.DATA = TEMP_DIR / "AppData" / "Da BT Dynamic Lock"
    I.STARTUP = TEMP_DIR / "Startup" / f"{I.APP_NAME}.lnk"
    for p in (I.STARTMENU, I.DESKTOP, I.STARTUP, I.DATA):
        p.parent.mkdir(parents=True, exist_ok=True)
    I.DATA.mkdir(exist_ok=True)
    # registry: a test key in HKCU instead of HKLM (admin only)
    I.REG_KEY = r"Software\DaBTDynamicLock-test"
    os.environ["APPDATA"] = str(TEMP_DIR / "AppData-profile")
    (TEMP_DIR / "AppData-profile").mkdir(parents=True, exist_ok=True)
    I._original_registry = winreg.HKEY_LOCAL_MACHINE
    winreg.HKEY_LOCAL_MACHINE = winreg.HKEY_CURRENT_USER


def cleanup():
    winreg.HKEY_LOCAL_MACHINE = I._original_registry
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER,
                         r"Software\DaBTDynamicLock-test")
    except OSError:
        pass
    shutil.rmtree(TEMP_DIR, ignore_errors=True)


def report(text):
    print(f"       … {text}")


print("Test of installing and uninstalling")
print("=" * 60)
prepare()
try:
    if not I.program_source().exists():
        print("SKIPPED - run installer/build_installer.ps1 first")
        sys.exit(0)

    print("\n1) INSTALLATION with all options")
    # task=False: creating the scheduled task is a real change to the system,
    # it is tested separately through the switch in the application itself
    result = I.install(task=False, start_menu=True, desktop=True,
                       report=report)
    check("installation reported success", True, result)
    check("program copied", True,
          (I.TARGET_DIR / "DaBTDynamicLock.exe").exists())
    check("default settings shipped", True,
          (I.TARGET_DIR / "config.default.json").exists())
    # the APK is deliberately NOT shipped - it is downloaded from the GitHub
    # release through the link in the app
    check("phone app NOT shipped with the installer", False,
          (I.TARGET_DIR / "telefon").exists())
    check("shortcut in the Start menu", True, I.STARTMENU.exists())
    check("shortcut on the desktop", True, I.DESKTOP.exists())
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, I.REG_KEY) as k:
            name = winreg.QueryValueEx(k, "DisplayName")[0]
            uninstall_cmd = winreg.QueryValueEx(k, "UninstallString")[0]
            size = winreg.QueryValueEx(k, "EstimatedSize")[0]
    except OSError as e:
        name, uninstall_cmd, size = f"error: {e}", "", 0
    check("entry in the list of applications", I.APP_NAME, name)
    check("uninstall command filled in", True, "--uninstall" in uninstall_cmd)
    check("size filled in (MB)", True, 10 < size / 1024 < 200)

    print("\n2) REPEATED INSTALLATION (over an existing one)")
    result = I.install(task=False, start_menu=True, desktop=True,
                       report=report)
    check("succeeds a second time as well", True, result)

    print("\n3) REINSTALL WHILE THE INSTALLED APP IS RUNNING")
    # Exactly this case failed in practice with "WinError 183: file already
    # exists" - taskkill returned before the process released the files,
    # deleting the old folder failed silently and the copy then did not go
    # through.
    import subprocess
    import time
    running_exe = I.TARGET_DIR / "DaBTDynamicLock.exe"
    process = subprocess.Popen([str(running_exe), "--dry-run", "--no-dialog"])
    time.sleep(6)
    check("the app is running", None, process.poll())
    result = I.install(task=False, start_menu=True, desktop=True,
                       report=report)
    check("reinstall went through over the running app", True, result)
    check("the running app was stopped", True, process.poll() is not None)
    check("the program is in place", True, running_exe.exists())

    print("\n4) INSTALLATION WITHOUT SHORTCUTS (removes the existing ones)")
    result = I.install(task=False, start_menu=False, desktop=False,
                       report=report)
    check("succeeds", True, result)
    check("Start menu shortcut removed", False, I.STARTMENU.exists())
    check("desktop shortcut removed", False, I.DESKTOP.exists())

    print("\n5) UNINSTALL (keeping the settings)")
    (I.DATA / "config.json").write_text("{}", encoding="utf-8")
    I.STARTUP.write_text("", encoding="utf-8")   # leftover from the fallback
    result = I.uninstall(delete_data=False, report=report)
    check("uninstall reported success", True, result)
    check("program deleted", False, I.TARGET_DIR.exists())
    check("Startup shortcut removed", False, I.STARTUP.exists())
    check("settings KEPT", True, (I.DATA / "config.json").exists())
    try:
        winreg.OpenKey(winreg.HKEY_CURRENT_USER, I.REG_KEY).Close()
        entry = True
    except OSError:
        entry = False
    check("entry removed from the list of applications", False, entry)

    print("\n6) INSTALLATION WHILE THE APP RUNS FROM SOURCE")
    # taskkill by process name misses this case - the app runs as pythonw.exe.
    # This verifies the installer finds it by the command line and stops it;
    # otherwise two copies would run after the installation.
    import subprocess
    import time
    project = Path(__file__).resolve().parent.parent
    pyw = project / ".venv" / "Scripts" / "pythonw.exe"

    def how_many_running():
        command = ("powershell -NoProfile -Command "
                   "\"(@(Get-CimInstance Win32_Process -Filter "
                   "'Name=''pythonw.exe''' | Where-Object "
                   "{ $_.CommandLine -like '*dyn_lock.py*' })).Count\"")
        return I.quiet(command)[1].strip()

    if pyw.exists():
        subprocess.Popen([str(pyw), str(project / "windows" / "dyn_lock.py"),
                          "--dry-run", "--no-dialog"])
        time.sleep(6)
        before = how_many_running()
        I.stop_running_app()
        time.sleep(1)
        after = how_many_running()
        # more than one may be running (the user may be starting the app right
        # now); what matters is that at least one ran before and none after
        check("the app ran from source", True, int(before or 0) >= 1)
        check("the installer stopped them all", "0", after)
    else:
        print("  SKIPPED - no .venv")

    print("\n7) ROLE DETECTION (installer vs uninstaller)")
    check("setup.exe without an argument = install", False,
          I.is_uninstall([], r"C:\x\DaBTDynamicLock-setup.exe"))
    check("setup.exe with an argument = uninstall", True,
          I.is_uninstall(["--uninstall"], r"C:\x\DaBTDynamicLock-setup.exe"))
    check("odinstalovat.exe double-clicked = uninstall", True,
          I.is_uninstall([],
                         r"C:\Program Files\Da BT Dynamic Lock\odinstalovat.exe"))
    check("odinstalovat.exe from Settings = uninstall", True,
          I.is_uninstall(["--uninstall"],
                         r"C:\Program Files\Da BT Dynamic Lock\odinstalovat.exe"))
    # an installation made by an older build has the Czech switch stored in
    # the registry - it has to keep working
    check("the old --odinstalovat switch still works", True,
          I.is_uninstall(["--odinstalovat"], r"C:\x\DaBTDynamicLock-setup.exe"))

    print("\n8) UNINSTALL (deleting the settings)")
    I.install(task=False, start_menu=False, desktop=False, report=report)
    I.uninstall(delete_data=True, report=report)
    check("settings deleted", False, I.DATA.exists())
finally:
    cleanup()

print("\n" + ("ALL OK" if not failures else f"FAILURES: {failures}"))
sys.exit(1 if failures else 0)
