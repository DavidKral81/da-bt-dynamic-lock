@echo off
rem Starts Da BT Dynamic Lock in the background (no console window).
rem To try it out without really locking the screen: start.bat --dry-run
cd /d "%~dp0"
start "" ".venv\Scripts\pythonw.exe" "windows\dyn_lock.py" %*
