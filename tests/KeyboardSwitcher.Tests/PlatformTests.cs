using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>Device order NPU -> GPU -> CPU, installation plans and load candidates for Intel and AMD.</summary>
[TestClass]
public class PlatformTests
{
    [TestInitialize]
    public void Init() => TestEnv.NewHome();   // nothing installed

    [TestCleanup]
    public void Cleanup() => Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", null);

    [DataTestMethod]
    [DataRow("auto", "NPU,GPU,CPU")]
    [DataRow("NPU", "NPU,GPU,CPU")]
    [DataRow("GPU", "GPU,NPU,CPU")]
    [DataRow("CPU", "CPU,NPU,GPU")]
    public void KindOrder(string choice, string expected) =>
        Assert.AreEqual(expected, string.Join(",", AiPlatform.KindOrder(choice)));

    [DataTestMethod]
    [DataRow("auto", "NPU,GPU.0,CPU")]
    [DataRow("NPU", "NPU,GPU.0,CPU")]
    [DataRow("GPU", "GPU.0,NPU,CPU")]
    [DataRow("CPU", "CPU,NPU,GPU.0")]
    public void OpenVinoOrder(string choice, string expected) =>
        // OpenVINO is not installed in the test folder, so devices can't be detected and nothing is filtered out
        Assert.AreEqual(expected, string.Join(",", OvDevices.Order(choice, new List<string> { "NPU", "GPU.0", "CPU" })));

    [TestMethod]
    public void DeviceKind() => Assert.AreEqual("GPU", OvDevices.Kind("GPU.1"));

    [TestMethod]
    public void Intel_PlanHasOpenVinoAndModel()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        var s = new Settings { AiModel = "qwen3-8b" };
        var (steps, gb) = AiPlatform.Plan(s);
        Assert.IsTrue(steps.Any(x => x.Contains("OpenVINO")), string.Join(" | ", steps));
        Assert.IsTrue(steps.Any(x => x.Contains("Qwen3-8B")), string.Join(" | ", steps));
        Assert.IsTrue(gb is > 4.5 and < 5.5, gb.ToString("F2"));
        Assert.IsFalse(AiPlatform.Installed(s));
    }

    [TestMethod]
    public void Intel_SmallestQwenIsConverted_4BIsReady()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        Assert.IsTrue(AiPlatform.ModelState(AiSetup.FindModelInfo("qwen3-1.7b"), new Settings { AiModel = "qwen3-1.7b" }).convert);
        Assert.IsFalse(AiPlatform.ModelState(AiSetup.FindModelInfo("qwen3-4b"), new Settings { AiModel = "qwen3-4b" }).convert);
    }

    [DataTestMethod]
    [DataRow("NPU", "GPU.0,CPU")]
    [DataRow("auto", "GPU.0,CPU")]
    [DataRow("CPU", "CPU,GPU.0")]
    public void Intel_ModelWithoutNpuVersion_SkipsTheNpu(string choice, string expected)
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        var m = AiSetup.FindModelInfo("qwen3-4b");
        Assert.IsFalse(m.OpenVinoNpu);
        // Pretend OpenVINO and the model are installed
        var dir = AiSetup.ModelDir(m);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "openvino_model.xml"), "<net/>");
        File.WriteAllText(Path.Combine(dir, ".complete"), m.Repo);
        Directory.CreateDirectory(AiSetup.RuntimeDir);
        File.WriteAllText(Path.Combine(AiSetup.RuntimeDir, "openvino_genai_c.dll"), "fake");
        var cands = AiPlatform.Candidates(new Settings { AiModel = "qwen3-4b", AiDevice = choice });
        Assert.AreEqual(expected, string.Join(",", cands.Select(c => c.Device)));
    }

    [TestMethod]
    public void Amd_PlanHasLlamaCppAndGguf_NoConversion()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "amd");
        Assert.IsTrue(AiPlatform.Amd);
        var s = new Settings { AiModel = "qwen3-4b", AiDevice = "auto" };
        var (steps, gb) = AiPlatform.Plan(s);
        Assert.IsTrue(steps.Any(x => x.Contains("llama.cpp")), string.Join(" | ", steps));
        Assert.IsTrue(steps.Any(x => x.Contains(".gguf")), string.Join(" | ", steps));
        Assert.IsFalse(steps.Any(x => x.Contains("ryzenai")), "no XDNA 2 NPU on the test machine");
        Assert.IsTrue(gb is > 2.2 and < 2.6, gb.ToString("F2"));
        Assert.IsFalse(AiPlatform.ModelState(AiSetup.FindModelInfo("qwen3-4b"), new Settings { AiModel = "qwen3-4b" }).convert);
        Assert.AreEqual(0, AiPlatform.Candidates(s).Count, "nothing installed");
    }

    [TestMethod]
    public void Amd_Installed_CpuCandidate_ThenBlockedByWindows_SwitchesToOpenVino()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "amd");
        var s = new Settings { AiModel = "qwen3-4b", AiDevice = "auto" };
        var m = AiSetup.FindModelInfo("qwen3-4b");

        // Pretend llama.cpp and the model are installed (an empty file of the right size)
        Directory.CreateDirectory(AmdEngines.LlamaDir);
        File.WriteAllText(AmdEngines.LlamaServer, "not a real program");
        Directory.CreateDirectory(Path.GetDirectoryName(AmdEngines.GgufPath(m))!);
        using (var f = new FileStream(AmdEngines.GgufPath(m), FileMode.Create)) f.SetLength(m.GgufBytes);
        try
        {
            Assert.IsTrue(AiPlatform.Installed(s));
            var left = AiPlatform.Plan(s).steps.Where(x => !x.Contains("Visual C++")).ToList();   // VC++ depends on the computer
            Assert.AreEqual(0, left.Count, string.Join(" | ", left));
            // The fake llama-server can't list Vulkan GPUs, so only the CPU is left
            var cands = AiPlatform.Candidates(s);
            Assert.AreEqual("CPU", string.Join(",", cands.Select(c => c.Device)));

            Hardware.MarkUnsignedBlocked();
            Assert.IsFalse(AiPlatform.Amd);
            Assert.IsTrue(AiPlatform.AmdBlockedBySac);
            Assert.IsTrue(AiPlatform.Plan(s).steps.Any(x => x.Contains("OpenVINO")));
        }
        finally { File.Delete(AmdEngines.GgufPath(m)); }
    }
}
