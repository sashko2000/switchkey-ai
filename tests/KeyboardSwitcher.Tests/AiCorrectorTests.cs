using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>Prompts, retries and fallbacks of AiCorrector with a scripted fake model.</summary>
[TestClass]
public class AiCorrectorTests
{
    private FakeBackend _fake = null!;
    private AiCorrector _ai = null!;

    [TestInitialize]
    public void Init()
    {
        _fake = new FakeBackend();
        _ai = new AiCorrector();
        _ai.UseBackend(_fake, "fake", "TEST");
    }

    [TestCleanup]
    public void Cleanup() => _ai.Dispose();

    [TestMethod]
    public void Fix_UsesProofreaderPromptWithExamples()
    {
        _fake.Reply = (_, _, _) => "Я вчера купил новый телефон.";
        Assert.AreEqual("Я вчера купил новый телефон.", _ai.Fix("я вчера купел новый тилефон"));
        var call = _fake.Calls.Single();
        StringAssert.Contains(call.System, "proofreader");
        Assert.IsFalse(call.Think);
        Assert.IsTrue(call.Messages >= 6, "few-shot examples");
    }

    [TestMethod]
    public void Fix_KeepsTheLanguage_HasEnglishExample()
    {
        _ai.Fix("I recieved you're mesage");
        StringAssert.Contains(_fake.Calls[0].System, "SAME language");
        Assert.IsTrue(_fake.Calls[0].Messages >= 8, "examples in Russian and English");
    }

    [TestMethod]
    public void Translate_MarksTheTextAsATask()
    {
        // Otherwise "Спасибо за помощь!" got "You're welcome!" instead of a translation
        _ai.Translate("Спасибо за помощь!", "en");
        Assert.AreEqual("Translate into English:\nСпасибо за помощь!", _fake.Calls[0].User);
    }

    [DataTestMethod]
    [DataRow("Polite", "Привет, коллеги! Сделайте отчёт.", "Russian")]
    [DataRow("Formal", "Hey, send me the report", "English")]
    [DataRow("Reply", "Привіт! Чи зможете ви прийти завтра?", "Ukrainian")]
    public void Transform_NamesTheLanguageOfTheText(string taskId, string text, string lang)
    {
        _ai.Transform(AssistantTask.FromId(taskId), text, "ru", "en", "en");
        StringAssert.Contains(_fake.Calls[0].System, lang);
    }

    [DataTestMethod]
    [DataRow("ru", "Russian")]
    [DataRow("en", "English")]
    [DataRow("uk", "Ukrainian")]
    [DataRow("sk", "Slovak")]
    [DataRow("es", "Spanish")]
    [DataRow("hi", "Hindi (in Devanagari script)")]
    public void Translate_PromptNamesTheTargetLanguage(string code, string name)
    {
        _fake.Reply = (_, _, _) => "ANSWER";
        _ai.Translate("Hello, how are you?", code);
        StringAssert.Contains(_fake.Calls[0].System, "translator into " + name);
    }

    [TestMethod]
    public void Translate_SlovakIsNotCzech()
    {
        _ai.Translate("Привет", "sk");
        StringAssert.Contains(_fake.Calls[0].System, "not Czech");
    }

    [TestMethod]
    public void Translate_UntranslatedAnswer_RetriesWithReasoning()
    {
        _fake.Reply = (_, _, think) => think ? "Привет, как дела?" : "Привіт, як справи?";
        Assert.AreEqual("Привет, как дела?", _ai.Translate("Привіт, як справи?", "ru"));
        Assert.AreEqual(2, _fake.Calls.Count);
        Assert.IsTrue(_fake.Calls[1].Think);
    }

    [TestMethod]
    public void Translate_Transliteration_FixesThenThinks()
    {
        _fake.Reply = (sys, _, think) =>
            sys.Contains("proofreader") ? "Мама мыла раму." : think ? "Mom washed the window frame." : "mama myla ramu";
        Assert.AreEqual("Mom washed the window frame.", _ai.Translate("мама мыла раму", "en"));
        Assert.AreEqual(4, _fake.Calls.Count, "translate, fix, translate, translate with reasoning");
        StringAssert.Contains(_fake.Calls[1].System, "proofreader");
        Assert.IsTrue(_fake.Calls[3].Think);
    }

    [TestMethod]
    public void Translate_TemplateWithoutSystemRole_FallsBackToPlainText()
    {
        _fake.ChatThrows = true;
        _fake.TextReply = _ => "Hello!";
        Assert.AreEqual("Hello!", _ai.Translate("Привет!", "en"));
        var prompt = _fake.TextCalls.Last();
        StringAssert.Contains(prompt, "translator into English");
        StringAssert.Contains(prompt, "Translation:");
    }

    public static IEnumerable<object[]> AllTasks => AssistantTask.All().Select(t => new object[] { t.Id });

    [DataTestMethod]
    [DynamicData(nameof(AllTasks))]
    public void Transform_EveryActionAnswers(string taskId)
    {
        var task = AssistantTask.FromId(taskId);
        _fake.Reply = (_, _, _) => "OUT";
        Assert.AreEqual("OUT", _ai.Transform(task, "Привет, коллеги! Завтра встреча.", "ru", "en", "en"));
        Assert.IsTrue(_fake.Calls.Count >= 1);
    }

    [DataTestMethod]
    [DataRow("Polite", "polite")]
    [DataRow("Formal", "formal business")]
    [DataRow("Casual", "conversational")]
    [DataRow("Shorten", "shorter")]
    [DataRow("Simplify", "simple")]
    [DataRow("Reply", "reply")]
    public void Transform_PromptMatchesAction(string taskId, string word)
    {
        _ai.Transform(AssistantTask.FromId(taskId), "Привет, коллеги!", "ru", "en", "en");
        Assert.IsTrue(_fake.Calls[0].System.Contains(word, StringComparison.OrdinalIgnoreCase), _fake.Calls[0].System);
    }

    [TestMethod]
    public void Explain_AnswersInInterfaceLanguageAtLength()
    {
        _ai.Transform(new AssistantTask(AiAction.Explain), "foo", "ru", "en", "uk");
        StringAssert.Contains(_fake.Calls[0].System, "in Ukrainian");
        Assert.IsTrue(_fake.Calls[0].MaxTokens >= 512);
    }

    [TestMethod]
    public void TranslatePair_RussianGoesToEnglish()
    {
        _ai.Transform(new AssistantTask(AiAction.TranslatePair), "Привет, коллеги!", "ru", "en", "en");
        StringAssert.Contains(_fake.Calls[0].System, "into English");
    }

    [TestMethod]
    public void TranslateToHindi_AsksForDevanagari()
    {
        _ai.Transform(new AssistantTask(AiAction.TranslateTo, "hi"), "Hello", "ru", "en", "en");
        StringAssert.Contains(_fake.Calls[0].System, "Devanagari");
    }

    [TestMethod]
    public void Unload_DisposesTheModel_AndFurtherRequestsFail()
    {
        _ai.Unload();
        for (int i = 0; i < 40 && !_fake.Disposed; i++) Thread.Sleep(50);   // unloading is asynchronous
        Assert.IsTrue(_fake.Disposed);
        Assert.ThrowsException<InvalidOperationException>(() => _ai.Fix("x"));
    }
}
