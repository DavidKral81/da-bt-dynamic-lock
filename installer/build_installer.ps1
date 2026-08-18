# Builds DaBTDynamicLock-setup.exe.
#
# Two steps:
#   1. PyInstaller packs Python, the libraries and dyn_lock.py into the
#      program folder - the target machine then needs no Python
#   2. PyInstaller packs the installer INCLUDING that folder into a single
#      .exe
#
# Run:  powershell -ExecutionPolicy Bypass -File build_installer.ps1
#
# No diacritics in this file on purpose: PowerShell 5.1 reads .ps1 as ANSI,
# so accented characters would come out mangled.

$ErrorActionPreference = "Stop"
$here    = $PSScriptRoot
$project = Split-Path $here -Parent
$python  = "$project\.venv\Scripts\python.exe"
$icon    = "$project\windows\icons\dyn_lock_tray.ico"
$build   = "$here\_build"

if (-not (Test-Path $python)) { throw "No .venv - run the app from source first" }

# --- 1) the program ---------------------------------------------------
Write-Host "1/2  packing the program"
if (Test-Path $build) { Remove-Item $build -Recurse -Force }

& $python -m PyInstaller `
    --noconfirm --clean --windowed `
    --name "DaBTDynamicLock" --icon $icon `
    --add-data "$icon;." `
    --distpath "$build\dist" --workpath "$build\work" --specpath "$build" `
    "$project\windows\dyn_lock.py"
if ($LASTEXITCODE -ne 0) { throw "PyInstaller (program) failed" }

# the default settings, the manuals and the phone app belong with the program
# the template of default values, NOT the developer's own config.json
Copy-Item "$project\windows\config.default.json" "$build\dist\DaBTDynamicLock\" -Force
# The manual is looked up by pattern - its name has diacritics and PowerShell
# 5.1 reads this script as ANSI, so a hard-coded name would fall apart.
# ALL manuals found (Czech and English), not just the first one.
$manuals = @(Get-ChildItem "$project\docs" -Filter "*INFO*.txt")
foreach ($m in $manuals) { Copy-Item $m.FullName "$build\dist\DaBTDynamicLock\" -Force }
Write-Host ("  manuals: " + $manuals.Count)
# The APK is NOT shipped with the installer. It lives in the GitHub release,
# where the app links to ("Download the phone app") - one place to keep up to
# date instead of two, and the installer stays smaller.

# --- 2) the installer -------------------------------------------------
# --paths: the installer speaks both languages out of windows\texts.py - the
# SAME dictionary the app uses, not a copy. Without this PyInstaller does not
# find the module and the built .exe dies on startup with ImportError.
Write-Host "2/2  packing the installer"
& $python -m PyInstaller `
    --noconfirm --clean --onefile --windowed --uac-admin `
    --name "DaBTDynamicLock-setup" --icon $icon `
    --paths "$project\windows" `
    --add-data "$icon;." `
    --add-data "$build\dist\DaBTDynamicLock;program" `
    --distpath "$here" --workpath "$build\work2" --specpath "$build" `
    "$here\installer.py"
if ($LASTEXITCODE -ne 0) { throw "PyInstaller (installer) failed" }

# Verify the shared modules really got packed - a missing one would only show
# up when a user runs the installer, and only as a crash.
foreach ($shared in @("texts", "marks")) {
    if (-not (Select-String -Path "$build\work2\DaBTDynamicLock-setup\Analysis-00.toc" `
                            -Pattern "windows..$shared.py" -Quiet)) {
        throw "$shared.py did NOT get packed - the installer would crash on startup"
    }
}

Remove-Item "$here\DaBTDynamicLock-setup.exe.manifest" -ErrorAction SilentlyContinue
$mb = [math]::Round((Get-Item "$here\DaBTDynamicLock-setup.exe").Length / 1MB, 1)
Write-Host ""
Write-Host "DONE: installer\DaBTDynamicLock-setup.exe  ($mb MB)"
