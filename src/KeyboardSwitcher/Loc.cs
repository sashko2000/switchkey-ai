using System.Globalization;
using System.Reflection;

namespace KeyboardSwitcher;

/// <summary>Language: for the interface and for translation.</summary>
public sealed record LangInfo(string Code, string NativeName, string PromptName);

public static class Languages
{
    /// <summary>Supported languages (interface and translation). Order = column order in Strings.tsv.</summary>
    public static readonly IReadOnlyList<LangInfo> All = new[]
    {
        new LangInfo("ru", "Русский", "Russian"),
        new LangInfo("en", "English", "English"),
        new LangInfo("uk", "Українська", "Ukrainian"),
        new LangInfo("sk", "Slovenčina", "Slovak"),
        new LangInfo("es", "Español", "Spanish"),
        new LangInfo("hi", "हिन्दी", "Hindi (in Devanagari script)"),
    };

    public static LangInfo Get(string code) => All.FirstOrDefault(l => l.Code == code) ?? All[0];
    public static bool IsKnown(string code) => All.Any(l => l.Code == code);

    /// <summary>Default interface language: the Windows language if supported, otherwise English.</summary>
    public static string DefaultUi()
    {
        var c = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return IsKnown(c) ? c : "en";
    }

    private static readonly HashSet<string> EsWords = new(StringComparer.OrdinalIgnoreCase)
        { "el", "la", "los", "las", "de", "del", "que", "y", "en", "por", "para", "con", "una", "un", "es", "está", "pero", "muy", "mi", "tu", "hola", "gracias", "como", "qué", "no", "sí" };
    private static readonly HashSet<string> SkWords = new(StringComparer.OrdinalIgnoreCase)
        { "a", "je", "sa", "na", "v", "to", "že", "som", "by", "ako", "sme", "pre", "do", "so", "aj", "ale", "nie", "áno", "ďakujem", "ahoj", "mám", "si", "ste", "sú", "tak", "už" };
    private static readonly HashSet<string> EnWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "is", "are", "to", "of", "you", "for", "it", "with", "this", "that", "i", "we", "be", "on", "have", "hello", "thanks", "please", "not", "can", "will", "my" };

    /// <summary>Detect the text language (roughly: by alphabet, special letters and common words).</summary>
    public static string Detect(string text)
    {
        int cyr = 0, lat = 0, dev = 0, uk = 0, ru = 0, sk = 0, es = 0;
        foreach (char c in text)
        {
            if (c is >= 'ऀ' and <= 'ॿ') dev++;
            else if (Layouts.IsCyrillic(c))
            {
                cyr++;
                if ("іїєґІЇЄҐ".IndexOf(c) >= 0) uk++;
                if ("ыэъёЫЭЪЁ".IndexOf(c) >= 0) ru++;
            }
            else if (char.IsLetter(c))
            {
                lat++;
                if ("ľĺŕôäčďňšťžýĽĹŔÔÄČĎŇŠŤŽÝ".IndexOf(c) >= 0) sk++;
                if ("ñ¿¡Ñ".IndexOf(c) >= 0) es += 3;
            }
            else if (c is '¿' or '¡') es += 3;
        }
        if (dev > cyr && dev > lat) return "hi";
        if (cyr >= lat) return uk > ru ? "uk" : "ru";

        int en = 0;
        foreach (var w in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = w.Trim(',', '.', '!', '?', ';', ':', '"', '\'', '(', ')', '¿', '¡');
            if (EsWords.Contains(t)) es++;
            if (SkWords.Contains(t)) sk++;
            if (EnWords.Contains(t)) en++;
        }
        if (sk > es && sk > en) return "sk";
        if (es > sk && es > en) return "es";
        return "en";
    }

    /// <summary>Translation target for "translate between A and B": A to B, B to A, anything else to A.</summary>
    public static string PairTarget(string text, string a, string b)
    {
        var src = Detect(text);
        // Russian and Ukrainian can't be told apart without special letters: if only one of them is in the pair, it's that one
        static bool Cyr(string c) => c is "ru" or "uk";
        if (Cyr(src) && Cyr(a) != Cyr(b)) src = Cyr(a) ? a : b;
        if (src == a) return b;
        return a;
    }
}

/// <summary>Interface strings in the selected language (Resources/Strings.tsv).</summary>
public static class L
{
    private static Dictionary<string, string[]>? _table;
    private static int _col;

    public static string Lang { get; private set; } = "ru";

    public static void SetLanguage(string code)
    {
        Lang = Languages.IsKnown(code) ? code : "en";
        _col = Languages.All.ToList().FindIndex(l => l.Code == Lang);
    }

    private static Dictionary<string, string[]> Table()
    {
        if (_table != null) return _table;
        var t = new Dictionary<string, string[]>();
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("KeyboardSwitcher.Resources.Strings.tsv");
        if (s != null)
        {
            using var r = new StreamReader(s);
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                t[parts[0]] = parts.Skip(1).Select(p => p.Replace("\\n", "\n").Replace("\\t", "\t")).ToArray();
            }
        }
        return _table = t;
    }

    /// <summary>Text by key (with {0}, {1}... substitution). No translation - English; none either - Russian.</summary>
    public static string T(string key, params object?[] args)
    {
        string text = key;
        if (Table().TryGetValue(key, out var row))
        {
            string? Pick(int i) => i >= 0 && i < row.Length && row[i].Length > 0 ? row[i] : null;
            text = Pick(_col) ?? Pick(1) ?? Pick(0) ?? key;
        }
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    /// <summary>Language name in the current interface language.</summary>
    public static string LangName(string code) => T("lang." + code);

    /// <summary>All keys (to check that each one has translations).</summary>
    public static IReadOnlyDictionary<string, string[]> AllStrings => Table();
}
