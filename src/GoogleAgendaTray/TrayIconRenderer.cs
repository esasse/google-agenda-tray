using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Win32;

namespace AgendaTray;

internal enum TrayGlyph
{
    /// <summary>Calendar page with how many events are left today: the quiet, default state.</summary>
    Day,
    /// <summary>Amber disc with the minutes left until the next meeting starts.</summary>
    Soon,
    /// <summary>Red disc with a camera: a meeting is going on right now.</summary>
    Live,
    /// <summary>Calendar page with "!": setup pending, or nothing to show and an error.</summary>
    Attention,
}

/// <summary>
/// Draws the tray icon at runtime. The original plugin had a text label in the bar
/// ("14:30 Standup · 8 min"); a tray icon cannot hold that, so the state is carried
/// by the shape and colour, the countdown by the number, and the words by the tooltip.
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly Color Amber = Color.FromArgb(255, 0xF2, 0xB2, 0x33);
    private static readonly Color Red = Color.FromArgb(255, 0xE5, 0x53, 0x4B);

    public static int IconSize()
    {
        var dpi = Native.GetDpiForSystem();
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi);
        return size < 16 ? 16 : size;
    }

    /// <summary>The taskbar has its own theme, independent of the apps' one.</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns an HICON the caller owns (DestroyIcon).</summary>
    public static IntPtr Render(TrayGlyph glyph, string text, int size, bool lightTaskbar)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var fg = lightTaskbar ? Color.FromArgb(255, 0x1C, 0x1C, 0x1C) : Color.White;
            switch (glyph)
            {
                case TrayGlyph.Soon:
                    DrawDisc(g, size, Amber);
                    DrawText(g, text, Color.FromArgb(255, 0x1B, 0x1B, 0x1B),
                        new RectangleF(size * 0.12f, size * 0.17f, size * 0.76f, size * 0.66f));
                    break;
                case TrayGlyph.Live:
                    DrawDisc(g, size, Red);
                    DrawCamera(g, size, Color.White);
                    break;
                case TrayGlyph.Attention:
                    DrawCalendar(g, size, fg, "!", Amber);
                    break;
                default:
                    DrawCalendar(g, size, fg, text, fg);
                    break;
            }
        }
        return bmp.GetHicon();
    }

    private static void DrawDisc(Graphics g, int size, Color fill)
    {
        using var brush = new SolidBrush(fill);
        g.FillEllipse(brush, 0.5f, 0.5f, size - 1f, size - 1f);
    }

    private static void DrawCalendar(Graphics g, int size, Color fg, string text, Color textColor)
    {
        // The shell gives a tray icon 16 px at 100% (20 at 125%) and shrinks anything
        // larger, so the only way to a bigger number is to waste less of the square:
        // the page runs almost to the edges and the binding strip is thin.
        float s = size;
        float stroke = Math.Max(1.2f, s / 14f);
        var body = new RectangleF(s * 0.02f + stroke / 2, s * 0.04f + stroke / 2,
            s * 0.96f - stroke, s * 0.94f - stroke);
        using var path = RoundRect(body, s * 0.16f);
        using (var pen = new Pen(fg, stroke))
            g.DrawPath(pen, path);

        // the binding strip across the top
        var band = new RectangleF(body.X, body.Y, body.Width, body.Height * 0.18f);
        g.SetClip(path);
        using (var brush = new SolidBrush(fg))
            g.FillRectangle(brush, band);
        g.ResetClip();

        var room = new RectangleF(body.X + stroke + s * 0.04f, band.Bottom + s * 0.06f,
            body.Width - 2 * stroke - s * 0.08f, body.Bottom - band.Bottom - stroke - s * 0.12f);
        DrawText(g, text, textColor, room);
    }

    private static void DrawCamera(Graphics g, int size, Color fg)
    {
        float s = size;
        using var brush = new SolidBrush(fg);
        var body = new RectangleF(s * 0.20f, s * 0.33f, s * 0.42f, s * 0.34f);
        using var path = RoundRect(body, s * 0.07f);
        g.FillPath(brush, path);
        g.FillPolygon(brush, new[]
        {
            new PointF(s * 0.60f, s * 0.47f),
            new PointF(s * 0.80f, s * 0.35f),
            new PointF(s * 0.80f, s * 0.65f),
            new PointF(s * 0.60f, s * 0.53f),
        });
    }

    /// <summary>
    /// Text as a filled path, scaled to the box and centred on its ink, not on the
    /// font's line box: at 20 px a line box puts a "12" visibly low.
    /// </summary>
    private static void DrawText(Graphics g, string text, Color color, RectangleF box)
    {
        if (string.IsNullOrEmpty(text) || box.Width <= 0 || box.Height <= 0) return;
        using var family = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var ink = path.GetBounds();
        if (ink.Width <= 0 || ink.Height <= 0) return;

        var scale = Math.Min(box.Width / ink.Width, box.Height / ink.Height);
        using var m = new Matrix();
        m.Translate(box.X + (box.Width - ink.Width * scale) / 2, box.Y + (box.Height - ink.Height * scale) / 2);
        m.Scale(scale, scale);
        m.Translate(-ink.X, -ink.Y);
        path.Transform(m);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
