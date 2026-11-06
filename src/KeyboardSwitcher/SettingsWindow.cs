using System.Runtime.InteropServices;
using static KeyboardSwitcher.Native;

namespace KeyboardSwitcher;

/// <summary>Settings window (standard Windows controls, no WinForms).</summary>
internal sealed class SettingsWindow
{
    /// <summary>What the window needs from the app.</summary>
    public interface IHost
    {
        Settings Settings { get; }
        bool Autostart { get; }
        string AiStatusText { get; }
        bool InstallRunning { get; }
        /// <returns>false - applying failed (the window stays open).</returns>
        bool Apply(Settings newSettings, bool autostart);
        void SettingsWindowClosed();
    }

    private const int ID_OK = 1, ID_CANCEL = 2, ID_APPLY = 1040,
        ID_AUTO = 1001, ID_AUTOSTART = 1002,
        ID_HK_LAST = 1010, ID_HK_SEL = 1011, ID_HK_AI = 1012, ID_HK_LAST_DEF = 1013, ID_HK_SEL_DEF = 1014, ID_HK_AI_DEF = 1015,
        ID_HK_LAST_OFF = 1016, ID_HK_SEL_OFF = 1017, ID_HK_AI_OFF = 1018,
        ID_APP_COMBO = 1020, ID_APP_ADD = 1021, ID_APP_LIST = 1022, ID_APP_REMOVE = 1023,
        ID_AI = 1030, ID_MODEL = 1031, ID_MODEL_NOTE = 1032, ID_STATUS = 1033,
        ID_HK_TR = 1050, ID_HK_TR_DEF = 1051, ID_HK_TR_OFF = 1052, ID_TR_MODE = 1053, ID_UNLOAD = 1054, ID_ON_DEMAND = 1055,
        ID_UI_LANG = 1060, ID_LANG_A = 1061, ID_LANG_B = 1062, ID_LAYOUT_PRIO = 1063, ID_DEVICE = 1064, ID_DEVICE_INFO = 1065;

    private const string ClassName = "KeyboardSwitcherSettings";
    private static WndProc? _classProc;      // one delegate per window class
    private static SettingsWindow? _current;

    private readonly IHost _host;
    private IntPtr _hwnd, _font;
    private double _scale = 1;
    private readonly Dictionary<int, IntPtr> _ctl = new();
    private static readonly Settings Defaults = new();
    // What was loaded into the hotkey field: if the user didn't change the field, keep the original string
    // (the Windows control can't show Win or bare keys like Pause)
    private readonly Dictionary<int, (string text, int value)> _loadedHotkeys = new();

    public IntPtr Hwnd => _hwnd;

    public SettingsWindow(IHost host) => _host = host;

    // ------------------------------------------------------------------ creation

    /// <returns>false - the window could not be created.</returns>
    public bool Show(IntPtr icon)
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) { ShowWindow(_hwnd, SW_RESTORE); SetForegroundWindow(_hwnd); return true; }

        var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(), dwICC = ICC_HOTKEY_CLASS | ICC_STANDARD_CLASSES };
        InitCommonControlsEx(ref icc);

        var hInst = GetModuleHandle(null);
        if (_classProc == null)
        {
            _classProc = StaticProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_classProc),
                hInstance = hInst,
                hIcon = icon,
                hIconSm = icon,
                hbrBackground = GetSysColorBrush(COLOR_BTNFACE),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
        }

        _scale = GetDpiForSystem() / 96.0;
        _font = CreateFont(-(int)Math.Round(9 * GetDpiForSystem() / 72.0), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

        const uint style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        const uint exStyle = WS_EX_CONTROLPARENT;
        var rc = new RECT { Right = S(840), Bottom = S(546) };
        AdjustWindowRectEx(ref rc, style, false, exStyle);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        int x = (GetSystemMetrics(0) - w) / 2, y = (GetSystemMetrics(1) - h) / 2;

        _current = this;
        // The window is scaled by the system DPI; on a monitor with a different scale Windows adjusts it itself
        var oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
        try
        {
            _hwnd = CreateWindowEx(exStyle, ClassName, L.T("sw.title"), style, x, y, w, h, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { Log.Write("Settings window was not created: " + Marshal.GetLastWin32Error()); return false; }
            BuildControls();
        }
        finally { if (oldCtx != IntPtr.Zero) SetThreadDpiAwarenessContext(oldCtx); }

        LoadValues(_host.Settings, _host.Autostart);
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        return true;
    }

    /// <summary>Enter: in the app field - "Add", on a button - press it (instead of closing the window).</summary>
    public bool PreTranslate(ref MSG msg)
    {
        if (msg.message != WM_KEYDOWN || (int)msg.wParam != VK_RETURN || _hwnd == IntPtr.Zero) return false;
        var focus = GetFocus();
        if (focus == IntPtr.Zero) return false;
        if (_ctl.TryGetValue(ID_APP_COMBO, out var combo) && (focus == combo || GetParent(focus) == combo))
        {
            OnCommand(ID_APP_ADD, 0);
            return true;
        }
        if (ClassOf(focus).Equals("Button", StringComparison.OrdinalIgnoreCase) && GetParent(focus) == _hwnd)
        {
            SendMessage(focus, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        return false;
    }

    public void SetAiChecked(bool on)
    {
        if (_hwnd != IntPtr.Zero && _ctl.ContainsKey(ID_AI)) SetCheck(ID_AI, on);
    }

    private int S(int v) => (int)Math.Round(v * _scale);

    private IntPtr Ctl(int id, string cls, string text, uint style, int x, int y, int w, int h, uint ex = 0)
    {
        var hwnd = CreateWindowEx(ex, cls, text, WS_CHILD | WS_VISIBLE | style, S(x), S(y), S(w), S(h), _hwnd, (IntPtr)id, GetModuleHandle(null), IntPtr.Zero);
        SendMessage(hwnd, WM_SETFONT, _font, (IntPtr)1);
        if (id != 0) _ctl[id] = hwnd;
        return hwnd;
    }

    private void Label(string text, int x, int y, int w, int h = 20, int id = 0) =>
        Ctl(id, "STATIC", text, SS_LEFT | SS_NOPREFIX, x, y, w, h);

    private static readonly int[] UnloadMinutes = { 0, 5, 10, 30, 60 };
    private static string UnloadText(int m) => m switch { 0 => L.T("sw.unload.never"), 60 => L.T("sw.unload.hour"), _ => L.T("sw.unload.min", m) };

    private void BuildControls()
    {
        // ---------- left column: general and layout
        Ctl(ID_AUTO, "BUTTON", L.T("sw.auto"), BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP, 16, 12, 396, 22);
        Ctl(ID_AUTOSTART, "BUTTON", L.T("sw.autostart"), BS_AUTOCHECKBOX | WS_TABSTOP, 16, 36, 396, 22);
        Label(L.T("sw.ui_lang"), 16, 70, 150);
        var ul = Ctl(ID_UI_LANG, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 170, 66, 240, 200);
        foreach (var l in Languages.All) SendMessage(ul, CB_ADDSTRING, IntPtr.Zero, l.NativeName);
        Label(L.T("sw.layout_prio"), 16, 100, 150);
        var lp = Ctl(ID_LAYOUT_PRIO, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 170, 96, 240, 200);
        SendMessage(lp, CB_ADDSTRING, IntPtr.Zero, L.T("sw.layout_auto"));
        foreach (var c in PriorityCodes.Skip(1)) SendMessage(lp, CB_ADDSTRING, IntPtr.Zero, Languages.Get(c).NativeName);

        Ctl(0, "BUTTON", L.T("sw.hotkeys"), BS_GROUPBOX, 8, 130, 408, 156);
        void HotkeyRow(int y, string text, int id, int defId, int offId)
        {
            Label(text, 20, y + 3, 124);
            var hk = Ctl(id, "msctls_hotkey32", "", WS_TABSTOP | WS_BORDER, 146, y, 140, 24);
            // Letters without Ctrl/Alt are not allowed: Ctrl+Alt is added by default
            SendMessage(hk, HKM_SETRULES, (IntPtr)(HKCOMB_NONE | HKCOMB_S), (IntPtr)(HOTKEYF_CONTROL | HOTKEYF_ALT));
            Ctl(defId, "BUTTON", L.T("sw.default"), BS_PUSHBUTTON | WS_TABSTOP, 292, y, 66, 24);
            Ctl(offId, "BUTTON", L.T("sw.off"), BS_PUSHBUTTON | WS_TABSTOP, 362, y, 48, 24);
        }
        HotkeyRow(154, L.T("sw.hk.last"), ID_HK_LAST, ID_HK_LAST_DEF, ID_HK_LAST_OFF);
        HotkeyRow(186, L.T("sw.hk.sel"), ID_HK_SEL, ID_HK_SEL_DEF, ID_HK_SEL_OFF);
        HotkeyRow(218, L.T("sw.hk.ai"), ID_HK_AI, ID_HK_AI_DEF, ID_HK_AI_OFF);
        HotkeyRow(250, L.T("sw.hk.tr"), ID_HK_TR, ID_HK_TR_DEF, ID_HK_TR_OFF);

        Ctl(0, "BUTTON", L.T("sw.excl"), BS_GROUPBOX, 8, 296, 408, 196);
        Ctl(ID_APP_COMBO, "COMBOBOX", "", CBS_DROPDOWN | CBS_AUTOHSCROLL | CBS_SORT | WS_VSCROLL | WS_TABSTOP, 20, 320, 250, 220);
        Ctl(ID_APP_ADD, "BUTTON", L.T("sw.add"), BS_PUSHBUTTON | WS_TABSTOP, 278, 319, 126, 26);
        Ctl(ID_APP_LIST, "LISTBOX", "", LBS_NOTIFY | LBS_SORT | LBS_NOINTEGRALHEIGHT | WS_VSCROLL | WS_TABSTOP, 20, 352, 250, 128, WS_EX_CLIENTEDGE);
        Ctl(ID_APP_REMOVE, "BUTTON", L.T("sw.remove"), BS_PUSHBUTTON | WS_TABSTOP, 278, 352, 126, 26);
        Label(L.T("sw.excl_hint"), 278, 386, 126, 90);

        // ---------- right column: model
        Ctl(0, "BUTTON", L.T("sw.ai_group"), BS_GROUPBOX, 428, 6, 404, 486);
        Ctl(ID_AI, "BUTTON", L.T("sw.ai_enable"), BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP, 440, 30, 384, 22);
        Label(L.T("sw.model"), 440, 66, 60);
        var cmb = Ctl(ID_MODEL, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 500, 62, 320, 200);
        foreach (var m in AiSetup.Catalog) SendMessage(cmb, CB_ADDSTRING, IntPtr.Zero, m.Title);
        Label("", 440, 94, 380, 54, ID_MODEL_NOTE);

        Label(L.T("sw.pair"), 440, 158, 140);
        var la = Ctl(ID_LANG_A, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 584, 154, 110, 200);
        Label("<->", 698, 158, 22);
        var lb = Ctl(ID_LANG_B, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 722, 154, 98, 200);
        foreach (var l in Languages.All)
        {
            SendMessage(la, CB_ADDSTRING, IntPtr.Zero, L.LangName(l.Code));
            SendMessage(lb, CB_ADDSTRING, IntPtr.Zero, L.LangName(l.Code));
        }

        Label(L.T("sw.tr_out"), 440, 190, 140);
        var tr = Ctl(ID_TR_MODE, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_TABSTOP, 584, 186, 236, 120);
        SendMessage(tr, CB_ADDSTRING, IntPtr.Zero, L.T("sw.tr_out.window"));
        SendMessage(tr, CB_ADDSTRING, IntPtr.Zero, L.T("sw.tr_out.replace"));

        Label(L.T("sw.unload"), 440, 222, 140);
        var un = Ctl(ID_UNLOAD, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_TABSTOP, 584, 218, 236, 160);
        foreach (var m in UnloadMinutes) SendMessage(un, CB_ADDSTRING, IntPtr.Zero, UnloadText(m));

        Label(L.T("sw.device"), 440, 254, 140);
        var dv = Ctl(ID_DEVICE, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_TABSTOP, 584, 250, 236, 160);
        foreach (var k in new[] { "sw.dev.auto", "sw.dev.npu", "sw.dev.gpu", "sw.dev.cpu" }) SendMessage(dv, CB_ADDSTRING, IntPtr.Zero, L.T(k));
        // Up to 4 devices with long names (Intel iGPU + NVIDIA dGPU) - room for three lines
        Label(L.T(AiPlatform.CanDetectDevices ? "sw.dev.detecting" : "sw.dev.unknown"), 440, 280, 380, 54, ID_DEVICE_INFO);

        Ctl(ID_ON_DEMAND, "BUTTON", L.T("sw.on_demand"), BS_AUTOCHECKBOX | WS_TABSTOP, 440, 338, 384, 22);
        Label(L.T("sw.memory_hint"), 440, 364, 380, 48);
        Label("", 440, 416, 380, 70, ID_STATUS);

        // ---------- buttons
        Ctl(ID_OK, "BUTTON", L.T("sw.ok"), BS_DEFPUSHBUTTON | WS_TABSTOP | WS_GROUP, 576, 506, 80, 28);
        Ctl(ID_CANCEL, "BUTTON", L.T("sw.cancel"), BS_PUSHBUTTON | WS_TABSTOP, 664, 506, 80, 28);
        Ctl(ID_APPLY, "BUTTON", L.T("sw.apply"), BS_PUSHBUTTON | WS_TABSTOP, 752, 506, 80, 28);
    }

    // ------------------------------------------------------------------ values

    private void LoadValues(Settings s, bool autostart)
    {
        SetCheck(ID_AUTO, s.AutoSwitch);
        SetCheck(ID_AUTOSTART, autostart);
        SetHotkey(ID_HK_LAST, s.HotkeyConvertLastWord);
        SetHotkey(ID_HK_SEL, s.HotkeyConvertSelection);
        SetHotkey(ID_HK_AI, s.HotkeyAiFix);
        SetHotkey(ID_HK_TR, s.HotkeyTranslate);
        SendMessage(_ctl[ID_TR_MODE], CB_SETCURSEL, (IntPtr)(s.TranslateToWindow ? 0 : 1), IntPtr.Zero);
        int ui = Array.IndexOf(UnloadMinutes, s.AiUnloadAfterMinutes);
        SendMessage(_ctl[ID_UNLOAD], CB_SETCURSEL, (IntPtr)(ui < 0 ? 2 : ui), IntPtr.Zero);
        SendMessage(_ctl[ID_UI_LANG], CB_SETCURSEL, (IntPtr)LangIndex(s.UiLanguage), IntPtr.Zero);
        SendMessage(_ctl[ID_LAYOUT_PRIO], CB_SETCURSEL, (IntPtr)Math.Max(0, Array.IndexOf(PriorityCodes, s.EffectiveLayoutPriority)), IntPtr.Zero);
        SendMessage(_ctl[ID_LANG_A], CB_SETCURSEL, (IntPtr)LangIndex(s.TranslateLangA), IntPtr.Zero);
        SendMessage(_ctl[ID_LANG_B], CB_SETCURSEL, (IntPtr)LangIndex(s.TranslateLangB), IntPtr.Zero);
        SetCheck(ID_ON_DEMAND, s.AiLoadOnDemand);
        int di = Array.FindIndex(DeviceCodes, c => string.Equals(c, s.AiDevice, StringComparison.OrdinalIgnoreCase));
        SendMessage(_ctl[ID_DEVICE], CB_SETCURSEL, (IntPtr)Math.Max(0, di), IntPtr.Zero);

        SendMessage(_ctl[ID_APP_LIST], LB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        foreach (var a in s.ExcludedApps) SendMessage(_ctl[ID_APP_LIST], LB_ADDSTRING, IntPtr.Zero, a);
        foreach (var a in RunningApps()) SendMessage(_ctl[ID_APP_COMBO], CB_ADDSTRING, IntPtr.Zero, a);

        SetCheck(ID_AI, s.AiEnabled);
        int idx = Math.Max(0, AiSetup.Catalog.ToList().FindIndex(m => m.Id == s.AiModel));
        SendMessage(_ctl[ID_MODEL], CB_SETCURSEL, (IntPtr)idx, IntPtr.Zero);
        UpdateModelNote();
        UpdateStatus(_host.AiStatusText);
    }

    private Settings ReadValues()
    {
        var src = _host.Settings;
        var s = new Settings
        {
            AutoSwitch = GetCheck(ID_AUTO),
            HotkeyConvertLastWord = GetHotkey(ID_HK_LAST),
            HotkeyConvertSelection = GetHotkey(ID_HK_SEL),
            HotkeyAiFix = GetHotkey(ID_HK_AI),
            HotkeyTranslate = GetHotkey(ID_HK_TR),
            TranslateToWindow = (int)SendMessage(_ctl[ID_TR_MODE], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero) != 1,
            AiUnloadAfterMinutes = UnloadMinutes[Math.Clamp((int)SendMessage(_ctl[ID_UNLOAD], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero), 0, UnloadMinutes.Length - 1)],
            UiLanguage = SelectedLang(ID_UI_LANG),
            LayoutPriority = PriorityCodes[Math.Clamp((int)SendMessage(_ctl[ID_LAYOUT_PRIO], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero), 0, PriorityCodes.Length - 1)],
            TranslateLangA = SelectedLang(ID_LANG_A),
            TranslateLangB = SelectedLang(ID_LANG_B),
            AssistantLastTask = src.AssistantLastTask,
            AiLoadOnDemand = GetCheck(ID_ON_DEMAND),
            AiEnabled = GetCheck(ID_AI),
            AiModel = SelectedModel().Id,
            ModelPath = src.ModelPath,
            AiDevices = src.AiDevices,
            AiDevice = DeviceCodes[Math.Clamp((int)SendMessage(_ctl[ID_DEVICE], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero), 0, DeviceCodes.Length - 1)],
            AiMaxChars = src.AiMaxChars,
            NeverSwitch = src.NeverSwitch,
            ExcludedApps = ListItems(),
        };
        return s;
    }

    /// <summary>Preferred layout options: "" - none, then the layouts auto-correction understands.</summary>
    private static readonly string[] PriorityCodes = { "", "ru", "uk", "en", "hi" };

    /// <summary>Device options in the combo box: automatic, NPU, Intel GPU, CPU.</summary>
    private static readonly string[] DeviceCodes = { "auto", "NPU", "GPU", "CPU" };

    /// <summary>Show the devices OpenVINO found (called by the app once detection finishes).</summary>
    public void UpdateDevices(IReadOnlyList<DeviceInfo>? found)
    {
        if (_hwnd == IntPtr.Zero || !_ctl.TryGetValue(ID_DEVICE_INFO, out var h)) return;
        if (found == null) { SetWindowText(h, L.T("sw.dev.unknown")); return; }
        var text = L.T("sw.dev.found", string.Join("; ", found.Select(d => $"{d.Label} - {d.Name}" + (d.Note != null ? $" ({d.Note})" : ""))));
        var choice = SelectedDevice();
        if (choice != "auto" && !found.Any(d => d.Kind == choice && d.Usable)) text += " " + L.T("sw.dev.not_found", choice);
        if (AiPlatform.AmdBlockedBySac) text += " " + L.T("sw.dev.sac");
        SetWindowText(h, text);
    }

    private string SelectedDevice() =>
        DeviceCodes[Math.Clamp((int)SendMessage(_ctl[ID_DEVICE], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero), 0, DeviceCodes.Length - 1)];

    private static int LangIndex(string code) => Math.Max(0, Languages.All.ToList().FindIndex(l => l.Code == code));

    private string SelectedLang(int id)
    {
        int i = (int)SendMessage(_ctl[id], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return Languages.All[Math.Clamp(i, 0, Languages.All.Count - 1)].Code;
    }

    private ModelInfo SelectedModel()
    {
        int i = (int)SendMessage(_ctl[ID_MODEL], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return AiSetup.Catalog[Math.Clamp(i, 0, AiSetup.Catalog.Count - 1)];
    }

    private void UpdateModelNote()
    {
        var m = SelectedModel();
        // The plan depends on the processor (AMD: other engines and model formats) and on the chosen device
        var probe = _host.Settings;
        var cur = new Settings { AiModel = m.Id, AiDevice = SelectedDevice(), ModelPath = probe.ModelPath, AiDevices = probe.AiDevices };
        var (installed, gb, convert) = AiPlatform.ModelState(m, cur);
        var state = installed ? L.T("sw.state.installed") : convert ? L.T("sw.state.convert", gb) : L.T("sw.state.download", gb);
        if (AiPlatform.Amd && m.NpuRepo != null && SelectedDevice() is "auto" or "NPU" && Hardware.AmdNpuUsable(Hardware.FindAmdNpu()))
            state += " " + L.T("sw.state.amd_npu", m.NpuRepo);
        SetWindowText(_ctl[ID_MODEL_NOTE], L.T("sw.model_note", m.Note, state));
    }

    /// <summary>Update the model/installation status line (called by the app).</summary>
    public void UpdateStatus(string text)
    {
        if (_hwnd == IntPtr.Zero || !_ctl.TryGetValue(ID_STATUS, out var h)) return;
        SetWindowText(h, L.T("sw.status", text));
        EnableWindow(_ctl[ID_MODEL], !_host.InstallRunning);
        UpdateModelNote();
    }

    private bool GetCheck(int id) => SendMessage(_ctl[id], BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == (IntPtr)1;
    private void SetCheck(int id, bool on) => SendMessage(_ctl[id], BM_SETCHECK, (IntPtr)(on ? 1 : 0), IntPtr.Zero);

    private static bool IsExtendedKey(int vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E; // PgUp..arrows, Insert, Delete

    private void SetHotkey(int id, string text)
    {
        int value = 0;
        if (Hotkey.TryParse(text, out uint mods, out int vk))
        {
            int f = ((mods & MOD_SHIFT) != 0 ? HOTKEYF_SHIFT : 0) | ((mods & MOD_CONTROL) != 0 ? HOTKEYF_CONTROL : 0)
                  | ((mods & MOD_ALT) != 0 ? HOTKEYF_ALT : 0) | (IsExtendedKey(vk) ? HOTKEYF_EXT : 0);
            value = (vk & 0xFF) | (f << 8);
        }
        SendMessage(_ctl[id], HKM_SETHOTKEY, (IntPtr)value, IntPtr.Zero);
        // remember what is actually shown in the field
        _loadedHotkeys[id] = (text, (int)SendMessage(_ctl[id], HKM_GETHOTKEY, IntPtr.Zero, IntPtr.Zero) & 0xFFFF);
    }

    private string GetHotkey(int id)
    {
        int r = (int)SendMessage(_ctl[id], HKM_GETHOTKEY, IntPtr.Zero, IntPtr.Zero) & 0xFFFF;
        if (_loadedHotkeys.TryGetValue(id, out var loaded) && loaded.value == r) return loaded.text;
        int vk = r & 0xFF, f = (r >> 8) & 0xFF;
        if (vk == 0) return "";
        uint mods = ((f & HOTKEYF_SHIFT) != 0 ? MOD_SHIFT : 0) | ((f & HOTKEYF_CONTROL) != 0 ? MOD_CONTROL : 0)
                  | ((f & HOTKEYF_ALT) != 0 ? MOD_ALT : 0);
        return Hotkey.Format(mods, vk);
    }

    private List<string> ListItems()
    {
        var list = new List<string>();
        var lb = _ctl[ID_APP_LIST];
        int n = (int)SendMessage(lb, LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < n; i++)
        {
            int len = (int)SendMessage(lb, LB_GETTEXTLEN, (IntPtr)i, IntPtr.Zero);
            var buf = new char[len + 1];
            SendMessage(lb, LB_GETTEXT, (IntPtr)i, buf);
            list.Add(new string(buf, 0, len));
        }
        return list;
    }

    /// <summary>Exe names of apps that have visible windows.</summary>
    private static List<string> RunningApps()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowTextLength(h) > 0)
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != self)
                {
                    var n = ProcessFileName(pid);
                    if (n.Length > 0 && !n.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) names.Add(n);
                }
            }
            return true;
        }, IntPtr.Zero);
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ events

    private static IntPtr StaticProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self != null && (self._hwnd == hWnd || self._hwnd == IntPtr.Zero))
        {
            try { if (self.Proc(hWnd, msg, wParam, lParam, out var r)) return r; }
            catch (Exception ex) { Log.Write("Error in the settings window: " + ex); }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);

    private bool Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case WM_CTLCOLORSTATIC:
            case WM_CTLCOLORBTN:
                SetBkMode(wParam, 1); // transparent text background
                result = GetSysColorBrush(COLOR_BTNFACE);
                return true;

            case WM_COMMAND:
                int id = (int)wParam & 0xFFFF, code = ((int)wParam >> 16) & 0xFFFF;
                OnCommand(id, code);
                return true;

            case WM_CLOSE:
                Close();
                return true;

            case WM_DESTROY:
                _hwnd = IntPtr.Zero;
                _ctl.Clear();
                _host.SettingsWindowClosed();
                return true;

            case WM_NCDESTROY:
                // delete the font once child controls are destroyed
                if (_font != IntPtr.Zero) { DeleteObject(_font); _font = IntPtr.Zero; }
                return false;
        }
        return false;
    }

    private void OnCommand(int id, int code)
    {
        switch (id)
        {
            case ID_OK:
                if (_host.Apply(ReadValues(), GetCheck(ID_AUTOSTART))) Close();
                break;
            case ID_APPLY:
                if (_host.Apply(ReadValues(), GetCheck(ID_AUTOSTART)) && _hwnd != IntPtr.Zero)
                {
                    SetCheck(ID_AI, _host.Settings.AiEnabled); // installation may have been declined
                    UpdateStatus(_host.AiStatusText);
                }
                break;
            case ID_CANCEL:
                Close();
                break;
            case ID_HK_LAST_DEF: SetHotkey(ID_HK_LAST, Defaults.HotkeyConvertLastWord); break;
            case ID_HK_SEL_DEF: SetHotkey(ID_HK_SEL, Defaults.HotkeyConvertSelection); break;
            case ID_HK_AI_DEF: SetHotkey(ID_HK_AI, Defaults.HotkeyAiFix); break;
            case ID_HK_LAST_OFF: SetHotkey(ID_HK_LAST, ""); break;
            case ID_HK_SEL_OFF: SetHotkey(ID_HK_SEL, ""); break;
            case ID_HK_AI_OFF: SetHotkey(ID_HK_AI, ""); break;
            case ID_HK_TR_DEF: SetHotkey(ID_HK_TR, Defaults.HotkeyTranslate); break;
            case ID_HK_TR_OFF: SetHotkey(ID_HK_TR, ""); break;
            case ID_APP_ADD:
                {
                    var name = GetText(_ctl[ID_APP_COMBO]).Trim();
                    if (name.Length == 0) break;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
                    var lb = _ctl[ID_APP_LIST];
                    if ((int)SendMessage(lb, LB_FINDSTRINGEXACT, (IntPtr)(-1), name) < 0)
                        SendMessage(lb, LB_ADDSTRING, IntPtr.Zero, name);
                    SetWindowText(_ctl[ID_APP_COMBO], "");
                    break;
                }
            case ID_APP_REMOVE:
                {
                    var lb = _ctl[ID_APP_LIST];
                    int sel = (int)SendMessage(lb, LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                    if (sel >= 0) SendMessage(lb, LB_DELETESTRING, (IntPtr)sel, IntPtr.Zero);
                    break;
                }
            case ID_MODEL when code == CBN_SELCHANGE:
            case ID_DEVICE when code == CBN_SELCHANGE:
                UpdateModelNote();
                break;
        }
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
