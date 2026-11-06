using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>Model catalog, interface translations and settings file.</summary>
[TestClass]
public class CatalogTests
{
    public static IEnumerable<object[]> Models => AiSetup.Catalog.Select(m => new object[] { m.Id });

    [TestMethod]
    public void ThreeModelsWithUniqueIds()
    {
        // Qwen3-8B, Qwen3-4B, Qwen3-1.7B (Mistral-7B and Phi-3.5-mini were removed after the model tests)
        Assert.AreEqual(3, AiSetup.Catalog.Count);
        Assert.AreEqual(AiSetup.Catalog.Count, AiSetup.Catalog.Select(m => m.Id).Distinct().Count());
    }

    [DataTestMethod]
    [DynamicData(nameof(Models))]
    public void ModelHasAllFormats(string id)
    {
        var m = AiSetup.FindModelInfo(id);
        Assert.IsTrue(m.Repo.Contains('/') && m.SizeGb > 0, "OpenVINO");
        Assert.IsTrue(m.GgufRepo != null && m.GgufFile!.EndsWith(".gguf") && m.GgufBytes > 1_000_000_000, "GGUF");
        Assert.IsTrue(m.NpuRepo != null && m.NpuRepo.StartsWith("amd/") && m.NpuGb > 0, "AMD NPU");
        foreach (var key in new[] { "model." + id + ".title", "model." + id + ".note" })
            Assert.IsTrue(L.AllStrings.TryGetValue(key, out var row) && row.Length == 6 && row.All(t => t.Length > 0), key);
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
