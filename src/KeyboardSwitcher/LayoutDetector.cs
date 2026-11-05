using System.Reflection;

namespace KeyboardSwitcher;

/// <summary>
/// Decides whether a word was typed in the wrong layout (English, Russian, Ukrainian, Hindi).
/// Runs on the CPU in a fraction of a millisecond: frequency dictionaries + a letter-combination model (trigrams).
/// </summary>
public sealed class LayoutDetector
{
    private readonly Dictionary<string, int>[] _rank = new Dictionary<string, int>[4];
    private readonly TrigramModel[] _model = new TrigramModel[4];
    private readonly HashSet<string> _never = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Threshold for unknown words: how much more plausible (on average per letter) the other layout must be.</summary>
    public double TrigramThreshold { get; set; } = 1.3;

    /// <summary>Preferred layout: if a word looks the same in several layouts ("мама"), pick this one.</summary>
    public Lang? Priority { get; set; }

    /// <summary>Among layouts where the text looks the same, pick the preferred one (preferred first, then Priority).</summary>
    private static Lang PickAmongSame(Lang best, IEnumerable<Lang> same, Lang? preferred, Lang? priority)
    {
        var list = same.ToList();
        if (preferred is Lang p && list.Contains(p)) return p;
        if (priority is Lang q && list.Contains(q)) return q;
        return best;
    }

    /// <summary>Rank given to words from the technical terms list (cmd, git, npm...) if they are not in the dictionary.</summary>
    public const int TechRank = 3000;

    public LayoutDetector(TextReader en, TextReader ru, TextReader uk, TextReader? hi = null, TextReader? tech = null)
    {
        _rank[(int)Lang.Hi] = LoadRanks(hi ?? new StringReader(""), out var hiWords);
        _model[(int)Lang.Hi] = new TrigramModel(hiWords, 64);
        _rank[(int)Lang.En] = LoadRanks(en, out var enWords);
        _rank[(int)Lang.Ru] = LoadRanks(ru, out var ruWords);
        _rank[(int)Lang.Uk] = LoadRanks(uk, out var ukWords);
        _model[(int)Lang.En] = new TrigramModel(enWords, 28);
        _model[(int)Lang.Ru] = new TrigramModel(ruWords, 35);
        _model[(int)Lang.Uk] = new TrigramModel(ukWords, 36);
        // The English dictionary is built from subtitles - it lacks cmd, npm, exe etc.: add the list of terms
        if (tech != null)
        {
            string? line;
            while ((line = tech.ReadLine()) != null)
            {
                var w = line.Trim().ToLowerInvariant();
                // Terms that are rare in subtitles (git is word #12811) are raised to TechRank
                if (w.Length > 0 && (!_rank[(int)Lang.En].TryGetValue(w, out int r) || r > TechRank)) _rank[(int)Lang.En][w] = TechRank;
            }
        }
    }

    public static LayoutDetector FromEmbeddedResources()
    {
        var asm = Assembly.GetExecutingAssembly();
        StreamReader Res(string n) => new(asm.GetManifestResourceStream("KeyboardSwitcher.Resources." + n)!);
        using var en = Res("en.txt");
        using var ru = Res("ru.txt");
        using var uk = Res("uk.txt");
        using var hi = Res("hi.txt");
        using var tech = Res("tech.txt");
        return new LayoutDetector(en, ru, uk, hi, tech);
    }

    public void SetNeverSwitch(IEnumerable<string> words)
    {
        _never.Clear();
        foreach (var w in words) _never.Add(w);
    }

    public void AddNeverSwitch(string word) => _never.Add(word);
    public bool IsNeverSwitch(string word) => _never.Contains(word);

    private static Dictionary<string, int> LoadRanks(TextReader r, out List<(string w, long c)> words)
    {
        var d = new Dictionary<string, int>(60000);
        words = new List<(string, long)>(60000);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var w = line[..sp];
            long.TryParse(line.AsSpan(sp + 1), out long c);
            if (d.TryAdd(w, d.Count + 1)) words.Add((w, c));
        }
        return d;
    }

    private static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е').Replace('ʼ', '\'').Replace('’', '\'');

    /// <summary>Strips characters at the word edges (anything that isn't a letter of this layout).</summary>
    public static string Core(string s, Lang lang)
    {
        int a = 0, b = s.Length;
        while (a < b && !Layouts.IsLetter(s[a], lang)) a++;
        while (b > a && !Layouts.IsLetter(s[b - 1], lang)) b--;
        return s[a..b];
    }

    public int Rank(string word, Lang lang)
    {
        var w = Normalize(word);
        if (_rank[(int)lang].TryGetValue(w, out int r)) return r;
        // The Ukrainian dictionary has no words with an apostrophe (п'ять) - check without it
        if (lang == Lang.Uk && w.Contains('\'') && _rank[(int)lang].TryGetValue(w.Replace("'", ""), out r)) return r;
        return 0;
    }

    public double Score(string word, Lang lang) => _model[(int)lang].Score(Normalize(word).Replace("'", ""));

    // ------------------------------------------------------------------ single-letter words

    private static string SingleWords(Lang l) => l switch { Lang.Ru => "явиксуоажб", Lang.Uk => "івузаоя", Lang.Hi => "", _ => "ai" };

    /// <summary>Is this letter in this layout a word by itself ("я", "в", "і", "a", "I").</summary>
    public static bool IsSingleLetterWord(KeyStroke k, Lang lang) =>
        SingleWords(lang).IndexOf(char.ToLowerInvariant(Layouts.CharFor(k, lang))) >= 0;

    /// <summary>
    /// Single letter: switch only to the language of the previous word, if it is a word there and not in the current layout
    /// (otherwise "x", "c" in formulas and code would break).
    /// </summary>
    public bool ShouldSwitchSingle(KeyStroke k, Lang current, Lang? previousWordLang, IReadOnlyCollection<Lang> candidates,
        out Lang target, out string converted)
    {
        target = current;
        converted = "";
        if (previousWordLang is not Lang prev || prev == current || !candidates.Contains(prev)) return false;
        converted = Layouts.CharFor(k, prev).ToString();
        string typed = Layouts.CharFor(k, current).ToString();
        if (converted == typed || _never.Contains(converted) || _never.Contains(typed)) return false;
        if (!IsSingleLetterWord(k, prev) || IsSingleLetterWord(k, current)) return false;
        target = prev;
        return true;
    }

    // ------------------------------------------------------------------ words

    /// <summary>Whether a word typed in the current layout should be switched to one of the candidate layouts.</summary>
    /// <param name="preferred">The language being written now (by previous words): wins when options are nearly equal.</param>
    public bool ShouldSwitch(IReadOnlyList<KeyStroke> keys, Lang current, IReadOnlyCollection<Lang> candidates,
        out Lang target, out string converted, out string reason, Lang? preferred = null)
    {
        target = current;
        converted = "";
        reason = "no-candidates";
        double best = double.MinValue;
        bool found = false;
        var reasons = new List<string>();
        var ok = new List<(Lang lang, string conv)>();
        foreach (var other in candidates)
        {
            if (other == current) continue;
            if (Evaluate(keys, current, other, out var conv, out var why, out double strength, other == preferred))
            {
                if (other == preferred) strength += 5; // a small bonus for the language currently being written
                ok.Add((other, conv));
                if (strength > best) { best = strength; target = other; converted = conv; found = true; }
            }
            reasons.Add($"{other}:{why}");
        }
        reason = string.Join(" ", reasons);
        if (found)
        {
            var conv0 = converted;
            // The same text comes out in another suitable layout (Russian/Ukrainian "мама") - take the preferred one
            target = PickAmongSame(target, ok.Where(o => o.conv == conv0).Select(o => o.lang), preferred, Priority);
        }
        return found;
    }

    /// <summary>Compatibility: compare only two layouts.</summary>
    public bool ShouldSwitch(IReadOnlyList<KeyStroke> keys, Lang current, out string converted, out string reason)
    {
        var other = current == Lang.En ? Lang.Ru : Lang.En;
        return ShouldSwitch(keys, current, new[] { current, other }, out _, out converted, out reason);
    }

    /// <summary>For manual conversion (hotkey): the most plausible other layout.</summary>
    public Lang BestTarget(IReadOnlyList<KeyStroke> keys, Lang shown, IReadOnlyCollection<Lang> candidates)
    {
        string typed = Layouts.TextFor(keys, shown);
        Lang best = shown;
        double bestScore = double.MinValue;
        foreach (var l in candidates)
        {
            if (l == shown) continue;
            var text = Layouts.TextFor(keys, l);
            if (text == typed) continue;
            var core = Core(text, l);
            int r = core.Length > 0 ? Rank(core, l) : 0;
            double score = r > 0 ? 100 - Math.Log(r) : (core.Length > 0 ? Score(core, l) : -100);
            if (score > bestScore) { bestScore = score; best = l; }
        }
        if (best == shown) best = candidates.FirstOrDefault(l => l != shown, shown == Lang.En ? Lang.Ru : Lang.En);
        var bestText = Layouts.TextFor(keys, best);
        return PickAmongSame(best, candidates.Where(l => l != shown && Layouts.TextFor(keys, l) == bestText), null, Priority);
    }

    /// <summary>Compare the current layout with one other. strength - how confident (to choose the best).</summary>
    private bool Evaluate(IReadOnlyList<KeyStroke> keys, Lang current, Lang other, out string converted, out string reason, out double strength,
        bool otherIsPreferred = false)
    {
        strength = 0;
        converted = "";
        if (keys.Count < 2) { reason = "short"; return false; }

        string typed = Layouts.TextFor(keys, current);
        string alt = Layouts.TextFor(keys, other);
        converted = alt;
        if (alt == typed) { reason = "same"; return false; }   // Russian and Ukrainian often give the same result

        string coreTyped = Core(typed, current);
        string coreAlt = Core(alt, other);
        if (coreAlt.Length < 2) { reason = "alt-short"; return false; }
        if (_never.Contains(typed) || _never.Contains(alt)) { reason = "never"; return false; }

        // If the word starts with a letter but the other layout has a symbol there (",the") - not it
        if (Layouts.IsLetter(typed[0], current) && !Layouts.IsLetter(alt[0], other)) { reason = "alt-leading-punct"; return false; }

        // Letters in the other layout must cover the whole word except symbols at the edges
        foreach (char c in coreAlt)
            if (!Layouts.IsLetter(c, other) && c != '-' && c != '\'') { reason = "alt-nonletter"; return false; }

        // Russian never has "ё" before a vowel: "памёять" is the Ukrainian "пам'ять"
        if (other == Lang.Ru && RuImpossible(coreAlt)) { reason = "ru-impossible"; return false; }
        // In Hindi a word doesn't start with a vowel sign, and two vowel signs in a row don't occur
        if (other == Lang.Hi && Layouts.HiImpossible(coreAlt)) { reason = "hi-impossible"; return false; }

        // Punctuation inside a word (e.g. "ghbdtn;rf") is a strong sign of a layout mistake
        bool punctInside = coreTyped.Length == 0 || HasInnerNonLetter(coreTyped, current)
            || (!Layouts.IsLetter(typed[0], current) && Layouts.IsLetter(alt[0], other))
            || (current == Lang.Ru && RuImpossible(coreTyped))
            || (current == Lang.Hi && Layouts.HiImpossible(coreTyped));

        // Short words in CAPITALS are more likely abbreviations (API, VS, ОК)
        bool allCaps = coreTyped.Length > 0 && coreTyped.All(c => !char.IsLetter(c) || char.IsUpper(c));
        if (allCaps && coreAlt.Length <= 3) { reason = "acronym"; return false; }

        // In Hindi Shift gives different letters: a capital in the middle of a word ("kaR", "meL") means it was typed in another layout
        if (other == Lang.Hi && !allCaps && coreTyped.Skip(1).Any(char.IsUpper)) punctInside = true;
        // And vice versa: a Hindi word in another layout gives capitals mid-word ("pleA") - nobody writes like that
        if (current == Lang.Hi && coreAlt.Skip(1).Any(char.IsUpper) && !coreAlt.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        { reason = "alt-odd-case"; return false; }
        bool hindi = current == Lang.Hi || other == Lang.Hi;

        int typedRank = coreTyped.Length > 0 ? Rank(coreTyped, current) : 0;
        int altRank = Rank(coreAlt, other);

        // Typed in the Russian layout something impossible in Russian, while in Ukrainian it is a normal word
        if (current == Lang.Ru && other == Lang.Uk && RuImpossible(coreTyped) && typedRank == 0)
        {
            strength = altRank > 0 ? 1000 - 50 * Math.Log(altRank) : 500;
            reason = "ru-impossible-typed";
            return true;
        }

        if (altRank > 0)
        {
            strength = 1000 - 50 * Math.Log(altRank);
            int limit = coreAlt.Length switch { 2 => 300, 3 => 5000, _ => int.MaxValue };
            if (altRank > limit && !punctInside) { reason = $"alt-rare({altRank})"; return false; }
            if (typedRank > 0 && !punctInside)
            {
                // Both words are known: switch only short ones where the other word is very frequent ("lf" -> "да")
                // (not for Hindi: short two-character words there coincide with English ones too often)
                if (!hindi && coreAlt.Length == 2 && alt.Length == 2 && altRank <= 150 && typedRank > 2000)
                { reason = $"alt-much-more-frequent({altRank}<{typedRank})"; return true; }
                // The previous word was Hindi, and this word is much more frequent in Hindi ("Eh" -> "आप")
                if (hindi && otherIsPreferred && altRank <= 500 && altRank * 20 < typedRank)
                { reason = $"alt-preferred({altRank}<{typedRank})"; return true; }
                reason = $"both-known({typedRank},{altRank})";
                return false;
            }
            // The Hindi dictionary is small: don't replace an unknown but plausible Hindi word with a rare word
            if (current == Lang.Hi && !punctInside && coreTyped.Length > 0)
            {
                double sTyped = Score(coreTyped, current), sAlt = Score(coreAlt, other);
                if (altRank > 20000 || (altRank > 1000 && sTyped >= sAlt)) { reason = $"hi-plausible({altRank},{sTyped:F2} vs {sAlt:F2})"; return false; }
            }
            reason = $"alt-known({altRank})";
            return true;
        }

        if (typedRank > 0 && !punctInside) { reason = $"typed-known({typedRank})"; return false; }

        // Both words are unknown: compare the plausibility of letter combinations
        if (coreAlt.Length >= 5 || (punctInside && coreAlt.Length >= 3))
        {
            double sTyped = coreTyped.Length > 0 ? Score(coreTyped, current) : -10;
            double sAlt = Score(coreAlt, other);
            double diff = sAlt - sTyped;
            double threshold = punctInside ? TrigramThreshold / 2 : TrigramThreshold;
            strength = diff * 10;
            reason = $"trigram({sTyped:F2} vs {sAlt:F2})";
            return diff > threshold;
        }
        reason = "unknown-short";
        return false;
    }

    private static bool RuImpossible(string s)
    {
        for (int i = 0; i + 1 < s.Length; i++)
        {
            char a = char.ToLowerInvariant(s[i]), b = char.ToLowerInvariant(s[i + 1]);
            if (a == 'ё' && "яюеиёэ".IndexOf(b) >= 0) return true;   // "памёять" = "пам'ять"
            if (a == 'ы' && "аоуяюэ".IndexOf(b) >= 0) return true;   // "клавыатура" = "клавіатура"
        }
        return false;
    }

    private static bool HasInnerNonLetter(string core, Lang lang)
    {
        for (int i = 1; i < core.Length - 1; i++)
            if (!Layouts.IsLetter(core[i], lang) && core[i] != '-' && core[i] != '\'') return true;
        return false;
    }
}

/// <summary>Three-letter combination model built from a frequency dictionary.</summary>
internal sealed class TrigramModel
{
    private readonly Dictionary<long, double> _tri = new();
    private readonly Dictionary<long, double> _bi = new();
    private readonly double _alphabet;

    private const char Start = '^', End = '$';

    public TrigramModel(List<(string w, long c)> words, int alphabetSize)
    {
        _alphabet = alphabetSize;
        foreach (var (w0, c) in words)
        {
            var w = w0.Replace("'", "");
            double weight = Math.Log(2 + c);
            var s = $"{Start}{Start}{w}{End}";
            for (int i = 2; i < s.Length; i++)
            {
                Inc(_tri, Key(s[i - 2], s[i - 1], s[i]), weight);
                Inc(_bi, Key(s[i - 2], s[i - 1], '\0'), weight);
            }
        }
    }

    private static long Key(char a, char b, char c) => ((long)a << 32) | ((long)b << 16) | c;

    private static void Inc(Dictionary<long, double> d, long k, double v) =>
        d[k] = d.TryGetValue(k, out var old) ? old + v : v;

    /// <summary>Average log probability per character (higher means more plausible).</summary>
    public double Score(string word)
    {
        var s = $"{Start}{Start}{word}{End}";
        double sum = 0;
        int n = 0;
        for (int i = 2; i < s.Length; i++)
        {
            _tri.TryGetValue(Key(s[i - 2], s[i - 1], s[i]), out double t);
            _bi.TryGetValue(Key(s[i - 2], s[i - 1], '\0'), out double b);
            sum += Math.Log((t + 0.1) / (b + 0.1 * _alphabet));
            n++;
        }
        return n == 0 ? -10 : sum / n;
    }
}
