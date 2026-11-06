using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>Model catalog, interface translations and settings file.</summary>
[TestClass]
public class CatalogTests
{
    public static IEnumerable<object[]> Models => AiSetup.Catalog.Select(m => new object[] { m.Id });

    [TestMethod]
    public void EightModelsWithUniqueIds()
    {
        // Qwen3 8B/4B/1.7B (OpenVINO + GGUF) and the GGUF-only Qwen3.5 9B/4B, Gemma 4 12B/E4B, Qwen3.8 27B
        // (Mistral-7B and Phi-3.5-mini were removed after the model tests)
        Assert.AreEqual(8, AiSetup.Catalog.Count);
        Assert.AreEqual(5, AiSetup.Catalog.Count(m => m.GgufOnly));
        Assert.AreEqual("qwen3-8b", AiSetup.Catalog[0].Id, "the default model comes first");
        Assert.AreEqual(AiSetup.Catalog.Count, AiSetup.Catalog.Select(m => m.Id).Distinct().Count());
    }

    [DataTestMethod]
    [DynamicData(nameof(Models))]
    public void ModelHasAllFormats(string id)
    {
        var m = AiSetup.FindModelInfo(id);
        Assert.AreEqual(id, m.Id);
        Assert.IsTrue(m.GgufRepo != null && m.GgufRepo.Contains('/') && m.GgufFile!.EndsWith(".gguf") && m.GgufBytes > 1_000_000_000, "GGUF");
        if (m.GgufOnly)
        {
            Assert.IsFalse(m.OpenVinoNpu);
            Assert.IsNull(m.NpuRepo);
        }
        else
        {
            Assert.IsTrue(m.Repo.Contains('/') && m.SizeGb > 0, "OpenVINO");
            Assert.IsTrue(m.NpuRepo != null && m.NpuRepo.StartsWith("amd/") && m.NpuGb > 0, "AMD NPU");
        }
        foreach (var key in new[] { "model." + id + ".title", "model." + id + ".note" })
            Assert.IsTrue(L.AllStrings.TryGetValue(key, out var row) && row.Length == 6 && row.All(t => t.Length > 0), key);
    }

    [TestMethod]
    public void DownloadSize_ForThisComputer()
    {
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        try
        {
            Assert.AreEqual(4.75, AiPlatform.DownloadGb(AiSetup.FindModelInfo("qwen3-8b")), 0.01, "OpenVINO version");
            Assert.AreEqual(5.68, AiPlatform.DownloadGb(AiSetup.FindModelInfo("qwen3.5-9b")), 0.01, "GGUF only");
            Assert.AreEqual("Qwen3.5-9B (new) - 5.7 GB", L.T("sw.model_item", AiSetup.FindModelInfo("qwen3.5-9b").Title, 5.68));
            Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "amd");
            if (AiPlatform.Amd) Assert.AreEqual(5.03, AiPlatform.DownloadGb(AiSetup.FindModelInfo("qwen3-8b")), 0.01, "GGUF on AMD");
        }
        finally { Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", null); }
    }

    [TestMethod]
    public void AllStringsInSixLanguages()
    {
        var incomplete = L.AllStrings.Where(kv => kv.Value.Length != 6 || kv.Value.Any(t => t.Length == 0)).Select(kv => kv.Key).ToList();
        Assert.AreEqual(0, incomplete.Count, string.Join(", ", incomplete.Take(10)));
    }

    [TestMethod]
    public void SettingsRoundTrip()
    {
        var s = new Settings { AiDevice = "GPU", LayoutPriority = "uk", TranslateLangA = "sk", TranslateLangB = "hi" };
        var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(s))!;
        Assert.AreEqual("GPU", back.AiDevice);
        Assert.AreEqual("uk", back.LayoutPriority);
        Assert.AreEqual("sk", back.TranslateLangA);
        Assert.AreEqual("hi", back.TranslateLangB);
        Assert.AreEqual("auto", new Settings().AiDevice);
        Assert.AreEqual("qwen3-8b", new Settings().AiModel);
    }
}
