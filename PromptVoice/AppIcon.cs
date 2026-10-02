using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PromptVoice;

/// <summary>
/// Draws the app mark at runtime. Generating it avoids shipping a binary asset
/// and keeps the tray icon crisp at whatever size Windows asks for.
/// </summary>
internal static class AppIcon
{
    private static Icon? window;
    private static Icon? tray;

    public static Icon Get() => window ??= Build(32);

    /// <summary>A separate small render; a downscaled 32px mark goes muddy in the tray.</summary>
    public static Icon ForTray() => tray ??= Build(16);

    private static Icon Build(int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float s = size / 32f;

            // Rounded accent tile, inset so the corners are not clipped.
            var tile = new RectangleF(1.5f * s, 1.5f * s, 29f * s, 29f * s);
            using (var path = Theme.RoundedRect(tile, 8f * s))
            using (var fill = new LinearGradientBrush(tile, Theme.AccentHover, Theme.AccentPressed, LinearGradientMode.ForwardDiagonal))
            {
                g.FillPath(fill, path);
            }

            // Microphone, nudged up so the capsule plus stand reads as centred.
            using var pen = new Pen(Color.White, Math.Max(1.4f, 2.4f * s))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            float cx = size / 2f;
            g.DrawArc(pen, cx - 4f * s, 8f * s, 8f * s, 12f * s, 0, 360);
            g.DrawArc(pen, cx - 7f * s, 13f * s, 14f * s, 11f * s, 0, 180);
            g.DrawLine(pen, cx, 24f * s, cx, 27f * s);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon outlives the temporary handle we destroy below.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
