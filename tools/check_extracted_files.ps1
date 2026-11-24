<#
Does the app survive its unpacked files being deleted while it runs?

The single-file build unpacks itself into a temporary folder at start. Temp
cleanup (Storage Sense, Disk Cleanup) may delete files there while the app has
been running for days. Every file the app has not opened yet is fair game - and
the first settings window after that crashed the app (0xC000027B, "file not
found" inside Microsoft.UI.Xaml.dll), with nothing in its log.

What it does: starts a dry-run copy of the built program with the unpacking
redirected to %LOCALAPPDATA%\da-bt-dynamic-lock-build\extract-check, moves away
every unpacked file that could be deleted (one the process holds cannot), then
sends that copy's tray icon a left click and checks the copy is still alive
with its settings window open.

When to run: after build-setup.ps1, before handing out an installer and after
any change to how the app is published or started.
Who sees what: a second tray icon and a settings window for about 20 s. Only the
dry-run copy is touched (found by its process id), never the installed app.

Exit code: 0 = survives, 1 = crashed or no window, 2 = could not run the test.
#>
param(
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) '_output\setup\DaBtDynamicLock.exe')
)
$ErrorActionPreference = 'Stop'

try {
    if (-not (Test-Path -LiteralPath $Exe)) { throw "No built program at $Exe - run app-2.0\build-setup.ps1 first." }
    $work = Join-Path $env:LOCALAPPDATA 'da-bt-dynamic-lock-build'
    $base = Join-Path $work 'extract-check'
    $aside = Join-Path $work 'extract-check-aside'
    New-Item -ItemType Directory -Force $base, $aside | Out-Null

    Add-Type -Namespace DdlCheck -Name W -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassNameW(System.IntPtr h, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessageW(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);
'@
}
catch {
    Write-Output "Could not run the test: $($_.Exception.Message)"
    exit 2
}

# Windows of one process, by class name: visible ones only, or all of them.
function Get-Windows([int]$ProcessId, [string]$Class, [bool]$VisibleOnly) {
    $script:found = @()
    $cb = [DdlCheck.W+EnumProc]{ param($h, $l)
        $procId = 0; [void][DdlCheck.W]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $ProcessId -and (-not $VisibleOnly -or [DdlCheck.W]::IsWindowVisible($h))) {
            $sb = New-Object System.Text.StringBuilder 256; [void][DdlCheck.W]::GetClassNameW($h, $sb, 256)
            if ($sb.ToString() -eq $Class) { $script:found += $h }
        }
        return $true }
    [void][DdlCheck.W]::EnumWindows($cb, [IntPtr]::Zero)
    return , $script:found
}

$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $base
$p = Start-Process -FilePath $Exe -ArgumentList '--dry-run', '--quit-after', '90' -PassThru
$code = 2
try {
    # Wait for the tray window: it exists once the app has finished starting.
    $tray = @()
    for ($i = 0; $i -lt 40 -and $tray.Count -eq 0; $i++) {
        Start-Sleep -Milliseconds 500
        if ($p.HasExited) { throw "the dry-run copy ended on its own (exit code $($p.ExitCode))" }
        $tray = Get-Windows $p.Id 'Da BT Dynamic Lock.TrayWindow' $false
    }
    if ($tray.Count -eq 0) { throw 'the dry-run copy has no tray window after 20 s' }
    Start-Sleep -Seconds 2

    # "Delete" what temp cleanup could delete: a file the process holds cannot be
    # opened for writing without sharing, and cannot be deleted either.
    $moved = 0; $held = 0
    foreach ($f in Get-ChildItem -Recurse -File $base) {
        try { [IO.File]::Open($f.FullName, 'Open', 'ReadWrite', 'None').Close() } catch { $held++; continue }
        Move-Item -LiteralPath $f.FullName -Destination (Join-Path $aside $f.Name) -Force
        $moved++
    }
    Write-Output "Unpacked files: $moved could be deleted, $held held by the app."

    # Left click: WM_TRAYCALLBACK (WM_APP + 1) with WM_LBUTTONUP in the low word.
    [void][DdlCheck.W]::PostMessageW($tray[0], 0x8001, [IntPtr]::Zero, [IntPtr]0x0202)
    if ($p.WaitForExit(6000)) {
        Write-Output ("FAIL: the app crashed after the click (exit code 0x{0:X8})." -f $p.ExitCode)
        $code = 1
    }
    elseif ((Get-Windows $p.Id 'WinUIDesktopWin32WindowClass' $true).Count -eq 0) {
        Write-Output 'FAIL: the app is alive, but no settings window opened.'
        $code = 1
    }
    else {
        Write-Output 'PASS: the app survived and opened its settings window.'
        $code = 0
    }
}
catch {
    Write-Output "Could not run the test: $($_.Exception.Message)"
    $code = 2
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; [void]$p.WaitForExit(10000) }
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $null
}
if ($code -ne 0) {
    $log = Join-Path (Split-Path -Parent $Exe) 'dry-run-data\dyn_lock.log'
    if (Test-Path -LiteralPath $log) {
        Write-Output 'Last lines of the dry-run log:'
        Get-Content -LiteralPath $log -Tail 6 -Encoding UTF8 | ForEach-Object { "  $_" }
    }
}
exit $code
