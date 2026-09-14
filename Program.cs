using AIUsage.UI;

namespace AIUsage;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Headless test mode: fetch everything once and render the UI to PNG files.
        if (args.Length >= 2 && args[0] == "--snapshot")
            return Snapshot.Run(args[1]);
        if (args.Length >= 2 && args[0] == "--selftest")
            return SelfTest.Run(args[1]);

        using var mutex = new Mutex(initiallyOwned: true, @"Local\AIUsage.Tray", out bool isFirstInstance);
        if (!isFirstInstance)
            return 0;

        Application.Run(new TrayApp());
        return 0;
    }
}
