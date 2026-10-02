using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PromptVoice;

public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly StatusWidget widget;
    private readonly AppSettings settings;
    private readonly NativeMethods.LowLevelKeyboardProc keyboardProc;
    private readonly IntPtr keyboardHook;

    private MainWindow? window;
    private bool spaceHeld;
    private MMDevice? activeCaptureDevice;
    private IntPtr dictationTarget;

    private IWaveIn? recorder;
    private MemoryStream capturedAudio = new();
    private WaveFormat? captureFormat;
    private bool recording;
    private bool busy;

    private readonly string runtimeDir;
    private float capturePeak;

    /// <summary>The input the last (or next) dictation actually uses, after fallback.</summary>
    public static string ActiveMicrophoneName { get; private set; } = "Windows default";

    public TrayApp(bool startedByWindows = false)
    {
        runtimeDir = AppPaths.DataDirectory;
        AppPaths.EnsureCreated();

        settings = AppSettings.Load();

        // Repairs a stale or missing Run entry after an upgrade or a move.
        StartupRegistration.Reconcile(settings);

        tray = new NotifyIcon
        {
            Icon = AppIcon.ForTray(),
            Visible = true,
            Text = "PromptVoice"
        };

        var menu = new ContextMenuStrip { Font = Theme.Body };
        var open = new ToolStripMenuItem("Open PromptVoice", null, (_, _) => ShowWindow()) { Font = Theme.BodyStrong };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => ShowWindow(2));
        menu.Items.Add("History", null, (_, _) => ShowWindow(1));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();

        widget = new StatusWidget();
        widget.Show();

        UpdateTrayText();

        keyboardProc = KeyboardHookCallback;
        keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, keyboardProc, IntPtr.Zero, 0);
        if (keyboardHook == IntPtr.Zero)
            MessageBox.Show("PromptVoice could not register its shortcut. Restart the app and try again.",
                "PromptVoice", MessageBoxButtons.OK, MessageBoxIcon.Error);

        if (!WhisperEngine.IsReady(settings))
            MessageBox.Show(
                $"The Whisper runtime is incomplete.\n\nExpected:\n{WhisperEngine.Executable}\n{WhisperEngine.ModelPath(settings.Model)}\n\nRun setup-whisper.ps1 again.",
                "PromptVoice", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Diagnostics.Log($"started: model={settings.Model} hotkey={settings.Hotkey} threads={settings.EffectiveThreads}");

        // First run has nothing to go on, so show the window once and explain
        // the shortcut. Afterwards the app stays out of the way in the tray.
        // Auto-start should be silent; a first manual run should not be.
        if (!File.Exists(AppPaths.SettingsPath))
        {
            settings.Save();
            if (!startedByWindows)
                ShowWindow();
        }
    }

    private void UpdateTrayText() =>
        tray.Text = $"PromptVoice - {HotkeyLabel(settings.Hotkey)} to dictate";

    private static string HotkeyLabel(Hotkey hotkey) => hotkey switch
    {
        Hotkey.AltSpace => "Hold Alt+Space",
        Hotkey.CtrlShiftSpace => "Hold Ctrl+Shift+Space",
        Hotkey.WinSpace => "Hold Win+Space",
        _ => "Hold Ctrl+Space"
    };

    private void ShowWindow(int section = 0)
    {
        if (window is null || window.IsDisposed)
            window = new MainWindow(settings, OnSettingsChanged);

        window.Show();
        window.WindowState = FormWindowState.Normal;
        window.Activate();
        _ = section;
    }

    private void OnSettingsChanged()
    {
        UpdateTrayText();
        Diagnostics.Log($"settings changed: model={settings.Model} hotkey={settings.Hotkey} " +
                        $"vocabulary={(settings.UseTechnicalVocabulary ? "on" : "off")}");
    }

    private void BeginDictation()
    {
        if (busy || recording)
            return;

        StartRecording();
    }

    private void StartRecording()
    {
        if (!WhisperEngine.IsReady(settings))
        {
            widget.FlashWarning(TimeSpan.FromSeconds(3));
            Diagnostics.Log("dictation aborted: whisper runtime or model missing");
            return;
        }

        try
        {
            capturedAudio.Dispose();
            capturedAudio = new MemoryStream();

            recorder?.Dispose();
            activeCaptureDevice?.Dispose();

            using var devices = new MMDeviceEnumerator();
            activeCaptureDevice = ResolveCaptureDevice(devices);

            try
            {
                recorder = new WasapiCapture(activeCaptureDevice);
            }
            catch (Exception ex) when (!string.IsNullOrEmpty(settings.CaptureDeviceId))
            {
                // The chosen device exists but will not open (unplugged, sleeping,
                // or re-enumerated). Fall back to the default rather than failing
                // the dictation over a stale preference.
                Diagnostics.Log($"selected microphone failed ({ex.Message}); falling back to the Windows default");
                activeCaptureDevice.Dispose();
                activeCaptureDevice = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                recorder = new WasapiCapture(activeCaptureDevice);
            }

            captureFormat = recorder.WaveFormat;
            capturePeak = 0f;
            ActiveMicrophoneName = activeCaptureDevice.FriendlyName;

            recorder.DataAvailable += OnAudioData;
            recorder.RecordingStopped += OnRecordingStopped;
            recorder.StartRecording();

            recording = true;
            widget.SetStatus(VoiceStatus.Listening);
            Diagnostics.Log($"dictation started on \"{activeCaptureDevice.FriendlyName}\", target hwnd=0x{dictationTarget:X}");
        }
        catch (Exception ex)
        {
            recording = false;
            recorder?.Dispose();
            recorder = null;

            Diagnostics.Log("microphone could not be opened: " + ex);
            widget.FlashWarning(TimeSpan.FromSeconds(3));
            MessageBox.Show(DescribeMicrophoneFailure(ex),
                "PromptVoice", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// The saved device, if it is still present and active; otherwise the
    /// Windows default. A device that vanished must never block dictation.
    /// </summary>
    private MMDevice ResolveCaptureDevice(MMDeviceEnumerator devices)
    {
        if (!string.IsNullOrEmpty(settings.CaptureDeviceId))
        {
            try
            {
                var chosen = devices.GetDevice(settings.CaptureDeviceId);
                if (chosen.State == DeviceState.Active)
                    return chosen;

                Diagnostics.Log($"selected microphone is {chosen.State}; using the Windows default");
                chosen.Dispose();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("selected microphone not found (" + ex.Message + "); using the Windows default");
            }
        }

        return devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
    }

    /// <summary>Turns WASAPI HRESULTs into something a person can act on.</summary>
    private static string DescribeMicrophoneFailure(Exception ex)
    {
        uint code = unchecked((uint)ex.HResult);
        string reason = code switch
        {
            0x88890004 => "The selected microphone is no longer available. It may be unplugged or asleep.",
            0x88890008 => "The microphone is in exclusive use by another application.",
            0x8889000A => "The microphone is being used by another app and cannot be shared right now.",
            0x80070005 => "Windows is blocking microphone access. Check Settings > Privacy & security > Microphone.",
            0x80070490 => "No microphone was found. Connect one, or check it is enabled in Sound settings.",
            _ => ex.Message
        };

        return reason + Environment.NewLine + Environment.NewLine +
               "Pick a different device under Settings > Microphone, or choose \"Windows default\"." +
               Environment.NewLine + $"(code 0x{code:X8})";
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        capturedAudio.Write(e.Buffer, 0, e.BytesRecorded);
        var format = captureFormat;
        if (format is not null && widget.IsHandleCreated)
        {
            var level = GetAudioLevel(e.Buffer, e.BytesRecorded, format);
            capturePeak = Math.Max(capturePeak, level);
            try
            {
                widget.BeginInvoke(() => widget.SetAudioLevel(level));
            }
            catch (ObjectDisposedException)
            {
                // Shutting down mid-recording.
            }
        }
    }

    private void StopRecording()
    {
        if (!recording || recorder is null)
            return;

        recording = false;
        busy = true;
        widget.SetStatus(VoiceStatus.Processing);

        try
        {
            recorder.StopRecording();
        }
        catch (Exception ex)
        {
            busy = false;
            recorder.Dispose();
            recorder = null;

            Diagnostics.Log("could not stop capture: " + ex);
            widget.FlashWarning(TimeSpan.FromSeconds(3));
        }
    }

    private async void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        var format = captureFormat;
        recorder?.Dispose();
        recorder = null;
        activeCaptureDevice?.Dispose();
        activeCaptureDevice = null;

        if (e.Exception is not null)
        {
            busy = false;
            widget.FlashWarning(TimeSpan.FromSeconds(3));
            Diagnostics.Log("capture failed: " + e.Exception);
            return;
        }

        bool noSpeech = false;

        try
        {
            if (format is null)
                throw new InvalidOperationException("Could not determine the microphone's audio format.");

            widget.SetStatus(VoiceStatus.Processing);

            string wavPath = AppPaths.CapturePath;
            double seconds = capturedAudio.Length / (double)format.AverageBytesPerSecond;
            Diagnostics.Log($"captured {seconds:F1} s from \"{ActiveMicrophoneName}\", peak level {capturePeak:F3}");

            // A flat capture is a microphone problem, not a speech problem;
            // say so instead of letting Whisper return nothing.
            if (capturePeak < 0.01f)
            {
                Diagnostics.Log("capture is silent: check Settings > Microphone and Windows sound input");
                Diagnostics.SaveTranscript(string.Empty);
                noSpeech = true;
                return;
            }

            CreateWhisperWav(wavPath, capturedAudio.ToArray(), format);

            var watch = Stopwatch.StartNew();
            string text = await WhisperEngine.Transcribe(wavPath, settings);
            watch.Stop();

            if (string.IsNullOrWhiteSpace(text))
            {
                // Quiet failure: the widget says so, no balloon, and the empty
                // result is still recorded for troubleshooting.
                Diagnostics.Log("whisper returned no speech");
                Diagnostics.SaveTranscript(string.Empty);
                noSpeech = true;
                return;
            }

            InsertText(text, dictationTarget, watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("transcription failed: " + ex);
            MessageBox.Show("Transcription failed:\n\n" + ex.Message,
                "PromptVoice", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;

            if (noSpeech)
                widget.FlashWarning(TimeSpan.FromSeconds(2.5));
            else
                widget.SetStatus(VoiceStatus.Idle);
        }
    }

    /// <summary>
    /// Hands the transcript to <see cref="TextInserter"/>, records it in history,
    /// and surfaces a quiet warning if it could not reach the target application.
    /// </summary>
    private void InsertText(string text, IntPtr targetWindow, long transcribeMs)
    {
        TextInserter.InsertAsync(text, targetWindow, succeeded =>
        {
            if (settings.KeepHistory)
                DictationHistory.Add(new DictationEntry(text, DateTime.UtcNow, succeeded, settings.Model, transcribeMs));

            if (succeeded || !widget.IsHandleCreated)
                return;

            try
            {
                widget.BeginInvoke(() => widget.FlashWarning(TimeSpan.FromSeconds(4)));
            }
            catch (ObjectDisposedException)
            {
                // The app is shutting down mid-dictation; the transcript is
                // already safe in last-transcription.txt.
            }
        });
    }

    private static void CreateWhisperWav(string path, byte[] bytes, WaveFormat sourceFormat)
    {
        using var sourceStream = new MemoryStream(bytes);
        using var source = new RawSourceWaveStream(sourceStream, sourceFormat);

        var target = new WaveFormat(16000, 16, 1);

        using var resampler = new MediaFoundationResampler(source, target)
        {
            ResamplerQuality = 60
        };

        using var pcm = new MemoryStream();
        var chunk = new byte[16000 * 2];
        int read;
        while ((read = resampler.Read(chunk, 0, chunk.Length)) > 0)
            pcm.Write(chunk, 0, read);

        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm.GetBuffer(), 0, samples, 0, samples.Length * 2);

        // Peak-normalise to about -3 dBFS: a quiet microphone starves Whisper,
        // and that shows up as dropped words rather than an obvious failure.
        int peak = 0;
        foreach (short s in samples)
            peak = Math.Max(peak, Math.Abs((int)s));

        if (peak > 0 && peak < 23000)
        {
            float gain = 23000f / peak;
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
        }

        // 300 ms of silence either side. Capture starts on key-down so the first
        // syllable arrives mid-attack, and Whisper mis-segments clips that end
        // abruptly; padding fixes both and costs nothing.
        const int pad = 16000 * 300 / 1000;
        var padded = new short[samples.Length + pad * 2];
        Array.Copy(samples, 0, padded, pad, samples.Length);

        using var writer = new WaveFileWriter(path, target);
        writer.WriteSamples(padded, 0, padded.Length);
    }

    private static float GetAudioLevel(byte[] buffer, int length, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            float peak = 0;
            for (var offset = 0; offset + 3 < length; offset += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, offset)));
            return Math.Min(1, peak * 2.2f);
        }

        if (format.BitsPerSample == 16)
        {
            float peak = 0;
            for (var offset = 0; offset + 1 < length; offset += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, offset)) / 32768f);
            return Math.Min(1, peak * 2.2f);
        }

        return 0;
    }

    protected override void ExitThreadCore()
    {
        try
        {
            if (recording)
                recorder?.StopRecording();
        }
        catch { }

        if (keyboardHook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(keyboardHook);

        window?.Dispose();

        widget.Close();
        widget.Dispose();

        tray.Visible = false;
        tray.Dispose();

        recorder?.Dispose();
        activeCaptureDevice?.Dispose();
        capturedAudio.Dispose();

        base.ExitThreadCore();
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(data);
            var messageId = message.ToInt32();

            if (key.vkCode == NativeMethods.VK_SPACE)
            {
                if ((messageId == NativeMethods.WM_KEYDOWN || messageId == NativeMethods.WM_SYSKEYDOWN) && ModifierHeld())
                {
                    if (!spaceHeld)
                    {
                        spaceHeld = true;
                        // Capture this synchronously, before any UI work can
                        // change the foreground window.
                        dictationTarget = NativeMethods.GetForegroundWindow();
                        widget.BeginInvoke(BeginDictation);
                    }

                    return (IntPtr)1;
                }

                if ((messageId == NativeMethods.WM_KEYUP || messageId == NativeMethods.WM_SYSKEYUP) && spaceHeld)
                {
                    spaceHeld = false;
                    widget.BeginInvoke(StopRecording);
                    return (IntPtr)1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(keyboardHook, code, message, data);
    }

    private bool ModifierHeld() => settings.Hotkey switch
    {
        Hotkey.AltSpace => Down(NativeMethods.VK_LMENU) || Down(NativeMethods.VK_RMENU),
        Hotkey.CtrlShiftSpace => (Down(NativeMethods.VK_LCONTROL) || Down(NativeMethods.VK_RCONTROL)) &&
                                 (Down(NativeMethods.VK_LSHIFT) || Down(NativeMethods.VK_RSHIFT)),
        Hotkey.WinSpace => Down(NativeMethods.VK_LWIN) || Down(NativeMethods.VK_RWIN),
        _ => Down(NativeMethods.VK_LCONTROL) || Down(NativeMethods.VK_RCONTROL)
    };

    private static bool Down(ushort virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
