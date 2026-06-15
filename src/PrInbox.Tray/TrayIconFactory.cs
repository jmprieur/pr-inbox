using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PrInbox.Tray;

/// <summary>
/// Builds the notification-area icon at runtime so the project ships no
/// binary .ico asset. A rounded accent square with white "PR" lettering,
/// rendered at 32x32 (Windows down-samples for the 16px tray slot).
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(ColorTranslator.FromHtml("#5DA4FF"));
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 7);
            g.FillPath(bg, path);

            using var fg = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("PR", font, fg, new RectangleF(0, 0, 32, 32), sf);
        }

        var hicon = bmp.GetHicon();
        try
        {
            // Clone() deep-copies into a self-contained managed Icon, so the
            // native HICON can be destroyed immediately (no leak, no lifetime
            // coupling to the NotifyIcon).
            return (Icon)Icon.FromHandle(hicon).Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
