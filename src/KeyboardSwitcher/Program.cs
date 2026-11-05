using KeyboardSwitcher;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(true, @"Local\KeyboardSwitcher-7f3c2a", out bool first);
        if (!first)
        {
            L.SetLanguage(Languages.DefaultUi());
            Native.MessageBox(IntPtr.Zero, L.T("msg.already_running"), "KeyboardSwitcher", 0x40);
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write("Fatal error: " + e.ExceptionObject);
            Native.MessageBox(IntPtr.Zero, L.T("msg.fatal", (e.ExceptionObject as Exception)?.Message, Log.FilePath), "KeyboardSwitcher", 0x10);
        };

        try
        {
            Log.Write("Starting " + typeof(Program).Assembly.GetName().Version);
            AiSetup.RegisterNativeResolver();
            var settings = Settings.Load();
            L.SetLanguage(settings.UiLanguage);
            var detector = LayoutDetector.FromEmbeddedResources();
            using var monitor = new KeyboardMonitor(detector);
            monitor.Start();
            using var ai = new AiCorrector();
            using var app = new TrayApp(settings, monitor, ai);
            app.Run();
            Log.Write("Exit");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("Startup error: " + ex);
            Native.MessageBox(IntPtr.Zero, L.T("msg.start_failed", ex.Message), "KeyboardSwitcher", 0x10);
            return 1;
        }
    }
}
