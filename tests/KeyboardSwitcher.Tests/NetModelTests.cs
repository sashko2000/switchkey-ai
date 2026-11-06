using System.Net;
using System.Text.Json.Nodes;
using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>Network models: requests to OpenAI, Claude, Gemini and Ollama (fake HTTP), errors, choice in settings.</summary>
[TestClass]
public class NetModelTests
{
    /// <summary>Answers HTTP requests from a script and keeps them.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string Body)> Calls = new();
        public Func<HttpRequestMessage, string, (HttpStatusCode, string)> Reply = (_, _) => (HttpStatusCode.OK, "{}");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            var (code, text) = Reply(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(text) };
        }
    }

    private static NetModel Model(string provider, string model = "m1") =>
        new() { Provider = provider, BaseUrl = NetModels.Find(provider).DefaultUrl, Model = model };

    private const string Messages = "[{\"role\":\"system\",\"content\":\"Be brief.\"},{\"role\":\"user\",\"content\":\"Hi\"},{\"role\":\"assistant\",\"content\":\"Hello\"},{\"role\":\"user\",\"content\":\"Привет\"}]";

    private static string OpenAiAnswer(string text) =>
        new JsonObject { ["choices"] = new JsonArray { new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text } } } }.ToJsonString();

    [TestMethod]
    public void OpenAi_ChatCompletions_BearerKey_NoTemperature()
    {
        var http = new FakeHttp { Reply = (_, _) => (HttpStatusCode.OK, OpenAiAnswer("Hey")) };
        using var be = new NetBackend(Model("openai", "gpt-x"), "sk-test", http);
        Assert.AreEqual("Hey", be.GenerateChat(Messages, false, 768));
        var (req, body) = http.Calls.Single();
        Assert.AreEqual("https://api.openai.com/v1/chat/completions", req.RequestUri!.ToString());
        Assert.AreEqual("Bearer sk-test", req.Headers.Authorization!.ToString());
        var j = JsonNode.Parse(body)!;
        Assert.AreEqual("gpt-x", j["model"]!.GetValue<string>());
        Assert.AreEqual(4, j["messages"]!.AsArray().Count);
        Assert.IsNull(j["temperature"], "newer OpenAI models accept only the default temperature");
        Assert.IsNull(j["max_tokens"]);
        Assert.IsTrue(j["max_completion_tokens"]!.GetValue<int>() >= 16000, "room for reasoning");
    }

    [TestMethod]
    public void Gemini_And_Ollama_UseOpenAiFormat_WithTemperature()
    {
        var http = new FakeHttp { Reply = (_, _) => (HttpStatusCode.OK, OpenAiAnswer("ok")) };
        using (var be = new NetBackend(Model("gemini", "gemini-x"), "AIza", http)) be.GenerateChat(Messages, false, 768);
        using (var be = new NetBackend(Model("ollama", "qwen3:8b"), null, http)) be.GenerateChat(Messages, false, 768);
        Assert.AreEqual("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", http.Calls[0].Request.RequestUri!.ToString());
        Assert.AreEqual("http://localhost:11434/v1/chat/completions", http.Calls[1].Request.RequestUri!.ToString());
        Assert.IsNull(http.Calls[1].Request.Headers.Authorization, "no key for Ollama");
        foreach (var (_, body) in http.Calls)
        {
            var j = JsonNode.Parse(body)!;
            Assert.AreEqual(0, j["temperature"]!.GetValue<int>());
            Assert.IsNotNull(j["max_tokens"]);
        }
    }

    [TestMethod]
    public void Anthropic_Messages_SystemSeparate_Headers()
    {
        var http = new FakeHttp
        {
            Reply = (_, _) => (HttpStatusCode.OK,
                "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"...\"},{\"type\":\"text\",\"text\":\"Прив\"},{\"type\":\"text\",\"text\":\"ет!\"}]}"),
        };
        using var be = new NetBackend(Model("anthropic", "claude-x"), "sk-ant", http);
        Assert.AreEqual("Привет!", be.GenerateChat(Messages, false, 768));
        var (req, body) = http.Calls.Single();
        Assert.AreEqual("https://api.anthropic.com/v1/messages", req.RequestUri!.ToString());
        Assert.AreEqual("sk-ant", req.Headers.GetValues("x-api-key").Single());
        Assert.AreEqual("2023-06-01", req.Headers.GetValues("anthropic-version").Single());
        Assert.IsNull(req.Headers.Authorization);
        var j = JsonNode.Parse(body)!;
        Assert.AreEqual("Be brief.", j["system"]!.GetValue<string>());
        var msgs = j["messages"]!.AsArray();
        Assert.AreEqual(3, msgs.Count, "no system message in the list");
        Assert.AreEqual("user", msgs[0]!["role"]!.GetValue<string>());
        Assert.IsTrue(j["max_tokens"]!.GetValue<int>() >= 768);
    }

    [DataTestMethod]
    [DataRow(401, "err.net_auth")]
    [DataRow(429, "err.net_limit")]
    [DataRow(404, "err.net_not_found")]
    public void Errors_AreReadable(int code, string key)
    {
        var http = new FakeHttp { Reply = (_, _) => ((HttpStatusCode)code, "{\"error\":{\"message\":\"server says no\",\"type\":\"x\"}}") };
        using var be = new NetBackend(Model("openai"), "sk", http);
        var ex = Assert.ThrowsException<NetModelException>(() => be.GenerateChat(Messages, false, 10));
        StringAssert.Contains(ex.Message, "server says no");
        StringAssert.Contains(ex.Message, "OpenAI");
        Assert.IsTrue(L.AllStrings.ContainsKey(key));
    }

    /// <summary>A server that doesn't answer: the request ends when cancelled (Check's Cancel button / timeout).</summary>
    private sealed class SilentHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [TestMethod]
    public void SilentServer_CheckCanBeCancelled()
    {
        using var be = new NetBackend(Model("gemini"), "AIza", new SilentHttp());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsException<OperationCanceledException>(() =>
            be.GenerateChatAsync(Messages, 16, cts.Token).GetAwaiter().GetResult());
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
    }

    [TestMethod]
    public void SilentServer_ModelList_CancelledToo()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.ThrowsException<OperationCanceledException>(() =>
            NetModels.ListModelsAsync(Model("ollama"), null, cts.Token, new SilentHttp()).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void NoKey_OrNoModel_FailsBeforeSending()
    {
        Assert.ThrowsException<NetModelException>(() => new NetBackend(Model("anthropic"), "", new FakeHttp()));
        Assert.ThrowsException<NetModelException>(() => new NetBackend(Model("openai", ""), "sk", new FakeHttp()));
        using var ok = new NetBackend(Model("ollama"), null, new FakeHttp());   // Ollama doesn't need a key
    }

    [TestMethod]
    public async Task ListModels_StripsGeminiPrefix_Sorted()
    {
        var http = new FakeHttp { Reply = (_, _) => (HttpStatusCode.OK, "{\"data\":[{\"id\":\"models/gemini-b\"},{\"id\":\"models/gemini-a\"},{\"id\":\"models/gemini-a\"}]}") };
        var list = await NetModels.ListModelsAsync(Model("gemini"), "AIza", CancellationToken.None, http);
        CollectionAssert.AreEqual(new[] { "gemini-a", "gemini-b" }, list);
        Assert.AreEqual("https://generativelanguage.googleapis.com/v1beta/openai/models", http.Calls[0].Request.RequestUri!.ToString());
    }

    [TestMethod]
    public void Corrector_WithNetworkModel_LongChatHistory()
    {
        var http = new FakeHttp { Reply = (_, _) => (HttpStatusCode.OK, OpenAiAnswer("<think>x</think>Answer")) };
        using var ai = new AiCorrector();
        ai.UseBackend(new NetBackend(Model("ollama"), null, http), "m1", "Ollama: m1");
        Assert.IsTrue(ai.IsNetwork);
        var history = new List<ChatMessage>();
        for (int i = 0; i < 20; i++) { history.Add(new("user", new string('q', 1000))); history.Add(new("assistant", new string('a', 1000))); }
        history.Add(new("user", "last"));
        Assert.AreEqual("Answer", ai.Converse(history));
        var sent = JsonNode.Parse(http.Calls.Single().Body)!["messages"]!.AsArray();
        Assert.AreEqual(history.Count + 1, sent.Count, "the whole history fits the network budget");
    }

    [TestMethod]
    public void Platform_NetworkModel_NothingToInstall_OneCandidate()
    {
        TestEnv.NewHome();
        var n = Model("anthropic", "claude-x");
        n.Name = "Claude";
        var s = new Settings { AiModel = n.Choice, NetModels = { n }, AiNvidia = true };
        Assert.IsTrue(NetModels.IsNet(s.AiModel));
        Assert.AreSame(n, NetModels.Selected(s));
        Assert.IsTrue(AiPlatform.Installed(s));
        Assert.AreEqual(0, AiPlatform.Plan(s).steps.Count);
        Assert.IsFalse(AiPlatform.UseNvidia(s));
        Assert.IsFalse(AiPlatform.NvidiaMissing(s));
        Assert.AreEqual("Claude", AiPlatform.ModelName(s));
        var c = AiPlatform.Candidates(s).Single();
        Assert.AreEqual("Claude (Anthropic): claude-x", c.Device);

        var key1 = AiPlatform.InstalledKey(s);
        n.Model = "claude-y";
        Assert.AreNotEqual(key1, AiPlatform.InstalledKey(s), "another model - load again");

        s.NetModels.Clear();
        Assert.IsNull(NetModels.Selected(s));
        Assert.IsFalse(AiPlatform.Installed(s));
        Assert.ThrowsException<InvalidOperationException>(() => AiPlatform.Candidates(s));
    }

    [TestMethod]
    public void Choices_CatalogThenNetwork_SettingsRoundTrip()
    {
        var n = Model("ollama", "qwen3:8b");
        var s = new Settings { NetModels = { n } };
        var choices = ModelChoices.All(s);
        Assert.AreEqual(AiSetup.Catalog.Count + 1, choices.Count);
        Assert.AreEqual(n.Choice, choices[^1].Id);
        StringAssert.Contains(choices[^1].Title, "qwen3:8b");
        StringAssert.Contains(choices[^1].Title, "(network)");
        Assert.IsTrue(NetModels.IsLocalServer(n));
        Assert.IsFalse(NetModels.IsLocalServer(Model("openai")));

        var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(s))!;
        Assert.AreEqual(1, back.NetModels.Count);
        Assert.AreEqual(n.Id, back.NetModels[0].Id);
        Assert.AreEqual("qwen3:8b", back.NetModels[0].Model);
    }

    [TestMethod]
    public void Choices_InstalledModelsAreMarked()
    {
        TestEnv.NewHome();
        Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", "intel");
        try
        {
            var m = AiSetup.FindModelInfo("qwen3.5-4b");
            Assert.IsFalse(ModelChoices.All(new Settings()).Single(c => c.Id == m.Id).Title.Contains("✓"));
            // Pretend llama.cpp and the GGUF file are installed
            Directory.CreateDirectory(AmdEngines.LlamaDir);
            File.WriteAllText(AmdEngines.LlamaServer, "x");
            Directory.CreateDirectory(Path.GetDirectoryName(AmdEngines.GgufPath(m))!);
            using (var f = new FileStream(AmdEngines.GgufPath(m), FileMode.Create)) f.SetLength(m.GgufBytes);
            var choices = ModelChoices.All(new Settings());
            var title = choices.Single(c => c.Id == m.Id).Title;
            StringAssert.Contains(title, "✓ installed");
            Assert.IsFalse(title.Contains("GB"), "no download size for an installed model");
            StringAssert.Contains(choices.Single(c => c.Id == "qwen3-8b").Title, "GB");
        }
        finally { Environment.SetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR", null); }
    }

    [TestMethod]
    public void Secret_RoundTrip_OnWindows()
    {
        Assert.AreEqual("", Secret.Protect(""));
        Assert.AreEqual("plain-typed-key", Secret.Unprotect("plain-typed-key"), "a key typed into settings.json by hand");
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("DPAPI exists only on Windows"); return; }
        var p = Secret.Protect("sk-secret");
        Assert.IsTrue(p.StartsWith("dpapi:") && !p.Contains("sk-secret"));
        Assert.AreEqual("sk-secret", Secret.Unprotect(p));
    }
}
