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

# --- version ----------------------------------------------------------
# VERSION is read out of windows\version.py, the same constant the app and
# the installer import. Generating the resource from it means the file
# properties cannot disagree with what the program reports about itself.
$versionPy = Get-Content "$project\windows\version.py" -Raw
if ($versionPy -notmatch '(?m)^VERSION\s*=\s*"([^"]+)"') {
    throw "Could not read VERSION out of windows\version.py"
}
$version = $Matches[1]
$parts  = @($version -split '\.') + @('0', '0', '0', '0')
$vtuple = "$($parts[0]), $($parts[1]), $($parts[2]), $($parts[3])"
Write-Host "version $version  -> $vtuple"

function New-VersionFile($path, $description, $internal, $original) {
    $text = @"
VSVersionInfo(
  ffi=FixedFileInfo(
    filevers=($vtuple), prodvers=($vtuple),
    mask=0x3f, flags=0x0, OS=0x40004, fileType=0x1, subtype=0x0, date=(0, 0)
  ),
  kids=[
    StringFileInfo([StringTable('040904B0', [
      StringStruct('CompanyName', 'David Kral'),
      StringStruct('FileDescription', '$description'),
      StringStruct('FileVersion', '$version'),
      StringStruct('InternalName', '$internal'),
      StringStruct('LegalCopyright', 'Apache License 2.0'),
      StringStruct('OriginalFilename', '$original'),
      StringStruct('ProductName', 'Da BT Dynamic Lock'),
      StringStruct('ProductVersion', '$version')])]),
    VarFileInfo([VarStruct('Translation', [1033, 1200])])
  ]
)
"@
    [System.IO.File]::WriteAllText(
        $path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# PyInstaller does not fail when a --version-file is missing or malformed, it
# just leaves the properties empty - which is exactly how the first release
# nearly went out. Both .exe files get one, so both are checked; the program
# is the one the user keeps in Program Files and looks at through Properties.
function Assert-VersionResource($path, $label) {
    if (-not (Test-Path $path)) { throw "$label was not built: $path" }
    $info = (Get-Item $path).VersionInfo
    if ($info.FileVersion -notlike "$version*") {
        throw "$label carries no version resource - expected $version, got '$($info.FileVersion)'"
    }
    Write-Host "version resource OK ($label): $($info.ProductName) $($info.FileVersion)"
}

# --- 1) the program ---------------------------------------------------
Write-Host "1/2  packing the program"
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
New-Item -ItemType Directory -Path $build -Force | Out-Null
New-VersionFile "$build\version-app.txt" `
    "Da BT Dynamic Lock" "DaBTDynamicLock" "DaBTDynamicLock.exe"
New-VersionFile "$build\version-setup.txt" `
    "Da BT Dynamic Lock installer" "DaBTDynamicLock-setup" `
    "DaBTDynamicLock-setup.exe"

& $python -m PyInstaller `
    --noconfirm --clean --windowed `
    --name "DaBTDynamicLock" --icon $icon `
    --version-file "$build\version-app.txt" `
    --add-data "$icon;." `
    --distpath "$build\dist" --workpath "$build\work" --specpath "$build" `
    "$project\windows\dyn_lock.py"
if ($LASTEXITCODE -ne 0) { throw "PyInstaller (program) failed" }

# Checked here, before the folder gets packed into the installer - a program
# with empty properties must not make it that far.
Assert-VersionResource "$build\dist\DaBTDynamicLock\DaBTDynamicLock.exe" "the program"

# the default settings, the manuals and the phone app belong with the program
# the template of default values, NOT the developer's own config.json
Copy-Item "$project\windows\config.default.json" "$build\dist\DaBTDynamicLock\" -Force
# The manuals are looked up by pattern, not by exact name, so renaming one
# cannot silently stop it from being shipped. ALL manuals found (Czech and
# English), not just the first one.
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
    --version-file "$build\version-setup.txt" `
    --paths "$project\windows" `
    --add-data "$icon;." `
    --add-data "$build\dist\DaBTDynamicLock;program" `
    --distpath "$here" --workpath "$build\work2" --specpath "$build" `
    "$here\installer.py"
if ($LASTEXITCODE -ne 0) { throw "PyInstaller (installer) failed" }

# Verify the shared modules really got packed - a missing one would only show
# up when a user runs the installer, and only as a crash.
foreach ($shared in @("texts", "marks", "version")) {
    if (-not (Select-String -Path "$build\work2\DaBTDynamicLock-setup\Analysis-00.toc" `
                            -Pattern "windows..$shared.py" -Quiet)) {
        throw "$shared.py did NOT get packed - the installer would crash on startup"
    }
}

Remove-Item "$here\DaBTDynamicLock-setup.exe.manifest" -ErrorAction SilentlyContinue

Assert-VersionResource "$here\DaBTDynamicLock-setup.exe" "the installer"

$mb = [math]::Round((Get-Item "$here\DaBTDynamicLock-setup.exe").Length / 1MB, 1)
Write-Host ""
Write-Host "DONE: installer\DaBTDynamicLock-setup.exe  ($mb MB)"
