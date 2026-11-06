using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>Clean-up of model answers, transliteration detection, language detection.</summary>
[TestClass]
public class TextTests
{
    [DataTestMethod]
    [DataRow("<think>hmm</think>\nResult", "x", "Result", DisplayName = "drops a closed <think> block")]
    [DataRow("<think>long thoughts", "x", "", DisplayName = "unfinished reasoning gives no answer")]
    [DataRow("\"Hello\"", "Привет", "Hello", DisplayName = "strips quotes")]
    [DataRow("\"Hello\"", "\"Привет\"", "\"Hello\"", DisplayName = "keeps quotes the original had")]
    [DataRow("Translation: Hello", "x", "Hello", DisplayName = "strips a 'Translation:' prefix")]
    [DataRow("Hello", "  Привет\n", "  Hello\n", DisplayName = "keeps surrounding spaces and line breaks")]
    [DataRow("```Hello```", "x", "Hello", DisplayName = "strips code fences")]
    public void Clean(string raw, string original, string expected) =>
        Assert.AreEqual(expected, AiCorrector.Clean(raw, original));

    [DataTestMethod]
    [DataRow("Мама мыла раму", "mama myla ramu", true)]
    [DataRow("мама міла раму", "mama mila ramu", true)]
    [DataRow("Мама мыла раму", "Mom washed the window frame", false)]
    [DataRow("Мама мыла раму", "Мама мыла раму", false)]
    public void LooksTransliterated(string source, string answer, bool expected) =>
        Assert.AreEqual(expected, AiCorrector.LooksTransliterated(source, answer));

    [DataTestMethod]
    [DataRow("Привет, как дела?", "ru")]
    [DataRow("Привіт, як справи? Їжа є.", "uk")]
    [DataRow("Hello, how are you?", "en")]
    [DataRow("Ďakujem, mám sa dobre, a ty?", "sk")]
    [DataRow("¿Hola, cómo estás? Gracias por la ayuda.", "es")]
    [DataRow("नमस्ते, आप कैसे हैं?", "hi")]
    public void DetectLanguage(string text, string lang) => Assert.AreEqual(lang, Languages.Detect(text));

    [DataTestMethod]
    [DataRow("Привет", "ru", "en", "en", DisplayName = "pair ru/en: Russian -> English")]
    [DataRow("Hello", "ru", "en", "ru", DisplayName = "pair ru/en: English -> Russian")]
    [DataRow("мама мила раму", "uk", "en", "en", DisplayName = "pair uk/en: Cyrillic without special letters -> English")]
    [DataRow("Hola, ¿cómo estás?", "ru", "en", "ru", DisplayName = "pair ru/en: other languages -> A")]
    public void PairTarget(string text, string a, string b, string expected) =>
        Assert.AreEqual(expected, Languages.PairTarget(text, a, b));

    [TestMethod]
    public void SixLanguages() => Assert.AreEqual(6, Languages.All.Count);
}
