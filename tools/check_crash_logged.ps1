<#
Does a crash the app cannot log itself end up in its log at the next start?

A crash inside WinUI (an exception in a DispatcherQueue callback, a native
failure inside Microsoft.UI.Xaml.dll) ends the process before any .NET handler
runs, so the log has not one line about it. Windows writes it to its own
Application log (Application Error 1000), and the next start copies it over.

What it does: starts a dry-run copy of the built program with --crash-after,
which crashes it exactly that way; waits until Windows has recorded the crash
of that process; starts the copy again and checks its log names the crashed
process; starts it once more and checks the crash is not written twice.

When to run: after a change to how crashes are logged or how the app starts,
and before handing out an installer.
Who sees what: a second tray icon three times for a few seconds. Only dry-run
copies run, never the installed app. Windows keeps its own record of the crash
(Application log, WER report in C:\ProgramData\Microsoft\Windows\WER) - allowed
for this test on 24.11.2026.

Exit code: 0 = the crash is logged once, 1 = it is not (or twice), 2 = could not
run the test.
#>
param(
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) '_output\setup\DaBtDynamicLock.exe')
)
$ErrorActionPreference = 'Stop'

# One run of the dry-run copy; returns the finished process.
function Invoke-Copy([string[]]$Arguments, [int]$Seconds) {
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
    if (-not $p.WaitForExit($Seconds * 1000)) {
        Stop-Process -Id $p.Id -Force
        [void]$p.WaitForExit(10000)
        throw "the copy started with '$($Arguments -join ' ')' did not end within $Seconds s"
    }
    return $p
}

function Get-LogText([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return [IO.File]::ReadAllText($Path) }
    return ''
}

$code = 2
$log = $null
try {
    if (-not (Test-Path -LiteralPath $Exe)) { throw "No built program at $Exe." }
    $Exe = (Resolve-Path -LiteralPath $Exe).Path
    $log = Join-Path (Split-Path -Parent $Exe) 'dry-run-data\dyn_lock.log'

    # 1. Crash a copy the way the app crashed: no .NET handler sees it.
    $crashed = Invoke-Copy @('--dry-run', '--crash-after', '6') 60
    $crashedId = $crashed.Id
    Write-Output ("Crashed copy: process {0}, exit code 0x{1:X8}." -f $crashedId, $crashed.ExitCode)
    if ($crashed.ExitCode -eq 0) { throw 'the copy ended normally instead of crashing' }
    if ((Get-LogText $log) -notmatch "Crashing on purpose \(--crash-after\), process $crashedId\.") {
        throw 'the copy never reached the crash (no line about it in its log)'
    }

    # 2. The measuring instrument: has Windows recorded THIS crash? Without the
    #    record there is nothing to copy, and a pass or fail would mean nothing.
    $xpath = ("*[System[Provider[@Name='Application Error'] and EventID=1000]" +
        " and EventData[Data[@Name='ProcessId']='0x{0:x}']]") -f $crashedId
    $record = $null
    for ($i = 0; $i -lt 30 -and -not $record; $i++) {
        Start-Sleep -Seconds 1
        try {
            $events = Get-WinEvent -LogName Application -FilterXPath $xpath -MaxEvents 5 -ErrorAction Stop
        }
        catch {
            # "Nothing matches yet" is an answer, not a failure; anything else is.
            if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw }
            $events = @()
        }
        $record = $events |
            Where-Object { ([xml]$_.ToXml()).Event.EventData.Data | Where-Object { $_.Name -eq 'AppPath' -and $_.'#text' -eq $Exe } } |
            Select-Object -First 1
    }
    if (-not $record) { throw "Windows recorded no crash of process $crashedId within 30 s (is error reporting switched off?)" }
    Write-Output "Windows recorded it at $($record.TimeCreated.ToString('dd.MM.yyyy HH:mm:ss'))."

    # 3. The next start copies it into the log.
    [void](Invoke-Copy @('--dry-run', '--quit-after', '8') 60)
    $pattern = "Windows recorded a crash of this program at .*process $crashedId \["
    $told = [regex]::Matches((Get-LogText $log), $pattern).Count
    if ($told -eq 0) {
        Write-Output "FAIL: the next start did not write the crash of process $crashedId into the log."
        $code = 1
    }
    else {
        # 4. ...and only once: the start after that must not repeat it.
        [void](Invoke-Copy @('--dry-run', '--quit-after', '8') 60)
        $told = [regex]::Matches((Get-LogText $log), $pattern).Count
        if ($told -ne 1) {
            Write-Output "FAIL: the crash of process $crashedId is in the log $told times, not once."
            $code = 1
        }
        else {
            Write-Output "PASS: the crash of process $crashedId is in the log, once."
            $code = 0
        }
    }
}
catch {
    Write-Output "Could not run the test: $($_.Exception.Message)"
    $code = 2
}
if ($code -ne 0 -and $log -and (Test-Path -LiteralPath $log)) {
    Write-Output 'Last lines of the dry-run log:'
    Get-Content -LiteralPath $log -Tail 8 -Encoding UTF8 | ForEach-Object { "  $_" }
}
exit $code
