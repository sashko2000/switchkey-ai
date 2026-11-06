using System.Text.Json.Nodes;
using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>Chat with the model: the request sent to the model, history trimming, fallback, saved chats.</summary>
[TestClass]
public class ChatTests
{
    private static ChatMessage U(string t) => new("user", t);
    private static ChatMessage A(string t) => new("assistant", t);

    private static (AiCorrector ai, FakeBackend fake, List<JsonArray> sent) Make(string device)
    {
        var fake = new FakeBackend();
        var sent = new List<JsonArray>();
        var ai = new AiCorrector();
        ai.UseBackend(new Recorder(fake, sent), "fake", device);
        return (ai, fake, sent);
    }

    [TestMethod]
    public void Converse_SendsSystemAndWholeHistory()
    {
        var (ai, fake, sent) = Make("GPU.0");
        using var _ = ai;
        fake.Reply = (_, _, _) => "<think>hmm</think> Paris.";
        var answer = ai.Converse(new[] { U("Hi"), A("Hello! How can I help?"), U("Capital of France?") });
        Assert.AreEqual("Paris.", answer);
        var msgs = sent.Single();
        Assert.AreEqual(4, msgs.Count);
        Assert.AreEqual("system", msgs[0]!["role"]!.GetValue<string>());
        StringAssert.Contains(msgs[0]!["content"]!.GetValue<string>(), "language of the user's last message");
        Assert.AreEqual("Capital of France?", msgs[3]!["content"]!.GetValue<string>());
        Assert.IsFalse(fake.Calls.Single().Think);
    }

    [TestMethod]
    public void Converse_OnNpu_KeepsOnlyRecentMessages_StartingWithUser()
    {
        var (ai, _, sent) = Make("NPU");
        using var _ = ai;
        var history = new List<ChatMessage>();
        for (int i = 0; i < 10; i++) { history.Add(U($"question {i} " + new string('q', 200))); history.Add(A($"answer {i} " + new string('a', 200))); }
        history.Add(U("last question"));
        ai.Converse(history);
        var msgs = sent.Single();
        Assert.IsTrue(msgs.Count < history.Count + 1, "old messages are dropped on the NPU");
        Assert.AreEqual("user", msgs[1]!["role"]!.GetValue<string>(), "the conversation starts with the user");
        Assert.AreEqual("last question", msgs[^1]!["content"]!.GetValue<string>());
        int chars = msgs.Sum(m => m!["content"]!.GetValue<string>().Length);
        Assert.IsTrue(chars <= 1800, $"fits the NPU prompt: {chars}");
    }

    [TestMethod]
    public void Converse_LongSingleMessage_IsStillSent()
    {
        var (ai, _, sent) = Make("NPU");
        using var _ = ai;
        ai.Converse(new[] { U(new string('x', 5000)) });
        Assert.AreEqual(2, sent.Single().Count);
    }

    [TestMethod]
    public void Converse_TemplateWithoutSystemRole_FallsBackToPlainText()
    {
        var (ai, fake, _) = Make("CPU");
        using var _ = ai;
        fake.ChatThrows = true;
        fake.TextReply = _ => " Sure. ";
        Assert.AreEqual("Sure.", ai.Converse(new[] { U("Hi"), A("Hello"), U("Help me") }));
        var prompt = fake.TextCalls.Single();
        StringAssert.Contains(prompt, "User: Hi");
        StringAssert.Contains(prompt, "Assistant: Hello");
        Assert.IsTrue(prompt.TrimEnd().EndsWith("Assistant:"));
    }

    [TestMethod]
    public void Converse_EmptyHistory_Throws()
    {
        var (ai, _, _) = Make("CPU");
        using var _ = ai;
        Assert.ThrowsException<ArgumentException>(() => ai.Converse(Array.Empty<ChatMessage>()));
    }

    [TestMethod]
    public void ChatStore_SaveListDelete()
    {
        TestEnv.NewHome();
        Assert.AreEqual(0, ChatStore.List().Count);
        var c1 = new ChatSession { Id = "20260101-100000-000" };
        c1.Messages.Add(U("Привет, как дела?\nвторая строка"));
        c1.Messages.Add(A("Хорошо!"));
        ChatStore.Save(c1);
        Thread.Sleep(20);
        var c2 = new ChatSession { Id = "20260101-110000-000" };
        c2.Messages.Add(U("Second chat"));
        ChatStore.Save(c2);
        ChatStore.Save(new ChatSession { Id = "empty" });   // empty chats are not saved

        var list = ChatStore.List();
        Assert.AreEqual(2, list.Count);
        Assert.AreEqual("Second chat", list[0].Title, "newest first");
        Assert.AreEqual("Привет, как дела?", list[1].Title);
        Assert.AreEqual("Хорошо!", list[1].Messages[1].Content);
        Assert.IsTrue(File.ReadAllText(Path.Combine(ChatStore.Dir, c1.Id + ".json")).Contains("Привет"), "readable UTF-8, not \\u escapes");

        ChatStore.Delete(list[0]);
        Assert.AreEqual(1, ChatStore.List().Count);
    }

    [TestMethod]
    public void ChatStore_BrokenFileIsSkipped()
    {
        TestEnv.NewHome();
        Directory.CreateDirectory(ChatStore.Dir);
        File.WriteAllText(Path.Combine(ChatStore.Dir, "bad.json"), "{ not json");
        Assert.AreEqual(0, ChatStore.List().Count);
    }

    [TestMethod]
    public void ChatStore_TitleIsShort()
    {
        Assert.AreEqual(60, ChatStore.MakeTitle(new string('a', 100)).Length);
        Assert.AreEqual("text", ChatStore.MakeTitle("\n\n  text  \nmore"));
    }

    /// <summary>Passes calls to the fake model and keeps the JSON messages that were sent.</summary>
    private sealed class Recorder(FakeBackend inner, List<JsonArray> sent) : ILlmBackend
    {
        public System.Diagnostics.Process? Child => null;
        public string GenerateChat(string json, bool think, int max) { sent.Add(JsonNode.Parse(json)!.AsArray()); return inner.GenerateChat(json, think, max); }
        public string GenerateText(string prompt, int max) => inner.GenerateText(prompt, max);
        public void Dispose() => inner.Dispose();
    }
}
