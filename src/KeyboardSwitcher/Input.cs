using System.Runtime.InteropServices;
using static KeyboardSwitcher.Native;

namespace KeyboardSwitcher;

/// <summary>Keystroke simulation, layout switching and the clipboard.</summary>
internal static class Input
{
    /// <summary>The app window - clipboard owner.</summary>
    public static IntPtr OwnerWindow;

    /// <summary>Marker for our own keystrokes so the hook skips them.</summary>
    public static readonly IntPtr Marker = (IntPtr)0x4B53_5701;

    private static INPUT Key(int vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = Marker }
    };

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = Marker }
    };

    private static void Send(List<INPUT> list)
    {
        if (list.Count == 0) return;
        var arr = list.ToArray();
        uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent != arr.Length) Log.Write($"SendInput: sent {sent} of {arr.Length}, error {Marshal.GetLastWin32Error()}");
    }

    /// <summary>Erase <paramref name="backspaces"/> characters, type the text and (if set) press the separator.</summary>
    public static void Replace(int backspaces, string text, int separatorVk)
    {
        var list = new List<INPUT>(backspaces * 2 + text.Length * 2 + 2);
        for (int i = 0; i < backspaces; i++) { list.Add(Key(VK_BACK, false)); list.Add(Key(VK_BACK, true)); }
        foreach (char c in text) { list.Add(Unicode(c, false)); list.Add(Unicode(c, true)); }
        if (separatorVk != 0) { list.Add(Key(separatorVk, false)); list.Add(Key(separatorVk, true)); }
        Send(list);
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    private static INPUT ScanKey(int vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)MapVirtualKey((uint)vk, 0), dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = Marker }
    };

    /// <summary>
    /// Erase the characters and "press" the same keys again (the layout is already switched by then) - as if
    /// the user typed the word again. More reliable than inserting Unicode characters: the new Windows 11 Notepad
    /// with a Cyrillic layout printed the wrong letters instead of the inserted ones ("аааа" instead of "мама").
    /// </summary>
    public static void Retype(int backspaces, IReadOnlyList<KeyStroke> keys, int separatorVk)
    {
        var list = new List<INPUT>(backspaces * 2 + keys.Count * 4 + 2);
        for (int i = 0; i < backspaces; i++) { list.Add(ScanKey(VK_BACK, false)); list.Add(ScanKey(VK_BACK, true)); }
        bool shiftHeld = IsDown(VK_SHIFT);
        foreach (var k in keys)
        {
            bool addShift = k.Shift && !shiftHeld;
            if (addShift) list.Add(ScanKey(VK_LSHIFT, false));
            list.Add(ScanKey(k.Vk, false));
            list.Add(ScanKey(k.Vk, true));
            if (addShift) list.Add(ScanKey(VK_LSHIFT, true));
        }
        if (separatorVk != 0) { list.Add(ScanKey(separatorVk, false)); list.Add(ScanKey(separatorVk, true)); }
        Send(list);
    }

    public static void SendChord(int modifierVk, int vk)
    {
        Send(new List<INPUT> { Key(modifierVk, false), Key(vk, false), Key(vk, true), Key(modifierVk, true) });
    }

    private static readonly int[] Modifiers =
        { VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LSHIFT, VK_RSHIFT, VK_LWIN, VK_RWIN };

    /// <summary>
    /// A hotkey was pressed together with Ctrl/Alt. Wait until the user releases them
    /// (otherwise Backspace becomes Ctrl+Backspace); if not released - "release" them ourselves.
    /// </summary>
    /// <summary>
    /// An unassigned "masking" key. The app sees Alt + this key, so releasing Alt
    /// doesn't open the window menu (otherwise Notepad highlights menu letters and Ctrl+C/V go to the menu).
    /// </summary>
    private const int VK_MASK = 0xE8;

    public static void MaskAltMenu()
    {
        if (!(IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN))) return;
        var list = new List<INPUT> { Key(VK_MASK, false), Key(VK_MASK, true) };
        // New Windows 11 apps (Notepad, Paint) ignore an unassigned key and still
        // show key tips (KeyTips) when Alt is released. They do see Ctrl - so we "press" it too.
        if (IsDown(VK_LCONTROL)) { list.Add(Key(VK_LCONTROL, true)); list.Add(Key(VK_LCONTROL, false)); }
        else if (IsDown(VK_RCONTROL)) { list.Add(Key(VK_RCONTROL, true)); list.Add(Key(VK_RCONTROL, false)); }
        else { list.Add(Key(VK_LCONTROL, false)); list.Add(Key(VK_LCONTROL, true)); }
        Send(list);
    }

    // ---------------- Copy/paste via messages (no keystrokes) ----------------

    private const uint WM_COPY = 0x0301, WM_PASTE = 0x0302, SMTO_ABORTIFHUNG = 0x2;
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private static IntPtr _copiedFrom;   // window copied from via WM_COPY

    /// <summary>
    /// Standard edit controls (including Notepad's) understand WM_COPY/WM_PASTE directly -
    /// so held Ctrl/Alt and key tips don't interfere. Other apps ignore them - then Ctrl+C/V.
    /// </summary>
    private static bool TryCopyByMessage(uint seqBefore)
    {
        var focus = FocusWindow();
        if (focus == IntPtr.Zero) return false;
        SendMessageTimeout(focus, WM_COPY, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 300, out _);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 200 && GetClipboardSequenceNumber() == seqBefore) Thread.Sleep(10);
        if (GetClipboardSequenceNumber() == seqBefore) return false;
        _copiedFrom = focus;
        return true;
    }

    public static void WaitModifiersReleased(int timeoutMs = 800)
    {
        MaskAltMenu();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && Modifiers.Any(IsDown)) Thread.Sleep(10);
        var list = new List<INPUT>();
        foreach (var vk in Modifiers) if (IsDown(vk)) list.Add(Key(vk, true));
        Send(list);
    }

    // ---------------- Layout ----------------

    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int n, IntPtr[]? list);

    /// <summary>Layout of the window with input focus in the active app.</summary>
    public static Lang? CurrentLang()
    {
        uint tid = GetWindowThreadProcessId(FocusWindow(), out _);
        long hkl = GetKeyboardLayout(tid).ToInt64();
        return FromHkl(new IntPtr(hkl));
    }

    private static Lang? FromLangId(int id) => id switch { 0x09 => Lang.En, 0x19 => Lang.Ru, 0x22 => Lang.Uk, 0x39 => Lang.Hi, _ => null };
    private static int LangId(Lang l) => l switch { Lang.En => 0x09, Lang.Ru => 0x19, Lang.Uk => 0x22, _ => 0x39 };

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);
    [DllImport("user32.dll")] private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] char[] buf, int bufSize, uint flags, IntPtr hkl);

    private static readonly Dictionary<long, bool> InscriptCache = new();

    /// <summary>
    /// Is the Hindi layout InScript (the K key gives क)? Phonetic layouts and other variants
    /// work differently - auto-correction is not enabled for them.
    /// </summary>
    private static bool IsInscript(IntPtr hkl)
    {
        lock (InscriptCache)
        {
            if (InscriptCache.TryGetValue(hkl.ToInt64(), out bool v)) return v;
            v = false;
            try
            {
                var buf = new char[8];
                uint scan = MapVirtualKeyEx('K', 0, hkl);
                // flag 4: don't change the keyboard state (Windows 10 1607+)
                int n = ToUnicodeEx('K', scan, new byte[256], buf, buf.Length, 4, hkl);
                v = n >= 1 && buf[0] == 'क';
            }
            catch (Exception e) { Log.Write("Hindi layout check: " + e.Message); }
            Log.Write($"Hindi layout {hkl.ToInt64():X}: " + (v ? "InScript, auto-correction enabled" : "not InScript, auto-correction disabled"));
            InscriptCache[hkl.ToInt64()] = v;
            return v;
        }
    }

    private static Lang? FromHkl(IntPtr hkl)
    {
        var l = FromLangId((int)(hkl.ToInt64() & 0x3FF));
        if (l == Lang.Hi && !IsInscript(hkl)) return null;
        return l;
    }

    private static IntPtr[] LayoutList()
    {
        int n = GetKeyboardLayoutList(0, null);
        var arr = new IntPtr[n];
        GetKeyboardLayoutList(n, arr);
        return arr;
    }

    private static IntPtr FindLayout(Lang lang)
    {
        foreach (var h in LayoutList()) if ((h.ToInt64() & 0x3FF) == LangId(lang) && FromHkl(h) == lang) return h;
        return IntPtr.Zero;
    }

    private static Lang[] _installed = Layouts.AllLangs;
    private static long _installedAt = long.MinValue;

    /// <summary>Layouts known to the app that are installed in Windows (refreshed every 10 s).</summary>
    public static Lang[] InstalledLangs()
    {
        long now = Environment.TickCount64;
        if (now - _installedAt > 10_000)
        {
            var list = LayoutList().Select(FromHkl).Where(l => l != null).Select(l => l!.Value).Distinct().ToArray();
            _installed = list.Length >= 2 ? list : new[] { Lang.En, Lang.Ru };
            _installedAt = now;
        }
        return _installed;
    }

    /// <summary>Switch the layout of the focused window (only to one the user already has installed).</summary>
    /// <returns>false - no such layout or no window found.</returns>
    public static bool SwitchLayout(Lang lang)
    {
        var hkl = FindLayout(lang);
        if (hkl == IntPtr.Zero) return false;
        return PostMessage(FocusWindow(), WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
    }

    /// <summary>
    /// Switch the layout and replace the word: erase <paramref name="backspaces"/> characters and type the keys again.
    /// If switching failed - insert the ready text.
    /// </summary>
    public static void SwitchAndRetype(Lang lang, int backspaces, IReadOnlyList<KeyStroke> keys, string text, int separatorVk)
    {
        // The window processes the layout change request before our keystrokes, so they are typed in the new layout
        if (SwitchLayout(lang)) Retype(backspaces, keys, separatorVk);
        else Replace(backspaces, text, separatorVk);
    }

    // ---------------- Clipboard ----------------

    private static bool Open()
    {
        for (int i = 0; i < 20; i++) { if (OpenClipboard(OwnerWindow)) return true; Thread.Sleep(15); }
        return false;
    }

    public static string? GetClipboardText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !Open()) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static bool SetClipboardText(string text)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (h == IntPtr.Zero) return false;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return false; }
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return false; }
            return true;
        }
        finally { CloseClipboard(); }
    }

    private static int _restoreVersion;

    /// <summary>
    /// Copy the selected text (Ctrl+C). Returns null if nothing is selected (the clipboard is then untouched).
    /// <paramref name="copySeq"/> - clipboard sequence number right after copying.
    /// </summary>
    public static string? CopySelection(out string? previousClipboard, out uint copySeq)
    {
        Interlocked.Increment(ref _restoreVersion); // cancel the pending restore from the previous operation
        previousClipboard = GetClipboardText();
        copySeq = 0;
        _copiedFrom = IntPtr.Zero;
        uint seq = GetClipboardSequenceNumber();
        if (!TryCopyByMessage(seq))
        {
            WaitModifiersReleased();
            SendChord(VK_CONTROL, 'C');
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 800 && GetClipboardSequenceNumber() == seq) Thread.Sleep(15);
            if (GetClipboardSequenceNumber() == seq) return null;
        }
        Thread.Sleep(30);
        copySeq = GetClipboardSequenceNumber();
        var text = GetClipboardText();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Put the previous text back into the clipboard if nobody changed it since <paramref name="seq"/>.</summary>
    public static void RestoreIfUnchanged(string? previous, uint seq)
    {
        if (previous != null && seq != 0 && GetClipboardSequenceNumber() == seq) SetClipboardText(previous);
    }

    /// <summary>Paste text over the selection (Ctrl+V), then restore the previous clipboard contents.</summary>
    public static void PasteText(string text, string? restore)
    {
        if (!SetClipboardText(text)) { Log.Write("Failed to write to the clipboard"); return; }
        uint seq = GetClipboardSequenceNumber();
        var focus = FocusWindow();
        if (_copiedFrom != IntPtr.Zero && focus == _copiedFrom)
        {
            WaitModifiersReleased();
            SendMessageTimeout(focus, WM_PASTE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out _);
        }
        else
        {
            WaitModifiersReleased();
            SendChord(VK_CONTROL, 'V');
        }
        _copiedFrom = IntPtr.Zero;
        if (restore == null) return;
        int version = Interlocked.Increment(ref _restoreVersion);
        var t = new Thread(() =>
        {
            Thread.Sleep(1000); // give the app time to paste the text
            // Restore only if there was no new operation meanwhile and the user didn't copy anything
            if (version == Volatile.Read(ref _restoreVersion) && GetClipboardSequenceNumber() == seq)
                SetClipboardText(restore);
        }) { IsBackground = true };
        t.Start();
    }
}
