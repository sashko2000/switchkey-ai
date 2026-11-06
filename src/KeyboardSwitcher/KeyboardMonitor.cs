using System.Runtime.InteropServices;
using static KeyboardSwitcher.Native;

namespace KeyboardSwitcher;

/// <summary>
/// Watches the keyboard on a separate thread and fixes words typed in the wrong layout.
/// Typed text is never stored or sent anywhere: only the current word is kept in memory.
/// </summary>
internal sealed class KeyboardMonitor : IDisposable
{
    private sealed record LastWord(List<KeyStroke> Keys, Lang TypedIn, Lang Shown, int SeparatorVk, bool Auto);

    private readonly LayoutDetector _detector;
    private readonly object _sync = new();
    private readonly List<KeyStroke> _word = new();
    private IntPtr _wordHwnd;
    private LastWord? _last;
    private LastWord? _prev;         // the word before the current one (if separated by a single space) - for single-letter words
    private Lang? _prevLang;         // the language of the last word in this window
    private Lang? _lastCyrillic;     // which Cyrillic layout was used last (Russian/Ukrainian)

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _kbHook, _mouseHook;
    private HookProc? _kbProc, _mouseProc;   // keep references so the garbage collector doesn't collect the delegates

    private bool _overflow;          // word longer than 64 keys - leave it alone until the next separator
    private bool _caps;              // Caps Lock state (tracked ourselves)
    private IntPtr _exclHwnd;        // cache: window and whether it is excluded
    private bool _exclValue;
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public volatile bool AutoSwitch = true;

    /// <summary>Our hotkeys (MOD_* modifiers, key): pressing them doesn't reset the word.</summary>
    public volatile HashSet<(uint mods, int vk)> Hotkeys = new();

    /// <summary>Raised when the user undoes an auto-correction of a word (to remember it).</summary>
    public event Action<string>? WordRejected;

    /// <summary>Is the focus in a code editor (no auto-correction there).</summary>
    public readonly CodeEditorProbe Editors = new();

    public KeyboardMonitor(LayoutDetector detector) => _detector = detector;

    public void SetExcludedApps(IEnumerable<string> apps)
    {
        lock (_sync) { _excluded = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase); _exclHwnd = IntPtr.Zero; }
    }

    public void Start()
    {
        // Warm-up (JIT and dictionaries) so that the first word in the hook is processed quickly
        _detector.ShouldSwitch(Layouts.KeysFor("ghbdtn", Lang.En)!, Lang.En, Layouts.AllLangs, out _, out _, out _);
        Input.InstalledLangs();
        _caps = (GetKeyState(VK_CAPITAL) & 1) != 0;

        var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() =>
        {
            try
            {
                _threadId = GetCurrentThreadId();
                _kbProc = KeyboardProc;
                _mouseProc = MouseProc;
                var hMod = GetModuleHandle(null);
                _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
                if (_kbHook == IntPtr.Zero) throw new InvalidOperationException("SetWindowsHookEx: error " + Marshal.GetLastWin32Error());
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
            }
            catch (Exception ex) { error = ex; }
            ready.Set();
            if (error != null) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            UnhookWindowsHookEx(_kbHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        })
        { IsBackground = true, Name = "KeyboardHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait();
        if (error != null) throw error;
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        Editors.Dispose();
    }

    // ------------------------------------------------------------------ hook

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int m = (int)wParam;
            if (m is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
                lock (_sync) ResetAll();
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // Leave injected keystrokes (ours and others') alone
                if ((data.flags & LLKHF_INJECTED) == 0)
                {
                    try
                    {
                        if (OnKeyDown((int)data.vkCode)) return (IntPtr)1; // the key was "swallowed"
                    }
                    catch (Exception ex) { Log.Write("Error in the keyboard handler: " + ex.Message); }
                }
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private static bool IsModifier(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LSHIFT or VK_RSHIFT
        or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN or VK_CAPITAL;

    /// <returns>true - the key must be swallowed (we send it ourselves after the correction).</returns>
    private bool OnKeyDown(int vk)
    {
        if (vk == VK_CAPITAL) { _caps = !_caps; return false; }
        if (IsModifier(vk)) return false;

        var fg = GetForegroundWindow();
        lock (_sync)
        {
            if (fg != _wordHwnd) { ResetAll(); _wordHwnd = fg; }

            // Ctrl/Alt/Win combinations are commands, not text
            uint mods = (IsDown(VK_CONTROL) ? MOD_CONTROL : 0) | (IsDown(VK_MENU) ? MOD_ALT : 0)
                      | (IsDown(VK_LWIN) || IsDown(VK_RWIN) ? MOD_WIN : 0);
            if (mods != 0 || Hotkeys.Contains((0, vk)))
            {
                if (mods != 0) mods |= IsDown(VK_SHIFT) ? MOD_SHIFT : 0;
                if (!Hotkeys.Contains((mods, vk)) && !Hotkeys.Contains((0, vk))) ResetAll();
                return false;
            }

            if (Layouts.IsWordKey(vk, Input.InstalledLangs().Contains(Lang.Hi)))
            {
                if (_word.Count == 0)
                {
                    Editors.Check(fg);   // the answer comes from a background thread before the word ends
                    _prev = _last?.SeparatorVk == VK_SPACE ? _last : null;
                    _last = null;
                }
                if (_overflow) return false;
                if (_word.Count >= 64) { _word.Clear(); _overflow = true; return false; }
                _word.Add(new KeyStroke(vk, IsDown(VK_SHIFT), _caps));
                return false;
            }

            if (vk == VK_BACK)
            {
                if (_overflow) return false;
                if (_word.Count > 0) _word.RemoveAt(_word.Count - 1); else { _last = null; _prev = null; }
                return false;
            }

            if (vk is VK_SPACE or VK_RETURN or VK_TAB)
            {
                if (_overflow || _word.Count == 0) { _overflow = false; _word.Clear(); _last = null; _prev = null; return false; }
                var keys = new List<KeyStroke>(_word);
                _word.Clear();
                var prev = _prev;
                _prev = null;

                var lang = Input.CurrentLang();
                if (lang == null) { _last = null; _prevLang = null; return false; }
                var cands = Input.InstalledLangs();
                bool allowed = AutoSwitch && !IsExcluded(fg) && !Editors.InCodeEditor && cands.Contains(lang.Value);

                // Single letter ("z" -> "я"): only if the previous word was in another language
                if (keys.Count == 1)
                {
                    if (allowed && _detector.ShouldSwitchSingle(keys[0], lang.Value, _prevLang, cands, out var singleLang, out var single))
                    {
                        // Switch the layout and type the same keys again (inserting Unicode characters in Notepad
                        // with a Cyrillic layout produced "аааа" instead of "мама")
                        Input.SwitchAndRetype(singleLang, 1, keys, single, vk);
                        _last = vk == VK_SPACE ? new LastWord(keys, lang.Value, singleLang, vk, Auto: true) : null;
                        _prevLang = singleLang;
                        return true;
                    }
                    _last = vk == VK_SPACE ? new LastWord(keys, lang.Value, lang.Value, vk, Auto: false) : null;
                    _prevLang = lang.Value;
                    return false;
                }

                if (allowed && _detector.ShouldSwitch(keys, lang.Value, cands, out var other, out var converted, out _,
                        preferred: _prevLang != null && _prevLang != lang.Value ? _prevLang : _detector.Priority ?? _lastCyrillic))
                {
                    if (other != Lang.En) _lastCyrillic = other;
                    int backspaces = keys.Count;
                    string text = converted;
                    var retype = keys;
                    // "z ghbdtn" -> "я привет": fix the single-letter word before it as well
                    if (prev != null && prev.Keys.Count == 1 && prev.Shown == lang.Value && !prev.Auto
                        && LayoutDetector.IsSingleLetterWord(prev.Keys[0], other) && !LayoutDetector.IsSingleLetterWord(prev.Keys[0], lang.Value)
                        && !_detector.IsNeverSwitch(Layouts.TextFor(prev.Keys, lang.Value)))
                    {
                        backspaces += 2;
                        text = Layouts.CharFor(prev.Keys[0], other) + " " + converted;
                        retype = new List<KeyStroke> { prev.Keys[0], new KeyStroke(VK_SPACE, false, false) };
                        retype.AddRange(keys);
                    }
                    // The separator (Space/Enter) is swallowed and re-sent after the corrected word
                    // Switch the layout and type the same keys again (inserting Unicode characters in Notepad
                    // with a Cyrillic layout produced "аааа" instead of "мама")
                    Input.SwitchAndRetype(other, backspaces, retype, text, vk);
                    // Undo is possible only after Space: after Enter/Tab the message is already sent or focus has moved
                    _last = vk == VK_SPACE ? new LastWord(keys, lang.Value, other, vk, Auto: true) : null;
                    _prevLang = other;
                    return true;
                }
                _last = vk == VK_SPACE ? new LastWord(keys, lang.Value, lang.Value, vk, Auto: false) : null;
                _prevLang = lang.Value;
                if (lang.Value != Lang.En) _lastCyrillic = lang.Value;
                return false;
            }

            // Arrows, Home, Delete, digits etc. - the word has ended
            ResetAll();
            return false;
        }
    }

    private void ResetAll()
    {
        _word.Clear();
        _last = null;
        _prev = null;
        _prevLang = null;
        _overflow = false;
    }


    private bool IsExcluded(IntPtr hwnd)
    {
        if (_excluded.Count == 0) return false;
        if (hwnd == _exclHwnd) return _exclValue;
        GetWindowThreadProcessId(hwnd, out uint pid);
        _exclHwnd = hwnd;
        _exclValue = _excluded.Contains(ProcessFileName(pid));
        return _exclValue;
    }

    // ------------------------------------------------------------------ commands (from the main thread)

    /// <summary>
    /// Convert the current or just typed word to another layout.
    /// If the word was auto-corrected - undo the correction.
    /// </summary>
    /// <returns>false - nothing to convert (then the selected text can be converted).</returns>
    public bool ConvertLastWord()
    {
        IntPtr fg = GetForegroundWindow();
        int backspaces, sep;
        string text;
        List<KeyStroke> keys;
        Lang target;
        string? rejected = null;

        lock (_sync)
        {
            if (fg != _wordHwnd) return false;
            if (_word.Count > 0)
            {
                var lang = Input.CurrentLang() ?? Lang.En;
                target = _detector.BestTarget(_word, lang, Input.InstalledLangs());
                backspaces = _word.Count;
                text = Layouts.TextFor(_word, target);
                keys = new List<KeyStroke>(_word);
                sep = 0;
                // the word stays the same set of keys and the layout changes - everything stays consistent
            }
            else if (_last != null)
            {
                // Undo of auto-correction - back to the original layout, otherwise - to the most plausible other one
                target = _last.Auto ? _last.TypedIn : _detector.BestTarget(_last.Keys, _last.Shown, Input.InstalledLangs());
                backspaces = _last.Keys.Count + (_last.SeparatorVk != 0 ? 1 : 0);
                text = Layouts.TextFor(_last.Keys, target);
                keys = new List<KeyStroke>(_last.Keys);
                sep = _last.SeparatorVk;
                if (_last.Auto)
                {
                    rejected = Layouts.TextFor(_last.Keys, _last.TypedIn);
                    _detector.AddNeverSwitch(rejected);
                }
                _last = _last with { Shown = target, Auto = false };
            }
            else return false;
        }

        Input.WaitModifiersReleased();
        // Switch the layout and type the same keys again (inserting Unicode characters in Notepad
        // with a Cyrillic layout produced "аааа" instead of "мама")
        Input.SwitchAndRetype(target, backspaces, keys, text, sep);

        if (rejected != null) WordRejected?.Invoke(rejected);
        return true;
    }

    /// <summary>Which layout to convert the selected text to: pick the most plausible one by dictionaries.</summary>
    public Lang BestTextTarget(string text, Lang from)
    {
        var cands = Input.InstalledLangs().Where(l => l != from).ToList();
        if (cands.Count == 0) return from == Lang.En ? Lang.Ru : Lang.En;
        if (cands.Count == 1) return cands[0];
        var best = cands[0];
        double bestScore = double.MinValue;
        lock (_sync)
        {
            foreach (var to in cands)
            {
                double score = 0;
                foreach (var w in Layouts.ConvertText(text, from, to).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    var core = LayoutDetector.Core(w, to);
                    if (core.Length == 0) continue;
                    int r = _detector.Rank(core, to);
                    score += r > 0 ? 3 : _detector.Score(core, to);
                }
                if (to == _lastCyrillic) score += 0.5;
                if (score > bestScore) { bestScore = score; best = to; }
            }
            // Same result in several layouts - the preferred one
            if (_detector.Priority is Lang p && p != best && cands.Contains(p)
                && Layouts.ConvertText(text, from, p) == Layouts.ConvertText(text, from, best)) best = p;
        }
        return best;
    }

    /// <summary>Preferred layout: "en", "ru", "uk", "hi" or empty.</summary>
    public void SetPriority(string code)
    {
        Lang? l = code switch { "en" => Lang.En, "ru" => Lang.Ru, "uk" => Lang.Uk, "hi" => Lang.Hi, _ => null };
        lock (_sync) _detector.Priority = l;
    }

    public void SetNeverSwitch(IEnumerable<string> words)
    {
        lock (_sync) _detector.SetNeverSwitch(words);
    }

    /// <summary>Fix the layout of individual words in a text (for selected text before the model).</summary>
    public string FixLayoutInText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int j = i;
            while (j < text.Length && !char.IsWhiteSpace(text[j])) j++;
            if (j > i)
            {
                var token = text[i..j];
                var lang = Layouts.GuessLang(token);
                var keys = Layouts.KeysFor(token, lang);
                string? conv = null;
                lock (_sync)
                {
                    if (keys != null && _detector.ShouldSwitch(keys, lang, Layouts.AllLangs, out _, out var c, out _, preferred: _detector.Priority ?? _lastCyrillic)) conv = c;
                }
                sb.Append(conv ?? token);
            }
            while (j < text.Length && char.IsWhiteSpace(text[j])) sb.Append(text[j++]);
            i = j;
        }
        return sb.ToString();
    }

    public void ResetWord()
    {
        lock (_sync) ResetAll();
    }
}
