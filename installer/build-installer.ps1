<#
    Builds the single-file PromptVoice installer.

    .\installer\build-installer.ps1
    .\installer\build-installer.ps1 -Version 1.1.0 -IncludeAllModels

    Bundles base.en only by default. tiny.en and small.en stay out of the
    installer and are downloaded on demand from inside the app, which keeps the
    setup file near 180 MB instead of 600 MB while still leaving a fresh install
    fully offline-capable.
#>
param(
    [string]$Version = "1.0.0",

    # Ship every model found in PromptVoice\runtime instead of just base.en.
    # Adds roughly 400 MB to the installer.
    [switch]$IncludeAllModels,

    # Reuse the existing publish output instead of rebuilding it.
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'PromptVoice\PromptVoice.csproj'
$publish = Join-Path $root 'PromptVoice\publish'
$runtime = Join-Path $root 'PromptVoice\runtime'
$dist    = Join-Path $root 'dist'
$script  = Join-Path $PSScriptRoot 'PromptVoice.iss'
$bundled = 'ggml-base.en.bin'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Find-ISCC {
    # winget installs Inno per-user under LOCALAPPDATA, not Program Files, so
    # check the uninstall registry key as well as the usual locations.
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($key in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
        if (Test-Path $key) {
            $location = (Get-ItemProperty $key).InstallLocation
            if ($location) { $candidates += (Join-Path $location 'ISCC.exe') }
        }
    }

    $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

# --- 1. prerequisites --------------------------------------------------------

Step 'Checking prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found in PATH. Install the .NET 8 SDK and retry.'
}

$iscc = Find-ISCC
if (-not $iscc) {
    Write-Host '  Inno Setup 6 not found; installing it with winget...'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'Inno Setup 6 is required. Install it from https://jrsoftware.org/isdl.php and retry.'
    }
    winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    $iscc = Find-ISCC
    if (-not $iscc) { throw 'Inno Setup installed but ISCC.exe was not found.' }
}
Write-Host "  ISCC: $iscc"

if (-not (Test-Path (Join-Path $runtime 'whisper-cli.exe'))) {
    throw "whisper-cli.exe is missing from $runtime. Run setup-whisper.ps1 first."
}
if (-not (Test-Path (Join-Path $runtime $bundled))) {
    throw "$bundled is missing from $runtime. Run setup-whisper.ps1 first."
}

# --- 2. publish --------------------------------------------------------------

if (-not $SkipPublish) {
    Step 'Publishing self-contained build'

    # Only stop an instance running out of the publish folder (it locks the
    # output); an installed copy under LOCALAPPDATA\Programs is left alone.
    Get-Process -Name PromptVoice -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($publish, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
    Start-Sleep -Milliseconds 400
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    dotnet publish $project -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}

# --- 3. trim the payload -----------------------------------------------------

Step 'Preparing installer payload'

if (-not $IncludeAllModels) {
    Get-ChildItem (Join-Path $publish 'runtime') -Filter 'ggml-*.en.bin' |
        Where-Object { $_.Name -ne $bundled } |
        ForEach-Object {
            Write-Host "  excluding $($_.Name) (downloaded on demand)"
            Remove-Item $_.FullName -Force
        }
}

# Development-only leftovers must never ship.
foreach ($junk in @('diagnostics.log','last-transcription.txt','input.wav','settings.json','history.json')) {
    $p = Join-Path $publish "runtime\$junk"
    if (Test-Path $p) { Remove-Item $p -Force; Write-Host "  removed stray $junk" }
}
Get-ChildItem $publish -Recurse -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

$required = @('PromptVoice.exe','runtime\whisper-cli.exe','runtime\whisper.dll','runtime\ggml.dll',
              'runtime\ggml-base.dll','runtime\ggml-cpu.dll',"runtime\$bundled")
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $publish $file))) { throw "Payload is missing $file" }
}
Write-Host ("  payload: {0:N1} MB" -f ((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

# --- 4. compile the installer ------------------------------------------------

Step 'Compiling installer'

New-Item -ItemType Directory -Force -Path $dist | Out-Null
& $iscc "/DAppVersion=$Version" "/DPayloadDir=$publish" $script
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$setup = Join-Path $dist "PromptVoice-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Expected installer not produced: $setup" }

Step 'Done'
Write-Host ("  {0}" -f $setup)
Write-Host ("  {0:N1} MB" -f ((Get-Item $setup).Length / 1MB))
Write-Host ''
Write-Host 'Test it with:'
Write-Host "  `"$setup`" /SILENT" -ForegroundColor Yellow
Write-Host '  (omit /SILENT for the wizard; add /LOG=install.log to trace it)'
