using System.Runtime.InteropServices;
using System.Text;

namespace PromptVoice;

/// <summary>
/// Puts recognised text into whichever window owned the caret when dictation
/// started. Everything runs on a dedicated STA thread so the clipboard is
/// usable and the widget's UI thread never blocks.
/// </summary>
internal static class TextInserter
{
    private const int ModifierReleaseWaitMs = 800;
    private const int ClipboardRestoreDelayMs = 700;

    /// <summary>Runs the insertion off-thread and reports success to <paramref name="onFinished"/>.</summary>
    public static void InsertAsync(string text, IntPtr target, Action<bool> onFinished)
    {
        var thread = new Thread(() =>
        {
            bool ok;
            try
            {
                ok = Insert(text, target);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("FATAL insertion error: " + ex);
                ok = false;
            }

            onFinished(ok);
        })
        { IsBackground = true, Name = "PromptVoice.Insert" };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static bool Insert(string text, IntPtr target)
    {
        Diagnostics.Log($"--- insert begin --- {text.Length} chars: {Quote(text)}");
        Diagnostics.Log("target hwnd=0x" + target.ToString("X") + " " + DescribeWindow(target));
        Diagnostics.SaveTranscript(text);

        ClearHeldModifiers();
        RestoreTarget(target);

        // Clipboard + Ctrl+V is the only mechanism that works across classic
        // Notepad, modern (RichEdit) Notepad, browsers, and Electron apps alike.
        string? savedClipboard = ReadClipboardText();
        if (WriteClipboardText(text) && SendCtrlV())
        {
            Thread.Sleep(ClipboardRestoreDelayMs);
            RestoreClipboard(text, savedClipboard);
            Diagnostics.Log("--- insert end: clipboard paste sent ---");
            return true;
        }

        // Fallback: synthesise the characters directly. Modifiers are already
        // cleared above, which is what made earlier attempts silently fail.
        Diagnostics.Log("falling back to Unicode keystroke injection");
        bool typed = TypeUnicode(text);
        RestoreClipboard(text, savedClipboard);
        Diagnostics.Log("--- insert end: typed=" + typed + " ---");
        return typed;
    }

    /// <summary>
    /// Ctrl is still physically held when the user lets go of Space, so any
    /// synthesised keystroke arrives as a Ctrl shortcut unless the modifier
    /// state is cleared first. Wait briefly for a real release, then force it.
    /// </summary>
    private static void ClearHeldModifiers()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ModifierReleaseWaitMs && AnyModifierDown())
            Thread.Sleep(20);

        var stuck = NativeMethods.ModifierKeys
            .Where(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) != 0)
            .ToArray();

        if (stuck.Length == 0)
        {
            Diagnostics.Log($"modifiers clear after {watch.ElapsedMilliseconds} ms");
            return;
        }

        Diagnostics.Log($"forcing key-up for still-held modifiers after {watch.ElapsedMilliseconds} ms: " +
                        string.Join(", ", stuck.Select(k => "0x" + k.ToString("X2"))));

        Send(stuck.Select(k => KeyInput(k, true)).ToArray(), "modifier release");
        Thread.Sleep(30);
    }

    private static bool AnyModifierDown() =>
        NativeMethods.ModifierKeys.Any(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) != 0);

    private static void RestoreTarget(IntPtr target)
    {
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            Diagnostics.Log("no usable target window; pasting into whatever has focus");
            return;
        }

        IntPtr current = NativeMethods.GetForegroundWindow();
        if (current == target)
        {
            Diagnostics.Log("target already in foreground");
            return;
        }

        Diagnostics.Log("foreground drifted to 0x" + current.ToString("X") + " " + DescribeWindow(current) + "; restoring target");

        // A process with no foreground window cannot call SetForegroundWindow
        // on its own, so borrow the current foreground thread's input queue.
        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(current, out _);
        uint targetThread = NativeMethods.GetWindowThreadProcessId(target, out _);
        bool attached = foregroundThread != 0 && targetThread != 0 &&
                        NativeMethods.AttachThreadInput(foregroundThread, targetThread, true);

        bool set = NativeMethods.SetForegroundWindow(target);
        int error = Marshal.GetLastWin32Error();

        if (attached)
            NativeMethods.AttachThreadInput(foregroundThread, targetThread, false);

        Thread.Sleep(60);
        IntPtr now = NativeMethods.GetForegroundWindow();
        Diagnostics.Log($"SetForegroundWindow={set} attached={attached} lastError={error} foregroundNow=0x{now:X}");
    }

    private static string? ReadClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("could not read the existing clipboard: " + ex.Message);
            return null;
        }
    }

    /// <summary>Clipboard ownership is contended; a couple of retries fixes nearly every failure.</summary>
    private static bool WriteClipboardText(string text)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                bool verified = Clipboard.ContainsText() && Clipboard.GetText() == text;
                Diagnostics.Log($"clipboard set on attempt {attempt}, verified={verified}");
                if (verified)
                    return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"clipboard attempt {attempt} failed: {ex.GetType().Name}: {ex.Message}");
            }

            Thread.Sleep(60 * attempt);
        }

        Diagnostics.Log("clipboard could not be populated");
        return false;
    }

    private static void RestoreClipboard(string ours, string? saved)
    {
        try
        {
            // Leave it alone if the user copied something else in the meantime.
            if (!Clipboard.ContainsText() || Clipboard.GetText() != ours)
            {
                Diagnostics.Log("clipboard changed by the user; leaving it as-is");
                return;
            }

            if (saved is null)
            {
                Clipboard.Clear();
                Diagnostics.Log("clipboard cleared (nothing to restore)");
            }
            else
            {
                Clipboard.SetText(saved);
                Diagnostics.Log("previous clipboard text restored");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("clipboard restore failed: " + ex.Message);
        }
    }

    private static bool SendCtrlV()
    {
        var inputs = new[]
        {
            KeyInput(NativeMethods.VK_CONTROL, false),
            KeyInput(NativeMethods.VK_V, false),
            KeyInput(NativeMethods.VK_V, true),
            KeyInput(NativeMethods.VK_CONTROL, true)
        };

        return Send(inputs, "Ctrl+V");
    }

    private static bool TypeUnicode(string text)
    {
        var inputs = new List<NativeMethods.INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(UnicodeInput(c, false));
            inputs.Add(UnicodeInput(c, true));
        }

        return inputs.Count > 0 && Send(inputs.ToArray(), "Unicode typing");
    }

    /// <summary>
    /// 40 bytes on x64, 28 on x86. A wrong value makes every SendInput call
    /// fail with ERROR_INVALID_PARAMETER and inject nothing at all, which is
    /// exactly how this app silently dropped every transcript.
    /// </summary>
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    private static bool Send(NativeMethods.INPUT[] inputs, string what)
    {
        int expected = IntPtr.Size == 8 ? 40 : 28;
        if (InputSize != expected)
        {
            Diagnostics.Log($"ABORT: sizeof(INPUT)={InputSize}, expected {expected}; SendInput would reject every event");
            return false;
        }

        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
        int error = Marshal.GetLastWin32Error();
        bool ok = sent == inputs.Length;
        Diagnostics.Log($"SendInput({what}): sent {sent}/{inputs.Length}" + (ok ? "" : $", lastError={error}"));
        return ok;
    }

    private static NativeMethods.INPUT KeyInput(ushort virtualKey, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = (ushort)NativeMethods.MapVirtualKey(virtualKey, NativeMethods.MAPVK_VK_TO_VSC),
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
            }
        }
    };

    private static NativeMethods.INPUT UnicodeInput(char c, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wScan = c,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0)
            }
        }
    };

    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return "(not a window)";

        var title = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

        string process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            process = "pid " + pid;
        }

        return $"[{process}] \"{title}\"";
    }

    private static string Quote(string text) => "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
