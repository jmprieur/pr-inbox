using System.Threading;
using System.Windows.Forms;

namespace PrInbox.Tray;

internal static class Program
{
    // Held for the whole process lifetime so a second launch can detect us.
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        _singleInstance = new Mutex(initiallyOwned: true, "PrInbox.Tray.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            MessageBox.Show(
                "PR Inbox is already running.\n\nLook for the PR icon in the notification area — click the ^ arrow near the clock if you don't see it.",
                "PR Inbox",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
        finally
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }
    }
}
