using System.Text;

namespace KeyboardSwitcher;

/// <summary>Keyboard layouts the auto-correction understands.</summary>
public enum Lang { En, Ru, Uk, Hi }

/// <summary>A single keystroke from which a character can be obtained in any layout.</summary>
public readonly record struct KeyStroke(int Vk, bool Shift, bool Caps);

/// <summary>
/// Key mapping tables: English (US, QWERTY), Russian (ЙЦУКЕН), Ukrainian and Hindi (InScript, Devanagari) Windows layouts.
/// </summary>
public static class Layouts
{
    public static readonly Lang[] AllLangs = { Lang.En, Lang.Ru, Lang.Uk, Lang.Hi };

    // Key -> characters [layout] = (normal, with Shift)
    private static readonly Dictionary<int, (char lo, char hi)[]> Keys = new();
    // Layout character -> key (with or without Shift)
    private static readonly Dictionary<char, (int vk, bool shift)>[] CharToKey =
        { new(), new(), new(), new() };

    public const int VK_OEM_1 = 0xBA, VK_OEM_PLUS = 0xBB, VK_OEM_COMMA = 0xBC, VK_OEM_MINUS = 0xBD,
        VK_OEM_PERIOD = 0xBE, VK_OEM_2 = 0xBF, VK_OEM_3 = 0xC0, VK_OEM_4 = 0xDB, VK_OEM_5 = 0xDC,
        VK_OEM_6 = 0xDD, VK_OEM_7 = 0xDE;

    static Layouts()
    {
        const string enRow = "qwertyuiopasdfghjklzxcvbnm";
        const string ruRow = "йцукенгшщзфывапролдячсмить";
        const string ukRow = "йцукенгшщзфівапролдячсмить"; // difference: s -> і
        // Hindi (InScript): without Shift - consonants and vowel signs (matras), with Shift - vowels and aspirated consonants
        const string hiLo = "ौैाीूबहगदजोे्िुपरकतॆंमनवलस";
        const string hiHi = "औऐआईऊभङघधझओएअइउफऱखथऎँणऩऴळश";
        for (int i = 0; i < enRow.Length; i++)
            Add(char.ToUpperInvariant(enRow[i]), (enRow[i], char.ToUpperInvariant(enRow[i])),
                (ruRow[i], char.ToUpperInvariant(ruRow[i])), (ukRow[i], char.ToUpperInvariant(ukRow[i])), (hiLo[i], hiHi[i]));

        // Keys that produce letters in Cyrillic layouts
        Add(VK_OEM_4, ('[', '{'), ('х', 'Х'), ('х', 'Х'), ('ड', 'ढ'));
        Add(VK_OEM_6, (']', '}'), ('ъ', 'Ъ'), ('ї', 'Ї'), ('\u093C', 'ञ'));   // nukta
        Add(VK_OEM_1, (';', ':'), ('ж', 'Ж'), ('ж', 'Ж'), ('च', 'छ'));
        Add(VK_OEM_7, ('\'', '"'), ('э', 'Э'), ('є', 'Є'), ('ट', 'ठ'));
        Add(VK_OEM_COMMA, (',', '<'), ('б', 'Б'), ('б', 'Б'), (',', 'ष'));
        Add(VK_OEM_PERIOD, ('.', '>'), ('ю', 'Ю'), ('ю', 'Ю'), ('.', '।'));
        Add(VK_OEM_3, ('`', '~'), ('ё', 'Ё'), ('\'', '₴'), ('ॊ', 'ऒ'));   // in Ukrainian: apostrophe
        Add(VK_OEM_2, ('/', '?'), ('.', ','), ('.', ','), ('य', '\u095F'));
        // These two keys produce letters only in Hindi: = -> ृ (कृपया), \ -> ॉ (डॉक्टर)
        Add(VK_OEM_PLUS, ('=', '+'), ('=', '+'), ('=', '+'), ('ृ', 'ऋ'));
        Add(VK_OEM_5, ('\\', '|'), ('\\', '/'), ('\\', '/'), ('ॉ', 'ऑ'));
    }

    private static readonly HashSet<int> HindiOnlyKeys = new() { VK_OEM_PLUS, VK_OEM_5 };

    private static void Add(int vk, (char, char) en, (char, char) ru, (char, char) uk, (char, char) hi)
    {
        var row = new[] { en, ru, uk, hi };
        Keys[vk] = row;
        for (int l = 0; l < row.Length; l++)
        {
            CharToKey[l].TryAdd(row[l].Item1, (vk, false));
            CharToKey[l].TryAdd(row[l].Item2, (vk, true));
        }
    }

    /// <summary>The key is part of typing a word (a letter in at least one layout).</summary>
    /// <param name="hindi">Whether the Hindi layout is installed: only there the = and \ keys produce letters.</param>
    public static bool IsWordKey(int vk, bool hindi = false) => Keys.ContainsKey(vk) && (hindi || !HindiOnlyKeys.Contains(vk));

    public static char CharFor(KeyStroke ks, Lang lang)
    {
        var (lo, hi) = Keys[ks.Vk][(int)lang];
        // Hindi has no capital letters: Caps Lock has no effect, Shift gives a different letter
        bool letter = lang != Lang.Hi && char.IsLetter(lo);
        bool upper = letter ? ks.Shift ^ ks.Caps : ks.Shift;
        return upper ? hi : lo;
    }

    public static string TextFor(IReadOnlyList<KeyStroke> keys, Lang lang)
    {
        var sb = new StringBuilder(keys.Count);
        foreach (var k in keys) sb.Append(CharFor(k, lang));
        return sb.ToString();
    }

    /// <summary>Which keys to press to type the text in this layout (null if impossible).</summary>
    public static List<KeyStroke>? KeysFor(string text, Lang lang)
    {
        var res = new List<KeyStroke>(text.Length);
        var map = CharToKey[(int)lang];
        foreach (char c in text)
        {
            if (!map.TryGetValue(c, out var k)) return null;
            res.Add(new KeyStroke(k.vk, k.shift, false));
        }
        return res;
    }

    public static bool IsEnLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    public static bool IsRuLetter(char c) => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё';
    public static bool IsUkLetter(char c) =>
        (c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' && "ыэъЫЭЪ".IndexOf(c) < 0) || "іїєґІЇЄҐ".IndexOf(c) >= 0;
    /// <summary>A Devanagari letter, including vowel signs (matras), virama, nukta, anusvara (but not digits or the danda ।).</summary>
    public static bool IsHiLetter(char c) => c is >= '\u0900' and <= '\u0963' || c is >= '\u0971' and <= '\u097F';
    public static bool IsDevanagari(char c) => c is >= '\u0900' and <= '\u097F';
    public static bool IsLetter(char c, Lang lang) => lang switch
    {
        Lang.En => IsEnLetter(c),
        Lang.Ru => IsRuLetter(c),
        Lang.Uk => IsUkLetter(c),
        _ => IsHiLetter(c),
    };

    /// <summary>
    /// A sign that can't start a Hindi word: vowel sign, virama, nukta, anusvara etc.
    /// </summary>
    public static bool IsHiMark(char c) => c is >= '\u0900' and <= '\u0903' || c is >= '\u093A' and <= '\u094F'
        && c != '\u093D' || c is >= '\u0951' and <= '\u0957' || c is '\u0962' or '\u0963';
    /// <summary>A vowel sign or virama (two in a row don't occur in Hindi).</summary>
    private static bool IsHiVowelSign(char c) => c is >= '\u093E' and <= '\u094F' || c is '\u093A' or '\u093B';

    /// <summary>Combinations impossible in Hindi: word starting with a vowel sign, two vowel signs in a row, nukta after a vowel sign.</summary>
    public static bool HiImpossible(string s)
    {
        if (s.Length == 0) return false;
        if (IsHiMark(s[0])) return true;
        for (int i = 0; i + 1 < s.Length; i++)
        {
            char a = s[i], b = s[i + 1];
            if (IsHiVowelSign(a) && (IsHiVowelSign(b) || b == '\u093C')) return true;
            if (a is '\u0901' or '\u0902' && IsHiVowelSign(b)) return true;   // anusvara before a vowel sign
        }
        return false;
    }

    /// <summary>Any Cyrillic (Russian, Ukrainian, Belarusian letters).</summary>
    public static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';

    /// <summary>Which layout the text was most likely typed in: Latin - En, Devanagari - Hi, Cyrillic with і/ї/є/ґ - Uk, otherwise Ru.</summary>
    public static Lang GuessLang(string text)
    {
        int en = 0, cyr = 0, uk = 0, ru = 0, hi = 0;
        foreach (char c in text)
        {
            if (IsEnLetter(c)) en++;
            else if (IsDevanagari(c)) hi++;
            else if (IsCyrillic(c))
            {
                cyr++;
                if ("іїєґІЇЄҐ".IndexOf(c) >= 0) uk++;
                if ("ыэъёЫЭЪЁ".IndexOf(c) >= 0) ru++;
            }
        }
        if (hi > en && hi > cyr) return Lang.Hi;
        if (cyr <= en) return Lang.En;
        return uk > ru ? Lang.Uk : Lang.Ru;
    }

    /// <summary>Convert text to another layout character by character (as if typed on the other keys).</summary>
    public static string ConvertText(string text, Lang from, Lang to)
    {
        var map = CharToKey[(int)from];
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (map.TryGetValue(c, out var k)) sb.Append(CharFor(new KeyStroke(k.vk, k.shift, false), to));
            else sb.Append(ShiftDigit(c, from, to));
        }
        return sb.ToString();
    }

    // Shifted digit row: @#$^&| in English, "№;:?/ in Cyrillic layouts
    private const string EnShiftDigits = "@#$^&|", CyrShiftDigits = "\"№;:?/";
    private static char ShiftDigit(char c, Lang from, Lang to)
    {
        if (from == Lang.Hi || to == Lang.Hi) return c;   // in Hindi these keys produce compound signs - leave them as is
        if ((from == Lang.En) == (to == Lang.En)) return c;
        int i = (from == Lang.En ? EnShiftDigits : CyrShiftDigits).IndexOf(c);
        return i < 0 ? c : (to == Lang.En ? EnShiftDigits : CyrShiftDigits)[i];
    }
}
