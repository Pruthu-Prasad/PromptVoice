# PromptVoice

Offline English dictation for Windows, tuned for prompting Claude Code and other
AI agents. Hold a shortcut, speak, release — the text is pasted at your cursor.
No cloud, no network calls after setup, CPU only.

**[Download the installer](../../releases/latest)** · [Setup & build guide](SETUP.md) · MIT licensed

Target: Laptops withintegrated graphics that cannot run larger models offline.

## Usage

1. Run `PromptVoice.exe`. The window opens on first run; afterwards it lives in
   the tray (double-click the tray icon to reopen).
2. Click into the app you want to dictate into.
3. **Hold** `Ctrl+Space`. The pill at the bottom of the screen turns red and its
   waveform follows your voice. Flat bars mean the wrong microphone.
4. Speak, then release. The pill turns blue while Whisper runs (~2.7 s for a
   short phrase on `base.en`), then the text appears at your caret.

Hold-to-talk, not toggle. No notification balloons — the pill is the only status.
It is click-through, so it never intercepts the mouse.

| Pill | Meaning |
|------|---------|
| Small grey badge | Idle |
| Red, live waveform | Recording |
| Blue, pulsing dots | Whisper transcribing |
| Amber, "No text inserted" | Nothing recognised, or insertion failed |

## Accuracy

Whisper's small models mangle developer vocabulary. Two things fix it, both on
by default. Measured here on synthesised developer dictation (8 phrases of
identifiers, tool names, and file paths), word error rate:

| Model | No prompt | With technical vocabulary | Latency |
|-------|-----------|---------------------------|---------|
| `tiny.en` | 10.7% | 11.7% | ~2.1 s |
| **`base.en`** (default) | 7.8% | **5.8%** | **~2.7 s** |
| `small.en` | 6.8% | **3.9%** | ~7.1 s |

* **Model** — `base.en` roughly halves `tiny.en`'s errors for about half a second
  more. `small.en` is the most accurate but 7 s is slow for quick prompts.
  Switch under **Settings → Model**.
* **Technical vocabulary** — feeds Whisper an initial prompt of coding and
  AI-tooling terms (Claude Code, npm, TypeScript, Postgres, refactor, async…) so
  it favours them over similar-sounding English. Note it *hurts* `tiny.en`,
  which is too small to use the context — another reason to stay on `base.en`.
* **Custom vocabulary** — add your own project names, libraries, or colleagues
  under **Settings**, comma separated. This is the fix when one specific word is
  always wrong.

Speak in full sentences: Whisper uses surrounding context to choose words, so
"refactor the auth middleware" lands far better than "auth middleware" alone.
Say symbols aloud — "dot t s", "slash", "dash".

## The app

* **Dictate** — your shortcut, current model, and tips.
* **History** — the last 50 transcripts, newest first, with model and timing.
  Click any entry to copy it. Nothing is ever lost to a failed paste.
* **Settings** — model, microphone, shortcut (`Ctrl`/`Alt`/`Ctrl+Shift`/`Win` +
  `Space`), technical vocabulary, custom vocabulary, history. Saved instantly to
  `runtime\settings.json`, so copying the folder carries your configuration.

## How insertion works

The transcript goes on the clipboard and is pasted with a synthesised `Ctrl+V` —
the only mechanism that works uniformly across classic Notepad, modern Notepad,
browser fields, Electron apps (VS Code, Slack), and plain Win32 edit controls.

Each dictation: records the focused window at shortcut-down → waits up to 800 ms
for you to release `Ctrl`/`Shift`/`Alt`/`Win` then forces a key-up for any still
held (otherwise injected keys arrive as shortcuts and insert nothing) → restores
the target if focus drifted → saves your clipboard text → writes the transcript
(4 attempts, clipboard ownership is contended) → sends `Ctrl+V` → falls back to
typing the characters → restores your clipboard after 700 ms unless you copied
something else → on failure flashes the amber pill and leaves the text in
`runtime\last-transcription.txt`.

Only clipboard **text** is preserved. An image or file on the clipboard is not
restored.

## Diagnostics

In the `runtime` folder beside `PromptVoice.exe` (nothing leaves the machine):

* `last-transcription.txt` — the latest transcript, written *before* insertion.
* `diagnostics.log` — one block per dictation, capped at 256 KB:

```
whisper ggml-base.en.bin t=6 prompt=on took 2671 ms exit=0
--- insert begin --- 21 chars: "testing one two three"
target hwnd=0xC0780 [Notepad] "notes.txt - Notepad"
modifiers clear after 5 ms
clipboard set on attempt 1, verified=True
SendInput(Ctrl+V): sent 4/4
--- insert end: clipboard paste sent ---
```

### Testing without a microphone

```
PromptVoice.exe --test-insert "testing one two three" 4
```

Runs the exact insertion path: waits 4 s for you to click into the target, then
inserts. Exit code 0 means the paste was delivered. Use it first when insertion
misbehaves — it separates insertion problems from microphone or Whisper ones.

```
PromptVoice.exe --ui-shots C:\some\folder
```

Renders every screen and pill state to PNG off-screen, for reviewing the UI.

## Troubleshooting

**Nothing is inserted.** Read the last block of `runtime\diagnostics.log`.

* `SendInput(...): sent 0/4, lastError=87` — the `INPUT` struct size is wrong for
  the architecture; `SendInput` rejects every event and injects nothing. This was
  the original bug. Check `NativeMethods.InputUnion` still declares all three
  union members.
* `forcing key-up for still-held modifiers`, then a successful paste — normal,
  you were still holding `Ctrl`.
* `clipboard could not be populated` — a clipboard manager is holding it open.
  The transcript is in `last-transcription.txt`; paste it manually.
* Nothing logged at all — the shortcut never arrived. Another app claimed it, or
  the target runs elevated while PromptVoice does not (Windows blocks input
  injection from lower privilege). Run both elevated, or dictate elsewhere.

**Amber pill immediately.** Whisper heard nothing. Check the waveform moves while
recording; if not, pick the right device in **Settings → Microphone** and check
Windows privacy settings (Settings → Privacy & security → Microphone).

**Words are wrong, not missing.** Switch to `base.en` or `small.en`, keep
technical vocabulary on, and add the problem words to custom vocabulary.

**Too slow.** Use `base.en` rather than `small.en`, and close other CPU-heavy
work. There is no GPU path here on purpose.

## Build

```
powershell -File setup-whisper.ps1                      # base.en + whisper.cpp
powershell -File setup-whisper.ps1 -IncludeSmallModel   # also small.en (465 MB)
dotnet build PromptVoice\PromptVoice.csproj -c Release
```

## Portable Windows build

Close any running `PromptVoice.exe` first — it locks the output folder.

```
dotnet publish PromptVoice\PromptVoice.csproj -c Release -r win-x64 --self-contained true -o .\PromptVoice\publish
```

The adjacent `runtime` folder must contain `whisper-cli.exe`, `whisper.dll`,
`ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, and at least one `ggml-*.en.bin`
model. Delete any model you do not want — the app lists whichever are present.
English-only models only; no GPU, Vulkan, or CUDA support by design.

Whisper runs on half the logical processors (6 threads on an i5-12450H).
