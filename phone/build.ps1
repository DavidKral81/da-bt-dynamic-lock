# Builds the APK straight with the Android SDK tools, without Gradle.
#
# Why: the app has no third-party libraries, so Gradle (and its ~1 GB of
# downloads plus a network connection on every build) is not needed for
# anything. This script works offline as well.
#
# Run:  powershell -ExecutionPolicy Bypass -File build.ps1
#
# No diacritics in this file on purpose: PowerShell 5.1 reads .ps1 as ANSI,
# so accented characters would come out mangled.

$ErrorActionPreference = "Stop"
# The path is derived from where the script sits - the real path contains
# diacritics and PowerShell 5.1 reads .ps1 as ANSI, so hard-coding it would
# fall apart.
$base  = $PSScriptRoot

# The source code and the key live here with the app. The tools (Java +
# Android SDK) sit next to it in the _android-build folder, which is meant to
# be deletable - so nothing that could be lost may live there.
$tools = Join-Path (Split-Path $base -Parent) "_android-build"
if (-not (Test-Path "$tools\jdk")) {
    throw "No tools in the _android-build folder. How to restore them is in its '___LZE SMAZAT.txt' file."
}

# The Android SDK tools (aapt2, d8, apksigner) are native and CANNOT handle
# paths with diacritics - and this one lives in "Muj disk\...(Work - osobni)".
# So the build runs in a temp folder with a plain ASCII name and the finished
# APK is copied back.
$work = Join-Path $env:TEMP "ddl-build"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null
Copy-Item "$base\src" "$work\app" -Recurse -Force

$app   = "$work\app"
$out   = "$work\output"
$jdk   = (Get-ChildItem "$tools\jdk" -Directory | Select-Object -First 1).FullName

# The tools are copied into the temp folder too - neither aapt2 nor javac
# could read android.jar while it sat in a path with diacritics
Write-Host "0/6  preparing the tools"
Copy-Item "$tools\sdk\build-tools\34.0.0" "$work\bt" -Recurse -Force
New-Item -ItemType Directory -Path "$work\plat" -Force | Out-Null
Copy-Item "$tools\sdk\platforms\android-34\android.jar" "$work\plat\android.jar" -Force

$bt    = "$work\bt"
$plat  = "$work\plat\android.jar"

$env:JAVA_HOME = $jdk
$javac    = "$jdk\bin\javac.exe"
$keytool  = "$jdk\bin\keytool.exe"
$aapt2    = "$bt\aapt2.exe"
$d8       = "$bt\d8.bat"
$zipalign = "$bt\zipalign.exe"
$signer   = "$bt\apksigner.bat"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path "$out\res","$out\gen","$out\classes" -Force | Out-Null

Write-Host "1/6  compiling resources (aapt2 compile)"
& $aapt2 compile --dir "$app\res" -o "$out\res.zip"
if ($LASTEXITCODE -ne 0) { throw "aapt2 compile failed" }

Write-Host "2/6  linking the package + generating R.java (aapt2 link)"
& $aapt2 link -o "$out\base.apk" `
    --manifest "$app\AndroidManifest.xml" `
    -I $plat `
    --java "$out\gen" `
    --min-sdk-version 26 --target-sdk-version 34 `
    --version-code 1 --version-name "1.0" `
    "$out\res.zip"
if ($LASTEXITCODE -ne 0) { throw "aapt2 link failed" }

Write-Host "3/6  compiling Java"
$sources = @(Get-ChildItem -Path "$app\java","$out\gen" -Recurse -Filter *.java | ForEach-Object { $_.FullName })
& $javac -source 8 -target 8 -nowarn -bootclasspath $plat -classpath $plat `
    -d "$out\classes" -encoding UTF-8 $sources
if ($LASTEXITCODE -ne 0) { throw "javac failed" }

Write-Host "4/6  converting to dex (d8)"
$classes = @(Get-ChildItem -Path "$out\classes" -Recurse -Filter *.class | ForEach-Object { $_.FullName })
& $d8 --lib $plat --min-api 26 --output "$out" $classes
if ($LASTEXITCODE -ne 0) { throw "d8 failed" }

Write-Host "5/6  putting the dex into the package and aligning"
Copy-Item "$out\base.apk" "$out\with-dex.apk" -Force
Push-Location $out
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open("$out\with-dex.apk", "Update")
[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, "$out\classes.dex", "classes.dex") | Out-Null
$zip.Dispose()
Pop-Location
& $zipalign -f 4 "$out\with-dex.apk" "$out\aligned.apk"
if ($LASTEXITCODE -ne 0) { throw "zipalign failed" }

Write-Host "6/6  signing"
# The key lives here with the app, NOT among the tools - those may be deleted,
# the key may not. Without it the app on the phone cannot be updated, it would
# have to be uninstalled first.
$keyPermanent = Join-Path $base "signing-key-DO-NOT-DELETE.jks"
# The password is NOT in this script - it sits next to the key in a file that
# is in .gitignore. It used to be hard-coded here and therefore went into the
# repository.
$passwordFile = Join-Path $base "signing-key-password.txt"
if (Test-Path $passwordFile) {
    $password = (Get-Content $passwordFile | Where-Object { $_.Trim() -ne "" } |
                 Select-Object -Last 1).Trim()
} elseif ($env:DDL_KEYSTORE_PASSWORD) {
    $password = $env:DDL_KEYSTORE_PASSWORD
} else {
    throw "No signing key password: expecting the file $passwordFile or the DDL_KEYSTORE_PASSWORD variable"
}
$key = "$work\key.jks"             # copied into an ASCII path for signing
if (-not (Test-Path $keyPermanent)) {
    Write-Host "     (creating a new signing key - DO NOT DELETE, updates need it)"
    # keytool writes an informational message to the error output and
    # PowerShell 5.1 takes that as a failure - so stopping on errors is turned
    # off here for a moment and success is verified by the file appearing
    $ErrorActionPreference = "Continue"
    & $keytool -genkeypair -keystore $key -alias ddl -keyalg RSA -keysize 2048 `
        -validity 10000 -storepass $password -keypass $password `
        -dname "CN=Da BT Dynamic Lock, O=David, C=CZ" | Out-Null
    $ErrorActionPreference = "Stop"
    if (-not (Test-Path $key)) { throw "the signing key was not created" }
    New-Item -ItemType Directory -Path (Split-Path $keyPermanent -Parent) -Force | Out-Null
    Copy-Item $key $keyPermanent -Force
} else {
    # apksigner runs on Java and a path with diacritics gives it trouble -
    # same as the other tools. That is why the key is copied into an ASCII
    # path for signing.
    Copy-Item $keyPermanent $key -Force
}
& $signer sign --ks $key --ks-pass "pass:$password" --key-pass "pass:$password" `
    --out "$out\DaBTDynamicLock.apk" "$out\aligned.apk"
if ($LASTEXITCODE -ne 0) { throw "signing failed" }

Copy-Item "$out\DaBTDynamicLock.apk" "$base\DaBTDynamicLock.apk" -Force
$size = [math]::Round((Get-Item "$base\DaBTDynamicLock.apk").Length / 1KB, 0)
Write-Host ""
Write-Host "DONE: phone\DaBTDynamicLock.apk  ($size kB)"
