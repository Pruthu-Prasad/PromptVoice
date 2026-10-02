using System.Drawing.Drawing2D;

namespace PromptVoice;

/// <summary>Shared base: double-buffered custom drawing, never focus-stealing.</summary>
internal abstract class PaintedControl : Control
{
    protected PaintedControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }
}

/// <summary>
/// A rounded surface that hosts other controls. Child padding plus the child
/// radius equals this card's radius, keeping nested corners optically coherent.
/// </summary>
internal sealed class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Canvas;
        Padding = new Padding(18);
    }

    public Color Fill { get; set; } = Theme.Surface;
    public Color Stroke { get; set; } = Theme.BorderSubtle;
    public int Radius { get; set; } = Theme.RadiusCard;

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.UseQuality(e.Graphics);
        var bounds = new RectangleF(0, 0, Width, Height);
        Theme.FillRounded(e.Graphics, bounds, Radius, Fill);
        Theme.DrawRounded(e.Graphics, bounds, Radius, Stroke);
    }
}

internal enum ButtonKind { Primary, Secondary, Ghost, Danger }

/// <summary>
/// Flat button with an interruptible hover fade and a restrained press state.
/// The 40px height keeps the hit area comfortable even for short labels.
/// </summary>
internal sealed class FlatButton : PaintedControl
{
    private readonly System.Windows.Forms.Timer motion;
    private float hover;
    private bool hovered;
    private bool pressed;

    public FlatButton(string text, ButtonKind kind = ButtonKind.Secondary)
    {
        Text = text;
        Kind = kind;
        Font = Theme.BodyStrong;
        Height = 40;
        Width = 120;
        Cursor = Cursors.Hand;

        motion = new System.Windows.Forms.Timer { Interval = 16 };
        motion.Tick += (_, _) =>
        {
            float target = hovered ? 1f : 0f;
            hover = Theme.Approach(hover, target, 0.28f);
            if (Math.Abs(hover - target) < 0.01f)
            {
                hover = target;
                motion.Stop();
            }

            Invalidate();
        };

        FitToText();
    }

    public ButtonKind Kind { get; set; }

    /// <summary>
    /// Sizes to the measured label so nothing is cut at higher display scaling,
    /// while keeping at least a 40px-tall hit area.
    /// </summary>
    public void FitToText()
    {
        var size = TextRenderer.MeasureText(Text, Font);
        Width = size.Width + 34;
        Height = Math.Max(40, Theme.LineHeight(Font) + 18);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; motion.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; motion.Start(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.UseQuality(e.Graphics);

        // Press sinks the face by one pixel rather than scaling the control,
        // which would look unstable at this size.
        var bounds = new RectangleF(0, pressed ? 1 : 0, Width, Height - 1);

        (Color fill, Color stroke, Color text) = Kind switch
        {
            ButtonKind.Primary => (
                Theme.Mix(Theme.Accent, Theme.AccentHover, hover),
                Color.Transparent,
                Color.White),
            ButtonKind.Danger => (
                Theme.Mix(Color.FromArgb(0x2A, 0x1A, 0x1E), Color.FromArgb(0x3A, 0x20, 0x25), hover),
                Theme.Recording,
                Theme.Recording),
            ButtonKind.Ghost => (
                Theme.Mix(Theme.Surface, Theme.SurfaceRaised, hover),
                Color.Transparent,
                Theme.TextSecondary),
            _ => (
                Theme.Mix(Theme.SurfaceRaised, Theme.Mix(Theme.SurfaceRaised, Theme.Border, 0.7f), hover),
                Theme.Border,
                Theme.TextPrimary)
        };

        if (pressed && Kind == ButtonKind.Primary)
            fill = Theme.AccentPressed;

        if (fill.A > 0)
            Theme.FillRounded(e.Graphics, bounds, Theme.RadiusControl, fill);

        if (stroke.A > 0)
            Theme.DrawRounded(e.Graphics, bounds, Theme.RadiusControl, stroke);

        TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Round(bounds), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            motion.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>Left-hand navigation entry with a selected rail and hover fade.</summary>
internal sealed class NavItem : PaintedControl
{
    private readonly System.Windows.Forms.Timer motion;
    private float hover;
    private bool hovered;
    private bool selected;

    public NavItem(string text, string glyph)
    {
        Text = text;
        Glyph = glyph;
        Font = Theme.BodyStrong;
        Height = Math.Max(42, Theme.LineHeight(Font) + 20);
        Cursor = Cursors.Hand;

        motion = new System.Windows.Forms.Timer { Interval = 16 };
        motion.Tick += (_, _) =>
        {
            float target = hovered ? 1f : 0f;
            hover = Theme.Approach(hover, target, 0.3f);
            if (Math.Abs(hover - target) < 0.01f) { hover = target; motion.Stop(); }
            Invalidate();
        };
    }

    public string Glyph { get; }

    public bool Selected
    {
        get => selected;
        set { selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; motion.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; motion.Start(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.UseQuality(e.Graphics);
        var bounds = new RectangleF(0, 1, Width, Height - 2);

        if (Selected)
            Theme.FillRounded(e.Graphics, bounds, Theme.RadiusControl, Theme.SurfaceRaised);
        else if (hover > 0.01f)
            Theme.FillRounded(e.Graphics, bounds, Theme.RadiusControl, Color.FromArgb((int)(26 * hover), 0xFF, 0xFF, 0xFF));

        if (Selected)
        {
            // Accent rail, vertically inset so it reads as a marker, not a border.
            using var rail = new SolidBrush(Theme.Accent);
            using var path = Theme.RoundedRect(new RectangleF(0, Height / 2f - 9, 3, 18), 1.5f);
            e.Graphics.FillPath(rail, path);
        }

        var textColor = Selected ? Theme.TextPrimary : Theme.Mix(Theme.TextSecondary, Theme.TextPrimary, hover);

        // Fixed-width glyph box so every label starts on the same x.
        TextRenderer.DrawText(e.Graphics, Glyph, Theme.Body, new Rectangle(14, 0, 22, Height), textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(42, 0, Width - 48, Height), textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            motion.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>A switch, because a dark-theme checkbox never looks deliberate.</summary>
internal sealed class ToggleSwitch : PaintedControl
{
    private readonly System.Windows.Forms.Timer motion;
    private float position;
    private bool on;

    public ToggleSwitch()
    {
        // 40px tall control around a 22px track keeps the hit area usable.
        Size = new Size(44, 40);
        Cursor = Cursors.Hand;

        motion = new System.Windows.Forms.Timer { Interval = 16 };
        motion.Tick += (_, _) =>
        {
            float target = on ? 1f : 0f;
            position = Theme.Approach(position, target, 0.3f);
            if (Math.Abs(position - target) < 0.005f) { position = target; motion.Stop(); }
            Invalidate();
        };
    }

    public event EventHandler? Toggled;

    public bool Checked
    {
        get => on;
        set
        {
            if (on == value)
                return;

            on = value;
            motion.Start();
        }
    }

    /// <summary>Sets the state without animating, for initial load.</summary>
    public void SetInitial(bool value)
    {
        on = value;
        position = value ? 1f : 0f;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        Checked = !on;
        Toggled?.Invoke(this, EventArgs.Empty);
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.UseQuality(e.Graphics);

        var track = new RectangleF(2, Height / 2f - 11, 40, 22);
        Theme.FillRounded(e.Graphics, track, 11, Theme.Mix(Theme.SurfaceRaised, Theme.Accent, position));
        if (position < 0.5f)
            Theme.DrawRounded(e.Graphics, track, 11, Theme.Border);

        float knobX = track.Left + 3 + position * (track.Width - 22);
        using var knob = new SolidBrush(Theme.Mix(Theme.TextSecondary, Color.White, position));
        e.Graphics.FillEllipse(knob, knobX, track.Top + 3, 16, 16);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            motion.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>A settings row: title, explanation, and an editor on the right.</summary>
internal sealed class SettingRow : Panel
{
    private readonly string title;
    private readonly string description;

    public SettingRow(string title, string description, Control editor, bool showDivider = true)
    {
        this.title = title;
        this.description = description;
        ShowDivider = showDivider;

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);

        BackColor = Theme.Surface;
        Dock = DockStyle.Top;

        Editor = editor;
        Controls.Add(editor);
        Resize += (_, _) => Remeasure();
        Remeasure();
    }

    public Control Editor { get; }

    public bool ShowDivider { get; }

    private int TextWidth => Math.Max(140, Width - Editor.Width - 54);

    /// <summary>
    /// Grows to fit however many lines the description actually wraps to at the
    /// current display scaling, instead of assuming two short lines.
    /// </summary>
    private void Remeasure()
    {
        int needed = 14 + Theme.LineHeight(Theme.BodyStrong) + 4 +
                     Theme.WrappedHeight(description, Theme.Small, TextWidth) + 16;

        int target = Math.Max(Editor.Height + 20, needed);
        if (Height != target)
            Height = target;

        Editor.Left = Width - Editor.Width - 18;
        Editor.Top = (Height - Editor.Height) / 2;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.UseQuality(e.Graphics);

        int titleHeight = Theme.LineHeight(Theme.BodyStrong);
        Theme.Label(e.Graphics, title, Theme.BodyStrong, new Point(18, 14), TextWidth, Theme.TextPrimary);

        TextRenderer.DrawText(e.Graphics, description, Theme.Small,
            new Rectangle(18, 14 + titleHeight + 4, TextWidth, Height - titleHeight - 30), Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoClipping);

        if (ShowDivider)
        {
            using var pen = new Pen(Theme.BorderSubtle);
            e.Graphics.DrawLine(pen, 18, Height - 1, Width - 18, Height - 1);
        }
    }
}

/// <summary>Dark drop-down; the stock ComboBox cannot be themed.</summary>
internal sealed class DarkSelect : ComboBox
{
    public DarkSelect()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.SurfaceRaised;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Body;
        ItemHeight = Theme.LineHeight(Theme.Body) + 8;
        Height = Theme.LineHeight(Theme.Body) + 12;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;

        Theme.UseQuality(e.Graphics);
        bool active = (e.State & DrawItemState.Selected) != 0;

        using var background = new SolidBrush(active ? Theme.Accent : Theme.SurfaceRaised);
        e.Graphics.FillRectangle(background, e.Bounds);

        TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString() ?? string.Empty, Theme.Body,
            new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
            active ? Color.White : Theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.UseQuality(e.Graphics);
        var bounds = new RectangleF(0, 0, Width, Height);
        Theme.FillRounded(e.Graphics, bounds, Theme.RadiusPill, Theme.SurfaceRaised);
        Theme.DrawRounded(e.Graphics, bounds, Theme.RadiusPill, Theme.Border);

        TextRenderer.DrawText(e.Graphics, Text, Font,
            new Rectangle(8, 0, Width - 28, Height), Theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Chevron, nudged up one pixel so it reads as optically centred.
        using var pen = new Pen(Theme.TextSecondary, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width - 14, cy = Height / 2f - 1;
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(cx - 4, cy - 1.5f),
            new PointF(cx, cy + 2.5f),
            new PointF(cx + 4, cy - 1.5f)
        });
    }
}
