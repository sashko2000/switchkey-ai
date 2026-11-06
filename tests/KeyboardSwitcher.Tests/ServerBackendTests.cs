using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>The local model server backend (llama-server / ryzenai-server) against a fake server.</summary>
[TestClass]
public class ServerBackendTests
{
    [TestMethod]
    public void Chat_Completion_Thinking_Errors()
    {
        using var server = new FakeServer(TimeSpan.FromSeconds(1));   // "loading" for a second first
        using var be = new ServerBackend(server.Url, "KEY", thinkingKwargs: true, TimeSpan.FromSeconds(20));
        Assert.AreEqual("echo:привет|think=False", be.GenerateChat("[{\"role\":\"user\",\"content\":\"привет\"}]", false, 50));
        Assert.AreEqual("echo:x|think=True", be.GenerateChat("[{\"role\":\"user\",\"content\":\"x\"}]", true, 50));
        Assert.AreEqual("txt:abc", be.GenerateText("abc", 10));
        var ex = Assert.ThrowsException<InvalidOperationException>(() => be.GenerateChat("[{\"role\":\"system\",\"content\":\"FAIL\"}]", false, 5));
        StringAssert.Contains(ex.Message, "500");
    }

    [TestMethod]
    public void NeverReady_TimesOut()
    {
        using var server = new FakeServer(TimeSpan.FromHours(1));
        var ex = Assert.ThrowsException<InvalidOperationException>(() => new ServerBackend(server.Url, null, false, TimeSpan.FromSeconds(2)));
        StringAssert.Contains(ex.Message, "did not load the model");
    }

    [TestMethod]
    public void ServerExits_ErrorShowsItsOutput()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("uses cmd.exe");
        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            new ServerBackend(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c echo boom: cannot load model & exit /b 3 & rem {port}",
                null, false, TimeSpan.FromSeconds(20)));
        StringAssert.Contains(ex.Message, "boom");
    }
}
