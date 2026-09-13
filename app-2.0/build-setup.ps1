# Builds the installer for Da BT Dynamic Lock 2.0.
#
# There is no separate installer program. The setup executable IS the
# application, published as one self-contained file. Run from anywhere other
# than its installed home it offers to install itself, because what a person
# does with a downloaded file is double-click it.
#
# The file KEEPS THE NAME IT WAS PUBLISHED UNDER and nothing here renames it.
# A WinUI 3 executable cannot be renamed: it finds its own XAML through
# resources keyed to the executable's name, so a renamed copy dies on a
# XamlParseException before it draws anything. Measured 13.09.2026 - and it is
# why installing copies this file in unchanged.
#
# Measured 13.09.2026, which is why it is built this way: an empty WinForms
# installer, self-contained, comes to 72 MB - more than the whole WinUI
# application takes at 69 MB. A second program would have carried .NET twice.
#
# Run it whenever a build is wanted. It writes nothing outside the build
# folder and installs nothing.
#
# No diacritics in this file on purpose: PowerShell 5.1 reads .ps1 as ANSI.

[CmdletBinding()]
param(
    # Where the finished setup goes. Outside the project by default: the
    # project lives in a mirrored folder, and a 69 MB file rewritten on every
    # build would be re-uploaded every time.
    [string] $Do = (Join-Path $env:LOCALAPPDATA "da-bt-dynamic-lock-build\setup")
)

$ErrorActionPreference = "Stop"

$base = $PSScriptRoot
$project = Join-Path $base "App\App.csproj"
$staging = Join-Path $env:LOCALAPPDATA "da-bt-dynamic-lock-build\setup-staging"

# The published name, kept as it is. See the note at the top: renaming breaks
# the program outright.
$setupName = "DaBtDynamicLock.exe"

Write-Host ""
Write-Host "Building the installer" -ForegroundColor Cyan
Write-Host "  project: $project"
Write-Host "  into:    $Do"
Write-Host ""

# A running copy holds its own file and the publish fails on MSB3027 - which
# looks like "the change did not take effect", because the OLD binary is still
# there. Said out loud rather than left to be puzzled over.
$running = @(Get-Process -Name "DaBtDynamicLock" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "A copy of the application is running (PID $($running.Id -join ', '))." -ForegroundColor Yellow
    Write-Host "Close it first, or the build will fail on a file in use." -ForegroundColor Yellow
    exit 1
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

# One publish profile, not a row of switches typed by hand: two builds must not
# differ by what somebody remembered to pass.
& dotnet publish $project -p:PublishProfile=SelfContained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -o $staging
if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet publish failed with $LASTEXITCODE." -ForegroundColor Red
    exit 1
}

$built = Join-Path $staging "DaBtDynamicLock.exe"
if (-not (Test-Path $built)) {
    # The result is checked, never the return code alone: this project has a
    # list of steps that reported success and produced nothing.
    Write-Host "The publish reported success but $built is not there." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $Do)) { New-Item -ItemType Directory -Path $Do -Force | Out-Null }
$setup = Join-Path $Do $setupName
Copy-Item $built $setup -Force

if (-not (Test-Path $setup)) {
    Write-Host "The setup file was not created at $setup." -ForegroundColor Red
    exit 1
}

# Publishing is not running. The finished file is started with the self-check
# switch, which drives the settings window's controls and reports whether each
# one reaches the settings file.
#
# Start-Process -Wait, because PowerShell does NOT wait for a windowed program:
# it returns at once, $LASTEXITCODE stays empty, and it looks as though the run
# did nothing. An empty exit code is not zero, it is a sign nobody waited.
Write-Host ""
Write-Host "Checking the finished file..." -ForegroundColor Cyan
$check = Start-Process -FilePath $setup -ArgumentList "--self-check" -Wait -PassThru
if ($check.ExitCode -ne 0) {
    Write-Host "The self-check failed with $($check.ExitCode)." -ForegroundColor Red
    Write-Host "See dry-run-data\dyn_lock.log beside the setup file." -ForegroundColor Red
    exit 1
}

$item = Get-Item $setup
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash
$version = (Get-Item $setup).VersionInfo.FileVersion

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  file:    $setup"
Write-Host "  size:    $('{0:N0}' -f $item.Length) B"
Write-Host "  written: $($item.LastWriteTime.ToString('dd.MM.yyyy HH:mm:ss'))"
Write-Host "  SHA-256: $hash"
Write-Host ""
Write-Host "Record the time, size and hash in CLAUDE.md - it is the only thing"
Write-Host "that tells two builds of the same version number apart."
Write-Host ""
exit 0
