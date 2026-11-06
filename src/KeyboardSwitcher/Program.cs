using KeyboardSwitcher;

internal static class Program
{
    private const string MutexName = @"Local\KeyboardSwitcher-7f3c2a";

    /// <summary>Take the single-instance mutex; wait a little: a restarting copy may still be closing.</summary>
    private static Mutex? TakeMutex()
    {
        var m = new Mutex(false, MutexName);
        try { if (m.WaitOne(TimeSpan.FromSeconds(8))) return m; }
        catch (AbandonedMutexException) { return m; }   // the previous copy crashed - the mutex is ours now
        m.Dispose();
        return null;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))) return Uninstall();

        var mutex = TakeMutex();
        if (mutex == null)
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
            // "Work in programs run as administrator": continue in an elevated copy (through the task - no prompt)
            if (settings.RunElevated && !Elevation.IsElevated)
            {
                mutex.ReleaseMutex();
                if ((Elevation.TaskExists() && Elevation.RunTask()) || Elevation.RestartElevated())
                {
                    Log.Write("Continuing in a copy with administrator rights");
                    mutex.Dispose();
                    return 0;
                }
                // UAC was declined: work without admin rights this time
                mutex.Dispose();
                mutex = TakeMutex();
                if (mutex == null) return 0;
            }
            Log.Write("Administrator rights: " + (Elevation.IsElevated ? "yes" : "no"));
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
        finally
        {
            try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }
    }

    /// <summary>
    /// "KeyboardSwitcher.exe --uninstall": remove what the app registered in Windows - the Task Scheduler task
    /// (Windows may ask for confirmation) and the autostart entry. Downloaded models and settings stay; the message
    /// says where they are.
    /// </summary>
    private static int Uninstall()
    {
        L.SetLanguage(Languages.DefaultUi());
        Log.Write("Uninstall");
        bool task = Elevation.DeleteTask();
        TrayApp.RemoveAutostartEntry();
        Native.MessageBox(IntPtr.Zero, L.T(task ? "msg.uninstalled" : "msg.uninstall_task_left", Elevation.TaskName,
            AiSetup.BaseDir, Path.GetDirectoryName(Settings.FilePath)), "KeyboardSwitcher", task ? 0x40u : 0x30u);
        return task ? 0 : 1;
    }
}
