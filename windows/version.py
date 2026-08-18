"""The version number, in one place.

The app (dyn_lock.py) and the installer (installer.py) both import VERSION
from here, and build_installer.ps1 reads it straight out of this file to
generate the Windows VERSIONINFO resource for both .exe files. One constant
means the line in the app window, the entry in "Installed apps" and the file
properties cannot end up disagreeing.

To release a new version change it HERE ONLY, then rebuild with
installer\\build_installer.ps1.
"""

VERSION = "1.0"
