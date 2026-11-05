using KeyboardSwitcher;

// Checks layout detection on dictionary words.
// dotnet run -c Release [-- -v]   (-v - show error examples for all checks)

string res = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KeyboardSwitcher", "Resources");
List<string> Words(string f) => File.ReadAllLines(Path.Combine(res, f)).Select(l => l.Split(' ')[0]).ToList();
var enAll = Words("en.txt");
var ruAll = Words("ru.txt");
var ukAll = Words("uk.txt");
var hiAll = Words("hi.txt");
bool verbose = args.Contains("-v");

// Detector trained on the first 40,000 words (Ukrainian - 28,000); the rest are "unknown" for testing trigrams
const int Train = 40000, TrainUk = 28000, TrainHi = 15000;
StringReader Head(string f, int n) => new(string.Join('\n', File.ReadAllLines(Path.Combine(res, f)).Take(n)));
var det = new LayoutDetector(Head("en.txt", Train), Head("ru.txt", Train), Head("uk.txt", TrainUk), Head("hi.txt", TrainHi), new StringReader(File.ReadAllText(Path.Combine(res, "tech.txt"))));

Lang[] all3 = { Lang.En, Lang.Ru, Lang.Uk };
Lang[] enRu = { Lang.En, Lang.Ru };
Lang[] all4 = { Lang.En, Lang.Ru, Lang.Uk, Lang.Hi };
Lang[] enHi = { Lang.En, Lang.Hi };

void Run(string title, IEnumerable<string> words, Lang typedIn, Lang wordLang, bool expectSwitch, Lang[] cands, int showMax = 12)
{
    int total = 0, ok = 0; var bad = new List<string>();
    foreach (var w in words)
    {
        var keys = Layouts.KeysFor(w, wordLang);
        if (keys == null) continue;
        var typed = Layouts.TextFor(keys, typedIn);
        if (expectSwitch && typed == w) continue; // the word looks the same in this layout - nothing to fix
        total++;
        bool sw = det.ShouldSwitch(keys, typedIn, cands, out var target, out var conv, out var why);
        bool good = expectSwitch ? sw && conv == w : !sw;
        if (good) ok++;
        else if (bad.Count < showMax) bad.Add($"{typed} -> {(sw ? conv + " (" + target + ")" : "не тронуто")} [{why}]");
    }
    double pct = total == 0 ? 100 : 100.0 * ok / total;
    Console.WriteLine($"{title,-62} {ok,6}/{total,-6} {pct,6:F2}%");
    if (verbose || pct < 97) foreach (var b in bad) Console.WriteLine("      " + b);
}

Console.WriteLine("--- Три раскладки (английская, русская, украинская) ---");
Run("RU в RU - не трогать", ruAll.Take(20000), Lang.Ru, Lang.Ru, false, all3);
Run("EN в EN - не трогать", enAll.Take(20000), Lang.En, Lang.En, false, all3);
Run("UK в UK - не трогать", ukAll.Take(20000), Lang.Uk, Lang.Uk, false, all3);
Run("RU, набранные в EN - исправить на RU", ruAll.Take(20000), Lang.En, Lang.Ru, true, all3);
Run("UK, набранные в EN - исправить на UK", ukAll.Take(20000), Lang.En, Lang.Uk, true, all3);
Run("EN, набранные в RU - исправить на EN", enAll.Take(20000), Lang.Ru, Lang.En, true, all3);
Run("EN, набранные в UK - исправить на EN", enAll.Take(20000), Lang.Uk, Lang.En, true, all3);
Run("UK, набранные в RU (есть і/ї/є/') - исправить на UK", ukAll.Take(20000), Lang.Ru, Lang.Uk, true, all3);
Run("RU, набранные в UK (есть ы/э/ъ/ё) - исправить на RU", ruAll.Take(20000), Lang.Uk, Lang.Ru, true, all3);
Run("Незнакомые RU в EN (len>=5) - исправить", ruAll.Skip(Train).Where(w => w.Length >= 5), Lang.En, Lang.Ru, true, all3);
Run("Незнакомые UK в EN (len>=5) - исправить", ukAll.Skip(TrainUk).Where(w => w.Length >= 5), Lang.En, Lang.Uk, true, all3);
Run("Незнакомые RU в RU (len>=5) - не трогать", ruAll.Skip(Train).Where(w => w.Length >= 5), Lang.Ru, Lang.Ru, false, all3);
Run("Незнакомые UK в UK (len>=5) - не трогать", ukAll.Skip(TrainUk).Where(w => w.Length >= 5), Lang.Uk, Lang.Uk, false, all3);
Run("Незнакомые EN в EN (len>=5) - не трогать", enAll.Skip(Train).Where(w => w.Length >= 5), Lang.En, Lang.En, false, all3);

Console.WriteLine("--- Две раскладки (английская, русская) ---");
Run("RU в RU - не трогать", ruAll.Take(20000), Lang.Ru, Lang.Ru, false, enRu);
Run("EN в EN - не трогать", enAll.Take(20000), Lang.En, Lang.En, false, enRu);
Run("RU, набранные в EN - исправить", ruAll.Take(20000), Lang.En, Lang.Ru, true, enRu);
Run("EN, набранные в RU - исправить", enAll.Take(20000), Lang.Ru, Lang.En, true, enRu);

Console.WriteLine("--- Хинди (InScript) вместе с английской, русской, украинской ---");
Run("HI в HI - не трогать", hiAll.Take(15000), Lang.Hi, Lang.Hi, false, all4);
Run("EN в EN - не трогать", enAll.Take(20000), Lang.En, Lang.En, false, all4);
Run("RU в RU - не трогать", ruAll.Take(20000), Lang.Ru, Lang.Ru, false, all4);
Run("UK в UK - не трогать", ukAll.Take(20000), Lang.Uk, Lang.Uk, false, all4);
Run("HI, набранные в EN - исправить на HI", hiAll.Take(15000), Lang.En, Lang.Hi, true, all4);
Run("HI, набранные в RU - исправить на HI", hiAll.Take(15000), Lang.Ru, Lang.Hi, true, all4);
Run("EN, набранные в HI - исправить на EN", enAll.Take(20000), Lang.Hi, Lang.En, true, all4);
Run("RU, набранные в HI - исправить на RU", ruAll.Take(20000), Lang.Hi, Lang.Ru, true, all4);
Run("RU, набранные в EN - исправить на RU", ruAll.Take(20000), Lang.En, Lang.Ru, true, all4);
Run("EN, набранные в RU - исправить на EN", enAll.Take(20000), Lang.Ru, Lang.En, true, all4);
Run("Незнакомые HI в EN - исправить", hiAll.Skip(TrainHi).Where(w => w.Length >= 4), Lang.En, Lang.Hi, true, all4);
Run("Незнакомые HI в HI - не трогать", hiAll.Skip(TrainHi).Where(w => w.Length >= 4), Lang.Hi, Lang.Hi, false, all4);
Run("Незнакомые EN в EN - не трогать", enAll.Skip(Train).Where(w => w.Length >= 5), Lang.En, Lang.En, false, all4);
Run("Код и имена в EN - не трогать (с хинди)", "ctx len int var src dst cfg tmp api sql xml yaml json http async await const func args kwargs params todo utils dotnet csproj github ovms qwen openvino llama gguf vulkan npm git dto foo bar baz lhs rhs idx ptr buf str obj msg req resp ctor dtor enum impl init repo diff merge rebase stash grep sed awk curl wget sudo chmod mkdir kubectl docker nginx redis postgres".Split(' '), Lang.En, Lang.En, false, all4, 50);
Run("Типичные HI в EN - исправить", "नमस्ते धन्यवाद कृपया आप कैसे हैं मैं ठीक हूँ क्या हाल है डॉक्टर कल मिलते".Split(' '), Lang.En, Lang.Hi, true, all4, 50);
Run("Типичные EN в HI - исправить", "hello thanks please meeting tomorrow today build release deploy server client model memory layout keyboard switch".Split(' '), Lang.Hi, Lang.En, true, all4, 50);
Run("HI в HI (две раскладки) - не трогать", hiAll.Take(15000), Lang.Hi, Lang.Hi, false, enHi);
Run("HI, набранные в EN (две раскладки) - исправить", hiAll.Take(15000), Lang.En, Lang.Hi, true, enHi);

// After a Hindi word, short words that coincide with English ones ("Eh" = "आप") are corrected
int okP = 0, totP = 0;
foreach (var (w, l, expect) in new[] { ("आप", Lang.Hi, true), ("कल", Lang.Hi, true), ("है", Lang.Hi, true),
    ("my", Lang.En, false), ("is", Lang.En, false), ("the", Lang.En, false), ("to", Lang.En, false), ("and", Lang.En, false) })
{
    var k = Layouts.KeysFor(w, l)!;
    bool sw = det.ShouldSwitch(k, Lang.En, all4, out var t, out var cv, out var why, preferred: Lang.Hi);
    bool good = expect ? sw && cv == w : !sw;
    totP++; if (good) okP++; else Console.WriteLine($"      {Layouts.TextFor(k, Lang.En)} после хинди -> {(sw ? cv : "не тронуто")} [{why}]");
}
Console.WriteLine($"{"Короткие слова после хинди",-62} {okP,6}/{totP,-6} {100.0 * okP / totP,6:F2}%");

// Preferred layout: "vfvf" -> "мама" is the same in Russian and Ukrainian
int okR = 0, totR = 0;
foreach (var (prio, pref, expect) in new (Lang?, Lang?, Lang)[] { (Lang.Ru, null, Lang.Ru), (Lang.Uk, null, Lang.Uk), (Lang.Ru, Lang.Uk, Lang.Uk), (null, Lang.Ru, Lang.Ru) })
{
    det.Priority = prio;
    foreach (var w in new[] { "мама", "вода", "сон", "зима", "сила" })
    {
        var k = Layouts.KeysFor(w, Lang.Ru)!;
        bool sw = det.ShouldSwitch(k, Lang.En, all4, out var t, out var cv, out _, preferred: pref);
        totR++; if (sw && t == expect && cv == w) okR++; else Console.WriteLine($"      {w}: приоритет {prio}, пред. {pref} -> {(sw ? cv + " " + t : "не тронуто")}, ожидалось {expect}");
    }
    var k2 = Layouts.KeysFor("мама", Lang.Ru)!;
    totR++; if (det.BestTarget(k2, Lang.En, all4) == (prio ?? Lang.Ru) || prio == null) okR++; else Console.WriteLine($"      BestTarget при приоритете {prio}");
}
det.Priority = null;
Console.WriteLine($"{"Приоритетная раскладка",-62} {okR,6}/{totR,-6} {100.0 * okR / totR,6:F2}%");

Console.WriteLine("--- Код, имена, типичные фразы ---");
var techAll = File.ReadAllLines(Path.Combine(res, "tech.txt")).Where(w => w.Length >= 3).ToList();
Run("Термины (cmd, git, npm...) в RU - исправить на EN", techAll, Lang.Ru, Lang.En, true, all3, 40);
Run("Термины в EN - не трогать", techAll, Lang.En, Lang.En, false, all3, 40);
var code = "ctx len int var src dst cfg tmp api sql xml yaml json http async await const func args kwargs params todo utils dotnet csproj github ovms qwen openvino llama gguf vulkan bat ps1 npm git dto foo bar baz lhs rhs idx ptr buf str obj msg req resp ctor dtor enum impl init repo diff merge rebase stash grep sed awk curl wget sudo chmod mkdir kubectl docker nginx redis postgres mongo kafka grpc proto webpack vite eslint pytest numpy pandas torch nvidia intel ryzen descartes oleksandr postoliuk VS API URL SQL NPU GPU CPU RAM SSD USB HDMI LLM".Split(' ');
Run("Код и имена в EN - не трогать", code, Lang.En, Lang.En, false, all3, 50);
Run("Типичные RU в EN - исправить", "привет как дела спасибо хорошо сегодня завтра работа модель память нужно сделать проверить приложение раскладка клавиатура исправить".Split(' '), Lang.En, Lang.Ru, true, all3, 50);
Run("Типичные UK в EN - исправить", "привіт як справи дякую добре сьогодні завтра робота модель пам'ять потрібно зробити перевірити програма розкладка клавіатура виправити".Split(' '), Lang.En, Lang.Uk, true, all3, 50);
Run("Типичные UK в RU - исправить", "привіт дякую сьогодні пам'ять перевірити розкладка клавіатура виправити їжа є".Split(' '), Lang.Ru, Lang.Uk, true, all3, 50);
Run("Типичные EN в RU - исправить", "hello thanks please meeting tomorrow today build release deploy server client model memory layout keyboard switch".Split(' '), Lang.Ru, Lang.En, true, all3, 50);

// Single-letter words
int okS = 0, totS = 0;
void Single(string ch, Lang typedIn, Lang? prev, Lang? expectTarget)
{
    var k = Layouts.KeysFor(ch, typedIn)![0];
    bool r = det.ShouldSwitchSingle(k, typedIn, prev, all3, out var target, out var conv);
    bool good = expectTarget == null ? !r : r && target == expectTarget;
    totS++; if (good) okS++; else Console.WriteLine($"      одна буква '{ch}' в {typedIn} (пред.: {prev?.ToString() ?? "нет"}) -> {(r ? conv + " " + target : "нет")}, ожидалось {expectTarget?.ToString() ?? "не трогать"}");
}
foreach (var c in "zdbcrejf") Single(c.ToString(), Lang.En, Lang.Ru, Lang.Ru);   // я в и с к у о а after a Russian word
foreach (var c in "zdejfs") Single(c.ToString(), Lang.En, Lang.Uk, Lang.Uk);    // я в у о а і after a Ukrainian word
foreach (var c in "zdb") Single(c.ToString(), Lang.En, Lang.En, null);           // after an English word - leave alone
foreach (var c in "zdb") Single(c.ToString(), Lang.En, null, null);              // no context - leave alone
Single("x", Lang.En, Lang.Ru, null);
Single("a", Lang.En, Lang.Ru, null);
Single("Z", Lang.En, Lang.Ru, Lang.Ru);
Single("ш", Lang.Ru, Lang.En, Lang.En);
Single("ф", Lang.Ru, Lang.En, Lang.En);
Single("я", Lang.Ru, Lang.En, null);
Single("ы", Lang.Ru, Lang.Uk, Lang.Uk);   // ы -> і after a Ukrainian word
Console.WriteLine($"{"Однобуквенные слова",-62} {okS,6}/{totS,-6} {100.0 * okS / totS,6:F2}%");

// Converting selected text between layouts
string Conv(string t, Lang f, Lang to) => Layouts.ConvertText(t, f, to);
var checks = new (string from, Lang f, Lang to, string expect)[]
{
    ("ghbdtn? rfr ltkf&", Lang.En, Lang.Ru, "привет, как дела?"),
    ("ghbdsn? zr cghfdb&", Lang.En, Lang.Uk, "привіт, як справи?"),
    ("руддщ цщкдв", Lang.Ru, Lang.En, "hello world"),
    ("п`ять", Lang.Uk, Lang.Uk, "п`ять"),
    ("g`znm", Lang.En, Lang.Uk, "п'ять"),
    ("vcmdls", Lang.En, Lang.Hi, "नमस्ते"),
    ("नमस्ते", Lang.Hi, Lang.En, "vcmdls"),
    ("k=h/e", Lang.En, Lang.Hi, "कृपया"),
};
int okC = 0;
foreach (var (from, f, to, expect) in checks)
{
    var got = Conv(from, f, to);
    if (got == expect) okC++; else Console.WriteLine($"      {from} ({f}->{to}) = {got}, ожидалось {expect}");
}
Console.WriteLine($"{"Перевод текста между раскладками",-62} {okC,6}/{checks.Length,-6} {100.0 * okC / checks.Length,6:F2}%");
