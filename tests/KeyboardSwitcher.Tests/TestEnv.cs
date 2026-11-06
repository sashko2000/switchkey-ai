using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>Everything the app would write goes to a temporary folder; the interface language is English.</summary>
[TestClass]
public static class TestEnv
{
    public static string Home { get; private set; } = "";

    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        Home = NewHome();
        L.SetLanguage("en");
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", null);
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "ks-tests-" + Environment.ProcessId), true); } catch { }
    }

    /// <summary>A fresh empty app folder (nothing installed) - set as the current one.</summary>
    public static string NewHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ks-tests-" + Environment.ProcessId, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_HOME", dir);
        return dir;
    }
}
