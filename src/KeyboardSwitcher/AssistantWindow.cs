using System.Runtime.InteropServices;
using static KeyboardSwitcher.Native;

namespace KeyboardSwitcher;

/// <summary>
/// Assistant window: source text, action choice (translation, correction, style...), result.
/// Ctrl+Enter - run, Esc - close.
/// </summary>
internal sealed class AssistantWindow
{
    public interface IHost
    {
        bool AiBusy { get; }
        AssistantTask LastTask { get; set; }
        string LangA { get; }
        string LangB { get; }
        void RunAssistant(AssistantTask task, string text);
        /// <summary>Paste the result into the window where the text was selected. false - nowhere to paste.</summary>
        bool PasteToSource(string text);
        bool CanPasteToSource { get; }
        void AssistantClosed();
    }

    private const string ClassName = "KeyboardSwitcherAssistant";
    private const int ID_SOURCE = 100, ID_ACTION = 101, ID_RUN = 102, ID_STATUS = 103, ID_RESULT = 104,
        ID_COPY = 105, ID_PASTE = 106, ID_TO_SOURCE = 107, ID_CLOSE = 2;
    private const uint ES_MULTILINE = 0x4, ES_READONLY = 0x800, ES_AUTOVSCROLL = 0x40, ES_WANTRETURN = 0x1000,
        WS_EX_TOOLWINDOW = 0x80, EM_SETSEL = 0x00B1;
    private static WndProc? _classProc;
    private static AssistantWindow? _current;

    private readonly IHost _host;
    private IntPtr _hwnd, _font;
    private readonly Dictionary<int, IntPtr> _ctl = new();
    private double _scale = 1;
    private string _result = "";
    private List<AssistantTask> _tasks = AssistantTask.All();

    public IntPtr Hwnd => _hwnd;
    public AssistantWindow(IHost host) => _host = host;
    private int S(int v) => (int)Math.Round(v * _scale);

    /// <summary>Open (or refresh) the window with text; runNow - run the action immediately.</summary>
    public void Show(string source, AssistantTask task, bool runNow, IntPtr icon)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) Create(icon);
        if (_hwnd == IntPtr.Zero) return;
        SetWindowText(_ctl[ID_SOURCE], ToEdit(source));
        SendMessage(_ctl[ID_ACTION], CB_SETCURSEL, (IntPtr)Math.Max(0, _tasks.IndexOf(task)), IntPtr.Zero);
        SetResult("");
        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
        UpdateButtons();
        if (runNow && source.Trim().Length > 0) Run();
    }

    private void Create(IntPtr icon)
    {
        var hInst = GetModuleHandle(null);
        if (_classProc == null)
        {
            _classProc = StaticProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_classProc),
                hInstance = hInst, hIcon = icon, hIconSm = icon,
                hbrBackground = GetSysColorBrush(COLOR_BTNFACE),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
        }

        _scale = GetDpiForSystem() / 96.0;
        _font = CreateFont(-(int)Math.Round(10 * GetDpiForSystem() / 72.0), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        const uint style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        const uint exStyle = WS_EX_CONTROLPARENT;
        int cw = 640, ch = 534;
        var rc = new RECT { Right = S(cw), Bottom = S(ch) };
        AdjustWindowRectEx(ref rc, style, false, exStyle);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        GetCursorPos(out var p);
        int x = Math.Clamp(p.X - w / 2, 0, Math.Max(0, GetSystemMetrics(0) - w));
        int y = Math.Clamp(p.Y - h / 3, 0, Math.Max(0, GetSystemMetrics(1) - h - S(48)));

        _current = this;
        var oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
        try
        {
            _hwnd = CreateWindowEx(exStyle, ClassName, L.T("aw.title"), style, x, y, w, h, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { Log.Write("Assistant window was not created: " + Marshal.GetLastWin32Error()); return; }

            Child(0, "STATIC", L.T("aw.source"), 0, 10, 8, 620, 20);
            Child(ID_SOURCE, "EDIT", "", ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN | WS_VSCROLL | WS_TABSTOP, 10, 28, 620, 150, WS_EX_CLIENTEDGE);
            Child(0, "STATIC", L.T("aw.action"), 0, 10, 190, 72, 20);
            var cmb = Child(ID_ACTION, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 84, 186, 330, 300);
            _tasks = AssistantTask.All();
            foreach (var t in _tasks) SendMessage(cmb, CB_ADDSTRING, IntPtr.Zero, t.Title(_host.LangA, _host.LangB));
            Child(ID_RUN, "BUTTON", L.T("aw.run"), BS_DEFPUSHBUTTON | WS_TABSTOP, 422, 185, 208, 28);
            Child(ID_STATUS, "STATIC", L.T("aw.result"), SS_NOPREFIX, 10, 224, 620, 20);
            Child(ID_RESULT, "EDIT", "", ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL | WS_VSCROLL | WS_TABSTOP, 10, 244, 620, 238, WS_EX_CLIENTEDGE);
            Child(ID_COPY, "BUTTON", L.T("aw.copy"), BS_PUSHBUTTON | WS_TABSTOP, 10, 494, 120, 30);
            Child(ID_PASTE, "BUTTON", L.T("aw.paste"), BS_PUSHBUTTON | WS_TABSTOP, 136, 494, 220, 30);
            Child(ID_TO_SOURCE, "BUTTON", L.T("aw.to_source"), BS_PUSHBUTTON | WS_TABSTOP, 362, 494, 150, 30);
            Child(ID_CLOSE, "BUTTON", L.T("aw.close"), BS_PUSHBUTTON | WS_TABSTOP, 540, 494, 90, 30);
        }
        finally { if (oldCtx != IntPtr.Zero) SetThreadDpiAwarenessContext(oldCtx); }
    }

    private IntPtr Child(int id, string cls, string text, uint style, int x, int y, int w, int h, uint ex = 0)
    {
        var c = CreateWindowEx(ex, cls, text, WS_CHILD | WS_VISIBLE | style, S(x), S(y), S(w), S(h), _hwnd, (IntPtr)id, GetModuleHandle(null), IntPtr.Zero);
        SendMessage(c, WM_SETFONT, _font, (IntPtr)1);
        if (id != 0) _ctl[id] = c;
        return c;
    }

    private static string ToEdit(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private AssistantTask SelectedTask()
    {
        int i = (int)SendMessage(_ctl[ID_ACTION], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return _tasks[Math.Clamp(i, 0, _tasks.Count - 1)];
    }

    private void Run()
    {
        if (_hwnd == IntPtr.Zero || _host.AiBusy) return;
        var text = GetText(_ctl[ID_SOURCE]).Replace("\r\n", "\n");
        if (text.Trim().Length == 0) { SetStatus(L.T("aw.enter_text")); return; }
        var task = SelectedTask();
        _host.LastTask = task;
        SetResult("");
        SetBusy(true);
        _host.RunAssistant(task, text);
    }

    // ------------------------------------------------------------------ called by the app

    public void SetBusy(bool busy, string? status = null)
    {
        if (_hwnd == IntPtr.Zero) return;
        EnableWindow(_ctl[ID_RUN], !busy);
        SetStatus(status ?? L.T(busy ? "aw.running" : "aw.result"));
    }

    public void SetStatus(string text)
    {
        if (_hwnd != IntPtr.Zero) SetWindowText(_ctl[ID_STATUS], text);
    }

    public void SetResult(string text)
    {
        _result = text;
        if (_hwnd == IntPtr.Zero) return;
        SetWindowText(_ctl[ID_RESULT], ToEdit(text));
        if (text.Length > 0) SetBusy(false);
        UpdateButtons();
    }

    public void SetError(string text)
    {
        SetResult("");
        SetBusy(false, L.T("aw.failed", text));
    }

    private void UpdateButtons()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool has = _result.Length > 0;
        EnableWindow(_ctl[ID_COPY], has);
        EnableWindow(_ctl[ID_PASTE], has && _host.CanPasteToSource);
        EnableWindow(_ctl[ID_TO_SOURCE], has);
    }

    // ------------------------------------------------------------------ keyboard and events

    public bool PreTranslate(ref MSG msg)
    {
        if (_hwnd == IntPtr.Zero || msg.message != WM_KEYDOWN) return false;
        int vk = (int)msg.wParam;
        bool ctrl = IsDown(VK_CONTROL);
        if (vk == VK_ESCAPE) { Close(); return true; }
        if (vk == VK_RETURN && ctrl) { Run(); return true; }
        var focus = GetFocus();
        if (ctrl && vk == 'A' && ClassOf(focus).Equals("Edit", StringComparison.OrdinalIgnoreCase))
        {
            SendMessage(focus, EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
            return true;
        }
        if (vk == VK_RETURN && focus != _ctl.GetValueOrDefault(ID_SOURCE))
        {
            // Enter on a button - press it; elsewhere - run
            if (ClassOf(focus).Equals("Button", StringComparison.OrdinalIgnoreCase)) SendMessage(focus, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            else Run();
            return true;
        }
        return false;
    }

    private void OnCommand(int id)
    {
        switch (id)
        {
            case ID_RUN: Run(); break;
            case ID_COPY:
                if (_result.Length > 0) { Input.SetClipboardText(_result); SetStatus(L.T("aw.copied")); }
                break;
            case ID_PASTE:
                if (_result.Length > 0)
                {
                    var r = _result;
                    if (_host.PasteToSource(r)) Close();
                    else SetStatus(L.T("aw.source_closed"));
                }
                break;
            case ID_TO_SOURCE:
                if (_result.Length > 0) { SetWindowText(_ctl[ID_SOURCE], ToEdit(_result)); SetResult(""); SetStatus(L.T("aw.moved")); }
                break;
            case ID_CLOSE: Close(); break;
        }
    }

    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(IntPtr hdc, uint color);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);

    private static IntPtr StaticProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self != null && (self._hwnd == hWnd || self._hwnd == IntPtr.Zero))
        {
            try
            {
                switch (msg)
                {
                    case WM_CTLCOLORSTATIC:
                        // Labels on the window background; the result field (read-only) is white like a regular field
                        if (lParam == self._ctl.GetValueOrDefault(ID_RESULT))
                        {
                            SetBkColor(wParam, GetSysColor(5)); // COLOR_WINDOW
                            return GetSysColorBrush(5);
                        }
                        SetBkMode(wParam, 1);
                        return GetSysColorBrush(COLOR_BTNFACE);
                    case WM_COMMAND:
                        if ((((int)wParam >> 16) & 0xFFFF) == BN_CLICKED) self.OnCommand((int)wParam & 0xFFFF);
                        return IntPtr.Zero;
                    case WM_CLOSE:
                        self.Close();
                        return IntPtr.Zero;
                    case WM_DESTROY:
                        self._hwnd = IntPtr.Zero;
                        self._ctl.Clear();
                        self._host.AssistantClosed();
                        return IntPtr.Zero;
                    case WM_NCDESTROY:
                        if (self._font != IntPtr.Zero) { DeleteObject(self._font); self._font = IntPtr.Zero; }
                        break;
                }
            }
            catch (Exception ex) { Log.Write("Error in the assistant window: " + ex); }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
