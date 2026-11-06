using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>NVIDIA cards (llama.cpp CUDA, only if the model fits) and GGUF-only models on Intel.</summary>
[TestClass]
public class NvidiaTests
{
    private const long GB = 1L << 30;

    [TestInitialize]
    public void Init()
    {
        TestEnv.NewHome();   // nothing installed
        // No NVIDIA card unless a test sets one: the real card of the test computer must not change the results
        // (a Blackwell card switches the CUDA build and its folder)
        Nvidia.Override = () => new();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Nvidia.Override = null;
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", null);
    }

    private static void Card(long freeGb, long totalGb = 8, string driver = "566.36") =>
        Nvidia.Override = () => new() { new Nvidia.Gpu(0, "NVIDIA GeForce RTX 4060 Laptop GPU", totalGb * GB, freeGb * GB, driver) };

    /// <summary>Pretend llama.cpp (Vulkan and CUDA) and the GGUF file are installed.</summary>
    private static void FakeInstall(ModelInfo m)
    {
        Directory.CreateDirectory(AmdEngines.LlamaDir);
        File.WriteAllText(AmdEngines.LlamaServer, "not a real program");
        Directory.CreateDirectory(Nvidia.CudaDir);
        File.WriteAllText(Nvidia.CudaServer, "not a real program");
        File.WriteAllText(Path.Combine(Nvidia.CudaDir, "cudart64_12.dll"), "");
        Directory.CreateDirectory(Path.GetDirectoryName(AmdEngines.GgufPath(m))!);
        using var f = new FileStream(AmdEngines.GgufPath(m), FileMode.Create);
        f.SetLength(m.GgufBytes);   // sparse: no disk space is used
    }

    [TestMethod]
    public void ParsesNvidiaSmi()
    {
        var list = Nvidia.Parse("0, 6144, 5920, 591.44, 12.0, NVIDIA RTX PRO 500 Blackwell Generation Laptop GPU\r\n" +
                                "1, 12282, 100, 551.61, 8.6, NVIDIA RTX A2000, 12GB\n\nbad line\n");
        Assert.AreEqual(2, list.Count);
        Assert.AreEqual("NVIDIA RTX PRO 500 Blackwell Generation Laptop GPU", list[0].Name);
        Assert.AreEqual(6144L << 20, list[0].TotalBytes);
        Assert.AreEqual(5920L << 20, list[0].FreeBytes);
        Assert.AreEqual("12.0", list[0].ComputeCap);
        Assert.AreEqual("NVIDIA RTX A2000, 12GB", list[1].Name, "a comma in the name");
        Assert.AreEqual(1, list[1].Index);
        Assert.IsTrue(Nvidia.DriverOk(list[1]));
        Assert.IsFalse(Nvidia.DriverOk(list[0] with { Driver = "537.58" }), "too old for CUDA 12.4");
    }

    [DataTestMethod]
    [DataRow("12.0", "591.44", "13.4")]   // Blackwell laptop (RTX PRO 500, RTX 50): native code in CUDA 13
    [DataRow("10.0", "580.88", "13.4")]
    [DataRow("12.0", "572.16", "12.4")]   // driver too old for CUDA 13: the driver compiles the code on the first start
    [DataRow("8.9", "591.44", "12.4")]    // Ada (RTX 40): CUDA 12.4 has native code
    [DataRow("7.5", "566.36", "12.4")]
    public void CudaBuild_ByCardAndDriver(string cc, string driver, string expected)
    {
        Assert.AreEqual(expected, Nvidia.BuildFor(new Nvidia.Gpu(0, "x", 8 * GB, 8 * GB, driver, cc)).Version);
        Nvidia.Override = () => new() { new Nvidia.Gpu(0, "x", 8 * GB, 8 * GB, driver, cc) };
        Assert.AreEqual(expected, Nvidia.CudaVersion);
        StringAssert.Contains(Nvidia.CudaDir, "cuda" + expected);
        Assert.IsTrue(Nvidia.CudaGb is > 0.5 and < 0.7, Nvidia.CudaGb.ToString("F2"));
    }

    [DataTestMethod]
    [DataRow("qwen3.5-4b", 6, true)]
    [DataRow("qwen3.5-9b", 7, true)]
    [DataRow("qwen3.5-9b", 6, false)]
    [DataRow("gemma-4-12b", 8, false)]
    [DataRow("gemma-4-12b", 10, true)]
    [DataRow("qwen3.8-27b", 16, false)]
    [DataRow("qwen3.8-27b", 22, true)]
    public void FitsIntoFreeMemory(string id, int freeGb, bool fits)
    {
        var m = AiSetup.FindModelInfo(id);
        Assert.IsTrue(Nvidia.NeededBytes(m) > m.GgufBytes + GB, "file + context + buffers");
        Assert.AreEqual(fits, Nvidia.Fits(m, new Nvidia.Gpu(0, "x", 24 * GB, freeGb * GB, "566.36")));
    }

    [TestMethod]
    public void Off_ByDefault_NoNvidiaSteps()
    {
        Card(8);
        var s = new Settings { AiModel = "qwen3.5-9b" };
        Assert.IsFalse(s.AiNvidia);
        Assert.IsFalse(AiPlatform.UseNvidia(s));
        Assert.IsFalse(AiPlatform.Plan(s).steps.Any(x => x.Contains("CUDA")));
    }

    [TestMethod]
    public void Intel_OpenVinoModel_WithNvidia_PlansCudaAndGguf()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        Card(8);
        var s = new Settings { AiModel = "qwen3-8b", AiNvidia = true };
        var (steps, gb) = AiPlatform.Plan(s);
        var all = string.Join(" | ", steps);
        Assert.IsTrue(steps.Any(x => x.Contains("OpenVINO")), all);
        Assert.IsTrue(steps.Any(x => x.Contains("CUDA")), all);
        Assert.AreEqual(1, steps.Count(x => x.Contains("Qwen3-8B-Q4_K_M.gguf")), all);
        Assert.IsTrue(gb > 4.75 + 4.6 + 0.6, gb.ToString("F2"));
        Assert.IsTrue(AiPlatform.NvidiaMissing(s));
    }

    [TestMethod]
    public void NoDriver_NoCuda()
    {
        Card(8, driver: "537.58");
        var s = new Settings { AiModel = "qwen3.5-9b", AiNvidia = true };
        Assert.IsFalse(AiPlatform.UseNvidia(s));
        Nvidia.Override = () => new();
        Assert.IsFalse(AiPlatform.UseNvidia(s));
    }

    [TestMethod]
    public void Fits_CudaIsTriedFirst()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        var s = new Settings { AiModel = "qwen3.5-9b", AiNvidia = true, AiDevice = "auto" };
        var m = AiSetup.FindModelInfo(s.AiModel);
        Card(freeGb: 7);     // first the card: it decides the CUDA build that FakeInstall fills
        FakeInstall(m);
        Assert.IsTrue(AiPlatform.Installed(s));
        Assert.IsFalse(AiPlatform.NvidiaMissing(s));
        var left = AiPlatform.Plan(s).steps.Where(x => !x.Contains("Visual C++")).ToList();
        Assert.AreEqual(0, left.Count, string.Join(" | ", left));
        var cands = AiPlatform.Candidates(s).Select(c => c.Device).ToList();
        Assert.AreEqual("GPU (NVIDIA GeForce RTX 4060 Laptop GPU, CUDA 12.4)", cands[0]);
        Assert.AreEqual("CPU", cands[^1], "the fake llama-server lists no Vulkan GPU");
    }

    [TestMethod]
    public void DoesNotFit_CudaIsSkipped()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        var s = new Settings { AiModel = "gemma-4-12b", AiNvidia = true };
        Card(freeGb: 7);   // needs ~8.3 GB
        FakeInstall(AiSetup.FindModelInfo(s.AiModel));
        var cands = AiPlatform.Candidates(s).Select(c => c.Device).ToList();
        Assert.IsFalse(cands.Any(c => c.Contains("CUDA")), string.Join(",", cands));
        Assert.AreEqual("CPU", string.Join(",", cands));
    }

    [TestMethod]
    public void Intel_GgufOnlyModel_UsesLlamaCpp_NoOpenVino_NoNpu()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        var s = new Settings { AiModel = "gemma-4-e4b", AiDevice = "NPU" };
        var m = AiSetup.FindModelInfo(s.AiModel);
        Assert.IsTrue(AiPlatform.UsesLlama(m));
        var (steps, gb) = AiPlatform.Plan(s);
        var all = string.Join(" | ", steps);
        Assert.IsFalse(steps.Any(x => x.Contains("OpenVINO")), all);
        Assert.IsTrue(steps.Any(x => x.Contains("llama.cpp")), all);
        Assert.IsTrue(steps.Any(x => x.Contains("gemma-4-E4B-it-Q4_K_M.gguf")), all);
        Assert.IsTrue(gb is > 4.6 and < 4.8, gb.ToString("F2"));
        Assert.IsFalse(AiPlatform.ModelState(m, s).convert);

        FakeInstall(m);
        Assert.IsTrue(AiPlatform.Installed(s));
        Assert.AreEqual("gemma-4-E4B-it-Q4_K_M", AiPlatform.ModelName(s));
        Assert.AreEqual("CPU", string.Join(",", AiPlatform.Candidates(s).Select(c => c.Device)), "no NPU for llama.cpp on Intel");
    }

    [TestMethod]
    public void GgufOnlyModel_BlockedBySmartAppControl()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        var s = new Settings { AiModel = "qwen3.5-4b" };
        FakeInstall(AiSetup.FindModelInfo(s.AiModel));
        Hardware.MarkUnsignedBlocked();
        Assert.IsTrue(AiPlatform.LlamaBlocked);
        var ex = Assert.ThrowsException<InvalidOperationException>(() => AiPlatform.Candidates(s));
        StringAssert.Contains(ex.Message, "llama-server");
        Card(8);
        Assert.IsFalse(AiPlatform.UseNvidia(new Settings { AiModel = "qwen3.5-4b", AiNvidia = true }), "CUDA llama.cpp is unsigned too");
    }

    /// <summary>
    /// A GGUF file like llama.cpp writes it: header, metadata (with a tokenizer array of strings and a float array),
    /// <paramref name="blocks"/> layers of <paramref name="layerBytes"/> each, then the output tensor up to the file end.
    /// </summary>
    private static void WriteGguf(string path, string arch, int blocks, long layerBytes, long fileBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        void S(string x) { var b = System.Text.Encoding.UTF8.GetBytes(x); w.Write((ulong)b.Length); w.Write(b); }
        w.Write(0x46554747u); w.Write(3u);
        w.Write((ulong)(blocks * 2 + 2));   // tensors: token_embd, 2 per layer, output
        w.Write(5ul);                       // metadata
        S("general.architecture"); w.Write(8u); S(arch);
        S(arch + ".block_count"); w.Write(4u); w.Write((uint)blocks);
        S("general.alignment"); w.Write(4u); w.Write(32u);
        S("tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write(3ul); S("a"); S("Привет"); S("<|im_end|>");
        S("tokenizer.ggml.scores"); w.Write(9u); w.Write(6u); w.Write(3ul); w.Write(0f); w.Write(1f); w.Write(2f);
        void T(string name, long offset) { S(name); w.Write(2u); w.Write(4096ul); w.Write(4096ul); w.Write(12u); w.Write((ulong)offset); }
        long embd = 100_000_000, half = layerBytes / 2;
        T("token_embd.weight", 0);
        for (int i = 0; i < blocks; i++)
        {
            T($"blk.{i}.attn_q.weight", embd + i * layerBytes);
            T($"blk.{i}.ffn_down.weight", embd + i * layerBytes + half);
        }
        T("output.weight", embd + blocks * layerBytes);
        w.Flush();
        fs.SetLength(fileBytes);   // sparse
    }

    [TestMethod]
    public void ReadsGgufHeader()
    {
        var path = Path.Combine(TestEnv.Home, "x.gguf");
        WriteGguf(path, "qwen35", 32, 150_000_000, 5_680_522_464);
        var g = GgufInfo.Read(path)!;
        Assert.AreEqual("qwen35", g.Architecture);
        Assert.AreEqual(32, g.BlockCount);
        Assert.AreEqual(32 * 150_000_000L, g.LayerBytes);
        Assert.IsTrue(g.OtherBytes > 100_000_000, "embeddings + output");
        long all = g.LayerBytes + g.OtherBytes;   // everything after the header
        Assert.IsTrue(all < 5_680_522_464L && all > 5_680_522_464L - 65536, all.ToString());
    }

    [TestMethod]
    public void NotGguf_IsNull()
    {
        var path = Path.Combine(TestEnv.Home, "zero.gguf");
        File.WriteAllBytes(path, new byte[1000]);
        Assert.IsNull(GgufInfo.Read(path));
        Assert.IsNull(GgufInfo.Read(Path.Combine(TestEnv.Home, "missing.gguf")));
    }

    [TestMethod]
    public void Offload_WholeOrAsManyLayersAsFit()
    {
        var m = AiSetup.FindModelInfo("qwen3.5-9b");
        var info = new GgufInfo("qwen35", 32, 32L * 160_000_000, 560_000_000);
        Assert.IsTrue(Nvidia.PlanOffload(m, new Nvidia.Gpu(0, "x", 24 * GB, 20 * GB, "566.36"), info)!.Full);

        var off = Nvidia.PlanOffload(m, new Nvidia.Gpu(0, "x", 6 * GB, 6 * GB, "566.36"), info)!;
        Assert.IsFalse(off.Full);
        Assert.AreEqual(Math.Min(31, (int)Math.Floor((6.0 * GB - Nvidia.PartialReserve) / (160_000_000 * 1.1))), off.Layers);
        Assert.AreEqual(32, off.Total);

        off = Nvidia.PlanOffload(m, new Nvidia.Gpu(0, "x", 6 * GB, 4 * GB, "566.36"), info)!;   // 3 GB / 176 MB = 18
        Assert.AreEqual(18, off.Layers);
        Assert.AreEqual(56, off.Percent);
    }

    [TestMethod]
    public void Offload_TooLittle_OrUnknownLayers_IsNull()
    {
        var m = AiSetup.FindModelInfo("qwen3.8-27b");
        var info = new GgufInfo("qwen35", 64, 64L * 240_000_000, 1_000_000_000);
        var small = new Nvidia.Gpu(0, "x", 4 * GB, 3 * GB, "566.36");   // (3 - 1) GB / 264 MB = 8 of 64 < 25%
        Assert.IsNull(Nvidia.PlanOffload(m, small, info));
        Assert.IsNull(Nvidia.PlanOffload(m, new Nvidia.Gpu(0, "x", 8 * GB, 8 * GB, "566.36"), null), "layers unknown");
        var six = new Nvidia.Gpu(0, "x", 6 * GB, 6 * GB, "566.36");
        var off = Nvidia.PlanOffload(m, six, info)!;
        Assert.IsTrue(off.Layers >= 16 && off.Layers < 64, off.ToString());
    }

    [DataTestMethod]
    [DataRow("qwen3.5-4b", 100)]
    [DataRow("qwen3-8b", 97)]
    [DataRow("qwen3.5-9b", 85)]
    [DataRow("gemma-4-12b", 68)]
    [DataRow("qwen3.8-27b", 29)]
    public void SixGbCard_EstimatedShare(string id, int percent) =>
        Assert.AreEqual(percent, Nvidia.EstimatePercent(AiSetup.FindModelInfo(id), 6 * GB));

    [TestMethod]
    public void DoesNotFitWhole_PartialCandidateFirst()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        var s = new Settings { AiModel = "qwen3.5-9b", AiNvidia = true };
        var m = AiSetup.FindModelInfo(s.AiModel);
        Card(freeGb: 6, totalGb: 6);
        FakeInstall(m);
        WriteGguf(AmdEngines.GgufPath(m), "qwen35", 32, 160_000_000, m.GgufBytes);
        var cands = AiPlatform.Candidates(s).Select(c => c.Device).ToList();
        StringAssert.StartsWith(cands[0], "GPU (NVIDIA GeForce RTX 4060 Laptop GPU, CUDA 12.4, ");
        StringAssert.Contains(cands[0], " of 32 layers)");
        Assert.AreEqual("CPU", cands[^1]);
    }

    [TestMethod]
    public void VulkanGpu_RadeonThenIntel_NeverNvidia()
    {
        AmdEngines.VulkanDevice D(string id, string name) => new(id, name);
        Assert.AreEqual("Vulkan1", AmdEngines.PickGpu(new() { D("Vulkan0", "NVIDIA GeForce RTX 4060"), D("Vulkan1", "AMD Radeon 780M Graphics"), D("Vulkan2", "Intel(R) Arc(TM) Graphics") })!.Id);
        Assert.AreEqual("Vulkan1", AmdEngines.PickGpu(new() { D("Vulkan0", "NVIDIA GeForce RTX 4060"), D("Vulkan1", "Intel(R) Arc(TM) 140T GPU") })!.Id);
        Assert.IsNull(AmdEngines.PickGpu(new() { D("Vulkan0", "NVIDIA GeForce RTX 4060"), D("Vulkan1", "llvmpipe (LLVM 15.0.7, 256 bits)") }));
    }
}
