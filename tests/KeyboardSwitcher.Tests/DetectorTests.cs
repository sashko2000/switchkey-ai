using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>
/// Layout detection on dictionary words. Each scenario must reach a minimum share of correct decisions
/// (a little below the current result, so a regression shows up). Details: tests\DetectorTests (dotnet run -- -v).
/// </summary>
[TestClass]
public class DetectorTests
{
    private const int Train = 40000, TrainUk = 28000, TrainHi = 15000;
    private static LayoutDetector _det = null!;
    private static List<string> _en = null!, _ru = null!, _uk = null!, _hi = null!, _tech = null!;

    private static readonly Lang[] All3 = { Lang.En, Lang.Ru, Lang.Uk }, EnRu = { Lang.En, Lang.Ru },
        All4 = { Lang.En, Lang.Ru, Lang.Uk, Lang.Hi }, EnHi = { Lang.En, Lang.Hi };

    private static string Res(string f) => Path.Combine(AppContext.BaseDirectory, "Resources", f);
    private static List<string> Words(string f) => File.ReadAllLines(Res(f)).Select(l => l.Split(' ')[0]).ToList();
    private static StringReader Head(string f, int n) => new(string.Join('\n', File.ReadAllLines(Res(f)).Take(n)));

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        // Trained on the first 40,000 words (Ukrainian 28,000, Hindi 15,000); the rest are "unknown" words for the trigram model
        _det = new LayoutDetector(Head("en.txt", Train), Head("ru.txt", Train), Head("uk.txt", TrainUk), Head("hi.txt", TrainHi),
            new StringReader(File.ReadAllText(Res("tech.txt"))));
        (_en, _ru, _uk, _hi) = (Words("en.txt"), Words("ru.txt"), Words("uk.txt"), Words("hi.txt"));
        _tech = File.ReadAllLines(Res("tech.txt")).Where(w => w.Length >= 3).ToList();
    }

    private sealed record Scenario(Func<IEnumerable<string>> Words, Lang TypedIn, Lang WordLang, bool Switch, Lang[] Candidates, double MinPercent);

    private const string Code = "ctx len int var src dst cfg tmp api sql xml yaml json http async await const func args kwargs params todo utils dotnet csproj github ovms qwen openvino llama gguf vulkan bat ps1 npm git dto foo bar baz lhs rhs idx ptr buf str obj msg req resp ctor dtor enum impl init repo diff merge rebase stash grep sed awk curl wget sudo chmod mkdir kubectl docker nginx redis postgres mongo kafka grpc proto webpack vite eslint pytest numpy pandas torch nvidia intel ryzen descartes oleksandr postoliuk VS API URL SQL NPU GPU CPU RAM SSD USB HDMI LLM";
    private const string TypicalEn = "hello thanks please meeting tomorrow today build release deploy server client model memory layout keyboard switch";

    private static readonly Dictionary<string, Scenario> Scenarios = new()
    {
        // three layouts
        ["RU untouched"] = new(() => _ru.Take(20000), Lang.Ru, Lang.Ru, false, All3, 99.9),
        ["EN untouched"] = new(() => _en.Take(20000), Lang.En, Lang.En, false, All3, 99.9),
        ["UK untouched"] = new(() => _uk.Take(20000), Lang.Uk, Lang.Uk, false, All3, 99.9),
        ["RU typed in EN"] = new(() => _ru.Take(20000), Lang.En, Lang.Ru, true, All3, 97),
        ["UK typed in EN"] = new(() => _uk.Take(20000), Lang.En, Lang.Uk, true, All3, 96.5),
        ["EN typed in RU"] = new(() => _en.Take(20000), Lang.Ru, Lang.En, true, All3, 95),
        ["EN typed in UK"] = new(() => _en.Take(20000), Lang.Uk, Lang.En, true, All3, 95),
        ["UK typed in RU"] = new(() => _uk.Take(20000), Lang.Ru, Lang.Uk, true, All3, 95),
        ["RU typed in UK"] = new(() => _ru.Take(20000), Lang.Uk, Lang.Ru, true, All3, 91),
        ["unknown RU typed in EN"] = new(() => _ru.Skip(Train).Where(w => w.Length >= 5), Lang.En, Lang.Ru, true, All3, 95),
        ["unknown UK typed in EN"] = new(() => _uk.Skip(TrainUk).Where(w => w.Length >= 5), Lang.En, Lang.Uk, true, All3, 94),
        ["unknown RU untouched"] = new(() => _ru.Skip(Train).Where(w => w.Length >= 5), Lang.Ru, Lang.Ru, false, All3, 99.5),
        ["unknown UK untouched"] = new(() => _uk.Skip(TrainUk).Where(w => w.Length >= 5), Lang.Uk, Lang.Uk, false, All3, 99),
        ["unknown EN untouched"] = new(() => _en.Skip(Train).Where(w => w.Length >= 5), Lang.En, Lang.En, false, All3, 99.9),
        // two layouts
        ["EN/RU: RU untouched"] = new(() => _ru.Take(20000), Lang.Ru, Lang.Ru, false, EnRu, 99.9),
        ["EN/RU: EN untouched"] = new(() => _en.Take(20000), Lang.En, Lang.En, false, EnRu, 99.9),
        ["EN/RU: RU typed in EN"] = new(() => _ru.Take(20000), Lang.En, Lang.Ru, true, EnRu, 97),
        ["EN/RU: EN typed in RU"] = new(() => _en.Take(20000), Lang.Ru, Lang.En, true, EnRu, 95),
        // with Hindi
        ["HI untouched"] = new(() => _hi.Take(15000), Lang.Hi, Lang.Hi, false, All4, 99.9),
        ["with HI: EN untouched"] = new(() => _en.Take(20000), Lang.En, Lang.En, false, All4, 99.9),
        ["with HI: RU untouched"] = new(() => _ru.Take(20000), Lang.Ru, Lang.Ru, false, All4, 99.9),
        ["with HI: UK untouched"] = new(() => _uk.Take(20000), Lang.Uk, Lang.Uk, false, All4, 99.9),
        ["HI typed in EN"] = new(() => _hi.Take(15000), Lang.En, Lang.Hi, true, All4, 92.5),
        ["HI typed in RU"] = new(() => _hi.Take(15000), Lang.Ru, Lang.Hi, true, All4, 93),
        ["EN typed in HI"] = new(() => _en.Take(20000), Lang.Hi, Lang.En, true, All4, 96.5),
        ["RU typed in HI"] = new(() => _ru.Take(20000), Lang.Hi, Lang.Ru, true, All4, 97),
        ["unknown HI typed in EN"] = new(() => _hi.Skip(TrainHi).Where(w => w.Length >= 4), Lang.En, Lang.Hi, true, All4, 79),
        ["unknown HI untouched"] = new(() => _hi.Skip(TrainHi).Where(w => w.Length >= 4), Lang.Hi, Lang.Hi, false, All4, 99.5),
        ["EN/HI: HI untouched"] = new(() => _hi.Take(15000), Lang.Hi, Lang.Hi, false, EnHi, 99.9),
        ["EN/HI: HI typed in EN"] = new(() => _hi.Take(15000), Lang.En, Lang.Hi, true, EnHi, 92.5),
        // code, technical words, typical words
        ["code and names untouched"] = new(() => Code.Split(' '), Lang.En, Lang.En, false, All3, 100),
        ["technical words typed in RU"] = new(() => _tech, Lang.Ru, Lang.En, true, All3, 98),
        ["technical words untouched"] = new(() => _tech, Lang.En, Lang.En, false, All3, 100),
        ["typical RU typed in EN"] = new(() => "привет как дела спасибо хорошо сегодня завтра работа модель память нужно сделать проверить приложение раскладка клавиатура исправить".Split(' '), Lang.En, Lang.Ru, true, All3, 100),
        ["typical UK typed in EN"] = new(() => "привіт як справи дякую добре сьогодні завтра робота модель пам'ять потрібно зробити перевірити програма розкладка клавіатура виправити".Split(' '), Lang.En, Lang.Uk, true, All3, 100),
        ["typical UK typed in RU"] = new(() => "привіт дякую сьогодні пам'ять перевірити розкладка клавіатура виправити їжа є".Split(' '), Lang.Ru, Lang.Uk, true, All3, 85),
        ["typical EN typed in RU"] = new(() => TypicalEn.Split(' '), Lang.Ru, Lang.En, true, All3, 100),
        ["typical HI typed in EN"] = new(() => "नमस्ते धन्यवाद कृपया आप कैसे हैं मैं ठीक हूँ क्या हाल है डॉक्टर कल मिलते".Split(' '), Lang.En, Lang.Hi, true, All4, 85),
        ["typical EN typed in HI"] = new(() => TypicalEn.Split(' '), Lang.Hi, Lang.En, true, All4, 100),
    };

    public static IEnumerable<object[]> ScenarioNames => Scenarios.Keys.Select(k => new object[] { k });

    [DataTestMethod]
    [DynamicData(nameof(ScenarioNames))]
    public void Scenario_ReachesMinimumAccuracy(string name)
    {
        var sc = Scenarios[name];
        int total = 0, ok = 0;
        var bad = new List<string>();
        foreach (var w in sc.Words())
        {
            var keys = Layouts.KeysFor(w, sc.WordLang);
            if (keys == null) continue;
            var typed = Layouts.TextFor(keys, sc.TypedIn);
            if (sc.Switch && typed == w) continue;   // looks the same in this layout - nothing to fix
            total++;
            bool sw = _det.ShouldSwitch(keys, sc.TypedIn, sc.Candidates, out var target, out var conv, out var why);
            bool good = sc.Switch ? sw && conv == w : !sw;
            if (good) ok++;
            else if (bad.Count < 8) bad.Add($"{typed} -> {(sw ? conv + " (" + target + ")" : "untouched")} [{why}]");
        }
        double pct = total == 0 ? 100 : 100.0 * ok / total;
        Console.WriteLine($"{name}: {ok}/{total} = {pct:F2}% (minimum {sc.MinPercent}%)");
        Assert.IsTrue(pct >= sc.MinPercent, $"{pct:F2}% < {sc.MinPercent}%. Examples: {string.Join("; ", bad)}");
    }

    [DataTestMethod]
    [DataRow("z", "En", "Ru", "Ru")] [DataRow("d", "En", "Ru", "Ru")] [DataRow("b", "En", "Ru", "Ru")] [DataRow("c", "En", "Ru", "Ru")]
    [DataRow("r", "En", "Ru", "Ru")] [DataRow("e", "En", "Ru", "Ru")] [DataRow("j", "En", "Ru", "Ru")] [DataRow("f", "En", "Ru", "Ru")]
    [DataRow("z", "En", "Uk", "Uk")] [DataRow("d", "En", "Uk", "Uk")] [DataRow("e", "En", "Uk", "Uk")] [DataRow("j", "En", "Uk", "Uk")]
    [DataRow("f", "En", "Uk", "Uk")] [DataRow("s", "En", "Uk", "Uk")]
    [DataRow("z", "En", "En", null)] [DataRow("d", "En", "En", null)] [DataRow("b", "En", "En", null)]
    [DataRow("z", "En", null, null)] [DataRow("d", "En", null, null)] [DataRow("b", "En", null, null)]
    [DataRow("x", "En", "Ru", null)] [DataRow("a", "En", "Ru", null)] [DataRow("Z", "En", "Ru", "Ru")]
    [DataRow("ш", "Ru", "En", "En")] [DataRow("ф", "Ru", "En", "En")] [DataRow("я", "Ru", "En", null)] [DataRow("ы", "Ru", "Uk", "Uk")]
    public void SingleLetterWord(string ch, string typedIn, string? prev, string? expect)
    {
        var t = Enum.Parse<Lang>(typedIn);
        Lang? p = prev == null ? null : Enum.Parse<Lang>(prev);
        var k = Layouts.KeysFor(ch, t)![0];
        bool r = _det.ShouldSwitchSingle(k, t, p, All3, out var target, out var conv);
        if (expect == null) Assert.IsFalse(r, $"'{ch}' -> {conv} {target}");
        else { Assert.IsTrue(r, $"'{ch}' not switched"); Assert.AreEqual(Enum.Parse<Lang>(expect), target); }
    }

    [DataTestMethod]
    [DataRow("ghbdtn? rfr ltkf&", "En", "Ru", "привет, как дела?")]
    [DataRow("ghbdsn? zr cghfdb&", "En", "Uk", "привіт, як справи?")]
    [DataRow("руддщ цщкдв", "Ru", "En", "hello world")]
    [DataRow("п`ять", "Uk", "Uk", "п`ять")]
    [DataRow("g`znm", "En", "Uk", "п'ять")]
    [DataRow("vcmdls", "En", "Hi", "नमस्ते")]
    [DataRow("नमस्ते", "Hi", "En", "vcmdls")]
    [DataRow("k=h/e", "En", "Hi", "कृपया")]
    public void ConvertText(string text, string from, string to, string expected) =>
        Assert.AreEqual(expected, Layouts.ConvertText(text, Enum.Parse<Lang>(from), Enum.Parse<Lang>(to)));

    [DataTestMethod]
    [DataRow("आप", "Hi", true)] [DataRow("कल", "Hi", true)] [DataRow("है", "Hi", true)]
    [DataRow("my", "En", false)] [DataRow("is", "En", false)] [DataRow("the", "En", false)] [DataRow("to", "En", false)] [DataRow("and", "En", false)]
    public void ShortWordAfterHindi(string word, string lang, bool expectSwitch)
    {
        var k = Layouts.KeysFor(word, Enum.Parse<Lang>(lang))!;
        bool sw = _det.ShouldSwitch(k, Lang.En, All4, out _, out var conv, out var why, preferred: Lang.Hi);
        Assert.AreEqual(expectSwitch, sw && (!expectSwitch || conv == word), $"{Layouts.TextFor(k, Lang.En)} -> {conv} [{why}]");
    }

    [DataTestMethod]
    [DataRow("Ru", null, "Ru")]
    [DataRow("Uk", null, "Uk")]
    [DataRow("Ru", "Uk", "Uk")]
    [DataRow(null, "Ru", "Ru")]
    public void PreferredLayout_SameWordInRussianAndUkrainian(string? priority, string? previous, string expected)
    {
        var det = new LayoutDetector(Head("en.txt", Train), Head("ru.txt", Train), Head("uk.txt", TrainUk), Head("hi.txt", TrainHi));
        det.Priority = priority == null ? null : Enum.Parse<Lang>(priority);
        Lang? pref = previous == null ? null : Enum.Parse<Lang>(previous);
        foreach (var w in new[] { "мама", "вода", "сон", "зима", "сила" })
        {
            var k = Layouts.KeysFor(w, Lang.Ru)!;
            Assert.IsTrue(det.ShouldSwitch(k, Lang.En, All4, out var t, out var conv, out _, preferred: pref), w);
            Assert.AreEqual(w, conv);
            Assert.AreEqual(Enum.Parse<Lang>(expected), t, w);
        }
    }
}
