namespace PromptVoice;

/// <summary>
/// Progress for a model download. Cancellable, because a 465 MB fetch on a slow
/// connection is something a user must be able to back out of.
/// </summary>
internal sealed class DownloadDialog : Form
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly string modelName;
    private double progress;

    public DownloadDialog(string modelName)
    {
        this.modelName = modelName;

        Text = "Downloading " + modelName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ClientSize = new Size(420, 150);
        BackColor = Theme.Surface;
        DoubleBuffered = true;
        Icon = AppIcon.Get();

        var cancel = new FlatButton("Cancel", ButtonKind.Secondary) { AccessibleName = "Cancel download" };
        cancel.Click += (_, _) =>
        {
            cancel.Enabled = false;
            cancellation.Cancel();
        };
        cancel.Location = new Point(ClientSize.Width - cancel.Width - 20, ClientSize.Height - cancel.Height - 18);
        Controls.Add(cancel);
    }

    public CancellationToken Cancellation => cancellation.Token;

    public void SetProgress(double value)
    {
        progress = Math.Clamp(value, 0, 1);

        if (!IsHandleCreated || IsDisposed)
            return;

        try
        {
            BeginInvoke(Invalidate);
        }
        catch (ObjectDisposedException)
        {
            // Closed while the last progress callback was in flight.
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.UseQuality(e.Graphics);
        e.Graphics.Clear(Theme.Surface);

        Theme.Label(e.Graphics, $"Downloading the {modelName} model", Theme.BodyStrong,
            new Point(20, 22), ClientSize.Width - 40, Theme.TextPrimary);
        Theme.Label(e.Graphics, "This happens once. PromptVoice works offline afterwards.",
            Theme.Small, new Point(20, 22 + Theme.LineHeight(Theme.BodyStrong) + 4),
            ClientSize.Width - 40, Theme.TextMuted);

        var track = new RectangleF(20, 82, ClientSize.Width - 40, 8);
        Theme.FillRounded(e.Graphics, track, 4, Theme.SurfaceRaised);

        if (progress > 0)
        {
            var fill = new RectangleF(track.X, track.Y, (float)(track.Width * progress), track.Height);
            Theme.FillRounded(e.Graphics, fill, 4, Theme.Accent);
        }

        // Tabular percentage: the number must not shuffle sideways as it climbs.
        TextRenderer.DrawText(e.Graphics, $"{progress * 100:N0}%", Theme.Mono,
            new Rectangle(20, 98, 80, Theme.LineHeight(Theme.Mono) + 2), Theme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            cancellation.Dispose();

        base.Dispose(disposing);
    }
}
