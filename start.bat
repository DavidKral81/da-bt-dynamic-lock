@echo off
rem Spusti Da BT Dynamic Lock na pozadi (bez okna konzole).
rem Pro zkouseni bez zamykani obrazovky pouzij: start.bat --dry-run
cd /d "%~dp0"
start "" ".venv\Scripts\pythonw.exe" "windows\dyn_lock.py" %*
