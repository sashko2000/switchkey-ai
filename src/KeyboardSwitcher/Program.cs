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

        // No system error boxes ("Bad Image", missing DLL) - also for child processes, which inherit this mode;
        // such errors are reported in the app's status and log instead
        Native.SetErrorMode(0x0001 /* SEM_FAILCRITICALERRORS */ | 0x0002 /* SEM_NOGPFAULTERRORBOX */ | 0x8000 /* SEM_NOOPENFILEERRORBOX */);

        try
        {
            Log.Write("Starting " + typeof(Program).Assembly.GetName().Version);
            if (Hardware.IsAmdCpu) Log.Write("AMD processor; Smart App Control: " + Hardware.SmartAppControlState);
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
