using System.Diagnostics;
using System.Drawing.Drawing2D;
using NAudio.CoreAudioApi;

namespace PromptVoice;

/// <summary>
/// The app window: a left rail plus three panes. Closing hides it rather than
/// quitting, because dictation lives in the tray.
/// </summary>
internal sealed class MainWindow : Form
{
    private readonly AppSettings settings;
    private readonly Action settingsChanged;
    private readonly List<(NavItem Nav, Control Pane)> sections = new();
    private readonly Panel content;
    private Panel rail = null!;
    private Panel brand = null!;
    private Panel? historyList;

    public MainWindow(AppSettings settings, Action settingsChanged)
    {
        this.settings = settings;
        this.settingsChanged = settingsChanged;

        Text = "PromptVoice";
        BackColor = Theme.Canvas;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Body;
        // Tall enough that the longest pane (Settings) needs no scrollbar at the
        // default size; it still scrolls if the user shrinks the window.
        ClientSize = new Size(920, 860);
        MinimumSize = new Size(840, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Get();
        DoubleBuffered = true;

        content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(28, 20, 28, 18) };

        BuildRail();
        Controls.Add(content);
        Controls.Add(rail);

        AddSection("Dictate", "●", BuildDictatePane());
        AddSection("History", "≡", BuildHistoryPane());
        AddSection("Settings", "⚙", BuildSettingsPane());
        PopulateRail();
        Select(0);

        DictationHistory.Changed += OnHistoryChanged;
        FormClosing += (_, e) =>
        {
            // The tray owns the app lifetime; the window just goes away.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void BuildRail()
    {
        // Wide enough for the wordmark at 150% display scaling, where the title
        // font grows but the rail does not.
        rail = new Panel { Dock = DockStyle.Left, Width = 244, BackColor = Theme.Sidebar, Padding = new Padding(12) };

        brand = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Theme.Sidebar };
        brand.Paint += (_, e) =>
        {
            Theme.UseQuality(e.Graphics);
            using var badge = new SolidBrush(Theme.Accent);
            using var path = Theme.RoundedRect(new RectangleF(4, 22, 34, 34), 10);
            e.Graphics.FillPath(badge, path);

            using var pen = new Pen(Color.White, 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawArc(pen, 17f, 30f, 8f, 12f, 0, 360);
            e.Graphics.DrawArc(pen, 14.5f, 34f, 13f, 11f, 0, 180);
            e.Graphics.DrawLine(pen, 21f, 45f, 21f, 48f);

            int available = brand.Width - 52;
            Theme.Label(e.Graphics, "PromptVoice", Theme.Title, new Point(48, 22), available, Theme.TextPrimary);
            Theme.Label(e.Graphics, "Offline dictation", Theme.Small,
                new Point(48, 22 + Theme.LineHeight(Theme.Title)), available, Theme.TextMuted);
        };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Theme.Sidebar };
        footer.Paint += (_, e) =>
        {
            Theme.UseQuality(e.Graphics);
            using var dot = new SolidBrush(Theme.Success);
            e.Graphics.FillEllipse(dot, 6, 19, 7, 7);
            TextRenderer.DrawText(e.Graphics, "Runs fully offline", Theme.Small,
                new Rectangle(20, 12, 170, 20), Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        };

        rail.Controls.Add(footer);
    }

    private void AddSection(string name, string glyph, Control pane)
    {
        var nav = new NavItem(name, glyph) { Dock = DockStyle.Top };
        int index = sections.Count;
        nav.Click += (_, _) => Select(index);

        pane.Dock = DockStyle.Fill;
        pane.Visible = false;
        content.Controls.Add(pane);

        sections.Add((nav, pane));
    }

    /// <summary>
    /// WinForms docks the most-recently-added control first, so the rail is
    /// filled back to front: last nav item, ..., first nav item, then the brand.
    /// </summary>
    private void PopulateRail()
    {
        for (var i = sections.Count - 1; i >= 0; i--)
            rail.Controls.Add(sections[i].Nav);

        rail.Controls.Add(brand);
    }

    /// <summary>Switches pane without a mouse; used by the --ui-shots review mode.</summary>
    internal void SelectSection(int index) => Select(index);

    private void Select(int index)
    {
        for (var i = 0; i < sections.Count; i++)
        {
            sections[i].Nav.Selected = i == index;
            sections[i].Pane.Visible = i == index;
        }

        if (index == 1)
            RefreshHistory();
    }

    // --- Dictate -------------------------------------------------------------

    private Control BuildDictatePane()
    {
        var pane = new Panel { BackColor = Theme.Canvas, AutoScroll = true };

        const string heroBody =
            "Click into any app, hold the shortcut, speak, then release.\nThe text is pasted at your cursor.";

        int heroHeight = 26 + Theme.LineHeight(Theme.Display) + 10 +
                         Theme.LineHeight(Theme.Body) * 2 + 14 + KeyCapHeight() + 26;

        var hero = new Panel { Dock = DockStyle.Top, Height = heroHeight, BackColor = Theme.Canvas };
        hero.Paint += (_, e) =>
        {
            Theme.UseQuality(e.Graphics);
            var bounds = new RectangleF(0, 0, hero.Width - 1, hero.Height - 1);
            Theme.FillRounded(e.Graphics, bounds, Theme.RadiusCard, Theme.Surface);
            Theme.DrawRounded(e.Graphics, bounds, Theme.RadiusCard, Theme.BorderSubtle);

            int y = 26;
            Theme.Label(e.Graphics, "Hold to talk", Theme.Display, new Point(26, y), hero.Width - 52, Theme.TextPrimary);
            y += Theme.LineHeight(Theme.Display) + 10;

            TextRenderer.DrawText(e.Graphics, heroBody, Theme.Body,
                new Rectangle(26, y, Math.Min(600, hero.Width - 52), Theme.LineHeight(Theme.Body) * 2 + 4),
                Theme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoClipping);
            y += Theme.LineHeight(Theme.Body) * 2 + 14;

            DrawKeyCaps(e.Graphics, 26, y, HotkeyCaps(settings.Hotkey));
        };

        int statHeight = 18 + Theme.LineHeight(Theme.SmallStrong) + 8 + Theme.LineHeight(Theme.Title) + 18;
        var stats = new Panel { Dock = DockStyle.Top, Height = statHeight + 14, BackColor = Theme.Canvas };
        var modelCard = StatCard("Model", () => WhisperEngine.Describe(settings.Model).Name);
        var vocabCard = StatCard("Vocabulary", () => settings.UseTechnicalVocabulary ? "Technical" : "Plain English");
        var threadCard = StatCard("Microphone", () => ResolvedMicrophoneName());
        stats.Controls.AddRange(new Control[] { modelCard, vocabCard, threadCard });
        stats.Resize += (_, _) =>
        {
            const int gap = 14;
            int w = (stats.Width - gap * 2) / 3;
            modelCard.SetBounds(0, 14, w, statHeight);
            vocabCard.SetBounds(w + gap, 14, w, statHeight);
            threadCard.SetBounds((w + gap) * 2, 14, stats.Width - (w + gap) * 2, statHeight);
        };

        bool onboarding = !settings.OnboardingComplete;

        string[] tipLines =
        {
            "Speak in full sentences; Whisper uses context to pick words.",
            "Say symbols aloud: \"dot t s\", \"slash\", \"dash\".",
            "Add project names under Settings, Custom vocabulary.",
            "Wait for the pill to turn blue; that is Whisper running."
        };

        string[] onboardingLines =
        {
            "Click into the app you want to dictate into - your cursor stays there.",
            "Hold the shortcut above and speak a whole sentence.",
            "Let go. The text appears where your cursor was.",
            "Nothing leaves this machine; transcription runs locally."
        };

        int tipStep = Theme.LineHeight(Theme.Body) + 6;
        int tipsHeight = 14 + 22 + Theme.LineHeight(Theme.BodyStrong) + 12 + tipLines.Length * tipStep + 20;

        var tips = new Panel { Dock = DockStyle.Top, Height = tipsHeight, BackColor = Theme.Canvas };
        tips.Paint += (_, e) =>
        {
            Theme.UseQuality(e.Graphics);
            var bounds = new RectangleF(0, 14, tips.Width - 1, tips.Height - 15);
            Theme.FillRounded(e.Graphics, bounds, Theme.RadiusCard, Theme.Surface);
            Theme.DrawRounded(e.Graphics, bounds, Theme.RadiusCard, Theme.BorderSubtle);

            Theme.Label(e.Graphics, onboarding ? "Get started in three steps" : "Getting the best results",
                Theme.BodyStrong, new Point(22, 36), tips.Width - 44, Theme.TextPrimary);

            string[] lines = onboarding ? onboardingLines : tipLines;
            int top = 36 + Theme.LineHeight(Theme.BodyStrong) + 12;

            for (var i = 0; i < lines.Length; i++)
            {
                int y = top + i * tipStep;

                if (onboarding)
                {
                    // Numbered markers, because these are ordered steps rather
                    // than an unordered list of advice.
                    var marker = new RectangleF(22, y + Theme.LineHeight(Theme.Body) / 2f - 9, 18, 18);
                    Theme.FillRounded(e.Graphics, marker, 9, Color.FromArgb(46, Theme.Accent));
                    TextRenderer.DrawText(e.Graphics, (i + 1).ToString(), Theme.SmallStrong,
                        Rectangle.Round(marker), Theme.Accent,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                else
                {
                    using var dot = new SolidBrush(Theme.Accent);
                    e.Graphics.FillEllipse(dot, 24, y + Theme.LineHeight(Theme.Body) / 2f - 2, 4, 4);
                }

                Theme.Label(e.Graphics, lines[i], Theme.Body, new Point(48, y), tips.Width - 72, Theme.TextSecondary);
            }
        };

        if (onboarding)
        {
            var done = new FlatButton("Got it", ButtonKind.Primary) { AccessibleName = "Finish onboarding" };
            done.Click += (_, _) =>
            {
                onboarding = false;
                settings.OnboardingComplete = true;
                settings.Save();
                done.Dispose();
                tips.Invalidate();
            };
            tips.Controls.Add(done);
            tips.Resize += (_, _) => done.Location =
                new Point(tips.Width - done.Width - 22, tips.Height - done.Height - 18);
            tips.Height += done.Height + 10;
        }

        pane.Controls.Add(tips);
        pane.Controls.Add(stats);
        pane.Controls.Add(hero);
        return pane;
    }

    /// <summary>What a dictation will actually record from right now, after fallback.</summary>
    private string ResolvedMicrophoneName()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(settings.CaptureDeviceId))
            {
                try
                {
                    using var chosen = devices.GetDevice(settings.CaptureDeviceId);
                    if (chosen.State == DeviceState.Active)
                        return chosen.FriendlyName;
                }
                catch { /* not present: fall through to the default */ }
            }

            using var fallback = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return fallback.FriendlyName;
        }
        catch
        {
            return "No microphone";
        }
    }

    private static Panel StatCard(string label, Func<string> value)
    {
        var card = new Panel { BackColor = Theme.Canvas };
        card.Paint += (_, e) =>
        {
            Theme.UseQuality(e.Graphics);
            var bounds = new RectangleF(0, 0, card.Width - 1, card.Height - 1);
            Theme.FillRounded(e.Graphics, bounds, Theme.RadiusCard, Theme.Surface);
            Theme.DrawRounded(e.Graphics, bounds, Theme.RadiusCard, Theme.BorderSubtle);

            Theme.Label(e.Graphics, label.ToUpperInvariant(), Theme.SmallStrong,
                new Point(18, 18), card.Width - 36, Theme.TextMuted);
            Theme.Label(e.Graphics, value(), Theme.Title,
                new Point(18, 18 + Theme.LineHeight(Theme.SmallStrong) + 8), card.Width - 36, Theme.TextPrimary);
        };
        return card;
    }

    private static string[] HotkeyCaps(Hotkey hotkey) => hotkey switch
    {
        Hotkey.AltSpace => new[] { "Alt", "Space" },
        Hotkey.CtrlShiftSpace => new[] { "Ctrl", "Shift", "Space" },
        Hotkey.WinSpace => new[] { "Win", "Space" },
        _ => new[] { "Ctrl", "Space" }
    };

    private static int KeyCapHeight() => Theme.LineHeight(Theme.BodyStrong) + 14;

    /// <summary>Keycaps read as keys, not as text, which makes the shortcut obvious.</summary>
    private static void DrawKeyCaps(Graphics g, int x, int y, string[] caps)
    {
        int height = KeyCapHeight();

        for (var i = 0; i < caps.Length; i++)
        {
            var size = TextRenderer.MeasureText(caps[i], Theme.BodyStrong);
            int width = Math.Max(48, size.Width + 22);
            var bounds = new RectangleF(x, y, width, height);

            Theme.FillRounded(g, bounds, Theme.RadiusControl, Theme.SurfaceRaised);
            Theme.DrawRounded(g, bounds, Theme.RadiusControl, Theme.Border);

            // One-pixel bottom highlight suggests a physical key face.
            using var lip = new Pen(Color.FromArgb(30, 255, 255, 255));
            g.DrawLine(lip, bounds.Left + 7, bounds.Bottom - 1.5f, bounds.Right - 7, bounds.Bottom - 1.5f);

            TextRenderer.DrawText(g, caps[i], Theme.BodyStrong, Rectangle.Round(bounds), Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            x += width;

            if (i < caps.Length - 1)
            {
                TextRenderer.DrawText(g, "+", Theme.Body, new Rectangle(x, y, 18, height), Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                x += 18;
            }
        }
    }

    // --- History -------------------------------------------------------------

    private Control BuildHistoryPane()
    {
        var pane = new Panel { BackColor = Theme.Canvas };

        var clear = new FlatButton("Clear history", ButtonKind.Ghost);
        int headerHeight = Math.Max(clear.Height + 12,
            6 + Theme.LineHeight(Theme.Title) + 4 + Theme.LineHeight(Theme.Small) + 12);

        var header = new Panel { Dock = DockStyle.Top, Height = headerHeight, BackColor = Theme.Canvas };
        header.Paint += (_, e) =>
        {
            Theme.UseQuality(e.Graphics);
            Theme.Label(e.Graphics, "Recent dictations", Theme.Title, new Point(0, 6),
                header.Width - clear.Width - 20, Theme.TextPrimary);
            Theme.Label(e.Graphics, "Stored locally. Click any entry to copy it.", Theme.Small,
                new Point(0, 6 + Theme.LineHeight(Theme.Title) + 4), header.Width - clear.Width - 20, Theme.TextMuted);
        };

        clear.Click += (_, _) => { DictationHistory.Clear(); RefreshHistory(); };
        header.Controls.Add(clear);
        header.Resize += (_, _) => clear.Location = new Point(header.Width - clear.Width, 6);

        historyList = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, AutoScroll = true };

        pane.Controls.Add(historyList);
        pane.Controls.Add(header);
        return pane;
    }

    private void OnHistoryChanged()
    {
        if (!IsHandleCreated)
            return;

        try
        {
            BeginInvoke(RefreshHistory);
        }
        catch (ObjectDisposedException)
        {
            // Window closed while a dictation was finishing.
        }
    }

    private void RefreshHistory()
    {
        if (historyList is null)
            return;

        historyList.SuspendLayout();
        foreach (Control existing in historyList.Controls.Cast<Control>().ToList())
            existing.Dispose();
        historyList.Controls.Clear();

        var entries = DictationHistory.All();

        if (entries.Count == 0)
        {
            var empty = new Panel { Dock = DockStyle.Top, Height = 170, BackColor = Theme.Canvas };
            empty.Paint += (_, e) =>
            {
                Theme.UseQuality(e.Graphics);
                using var dashed = new Pen(Theme.Border) { DashStyle = DashStyle.Dash, DashPattern = new[] { 4f, 4f } };
                using var path = Theme.RoundedRect(new RectangleF(0, 0, empty.Width - 1, 158), Theme.RadiusCard);
                e.Graphics.DrawPath(dashed, path);

                TextRenderer.DrawText(e.Graphics, "Nothing dictated yet", Theme.BodyStrong,
                    new Rectangle(0, 58, empty.Width, 22), Theme.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, "Hold your shortcut anywhere and speak.", Theme.Small,
                    new Rectangle(0, 82, empty.Width, 20), Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            };
            historyList.Controls.Add(empty);
        }
        else
        {
            // Reverse insertion keeps the newest entry at the top under DockStyle.Top.
            for (var i = entries.Count - 1; i >= 0; i--)
                historyList.Controls.Add(HistoryRow(entries[i]));
        }

        historyList.ResumeLayout();
    }

    private static Panel HistoryRow(DictationEntry entry)
    {
        int rowHeight = 11 + Theme.LineHeight(Theme.Body) + 4 + Theme.LineHeight(Theme.Mono) + 11 + 10;
        var row = new Panel { Dock = DockStyle.Top, Height = rowHeight, BackColor = Theme.Canvas, Cursor = Cursors.Hand };
        bool hovered = false;
        bool copied = false;

        row.MouseEnter += (_, _) => { hovered = true; row.Invalidate(); };
        row.MouseLeave += (_, _) => { hovered = false; row.Invalidate(); };
        row.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(entry.Text);
                copied = true;
                row.Invalidate();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("history copy failed: " + ex.Message);
            }
        };

        row.Paint += (_, e) =>
        {
            Theme.UseQuality(e.Graphics);
            var bounds = new RectangleF(0, 0, row.Width - 1, row.Height - 10);
            Theme.FillRounded(e.Graphics, bounds, Theme.RadiusCard, hovered ? Theme.SurfaceRaised : Theme.Surface);
            Theme.DrawRounded(e.Graphics, bounds, Theme.RadiusCard, hovered ? Theme.Border : Theme.BorderSubtle);

            string badge = copied ? "Copied" : entry.Inserted ? "Inserted" : "Not inserted";
            var badgeColor = copied ? Theme.Accent : entry.Inserted ? Theme.Success : Theme.Warning;
            var badgeSize = TextRenderer.MeasureText(badge, Theme.SmallStrong);
            int badgeRoom = badgeSize.Width + 52;

            int textWidth = Math.Max(60, row.Width - badgeRoom);
            Theme.Label(e.Graphics, entry.Text, Theme.Body, new Point(18, 11), textWidth, Theme.TextPrimary);

            // Monospaced metadata so the timings line up down the column.
            string meta = $"{entry.Utc.ToLocalTime():HH:mm}  |  {WhisperEngine.Describe(entry.Model).Name}  |  {entry.Milliseconds} ms";
            Theme.Label(e.Graphics, meta, Theme.Mono,
                new Point(18, 11 + Theme.LineHeight(Theme.Body) + 4), textWidth, Theme.TextMuted);

            int badgeHeight = Theme.LineHeight(Theme.SmallStrong) + 8;
            var badgeRect = new RectangleF(row.Width - badgeSize.Width - 34,
                (row.Height - 10 - badgeHeight) / 2f, badgeSize.Width + 16, badgeHeight);

            Theme.FillRounded(e.Graphics, badgeRect, Theme.RadiusPill, Color.FromArgb(38, badgeColor));
            TextRenderer.DrawText(e.Graphics, badge, Theme.SmallStrong, Rectangle.Round(badgeRect), badgeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        return row;
    }

    // --- Settings ------------------------------------------------------------

    private Control BuildSettingsPane()
    {
        var pane = new Panel { BackColor = Theme.Canvas, AutoScroll = true };

        int titleHeight = 6 + Theme.LineHeight(Theme.Title) + 4 + Theme.LineHeight(Theme.Small) + 12;
        var title = new Panel { Dock = DockStyle.Top, Height = titleHeight, BackColor = Theme.Canvas };
        title.Paint += (_, e) =>
        {
            Theme.UseQuality(e.Graphics);
            Theme.Label(e.Graphics, "Settings", Theme.Title, new Point(0, 6), title.Width, Theme.TextPrimary);
            Theme.Label(e.Graphics, "Saved instantly, next to the model files.", Theme.Small,
                new Point(0, 6 + Theme.LineHeight(Theme.Title) + 4), title.Width, Theme.TextMuted);
        };

        var group = new Card { Dock = DockStyle.Top, Padding = new Padding(1), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

        var modelSelect = new DarkSelect { Width = 210, AccessibleName = "Whisper model" };
        var catalogue = WhisperEngine.Catalogued;
        foreach (var model in catalogue)
            modelSelect.Items.Add(WhisperEngine.IsInstalled(model.FileName)
                ? model.Name
                : $"{model.Name} (download {ModelInstaller.DescribeSize(model.FileName)})");

        int installedIndex = 0;
        for (var i = 0; i < catalogue.Count; i++)
            if (catalogue[i].FileName == settings.Model) installedIndex = i;
        modelSelect.SelectedIndex = installedIndex;

        modelSelect.SelectedIndexChanged += async (_, _) =>
        {
            var chosen = catalogue[modelSelect.SelectedIndex];
            if (WhisperEngine.IsInstalled(chosen.FileName))
            {
                settings.Model = chosen.FileName;
                Commit();
                return;
            }

            await DownloadModel(chosen, modelSelect, installedIndex);
            installedIndex = modelSelect.SelectedIndex;
        };

        var deviceSelect = new DarkSelect { Width = 210 };
        var devices = new List<(string? Id, string Name)> { (null, "Windows default") };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                devices.Add((device.ID, device.FriendlyName));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("could not enumerate capture devices: " + ex.Message);
        }

        foreach (var device in devices)
            deviceSelect.Items.Add(device.Name);
        deviceSelect.SelectedIndex = Math.Max(0, devices.FindIndex(d => d.Id == settings.CaptureDeviceId));
        deviceSelect.SelectedIndexChanged += (_, _) =>
        {
            settings.CaptureDeviceId = devices[deviceSelect.SelectedIndex].Id;
            Commit();
        };

        var hotkeys = new[]
        {
            (Hotkey.CtrlSpace, "Ctrl + Space"),
            (Hotkey.AltSpace, "Alt + Space"),
            (Hotkey.CtrlShiftSpace, "Ctrl + Shift + Space"),
            (Hotkey.WinSpace, "Win + Space")
        };
        var hotkeySelect = new DarkSelect { Width = 210 };
        foreach (var (_, label) in hotkeys)
            hotkeySelect.Items.Add(label);
        hotkeySelect.SelectedIndex = Math.Max(0, Array.FindIndex(hotkeys, h => h.Item1 == settings.Hotkey));
        hotkeySelect.SelectedIndexChanged += (_, _) =>
        {
            settings.Hotkey = hotkeys[hotkeySelect.SelectedIndex].Item1;
            Commit();
        };

        var vocabToggle = new ToggleSwitch();
        vocabToggle.SetInitial(settings.UseTechnicalVocabulary);
        vocabToggle.Toggled += (_, _) => { settings.UseTechnicalVocabulary = vocabToggle.Checked; Commit(); };

        bool gpu = WhisperEngine.GpuAvailable;
        var computeSelect = new DarkSelect { Width = 210, AccessibleName = "Compute device" };
        computeSelect.Items.Add(gpu ? "Automatic (GPU)" : "Automatic (CPU)");
        computeSelect.Items.Add("CPU only");
        computeSelect.Items.Add(gpu ? "GPU" : "GPU (not installed)");
        computeSelect.SelectedIndex = (int)settings.Compute;
        computeSelect.SelectedIndexChanged += (_, _) =>
        {
            if (computeSelect.SelectedIndex == (int)ComputeDevice.Gpu && !gpu)
            {
                computeSelect.SelectedIndex = (int)settings.Compute;
                MessageBox.Show(this,
                    "This copy of PromptVoice has a CPU-only Whisper runtime." + Environment.NewLine + Environment.NewLine +
                    "GPU support needs a Vulkan build of whisper-cli.exe: run setup-whisper.ps1 -Vulkan " +
                    "with the Vulkan SDK installed, then rebuild the installer.",
                    "GPU not available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            settings.Compute = (ComputeDevice)computeSelect.SelectedIndex;
            Commit();
        };

        var startupToggle = new ToggleSwitch { AccessibleName = "Start with Windows" };
        startupToggle.SetInitial(settings.LaunchAtStartup && StartupRegistration.IsEnabled());
        startupToggle.Toggled += (_, _) =>
        {
            if (StartupRegistration.Set(startupToggle.Checked))
            {
                settings.LaunchAtStartup = startupToggle.Checked;
                Commit();
            }
            else
            {
                // Policy or a locked profile refused the write; never leave the
                // switch showing a state Windows will not honour.
                startupToggle.SetInitial(!startupToggle.Checked);
                MessageBox.Show(this,
                    "Windows would not let PromptVoice change the startup setting." +
                    Environment.NewLine + Environment.NewLine +
                    "This is usually a company policy on the Run registry key. " +
                    "See diagnostics.log for details.",
                    "Start with Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var historyToggle = new ToggleSwitch();
        historyToggle.SetInitial(settings.KeepHistory);
        historyToggle.Toggled += (_, _) => { settings.KeepHistory = historyToggle.Checked; Commit(); };

        var vocabBox = new TextBox
        {
            Width = 210,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Body,
            Text = settings.CustomVocabulary
        };
        vocabBox.Leave += (_, _) => { settings.CustomVocabulary = vocabBox.Text.Trim(); Commit(); };

        var correctionsBox = new TextBox
        {
            Width = 210,
            Height = 92,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Mono,
            Text = string.Join(Environment.NewLine, settings.Corrections),
            AccessibleName = "Corrections, one heard => meant pair per line"
        };
        correctionsBox.Leave += (_, _) =>
        {
            settings.Corrections = correctionsBox.Lines
                .Select(l => l.Trim())
                .Where(l => l.Contains("=>"))
                .ToList();
            Commit();
        };

        // Added bottom-first: DockStyle.Top stacks in reverse.
        group.Controls.Add(new SettingRow("Corrections",
            "Autocorrect: one \"heard => meant\" per line, applied after every dictation. Add the words Whisper keeps getting wrong.",
            correctionsBox, showDivider: false));
        group.Controls.Add(new SettingRow("Custom vocabulary",
            "Names Whisper keeps mishearing, comma separated.", vocabBox));
        group.Controls.Add(new SettingRow("Compute device",
            gpu ? "GPU runtime detected." : "CPU only in this build; see the README for a GPU build.", computeSelect));
        group.Controls.Add(new SettingRow("Start with Windows",
            "Launch automatically and wait in the tray.", startupToggle));
        group.Controls.Add(new SettingRow("Keep history",
            "Store the last 50 transcripts locally.", historyToggle));
        group.Controls.Add(new SettingRow("Technical vocabulary",
            "Primes Whisper with coding and AI tool names.", vocabToggle));
        group.Controls.Add(new SettingRow("Shortcut",
            "Hold to record, release to transcribe.", hotkeySelect));
        group.Controls.Add(new SettingRow("Microphone",
            "Which input device to record from.", deviceSelect));
        group.Controls.Add(new SettingRow("Model",
            "Larger models are more accurate but slower.", modelSelect));

        var openRuntime = new FlatButton("Open runtime folder") { Left = 0, Top = 18 };
        var footer = new Panel { Dock = DockStyle.Top, Height = openRuntime.Height + 30, BackColor = Theme.Canvas };
        openRuntime.Click += (_, _) => Process.Start("explorer.exe", Diagnostics.RuntimeDirectory);
        var openLog = new FlatButton("Open diagnostics log") { Left = openRuntime.Width + 14, Top = 18 };
        openLog.Click += (_, _) =>
        {
            Diagnostics.Log("diagnostics log opened from settings");
            Process.Start(new ProcessStartInfo(Diagnostics.LogPath) { UseShellExecute = true });
        };
        footer.Controls.Add(openRuntime);
        footer.Controls.Add(openLog);

        pane.Controls.Add(footer);
        pane.Controls.Add(group);
        pane.Controls.Add(title);
        return pane;
    }

    /// <summary>
    /// Confirms, downloads with progress, and reverts the picker on failure so
    /// the selection never claims a model that is not actually on disk.
    /// </summary>
    private async Task DownloadModel(WhisperModel model, DarkSelect picker, int previousIndex)
    {
        var confirm = MessageBox.Show(this,
            $"{model.Name} is not installed yet." + Environment.NewLine + Environment.NewLine +
            $"Download it now? This is about {ModelInstaller.DescribeSize(model.FileName)} " +
            "and only needs to happen once.",
            "Download model", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (confirm != DialogResult.OK)
        {
            picker.SelectedIndex = previousIndex;
            return;
        }

        picker.Enabled = false;
        using var progressWindow = new DownloadDialog(model.Name);
        progressWindow.Show(this);

        var result = await ModelInstaller.Download(
            model.FileName,
            new Progress<double>(progressWindow.SetProgress),
            progressWindow.Cancellation);

        progressWindow.Close();
        picker.Enabled = true;

        if (result.Success)
        {
            settings.Model = model.FileName;
            picker.Items[picker.SelectedIndex] = model.Name;
            Commit();
        }
        else
        {
            picker.SelectedIndex = previousIndex;
            MessageBox.Show(this, result.Message, "Download model",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Commit()
    {
        settings.Save();
        settingsChanged();
        foreach (var (_, sectionPane) in sections)
            sectionPane.Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DictationHistory.Changed -= OnHistoryChanged;

        base.Dispose(disposing);
    }
}
