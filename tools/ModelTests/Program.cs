using System.Diagnostics;
using System.Text;
using KeyboardSwitcher;

// Tests every catalog model on every device of this computer with real requests and writes model_test_report.md.
// Intel: OpenVINO on NPU / GPU / CPU (GGUF-only models: llama.cpp on the Arc GPU and the CPU).
// AMD: Ryzen AI NPU, llama.cpp on the Radeon GPU and the CPU (or OpenVINO on the CPU when Windows Smart App Control
// blocks unsigned programs). NVIDIA: llama.cpp CUDA, if the model fits into the card's free memory.
// Missing engines and models are downloaded (or converted) exactly as the app does it.

Console.OutputEncoding = Encoding.UTF8;
string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
// The 27B model (16.5 GB) only when asked for: --models qwen3.8-27b
var modelIds = (Arg("--models") ?? string.Join(",", AiSetup.Catalog.Where(m => m.GgufBytes < 10_000_000_000).Select(m => m.Id)))
    .Split(',', StringSplitOptions.RemoveEmptyEntries);
var kinds = (Arg("--devices") ?? "NPU,GPU,CPU" + (Nvidia.Present ? ",NVIDIA" : "")).ToUpperInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries);
bool download = !args.Contains("--no-download"), quick = args.Contains("--quick");

AiSetup.RegisterNativeResolver();
L.SetLanguage("en");
var reportPath = Path.Combine(Environment.CurrentDirectory, "model_test_report.md");
var started = DateTime.Now;
var rows = new List<string>();
var details = new StringBuilder();

Console.WriteLine($"Processor: {Hardware.CpuName} ({(Hardware.IsAmdCpu ? "AMD" : "Intel")}), engines: {(AiPlatform.Amd ? "AMD (llama.cpp, Ryzen AI)" : "OpenVINO")}, Smart App Control: {Hardware.SmartAppControlState}");
var devices = AiPlatform.Devices();
Console.WriteLine("Devices: " + (devices == null ? "unknown yet (engine not installed)" : string.Join("; ", devices.Select(d => $"{d.Label} - {d.Name}" + (d.Note != null ? $" ({d.Note})" : "")))));

foreach (var id in modelIds)
{
    var m = AiSetup.FindModelInfo(id);
    if (m.Id != id) { Console.WriteLine($"Unknown model {id}, skipped"); continue; }
    Console.WriteLine($"\n=== {m.Id}");

    if (download)
    {
        try
        {
            // AMD: "NPU" installs both the NPU and the llama.cpp engine when the NPU is usable
            var install = new Settings { AiModel = m.Id, AiDevice = AiPlatform.Amd ? "NPU" : "auto", AiNvidia = kinds.Contains("NVIDIA") };
            var (steps, gb) = AiPlatform.Plan(install);
            if (steps.Count > 0)
            {
                Console.WriteLine($"Installing (~{gb:F1} GB): " + string.Join("; ", steps));
                await AiPlatform.InstallAsync(install, new ConsoleProgress(), CancellationToken.None);
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nInstallation failed: {ex.Message}");
            rows.Add($"| {m.Id} | - | - | - | - | install failed: {Esc(ex.Message)} | - |");
            continue;
        }
    }

    foreach (var kind in kinds)
    {
        // NVIDIA: the CUDA candidate comes first when allowed (and the model fits into the free memory)
        var s = new Settings { AiModel = m.Id, AiDevice = kind == "NVIDIA" ? "auto" : kind, AiNvidia = kind == "NVIDIA" };
        LoadCandidate? cand;
        try { cand = AiPlatform.Candidates(s).FirstOrDefault(); }
        catch (Exception ex) { Console.WriteLine($"{kind}: {ex.Message}"); cand = null; }
        if (cand == null || (kind == "NVIDIA" ? !cand.Device.Contains("CUDA") : KindOf(cand.Device) != kind))
        {
            Console.WriteLine($"{kind}: not available on this computer or not installed - skipped");
            rows.Add($"| {m.Id} | {kind} | - | - | - | not available | - |");
            continue;
        }

        Console.Write($"{cand.Device}: loading... ");
        var sw = Stopwatch.StartNew();
        ILlmBackend backend;
        try { backend = cand.Create(); }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            rows.Add($"| {m.Id} | {cand.Device} | - | 0 | 0 | load failed: {Esc(ex.Message)} | - |");
            continue;
        }
        double load = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{load:F0} s");

        using var ai = new AiCorrector();
        ai.UseBackend(backend, m.Id, cand.Device);
        var results = new List<Result>();
        foreach (var t in Suite.Tasks(quick))
        {
            var tsw = Stopwatch.StartNew();
            string output;
            Status status;
            string note;
            try
            {
                output = t.Run(ai);
                (status, note) = t.Check(output);
            }
            catch (Exception ex) { output = ""; status = Status.Fail; note = ex.Message; }
            var r = new Result(t.Name, status, tsw.Elapsed.TotalSeconds, output, note);
            results.Add(r);
            Console.WriteLine($"  {r.Status,-4} {r.Name,-14} {r.Seconds,5:F1} s  {Short(r.Output, 70)}{(r.Note.Length > 0 ? "  <- " + r.Note : "")}");
        }

        int pass = results.Count(r => r.Status == Status.Pass), warn = results.Count(r => r.Status == Status.Warn), fail = results.Count(r => r.Status == Status.Fail);
        rows.Add($"| {m.Id} | {cand.Device} | {load:F0} | {pass} | {warn} | {fail} | {results.Average(r => r.Seconds):F1} |");
        details.AppendLine($"### {m.Id} on {cand.Device}\n\nLoad: {load:F0} s\n\n| Test | Result | Time, s | Answer | Note |\n|---|---|---|---|---|");
        foreach (var r in results)
            details.AppendLine($"| {r.Name} | {r.Status} | {r.Seconds:F1} | {Esc(Short(r.Output, 160))} | {Esc(r.Note)} |");
        details.AppendLine();
        WriteReport();
    }
}
WriteReport();
Console.WriteLine($"\nReport: {reportPath}");
return 0;

void WriteReport()
{
    var sb = new StringBuilder();
    sb.AppendLine($"# Model test report\n\n{started:yyyy-MM-dd HH:mm} - {DateTime.Now:HH:mm}\n");
    sb.AppendLine($"- Processor: {Hardware.CpuName} ({(Hardware.IsAmdCpu ? "AMD" : "Intel")})");
    sb.AppendLine($"- Engines: {(AiPlatform.Amd ? "AMD (llama.cpp, Ryzen AI)" : "OpenVINO")}; Smart App Control: {(Hardware.SmartAppControlState is { Length: > 0 } st ? st : "unknown")}");
    var dev = AiPlatform.Devices();
    if (dev != null) sb.AppendLine("- Devices: " + string.Join("; ", dev.Select(d => $"{d.Label} - {d.Name}" + (d.Note != null ? $" ({d.Note})" : ""))));
    sb.AppendLine($"- Tests per device: {Suite.Tasks(quick).Count} (PASS - correct, WARN - answered but an expected word is missing, FAIL - wrong script, empty or error)\n");
    sb.AppendLine("| Model | Device | Load, s | Pass | Warn | Fail | Avg per test, s |\n|---|---|---|---|---|---|---|");
    foreach (var r in rows) sb.AppendLine(r);
    sb.AppendLine("\n## Details\n");
    sb.Append(details);
    File.WriteAllText(reportPath, sb.ToString());
}

static string KindOf(string device) => device.Split(' ', '.', '(')[0].ToUpperInvariant();
static string Short(string s, int n) { s = s.Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length > n ? s[..n] + "..." : s; }
static string Esc(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

enum Status { Pass, Warn, Fail }
record Result(string Name, Status Status, double Seconds, string Output, string Note);

sealed class ConsoleProgress : IProgress<string>
{
    private long _last;
    public void Report(string value)
    {
        long now = Environment.TickCount64;
        if (value.Contains('%') && now - _last < 1000) return;
        _last = now;
        var line = value.Length > 110 ? value[..110] : value;
        Console.Write("\r" + line.PadRight(112));
    }
}

/// <summary>The tests run on every model and device.</summary>
static class Suite
{
    public sealed record TestCase(string Name, Func<AiCorrector, string> Run, Func<string, (Status, string)> Check, bool Quick = false);

    private const string RuSentence = "Завтра в 10 утра у нас встреча с клиентом, пожалуйста, не опаздывай.";
    private const string EnSentence = "Tomorrow at 10 am we have a meeting with the client, please don't be late.";
    private const string RuLetter = "Коллеги, напоминаю, что отчёт нужно сдать до пятницы. Кто не успевает, напишите мне сегодня, " +
                                    "обсудим, что можно перенести. Спасибо всем за работу на этой неделе.";

    public static List<TestCase> Tasks(bool quick)
    {
        var all = new List<TestCase>
        {
            new("fix-ru", ai => ai.Fix("я вчира купел новый тилефон, он очень харошый"),
                o => Expect(o, Script.Cyrillic, "вчера", "телефон"), Quick: true),
            new("fix-en", ai => ai.Fix("I recieved you're mesage yesterday, thank you"),
                o => Expect(o, Script.Latin, "received", "message")),
            new("fix-layout", ai => ai.Fix("ghbdtn, rfr ltkf?"),
                o => Expect(o, Script.Cyrillic, "привет")),
            new("ru->en", ai => ai.Translate(RuSentence, "en"), o => Lang(o, "en", "tomorrow"), Quick: true),
            new("ru->uk", ai => ai.Translate(RuSentence, "uk"), o => Lang(o, "uk", "завтра")),
            new("en->ru", ai => ai.Translate(EnSentence, "ru"), o => Lang(o, "ru", "завтра")),
            new("ru->sk", ai => ai.Translate(RuSentence, "sk"), o => Translated(o, Script.Latin, RuSentence, "zajtra")),
            new("ru->es", ai => ai.Translate(RuSentence, "es"), o => Translated(o, Script.Latin, RuSentence, "mañana")),
            new("ru->hi", ai => ai.Translate(RuSentence, "hi"), o => Expect(o, Script.Devanagari), Quick: true),
            new("pair ru/en", ai => ai.Transform(new AssistantTask(AiAction.TranslatePair), "Спасибо за помощь!", "ru", "en", "en"),
                o => Lang(o, "en", "thank")),
            new("polite", ai => ai.Transform(new AssistantTask(AiAction.Polite), "Сделай отчёт до завтра, иначе будут проблемы.", "ru", "en", "en"),
                o => Expect(o, Script.Cyrillic, "отчёт|отчет"), Quick: true),
            new("formal", ai => ai.Transform(new AssistantTask(AiAction.Formal), "Привет, скинь отчёт, плиз", "ru", "en", "en"),
                o => Expect(o, Script.Cyrillic, "отчёт|отчет")),
            new("casual", ai => ai.Transform(new AssistantTask(AiAction.Casual), "Уведомляем Вас о переносе совещания на 15:00.", "ru", "en", "en"),
                o => Expect(o, Script.Cyrillic, "15")),
            new("shorten", ai => ai.Transform(new AssistantTask(AiAction.Shorten), RuLetter, "ru", "en", "en"),
                o => Shorter(o, RuLetter)),
            new("simplify", ai => ai.Transform(new AssistantTask(AiAction.Simplify), "Вследствие изменения регламента процедура согласования документов претерпела существенные модификации.", "ru", "en", "en"),
                o => Expect(o, Script.Cyrillic)),
            new("explain", ai => ai.Transform(new AssistantTask(AiAction.Explain), "git rebase -i HEAD~3", "ru", "en", "en"),
                o => Expect(o, Script.Latin, "commit")),
            new("reply", ai => ai.Transform(new AssistantTask(AiAction.Reply), "Здравствуйте! Сможете ли вы прийти на встречу завтра в 10?", "ru", "en", "en"),
                o => Expect(o, Script.Cyrillic)),
        };
        return quick ? all.Where(t => t.Quick).ToList() : all;
    }

    private enum Script { Latin, Cyrillic, Devanagari }

    private static Script? Dominant(string s)
    {
        int lat = 0, cyr = 0, dev = 0;
        foreach (char c in s)
        {
            if (c is >= '\u0900' and <= '\u097F') dev++;
            else if (Layouts.IsCyrillic(c)) cyr++;
            else if (char.IsLetter(c)) lat++;
        }
        if (lat + cyr + dev == 0) return null;
        return dev >= lat && dev >= cyr ? Script.Devanagari : cyr >= lat ? Script.Cyrillic : Script.Latin;
    }

    /// <summary>Right script, then the expected words ("a|b" - any of them).</summary>
    private static (Status, string) Expect(string o, Script script, params string[] words)
    {
        if (string.IsNullOrWhiteSpace(o)) return (Status.Fail, "empty answer");
        var d = Dominant(o);
        if (d != script) return (Status.Fail, $"expected {script} text, got {d}");
        var missing = words.Where(w => !w.Split('|').Any(x => o.Contains(x, StringComparison.OrdinalIgnoreCase))).ToList();
        return missing.Count == 0 ? (Status.Pass, "") : (Status.Warn, "missing: " + string.Join(", ", missing));
    }

    private static (Status, string) Lang(string o, string code, string word)
    {
        var script = code switch { "ru" or "uk" => Script.Cyrillic, "hi" => Script.Devanagari, _ => Script.Latin };
        var (st, note) = Expect(o, script, word);
        if (st == Status.Fail) return (st, note);
        if (code is "uk" && Languages.Detect(o) != "uk") return (Status.Warn, "looks like Russian, not Ukrainian");
        if (code is "ru" && Languages.Detect(o) == "uk") return (Status.Warn, "looks like Ukrainian, not Russian");
        return (st, note);
    }

    private static (Status, string) Translated(string o, Script script, string source, string word)
    {
        var (st, note) = Expect(o, script, word);
        if (st == Status.Fail) return (st, note);
        if (AiCorrector.LooksTransliterated(source, o)) return (Status.Fail, "transliteration, not a translation");
        return (st, note);
    }

    private static (Status, string) Shorter(string o, string source)
    {
        var (st, note) = Expect(o, Script.Cyrillic);
        if (st == Status.Fail) return (st, note);
        return o.Length < source.Length ? (Status.Pass, "") : (Status.Warn, $"not shorter ({o.Length} >= {source.Length})");
    }
}
