param(
    # Also fetch small.en: the most accurate English model, but roughly 7s per
    # phrase on an i5-12450H instead of base.en's ~2.7s.
    [switch]$IncludeSmallModel,

    # Build whisper-cli with the Vulkan GPU backend (Intel/AMD/NVIDIA). Needs
    # the Vulkan SDK (https://vulkan.lunarg.com) so $env:VULKAN_SDK is set.
    # Produces ggml-vulkan.dll, which the app detects to enable its GPU option.
    [switch]$Vulkan
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$runtime = Join-Path $root "PromptVoice\runtime"
New-Item -ItemType Directory -Force -Path $runtime | Out-Null

foreach ($tool in @("git", "cmake")) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool was not found in PATH. Install it and run this script again."
    }
}

$whisperSrc = Join-Path $runtime "whisper.cpp"

if (-not (Test-Path (Join-Path $whisperSrc ".git"))) {
    git clone --depth 1 https://github.com/ggml-org/whisper.cpp.git $whisperSrc
}

Push-Location $whisperSrc
$cmakeFlags = @('-DCMAKE_BUILD_TYPE=Release')
if ($Vulkan) {
    if (-not $env:VULKAN_SDK) { throw 'The Vulkan SDK is not installed (VULKAN_SDK is unset). Install it from https://vulkan.lunarg.com and retry.' }
    $cmakeFlags += '-DGGML_VULKAN=ON'
    Write-Host "Building with Vulkan GPU support (SDK: $env:VULKAN_SDK)"
}
cmake -B build @cmakeFlags
cmake --build build --config Release --target whisper-cli
Pop-Location

$exeCandidates = @(
    (Join-Path $whisperSrc "build\bin\Release\whisper-cli.exe"),
    (Join-Path $whisperSrc "build\bin\whisper-cli.exe")
)

$exe = $exeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $exe) {
    throw "whisper-cli.exe was not produced by the build."
}

# whisper-cli is dynamically linked on Windows.  Copy the executable *and all*
# sibling DLLs; copying only the EXE produces the "whisper.dll/ggml.dll was not
# found" error when the tray app starts transcription.
$binaryDir = Split-Path -Parent $exe
Copy-Item (Join-Path $binaryDir "whisper-cli.exe") (Join-Path $runtime "whisper-cli.exe") -Force
Get-ChildItem -Path $binaryDir -Filter "*.dll" | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $runtime $_.Name) -Force
}

$hub = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main"

# base.en is the default: measured on developer dictation it roughly halves the
# word error rate of tiny.en for about half a second more per phrase.
# small.en is opt-in because it takes ~7s per phrase on an i5-12450H.
$wanted = @(
    @{ File = "ggml-base.en.bin";  Size = "~141 MiB" }
)

if ($IncludeSmallModel) {
    $wanted += @{ File = "ggml-small.en.bin"; Size = "~465 MiB" }
}

foreach ($model in $wanted) {
    $dest = Join-Path $runtime $model.File

    if (Test-Path $dest) {
        Write-Host "$($model.File) already present."
        continue
    }

    Write-Host "Downloading $($model.File) ($($model.Size))..."
    $client = New-Object System.Net.WebClient
    $client.DownloadFile("$hub/$($model.File)", $dest)
}

Write-Host ""
Write-Host "PromptVoice Whisper setup complete."
Write-Host "Models installed:"
Get-ChildItem -Path $runtime -Filter "ggml-*.en.bin" |
    ForEach-Object { Write-Host ("  {0,-22} {1,6:N0} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host ""
Write-Host "Re-run with -IncludeSmallModel for the most accurate (slowest) English model."
