using System.Xml.Linq;
using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>The Task Scheduler task for "also work in programs run as administrator".</summary>
[TestClass]
public class ElevationTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [TestMethod]
    public void TaskXml_HighestRights_NoTimeLimit_OnlyThisUser()
    {
        var xml = XDocument.Parse(Elevation.TaskXml(@"C:\Tools & Apps\KeyboardSwitcher.exe", @"PC\Олександр", atLogon: true));
        var p = xml.Descendants(Ns + "Principal").Single();
        Assert.AreEqual("HighestAvailable", p.Element(Ns + "RunLevel")!.Value);
        Assert.AreEqual("InteractiveToken", p.Element(Ns + "LogonType")!.Value);
        Assert.AreEqual(@"PC\Олександр", p.Element(Ns + "UserId")!.Value);
        Assert.AreEqual(@"PC\Олександр", xml.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.AreEqual(@"C:\Tools & Apps\KeyboardSwitcher.exe", xml.Descendants(Ns + "Command").Single().Value, "escaped and read back");
        Assert.AreEqual("PT0S", xml.Descendants(Ns + "ExecutionTimeLimit").Single().Value, "not stopped after 72 hours");
        Assert.AreEqual("false", xml.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.AreEqual("false", xml.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value);
    }

    [TestMethod]
    public void TaskXml_WithoutAutostart_NoTrigger()
    {
        var xml = XDocument.Parse(Elevation.TaskXml(@"C:\a.exe", "u", atLogon: false));
        Assert.AreEqual(0, xml.Descendants(Ns + "LogonTrigger").Count());
    }

    [TestMethod]
    public void OffByDefault() => Assert.IsFalse(new Settings().RunElevated);
}
