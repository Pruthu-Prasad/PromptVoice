# Setup

## Install (most users)

1. Download `PromptVoice-Setup-1.1.2.exe` from the
   [latest release](../../releases/latest).
2. Run it. No admin rights needed; it installs to
   `%LOCALAPPDATA%\Programs\PromptVoice`. Windows SmartScreen may warn because
   the installer is unsigned: **More info → Run anyway**.
3. Optional checkboxes: desktop shortcut, start with Windows.
4. Hold **Ctrl+Space** in any app, speak, release.

Requirements: Windows 10 1809+ x64, a microphone. Everything runs offline.
Silent install: `PromptVoice-Setup-1.1.2.exe /SILENT`.

## Build from source

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
Git, CMake + Visual Studio C++ build tools (for whisper.cpp).

```powershell
git clone https://github.com/Pruthu-Prasad/PromptVoice.git
cd PromptVoice
powershell -File setup-whisper.ps1        # builds whisper-cli, downloads base.en model
dotnet build PromptVoice\PromptVoice.csproj -c Release
```

## Build the installer

Needs [Inno Setup 6](https://jrsoftware.org/isdl.php) (auto-installed via winget if missing).

```powershell
powershell -File installer\build-installer.ps1 -Version 1.1.2
# output: dist\PromptVoice-Setup-1.1.2.exe
```
