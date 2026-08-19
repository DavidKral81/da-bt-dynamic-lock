"""Facts about the project that more than one program needs.

Both the app (dyn_lock.py) and the installer (installer.py) import from here,
so neither can hold a copy that quietly goes out of date.

VERSION is read by build_installer.ps1 as well, which generates the Windows
VERSIONINFO resource for both .exe files out of it, and by phone/build.ps1
for the APK's --version-name AND its --version-code (1.0 -> 10000), the
number Android actually compares releases by. So the version shown in the app
window, in the file properties, in "Installed apps" and in the phone's app
info all come from this one line.

To release a new version change it HERE ONLY, then rebuild with
installer\\build_installer.ps1 (and phone\\build.ps1 for the APK).
"""

VERSION = "1.1"

# Where the releases live. The app opens PROJECT_URL + "/releases/latest" for
# the phone app, and the installer offers the same page when it finishes -
# the APK is deliberately not shipped with the installer.
PROJECT_URL = "https://github.com/DavidKral81/da-bt-dynamic-lock"
