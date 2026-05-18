using System.Runtime.InteropServices;
using System.Text;

namespace PrInbox.Web.Services;

/// <summary>
/// Thin User32 P/Invoke wrappers used by <see cref="ConsoleWindowRegistry"/>
/// to find, validate and toggle visibility of console windows spawned by
/// <see cref="ReviewLauncher"/>.
/// </summary>
internal static class WindowInterop
{
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    /// <summary>
    /// Returns the process id that owns <paramref name="hwnd"/>, or 0 if
    /// the window is gone / the call failed. Used to anchor liveness to a
    /// stable identity — the running process inside the window can rewrite
    /// its own title, but it cannot change its PID.
    /// </summary>
    public static uint GetOwningProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        return GetWindowThreadProcessId(hwnd, out var pid) == 0 ? 0 : pid;
    }

    /// <summary>
    /// Read a window's title via <c>GetWindowText</c>. Returns empty string
    /// when the window is gone or the title is empty.
    /// </summary>
    public static string GetTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        var got = GetWindowText(hwnd, sb, sb.Capacity);
        return got <= 0 ? string.Empty : sb.ToString();
    }

    /// <summary>
    /// Walks every top-level window and returns the first one whose title
    /// contains <paramref name="token"/>. Returns <see cref="IntPtr.Zero"/>
    /// if none match (the window may not exist yet, or has been closed).
    /// </summary>
    public static IntPtr FindByTitleToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var title = GetTitle(hwnd);
            if (!string.IsNullOrEmpty(title) && title.Contains(token, StringComparison.Ordinal))
            {
                found = hwnd;
                return false; // stop enumeration
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
