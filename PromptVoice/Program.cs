using System.Threading;

namespace PromptVoice;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Verifies the insertion path on its own, without a microphone:
        //   PromptVoice.exe --test-insert "testing one two three" [delaySeconds]
        // Focus the target application during the delay; the result lands in
        // runtime\diagnostics.log.
        if (args.Length >= 2 && args[0] is "--test-insert")
        {
            RunInsertionSelfTest(args);
            return;
        }

        // Renders every screen to PNG off-screen, so the UI can be reviewed
        // without driving the real desktop:
        //   PromptVoice.exe --ui-shots <output directory>
        if (args.Length >= 2 && args[0] is "--ui-shots")
        {
            RenderUiShots(args[1]);
            return;
        }

        using var mutex = new Mutex(true, "PromptVoice.SingleInstance.v2", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "PromptVoice is already running. Check the Windows system tray.",
                "PromptVoice",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayApp(args.Contains("--autostart")));
    }

    private static void RenderUiShots(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var settings = AppSettings.Load();

        using (var window = new MainWindow(settings, () => { }))
        {
            // Off-screen, so nothing appears on the user's desktop.
            window.StartPosition = FormStartPosition.Manual;
            window.Location = new Point(-4000, -4000);
            window.Show();
            Application.DoEvents();

            string[] names = { "dictate", "history", "settings" };
            for (var i = 0; i < names.Length; i++)
            {
                window.SelectSection(i);
                Application.DoEvents();
                Thread.Sleep(150);
                Application.DoEvents();

                using var shot = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(shot, new Rectangle(0, 0, window.Width, window.Height));
                shot.Save(Path.Combine(outputDirectory, $"window-{i + 1}-{names[i]}.png"));
            }

            window.Hide();
        }

        using (var widget = new StatusWidget())
        {
            widget.Location = new Point(-4000, -4000);
            widget.Show();
            Application.DoEvents();

            (VoiceStatus Status, float Level, string Name)[] states =
            {
                (VoiceStatus.Idle, 0f, "idle"),
                (VoiceStatus.Listening, 0.75f, "listening"),
                (VoiceStatus.Processing, 0f, "processing"),
                (VoiceStatus.Warning, 0f, "warning")
            };

            foreach (var (status, level, name) in states)
            {
                using var shot = widget.Preview(status, level);
                shot.Save(Path.Combine(outputDirectory, $"pill-{name}.png"));
            }

            widget.Hide();
        }

        Console.WriteLine("ui shots written to " + outputDirectory);
    }

    private static void RunInsertionSelfTest(string[] args)
    {
        int delaySeconds = args.Length >= 3 && int.TryParse(args[2], out int parsed) ? parsed : 4;

        Diagnostics.Log($"=== self-test: waiting {delaySeconds}s for you to focus the target window ===");
        Thread.Sleep(delaySeconds * 1000);

        // Exactly the handle the keyboard hook would have captured.
        IntPtr target = NativeMethods.GetForegroundWindow();
        bool ok = TextInserter.Insert(args[1], target);

        Diagnostics.Log("=== self-test result: " + (ok ? "INSERTED" : "FAILED") + " ===");
        Environment.ExitCode = ok ? 0 : 1;
    }
}
