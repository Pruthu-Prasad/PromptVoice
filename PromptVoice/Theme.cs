using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PromptVoice;

/// <summary>
/// One source of truth for colour, type, radius, and the drawing primitives the
/// custom controls share. Radii follow the concentric rule: an inner surface's
/// radius plus its padding equals the radius of the surface containing it.
/// </summary>
internal static class Theme
{
    // Surfaces, darkest to lightest.
    public static readonly Color Canvas = Color.FromArgb(0x0F, 0x11, 0x16);
    public static readonly Color Sidebar = Color.FromArgb(0x13, 0x16, 0x1C);
    public static readonly Color Surface = Color.FromArgb(0x1A, 0x1E, 0x26);
    public static readonly Color SurfaceRaised = Color.FromArgb(0x22, 0x27, 0x31);
    public static readonly Color Border = Color.FromArgb(0x2C, 0x32, 0x3E);
    public static readonly Color BorderSubtle = Color.FromArgb(0x23, 0x28, 0x32);

    // Text.
    public static readonly Color TextPrimary = Color.FromArgb(0xEC, 0xEF, 0xF4);
    public static readonly Color TextSecondary = Color.FromArgb(0x9A, 0xA3, 0xB4);
    public static readonly Color TextMuted = Color.FromArgb(0x6A, 0x73, 0x85);

    // Accent and status.
    public static readonly Color Accent = Color.FromArgb(0x6D, 0x7A, 0xFF);
    public static readonly Color AccentHover = Color.FromArgb(0x82, 0x8D, 0xFF);
    public static readonly Color AccentPressed = Color.FromArgb(0x5B, 0x68, 0xE8);
    public static readonly Color Recording = Color.FromArgb(0xFF, 0x54, 0x56);
    public static readonly Color Processing = Color.FromArgb(0x4D, 0x9B, 0xFF);
    public static readonly Color Warning = Color.FromArgb(0xE5, 0x9A, 0x24);
    public static readonly Color Success = Color.FromArgb(0x3F, 0xC1, 0x73);

    public const int RadiusCard = 12;
    public const int RadiusControl = 8;
    public const int RadiusPill = 6;

    private const string PreferredFamily = "Segoe UI Variable Text";
    private const string FallbackFamily = "Segoe UI";

    private static readonly string Family = ResolveFamily();

    public static readonly Font Display = new(Family, 17f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Title = new(Family, 11.5f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Body = new(Family, 9.75f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font BodyStrong = new(Family, 9.75f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Small = new(Family, 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font SmallStrong = new(Family, 8.5f, FontStyle.Bold, GraphicsUnit.Point);

    /// <summary>Monospaced so changing counters and timings do not shift sideways.</summary>
    public static readonly Font Mono = ResolveMono();

    private static string ResolveFamily()
    {
        using var installed = new InstalledFontCollection();
        foreach (var family in installed.Families)
        {
            if (family.Name == PreferredFamily)
                return PreferredFamily;
        }

        return FallbackFamily;
    }

    private static Font ResolveMono()
    {
        using var installed = new InstalledFontCollection();
        foreach (var name in new[] { "Cascadia Mono", "Consolas" })
        {
            foreach (var family in installed.Families)
            {
                if (family.Name == name)
                    return new Font(name, 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            }
        }

        return new Font(FontFamily.GenericMonospace, 8.5f);
    }

    /// <summary>
    /// Height of one line for this font at the current DPI. Layout must be
    /// derived from this, never from hardcoded pixels: fonts scale with the
    /// display and fixed rectangles clip their own text at 125% and above.
    /// </summary>
    public static int LineHeight(Font font) => TextRenderer.MeasureText("Ag", font).Height;

    /// <summary>Height needed to draw <paramref name="text"/> wrapped to a width.</summary>
    public static int WrappedHeight(string text, Font font, int width) =>
        TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;

    /// <summary>Single-line label. NoClipping keeps descenders intact.</summary>
    public static void Label(Graphics g, string text, Font font, Point at, int width, Color color)
    {
        TextRenderer.DrawText(g, text, font, new Rectangle(at.X, at.Y, width, LineHeight(font) + 2), color,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoClipping |
            TextFormatFlags.EndEllipsis);
    }

    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();

        if (radius <= 0.5f || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        float d = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, RectangleF bounds, float radius, Color fill)
    {
        using var path = RoundedRect(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
    }

    /// <summary>A hairline border reads as separation; depth is left to shadows.</summary>
    public static void DrawRounded(Graphics g, RectangleF bounds, float radius, Color stroke, float width = 1f)
    {
        using var path = RoundedRect(RectangleF.Inflate(bounds, -width / 2f, -width / 2f), radius);
        using var pen = new Pen(stroke, width);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// Layered translucent passes rather than one hard drop shadow, so the
    /// result sits correctly on any background.
    /// </summary>
    public static void DrawSoftShadow(Graphics g, RectangleF bounds, float radius, int spread, int alpha)
    {
        for (var i = spread; i >= 1; i--)
        {
            int a = Math.Max(1, alpha / (i + 1));
            using var path = RoundedRect(RectangleF.Inflate(bounds, i, i), radius + i);
            using var pen = new Pen(Color.FromArgb(a, 0, 0, 0), 1.6f);
            g.DrawPath(pen, path);
        }
    }

    public static void UseQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Eases a value toward a target; used for interruptible state motion.</summary>
    public static float Approach(float current, float target, float rate) =>
        current + (target - current) * Math.Clamp(rate, 0f, 1f);
}
