using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KeyboardSwitcher;

/// <summary>Assistant actions.</summary>
public enum AiAction { TranslatePair, TranslateTo, Fix, Polite, Formal, Casual, Shorten, Simplify, Explain, Reply }

/// <summary>Assistant task: action and (for translation) language. Id - for storing in settings.</summary>
public readonly record struct AssistantTask(AiAction Action, string? Lang = null)
{
    public string Id => Action == AiAction.TranslateTo ? "to:" + Lang : Action.ToString();

    public static AssistantTask FromId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return new(AiAction.TranslatePair);
        if (id.StartsWith("to:") && Languages.IsKnown(id[3..])) return new(AiAction.TranslateTo, id[3..]);
        return Enum.TryParse<AiAction>(id, out var a) && a != AiAction.TranslateTo ? new(a) : new(AiAction.TranslatePair);
    }

    public string Title(string langA, string langB) => Action switch
    {
        AiAction.TranslatePair => L.T("task.pair", L.LangName(langA), L.LangName(langB)),
        AiAction.TranslateTo => L.T("task.to", L.LangName(Lang ?? "en")),
        _ => L.T("task." + Action.ToString().ToLowerInvariant()),
    };

    /// <summary>List for the assistant window: pair, translation to each language, other actions.</summary>
    public static List<AssistantTask> All()
    {
        var list = new List<AssistantTask> { new(AiAction.TranslatePair) };
        foreach (var l in Languages.All) list.Add(new(AiAction.TranslateTo, l.Code));
        foreach (var a in new[] { AiAction.Fix, AiAction.Polite, AiAction.Formal, AiAction.Casual, AiAction.Shorten,
                     AiAction.Simplify, AiAction.Explain, AiAction.Reply })
            list.Add(new(a));
        return list;
    }
}

/// <summary>Correction, translation and rewriting of text by a small language model on the NPU (or GPU/CPU).</summary>
internal sealed class AiCorrector : IDisposable
{
    public enum State { Off, Loading, Ready, Failed }

    private readonly object _gen = new();
    private ILlmBackend? _backend;
    private int _loadGeneration;   // current load number: stale loads release their model
    public volatile State Status = State.Off;
    public string Device { get; private set; } = "";
    public string Message { get; private set; } = L.T("st.off");
    public string ModelName { get; private set; } = "";

    public event Action? StatusChanged;

    /// <summary>When the model was last used (for unloading from memory when idle).</summary>
    public DateTime LastUsed { get; private set; } = DateTime.Now;

    private const int NpuMinResponseTokens = OvBackend.NpuMinResponseTokens;

    /// <summary>Child process holding the model (local server), for the memory display.</summary>
    public System.Diagnostics.Process? Child => Volatile.Read(ref _backend)?.Child;

    /// <summary>Find the model folder: custom folder from settings, installed catalog model, or the models folder next to the app.</summary>
    public static string? FindModel(Settings s)
    {
        if (!string.IsNullOrWhiteSpace(s.ModelPath))
            return File.Exists(Path.Combine(s.ModelPath, "openvino_model.xml")) ? s.ModelPath : null;
        var info = AiSetup.FindModelInfo(s.AiModel);
        if (AiSetup.ModelInstalled(info)) return AiSetup.ModelDir(info);
        var root = Path.Combine(AppContext.BaseDirectory, "models");
        if (!Directory.Exists(root)) return null;
        var dirs = Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "openvino_model.xml")))
            // models prepared for NPU (symmetric per-channel INT4) take priority
            .OrderByDescending(d => Path.GetFileName(d).Contains("-cw-", StringComparison.OrdinalIgnoreCase)
                                    || Path.GetFileName(d).Contains("npu", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d)
            .ToList();
        return dirs.FirstOrDefault();
    }

    /// <summary>OpenVINO GenAI takes an ANSI path - for paths with Cyrillic we use the 8.3 short name.</summary>
    internal static string SafePath(string path)
    {
        if (path.All(c => c < 128)) return path;
        var buf = new char[1024];
        uint n = Native.GetShortPathName(path, buf, (uint)buf.Length);
        var shortPath = n > 0 && n < buf.Length ? new string(buf, 0, (int)n) : path;
        if (shortPath.Any(c => c >= 128))
            throw new InvalidOperationException(L.T("err.nonlatin_path", path));
        return shortPath;
    }

    /// <param name="candidates">Ways to load the model in order; computed on the background thread (device detection takes time).</param>
    public void LoadAsync(string modelName, Func<List<LoadCandidate>> candidates)
    {
        if (Status == State.Loading) return;
        Unload();
        int myGeneration = Interlocked.Increment(ref _loadGeneration);
        Status = State.Loading;
        ModelName = modelName;
        Message = L.T("st.loading");
        StatusChanged?.Invoke();

        Task.Run(() =>
        {
            var errors = new List<string>();
            List<LoadCandidate> order;
            try { order = candidates(); }
            catch (Exception ex) { Log.Write("Device order: " + ex.Message); order = new(); errors.Add(ex.Message); }
            Log.Write("Trying devices: " + string.Join(", ", order.Select(c => c.Device)));
            if (order.Count == 0 && errors.Count == 0) errors.Add(L.T("st.not_installed"));
            foreach (var c in order)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    Message = L.T("st.loading_on", c.Device);
                    StatusChanged?.Invoke();
                    var backend = c.Create();
                    if (myGeneration != Volatile.Read(ref _loadGeneration))
                    {
                        // The model was disabled or reloaded while loading - this copy is not needed
                        backend.Dispose();
                        return;
                    }
                    Interlocked.Exchange(ref _backend, backend);
                    LastUsed = DateTime.Now;
                    Device = c.Device;
                    Status = State.Ready;
                    Message = L.T("st.ready_on", c.Device, sw.Elapsed.TotalSeconds);
                    Log.Write($"Model {ModelName} loaded on {c.Device} in {sw.Elapsed.TotalSeconds:F1} s");
                    StatusChanged?.Invoke();
                    return;
                }
                catch (DllNotFoundException ex) when (myGeneration == Volatile.Read(ref _loadGeneration))
                {
                    Status = State.Failed;
                    Message = L.T("st.no_libs");
                    Log.Write("OpenVINO libraries not found: " + ex.Message);
                    StatusChanged?.Invoke();
                    return;
                }
                catch (Exception ex)
                {
                    errors.Add($"{c.Device}: {ex.Message}");
                    Log.Write($"Failed to load the model on {c.Device}: {ex.Message}");
                }
            }
            if (myGeneration != Volatile.Read(ref _loadGeneration)) return;
            Status = State.Failed;
            Message = L.T("st.load_failed", string.Join("; ", errors));
            StatusChanged?.Invoke();
        });
    }

    private const string Instruction =
        "Исправь в тексте ниже опечатки, ошибки раскладки клавиатуры (слова, набранные латиницей вместо кириллицы " +
        "или наоборот, например \"ghbdtn\" = \"привет\"), заглавные буквы и пунктуацию. " +
        "Не меняй смысл, стиль, язык и порядок слов. Ничего не добавляй и не объясняй. " +
        "Верни только исправленный текст.\n\nТекст:\n";

    /// <summary>Correct the text. Runs synchronously (call from a background thread).</summary>
    public string Fix(string text)
    {
        // Via chat (like translation). If that fails - a plain request with the instruction inside the text.
        const string system =
            "You are a careful proofreader. Fix typos, spelling, wrong keyboard layout (Latin letters typed instead of " +
            "Cyrillic or vice versa, e.g. \"ghbdtn\" = \"привет\"), capitalization and punctuation in the user's text. " +
            "Do not change the meaning, style, tone, language or word order, even if the text is rude. " +
            "The answer must be in the SAME language as the user's text: English stays English, Russian stays Russian. " +
            "Do not translate, do not add anything, do not comment. Reply with the corrected text only.";
        // Examples in several languages: with only Russian ones, models translated English text into Russian
        var examples = new[]
        {
            ("ghbdtn, rfr ltkf?", "Привет, как дела?"),
            ("I recieved you're mesage, thanks", "I received your message, thanks."),
            ("я вчера купел новый тилефон", "Я вчера купил новый телефон."),
        };
        try { return Chat(system, examples, text); }
        catch (InvalidOperationException ex)
        {
            Log.Write("Correction via chat failed (" + ex.Message + "), falling back to a plain request");
            return Run(Instruction + text + "\n/no_think", text);
        }
    }

    /// <summary>Translate the text to another language (Cyrillic - to English, everything else - to Russian).</summary>
    /// <summary>Translate to the language with the given code (ru, en, uk, sk, es, hi).</summary>
    public string Translate(string text, string targetCode)
    {
        var target = Languages.Get(targetCode);
        string name = target.PromptName;
        // System instruction + examples: otherwise small models often just rewrite the text in Latin letters
        string system =
            $"You are a professional translator into {name}. The user's text may be in any language and may contain " +
            "typos, mixed Russian/Ukrainian letters or words typed in the wrong keyboard layout - understand what was meant. " +
            $"Translate the MEANING into natural {name}. Never transliterate words into another alphabet. " +
            "Keep formatting, line breaks, names, code and links. Reply with the translation only.";
        var hello = targetCode switch
        {
            "en" => "Hi, how are you?",
            "ru" => "Привет, как дела?",
            "uk" => "Привіт, як справи?",
            "sk" => "Ahoj, ako sa máš?",
            "es" => "Hola, ¿cómo estás?",
            "hi" => "नमस्ते, आप कैसे हैं?",
            _ => "Hi, how are you?",
        };
        var examples = new List<(string, string)> { (targetCode == "ru" ? "Hi, how are yuo?" : "Привет, как дила?", hello) };
        if (targetCode == "en") examples.Add(("я вчора бачив кота на вулиці", "I saw a cat on the street yesterday."));
        if (targetCode == "ru") examples.Add(("Thanks for you're help!", "Спасибо за помощь!"));
        // A thank-you phrase is translated, not answered (Qwen3-8B answered "Спасибо за помощь!" with "You're welcome!")
        var thanks = targetCode switch
        {
            "en" => "Thank you very much, see you tomorrow!",
            "ru" => "Большое спасибо, до завтра!",
            "uk" => "Щиро дякую, до завтра!",
            "sk" => "Ďakujem pekne, uvidíme sa zajtra!",
            "es" => "¡Muchas gracias, hasta mañana!",
            "hi" => "बहुत धन्यवाद, कल मिलते हैं!",
            _ => "Thank you very much, see you tomorrow!",
        };
        examples.Add((targetCode == "ru" ? "Thanks a lot, see you tomorrow!" : "Большое спасибо, до завтра!", thanks));
        var ex = examples.ToArray();

        if (targetCode == "sk") system += " Use standard Slovak spelling and words, not Czech.";

        // The text is marked as a task, so the model translates it instead of answering ("Спасибо!" -> "You're welcome!")
        string Task(string t) => $"Translate into {name}:\n" + t;
        for (int i = 0; i < ex.Length; i++) ex[i] = (Task(ex[i].Item1), ex[i].Item2);
        var result = Chat(system, ex, text, userContent: Task(text));
        // The model returned the text untranslated (happens between Russian and Ukrainian) - retry with reasoning
        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (Norm(result) == Norm(text) && Languages.Detect(text) != targetCode && Norm(text).Length > 0)
        {
            Log.Write("The model did not translate the text - retrying with reasoning");
            var again = Chat(system, ex, text, think: true, userContent: Task(text));
            if (!string.IsNullOrWhiteSpace(again)) result = again;
        }
        // Translation to a Latin-script language came out as a transliteration of the Cyrillic text
        bool latinTarget = targetCode is "en" or "sk" or "es";
        if (!latinTarget || !text.Any(Layouts.IsCyrillic) || !LooksTransliterated(text, result)) return result;

        // Attempt 2: fix typos first, then translate the fixed text
        Log.Write("The translation looks like a transliteration - fixing typos and translating again");
        string fixedText = text;
        try { fixedText = Fix(text); } catch (Exception e) { Log.Write("Correction before translation: " + e.Message); }
        if (string.IsNullOrWhiteSpace(fixedText)) fixedText = text;
        result = Chat(system, ex, fixedText, userContent: Task(fixedText));
        if (!LooksTransliterated(fixedText, result) && !LooksTransliterated(text, result)) return result;

        // Attempt 3: let the model think before answering (slower but more accurate)
        Log.Write("Transliteration again - translating with reasoning");
        var thought = Chat(system, ex, fixedText, think: true, userContent: Task(fixedText));
        return string.IsNullOrWhiteSpace(thought) || LooksTransliterated(text, thought) ? result : thought;
    }

    /// <param name="userContent">What is sent as the user's message (default - the text itself).</param>
    private string Chat(string system, (string user, string assistant)[] examples, string text, bool think = false, int? maxTokens = null,
        string? userContent = null)
    {
        var msgs = new List<object> { new { role = "system", content = system } };
        foreach (var (u, a) in examples)
        {
            msgs.Add(new { role = "user", content = u });
            msgs.Add(new { role = "assistant", content = a });
        }
        msgs.Add(new { role = "user", content = userContent ?? text });
        var json = System.Text.Json.JsonSerializer.Serialize(msgs);

        if (Status != State.Ready) throw new InvalidOperationException(L.T("err.not_ready", Message));
        // With reasoning the model writes its thoughts first - give it more room
        int maxNew = think ? NpuMinResponseTokens : maxTokens ?? Math.Clamp(text.Length + 64, 64, NpuMinResponseTokens);
        string raw;
        lock (_gen)
        {
            var be = Volatile.Read(ref _backend) ?? throw new InvalidOperationException(L.T("err.unloaded"));
            LastUsed = DateTime.Now;
            try { raw = be.GenerateChat(json, think, maxNew); }
            catch (InvalidOperationException ex)
            {
                // Some models' templates (Mistral) don't accept the system role - merge everything into one request
                Log.Write("Chat mode failed (" + ex.Message + "), falling back to a plain request");
                var sb = new System.Text.StringBuilder(system).Append("\n\n");
                foreach (var (u, a) in examples) sb.Append("Text: ").Append(System.Text.RegularExpressions.Regex.Replace(u, @"^Translate into [^:\n]+:\n", "")).Append("\nTranslation: ").Append(a).Append("\n\n");
                sb.Append("Text: ").Append(text).Append("\nTranslation:\n/no_think");
                raw = be.GenerateText(sb.ToString(), maxNew);
            }
            LastUsed = DateTime.Now;
        }
        return Clean(raw, text);
    }

    /// <summary>
    /// Answer in a chat. history - the whole conversation (user/assistant turns, the last one is the user's);
    /// older turns are dropped when they don't fit the model's prompt (the NPU takes ~1000 tokens).
    /// </summary>
    public string Converse(IReadOnlyList<ChatMessage> history)
    {
        if (history.Count == 0) throw new ArgumentException("empty chat", nameof(history));
        const string system =
            "You are a helpful assistant that runs locally on the user's computer. Answer in the language of the user's " +
            "last message. Be concise and accurate; if you are not sure, say so. Use plain text, no Markdown tables.";
        // Rough budget in characters: Cyrillic ~2-3 characters per token, the answer needs room too
        int budget = IsNetwork ? 60000
                   : Device.StartsWith("NPU (Ryzen", StringComparison.Ordinal) ? 3000
                   : Device.StartsWith("NPU", StringComparison.Ordinal) ? 1800 : 10000;
        var kept = new List<ChatMessage>();
        int used = system.Length;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            if (kept.Count > 0 && used + m.Content.Length > budget) break;
            kept.Insert(0, m);
            used += m.Content.Length;
        }
        while (kept.Count > 1 && kept[0].Role != "user") kept.RemoveAt(0);   // start with the user's turn

        var msgs = new List<object> { new { role = "system", content = system } };
        foreach (var m in kept) msgs.Add(new { role = m.Role, content = m.Content });
        var json = System.Text.Json.JsonSerializer.Serialize(msgs);
        if (Status != State.Ready) throw new InvalidOperationException(L.T("err.not_ready", Message));
        string raw;
        lock (_gen)
        {
            var be = Volatile.Read(ref _backend) ?? throw new InvalidOperationException(L.T("err.unloaded"));
            LastUsed = DateTime.Now;
            try { raw = be.GenerateChat(json, false, NpuMinResponseTokens); }
            catch (InvalidOperationException ex)
            {
                // A template without the system role: the conversation as plain text
                Log.Write("Chat mode failed (" + ex.Message + "), falling back to a plain request");
                var sb = new System.Text.StringBuilder(system).Append("\n\n");
                foreach (var m in kept) sb.Append(m.Role == "user" ? "User: " : "Assistant: ").Append(m.Content).Append("\n\n");
                sb.Append("Assistant:");
                raw = be.GenerateText(sb.ToString(), NpuMinResponseTokens);
            }
            LastUsed = DateTime.Now;
        }
        if (raw.Contains("<think>") && !raw.Contains("</think>")) return "";
        return Think.Replace(raw, "").Replace("<think>", "").Replace("</think>", "").Trim();
    }

    /// <summary>Run an assistant task. langA/langB - translation pair, uiLang - language for explanations.</summary>
    public string Transform(AssistantTask task, string text, string langA, string langB, string uiLang)
    {
        // Name the language explicitly: "keep the same language" alone was ignored (Mistral answered in English)
        var lang = Languages.Get(Languages.Detect(text)).PromptName;
        string keep = $" The text is in {lang}: write the result in {lang} too (do not translate), keep all facts, names, numbers, " +
                      "code and links. Reply with the rewritten text only, without comments or quotes.";
        var none = Array.Empty<(string, string)>();
        switch (task.Action)
        {
            case AiAction.TranslatePair: return Translate(text, Languages.PairTarget(text, langA, langB));
            case AiAction.TranslateTo: return Translate(text, task.Lang ?? langB);
            case AiAction.Fix: return Fix(text);
            case AiAction.Polite:
                return Chat("Rewrite the user's text to be more polite, friendly and tactful, without changing the meaning." + keep, none, text);
            case AiAction.Formal:
                return Chat("Rewrite the user's text in a formal business style suitable for work e-mail." + keep, none, text);
            case AiAction.Casual:
                return Chat("Rewrite the user's text in a relaxed, friendly conversational style, as in a chat with a colleague." + keep, none, text);
            case AiAction.Shorten:
                return Chat("Make the user's text noticeably shorter and clearer, removing repetitions and filler words, " +
                    "but keep everything important." + keep, none, text);
            case AiAction.Simplify:
                return Chat("Rewrite the user's text in simple, easy-to-understand words with short sentences." + keep, none, text);
            case AiAction.Explain:
                return Chat($"Explain briefly in {Languages.Get(uiLang).PromptName} what the user's text means: the main point, " +
                    "and difficult words or terms. If it is code, explain what it does. Use a few short sentences.",
                    none, text, maxTokens: NpuMinResponseTokens);
            case AiAction.Reply:
                return Chat($"The user received this message. Write a short, polite reply to it in {lang}, the language of the message. " +
                    "Reply with the answer text only.", none, text, maxTokens: NpuMinResponseTokens);
            default:
                throw new ArgumentOutOfRangeException(nameof(task));
        }
    }

    /// <summary>Is the answer the same text in Latin letters (transliteration) rather than a translation?</summary>
    public static bool LooksTransliterated(string source, string result)
    {
        if (result.Any(Layouts.IsCyrillic)) return false;
        string a = Translit(source), b = new string(result.ToLowerInvariant().Where(char.IsLetter).ToArray());
        if (a.Length < 3 || b.Length == 0) return false;
        return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length) >= 0.6;
    }

    private static string Translit(string s)
    {
        const string cyr = "абвгдеёжзийклмнопрстуфхцчшщъыьэюяіїєґ";
        string[] lat = { "a","b","v","g","d","e","e","zh","z","i","i","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","sch","","y","","e","yu","ya","i","i","e","g" };
        var sb = new System.Text.StringBuilder();
        foreach (char c in s.ToLowerInvariant())
        {
            int i = cyr.IndexOf(c);
            if (i >= 0) sb.Append(lat[i]);
            else if (char.IsLetter(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0]; d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }

    private string Run(string prompt, string text)
    {
        if (Status != State.Ready) throw new InvalidOperationException(L.T("err.not_ready", Message));
        // Estimate: Russian text ~ 2-3 characters per token, English ~ 4
        int maxNew = Math.Clamp(text.Length + 64, 64, NpuMinResponseTokens);
        string raw;
        lock (_gen)
        {
            var be = Volatile.Read(ref _backend) ?? throw new InvalidOperationException(L.T("err.unloaded"));
            LastUsed = DateTime.Now;
            raw = be.GenerateText(prompt, maxNew);
            LastUsed = DateTime.Now;
        }
        return Clean(raw, text);
    }

    private static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.Compiled);

    internal static string Clean(string raw, string original)
    {
        // The reasoning didn't fit and was cut off - no answer
        if (raw.Contains("<think>") && !raw.Contains("</think>")) return "";
        var s = Think.Replace(raw, "");
        s = s.Replace("<think>", "").Replace("</think>", "").Trim();
        if (s.StartsWith("```")) s = s.Trim('`').Trim();
        if (s.Length > 1 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '«' && s[^1] == '»'))
            && !(original.TrimStart().StartsWith(s[0]))) s = s[1..^1];
        foreach (var prefix in new[] { "Исправленный текст:", "Перевод:", "Текст:", "Translation:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..].Trim();
        // Keep leading/trailing spaces and line breaks as they were in the selection
        int lead = original.Length - original.TrimStart().Length;
        int trail = original.Length - original.TrimEnd().Length;
        return original[..lead] + s + original[(original.Length - trail)..];
    }

    /// <summary>Use an already loaded backend (tests: a fake backend or a model loaded on a specific device).</summary>
    /// <summary>The loaded model is on a server (OpenAI, Claude, Gemini, Ollama...): long context, no NPU limits.</summary>
    public bool IsNetwork => Volatile.Read(ref _backend) is NetBackend;

    internal void UseBackend(ILlmBackend backend, string modelName, string device)
    {
        Unload(wait: true);
        Interlocked.Exchange(ref _backend, backend);
        ModelName = modelName;
        Device = device;
        Status = State.Ready;
        Message = L.T("st.ready_on", device, 0.0);
    }

    /// <summary>Unload the model. Non-blocking: if a correction is running, the model is released after it.</summary>
    public void Unload() => Unload(wait: false);

    private void Unload(bool wait)
    {
        Interlocked.Increment(ref _loadGeneration);
        var be = Interlocked.Exchange(ref _backend, null);
        if (be != null)
        {
            var t = Task.Run(() =>
            {
                lock (_gen) { try { be.Dispose(); } catch { } }
            });
            if (wait) t.Wait(5000);
        }
        Status = State.Off;
        Message = L.T("st.off");
        StatusChanged?.Invoke();
    }

    public void Dispose() => Unload(wait: true);
}
