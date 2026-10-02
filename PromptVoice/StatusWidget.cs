using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PromptVoice;

public enum VoiceStatus { Idle, Listening, Processing, Warning }

/// <summary>
/// The always-visible recording pill: click-through, always on top, drawn with
/// per-pixel alpha so the corners and shadow are genuinely soft.
///
/// It collapses to a small microphone badge at rest and widens into a live
/// waveform while recording. Enter motion is generous, exit is shorter and
/// quieter, and the timer stops once the pill has settled at rest so an idle
/// app costs no CPU.
/// </summary>
public sealed class StatusWidget : Form
{
    private const int CollapsedWidth = 56;
    private const int ExpandedWidth = 232;
    private const int PillHeight = 44;
    private const int ShadowPadding = 14;
    private const int BarCount = 13;

    private readonly System.Windows.Forms.Timer animation;
    private readonly float[] bars = new float[BarCount];
    private readonly float[] barTargets = new float[BarCount];
    private readonly Random jitter = new();

    private VoiceStatus status = VoiceStatus.Idle;
    private float expansion;      // 0 = collapsed badge, 1 = full pill
    private float phase;
    private float audioLevel;
    private float displayLevel;
    private Color currentColor = Theme.Surface;
    private DateTime warningUntil = DateTime.MinValue;
    private int settledFrames;

    public StatusWidget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(ExpandedWidth + ShadowPadding * 2, PillHeight + ShadowPadding * 2);
        Reposition();

        animation = new System.Windows.Forms.Timer { Interval = 16 };
        animation.Tick += (_, _) => Step();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED |
                          NativeMethods.WS_EX_NOACTIVATE |
                          NativeMethods.WS_EX_TOOLWINDOW |
                          NativeMethods.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        animation.Start();
    }

    /// <summary>Bottom centre, clear of most application toolbars.</summary>
    private void Reposition()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            area.Bottom - Height - 24);
    }

    public void SetStatus(VoiceStatus value)
    {
        if (status == value)
            return;

        status = value;
        if (value != VoiceStatus.Warning)
            warningUntil = DateTime.MinValue;

        Wake();
    }

    /// <summary>Quiet, visible failure: no balloon, just an amber pill that fades back.</summary>
    public void FlashWarning(TimeSpan duration)
    {
        status = VoiceStatus.Warning;
        warningUntil = DateTime.UtcNow + duration;
        Wake();
    }

    public void SetAudioLevel(float value) => audioLevel = Math.Clamp(value, 0f, 1f);

    private void Wake()
    {
        settledFrames = 0;
        if (IsHandleCreated && !animation.Enabled)
            animation.Start();
    }

    private void Step()
    {
        if (status == VoiceStatus.Warning && warningUntil != DateTime.MinValue && DateTime.UtcNow > warningUntil)
        {
            status = VoiceStatus.Idle;
            warningUntil = DateTime.MinValue;
        }

        bool active = status != VoiceStatus.Idle;

        // Enter is eased in over ~12 frames; exit is faster and quieter.
        expansion = Theme.Approach(expansion, active ? 1f : 0f, active ? 0.16f : 0.22f);

        phase += status == VoiceStatus.Processing ? 0.16f : 0.1f;
        displayLevel = Theme.Approach(displayLevel, audioLevel, 0.35f);

        var target = status switch
        {
            VoiceStatus.Listening => Theme.Recording,
            VoiceStatus.Processing => Theme.Processing,
            VoiceStatus.Warning => Theme.Warning,
            _ => Theme.Surface
        };
        currentColor = Theme.Mix(currentColor, target, 0.18f);

        UpdateBars();
        Render();

        // Once at rest and fully collapsed, stop repainting entirely.
        bool settled = !active &&
                       expansion < 0.004f &&
                       displayLevel < 0.01f &&
                       Math.Abs(currentColor.R - Theme.Surface.R) < 2 &&
                       Math.Abs(currentColor.G - Theme.Surface.G) < 2 &&
                       Math.Abs(currentColor.B - Theme.Surface.B) < 2;

        if (settled && ++settledFrames > 4)
        {
            expansion = 0f;
            currentColor = Theme.Surface;
            Render();
            animation.Stop();
        }
        else if (!settled)
        {
            settledFrames = 0;
        }
    }

    private void UpdateBars()
    {
        for (var i = 0; i < BarCount; i++)
        {
            if (status == VoiceStatus.Listening)
            {
                // Centre bars react hardest, so the shape reads as a voice and
                // a silent microphone stays visibly flat.
                float centreWeight = 1f - Math.Abs(i - (BarCount - 1) / 2f) / ((BarCount - 1) / 2f);
                float shaped = displayLevel * (0.45f + 0.55f * centreWeight);
                barTargets[i] = shaped * (0.75f + (float)jitter.NextDouble() * 0.5f);
            }
            else
            {
                barTargets[i] = 0f;
            }

            bars[i] = Theme.Approach(bars[i], barTargets[i], 0.4f);
        }
    }

    // --- compositing ---------------------------------------------------------

    private void Render()
    {
        if (!IsHandleCreated)
            return;

        using var bitmap = Compose();
        Push(bitmap);
    }

    private Bitmap Compose()
    {
        float pillWidth = CollapsedWidth + (ExpandedWidth - CollapsedWidth) * Ease(expansion);

        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            Theme.UseQuality(g);
            g.Clear(Color.Transparent);

            var pill = new RectangleF(
                (Width - pillWidth) / 2f,
                ShadowPadding,
                pillWidth,
                PillHeight);

            float radius = PillHeight / 2f;

            DrawShadow(g, pill, radius);
            DrawBody(g, pill, radius);
            DrawContent(g, pill);
        }

        return bitmap;
    }

    /// <summary>
    /// Renders one fully-settled state off-screen. Used by --ui-shots to review
    /// the pill without driving the real desktop.
    /// </summary>
    internal Bitmap Preview(VoiceStatus previewStatus, float level)
    {
        status = previewStatus;
        expansion = previewStatus == VoiceStatus.Idle ? 0f : 1f;
        audioLevel = displayLevel = level;
        phase = 1.1f;
        currentColor = previewStatus switch
        {
            VoiceStatus.Listening => Theme.Recording,
            VoiceStatus.Processing => Theme.Processing,
            VoiceStatus.Warning => Theme.Warning,
            _ => Theme.Surface
        };

        for (var i = 0; i < BarCount; i++)
        {
            float centreWeight = 1f - Math.Abs(i - (BarCount - 1) / 2f) / ((BarCount - 1) / 2f);
            bars[i] = previewStatus == VoiceStatus.Listening ? level * (0.45f + 0.55f * centreWeight) : 0f;
        }

        return Compose();
    }

    private static void DrawShadow(Graphics g, RectangleF pill, float radius)
    {
        for (var i = ShadowPadding; i >= 1; i--)
        {
            int alpha = Math.Max(1, 46 / (i + 1));
            using var path = Theme.RoundedRect(RectangleF.Inflate(pill, i, i * 0.75f), radius + i);
            using var pen = new Pen(Color.FromArgb(alpha, 0, 0, 0), 2f);
            g.DrawPath(pen, path);
        }
    }

    private void DrawBody(Graphics g, RectangleF pill, float radius)
    {
        using var path = Theme.RoundedRect(pill, radius);

        // A vertical sheen keeps the surface from looking like flat paint.
        using (var fill = new LinearGradientBrush(
                   new RectangleF(pill.X, pill.Y - 1, pill.Width, pill.Height + 2),
                   Theme.Mix(currentColor, Color.White, 0.10f),
                   Theme.Mix(currentColor, Color.Black, 0.14f),
                   LinearGradientMode.Vertical))
        {
            g.FillPath(fill, path);
        }

        using var hairline = new Pen(Color.FromArgb(38, 255, 255, 255), 1f);
        g.DrawPath(hairline, path);
    }

    private void DrawContent(Graphics g, RectangleF pill)
    {
        var centre = new PointF(pill.X + pill.Width / 2f, pill.Y + pill.Height / 2f);

        // Cross-fade the badge glyph out as the pill widens, rather than
        // snapping between two different contents.
        float badge = 1f - Math.Min(1f, Ease(expansion) * 1.6f);
        if (badge > 0.01f)
            DrawMicrophone(g, centre, badge);

        float wide = Math.Max(0f, (Ease(expansion) - 0.35f) / 0.65f);
        if (wide <= 0.01f)
            return;

        switch (status)
        {
            case VoiceStatus.Listening:
                DrawWave(g, pill, wide);
                break;
            case VoiceStatus.Processing:
                DrawProcessing(g, pill, wide);
                break;
            case VoiceStatus.Warning:
                DrawWarning(g, pill, wide);
                break;
        }
    }

    private static void DrawMicrophone(Graphics g, PointF centre, float opacity)
    {
        int alpha = (int)(215 * opacity);
        using var pen = new Pen(Color.FromArgb(alpha, Theme.TextSecondary), 2.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        // The capsule sits a pixel above centre so the glyph plus its stand
        // reads as optically centred.
        float cx = centre.X;
        float cy = centre.Y - 1.5f;
        g.DrawArc(pen, cx - 4.5f, cy - 8f, 9f, 13f, 0, 360);
        g.DrawArc(pen, cx - 7.5f, cy - 3f, 15f, 13f, 0, 180);
        g.DrawLine(pen, cx, cy + 7f, cx, cy + 10f);
    }

    private void DrawWave(Graphics g, RectangleF pill, float opacity)
    {
        float usable = pill.Width - 74;
        float step = usable / (BarCount - 1);
        float left = pill.X + 37;
        float mid = pill.Y + pill.Height / 2f;
        int alpha = (int)(240 * opacity);

        using var pen = new Pen(Color.FromArgb(alpha, Color.White), 2.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        for (var i = 0; i < BarCount; i++)
        {
            float x = left + i * step;
            float height = 2f + bars[i] * 13f;
            g.DrawLine(pen, x, mid - height, x, mid + height);
        }

        // A steady dot on the left reads as "armed" even in total silence.
        using var dot = new SolidBrush(Color.FromArgb(alpha, Color.White));
        g.FillEllipse(dot, pill.X + 17f, mid - 3.5f, 7f, 7f);
    }

    private void DrawProcessing(Graphics g, RectangleF pill, float opacity)
    {
        float mid = pill.Y + pill.Height / 2f;
        float centre = pill.X + pill.Width / 2f;

        for (var i = 0; i < 3; i++)
        {
            // Staggered phase so the three dots breathe in sequence.
            float wave = (MathF.Sin(phase - i * 0.7f) + 1f) / 2f;
            int alpha = (int)((110 + 145 * wave) * opacity);
            float size = 6f + wave * 2.5f;
            using var brush = new SolidBrush(Color.FromArgb(alpha, Color.White));
            g.FillEllipse(brush, centre - 18f + i * 16f - size / 2f, mid - size / 2f, size, size);
        }
    }

    private void DrawWarning(Graphics g, RectangleF pill, float opacity)
    {
        int alpha = (int)(245 * opacity);
        var rect = new Rectangle(
            (int)pill.X + 16,
            (int)pill.Y,
            (int)pill.Width - 32,
            (int)pill.Height);

        TextRenderer.DrawText(g, "No text inserted", Theme.SmallStrong, rect,
            Color.FromArgb(alpha, Color.White),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    /// <summary>Ease-out cubic: fast to leave, gentle to arrive.</summary>
    private static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    private void Push(Bitmap bitmap)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = NativeMethods.SelectObject(memoryDc, hBitmap);

            var source = new NativeMethods.POINT(0, 0);
            var destination = new NativeMethods.POINT(Left, Top);
            var size = new NativeMethods.SIZE(bitmap.Width, bitmap.Height);
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size,
                memoryDc, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);

            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
                NativeMethods.DeleteObject(hBitmap);
            }

            NativeMethods.DeleteDC(memoryDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            animation.Dispose();

        base.Dispose(disposing);
    }
}
