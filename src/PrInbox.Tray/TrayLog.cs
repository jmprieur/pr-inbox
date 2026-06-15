using System.Text;

namespace PrInbox.Tray;

/// <summary>
/// Minimal append-only diagnostic log for the tray host itself (distinct from
/// the web child's captured stdout). Written under
/// %LOCALAPPDATA%\PrInbox\logs\tray-*.log so a launch that fails before the
/// browser ever opens still leaves a breadcrumb the user can inspect via the
/// tray menu.
/// </summary>
internal static class TrayLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path => _path ??= Init();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    Path,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never throw into the launch path.
        }
    }

    private static string Init()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrInbox", "logs");
        Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, $"tray-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }
}
