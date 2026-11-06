using System.Runtime.InteropServices;
using static KeyboardSwitcher.Native;

namespace KeyboardSwitcher;

/// <summary>
/// Chat with the model: saved conversations (list on top), the conversation, the message field.
/// Ctrl+Enter - send, Enter - new line, Esc - close.
/// </summary>
internal sealed class ChatWindow
{
    public interface IHost
    {
        bool AiBusy { get; }
        /// <summary>Send the conversation to the model; done(answer, error) is called on the main thread.</summary>
        void SendChat(IReadOnlyList<ChatMessage> history, Action<string?, string?> done);
        void ChatClosed();
        IReadOnlyList<(string Id, string Title)> ModelChoices { get; }
        string CurrentModel { get; }
        void SelectModel(string id);
    }

    private const string ClassName = "KeyboardSwitcherChat";
    private const int ID_HISTORY = 200, ID_NEW = 201, ID_DELETE = 202, ID_LOG = 203, ID_STATUS = 204, ID_INPUT = 205,
        ID_SEND = 206, ID_COPY = 207, ID_MODEL = 208, ID_CLOSE = 2;
    private const uint ES_MULTILINE = 0x4, ES_READONLY = 0x800, ES_AUTOVSCROLL = 0x40, ES_WANTRETURN = 0x1000,
        EM_SETSEL = 0x00B1, EM_SCROLLCARET = 0x00B7, CBN_SELCHANGE_CODE = 1;
    private static WndProc? _classProc;
    private static ChatWindow? _current;

    private readonly IHost _host;
    private IntPtr _hwnd, _font;
    private readonly Dictionary<int, IntPtr> _ctl = new();
    private IReadOnlyList<(string Id, string Title)> _choices = Array.Empty<(string, string)>();
    private double _scale = 1;
    private ChatSession _chat = new();
    private List<ChatSession> _saved = new();
    private bool _busy;

    public IntPtr Hwnd => _hwnd;
    public ChatWindow(IHost host) => _host = host;
    private int S(int v) => (int)Math.Round(v * _scale);

    /// <summary>Open the window; text (for example, the selected text) goes into the message field.</summary>
    public void Show(string? text, IntPtr icon)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
        {
            Create(icon);
            if (_hwnd == IntPtr.Zero) return;
            LoadHistory(selectCurrent: false);
            ShowChat();
        }
        if (!string.IsNullOrEmpty(text)) SetWindowText(_ctl[ID_INPUT], ToEdit(text));
        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
        SetFocus(_ctl[ID_INPUT]);
        SendMessage(_ctl[ID_INPUT], EM_SETSEL, (IntPtr)(-1), (IntPtr)(-1));
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
        var rc = new RECT { Right = S(680), Bottom = S(634) };
        AdjustWindowRectEx(ref rc, style, false, exStyle);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        int x = Math.Max(0, (GetSystemMetrics(0) - w) / 2), y = Math.Max(0, (GetSystemMetrics(1) - h) / 2);

        _current = this;
        var oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
        try
        {
            _hwnd = CreateWindowEx(exStyle, ClassName, L.T("cw.title"), style, x, y, w, h, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { Log.Write("Chat window was not created: " + Marshal.GetLastWin32Error()); return; }

            Child(0, "STATIC", L.T("aw.model"), 0, 10, 12, 70, 20);
            var mc = Child(ID_MODEL, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 82, 8, 588, 400);
            ModelChoices.Fill(mc, _choices = _host.ModelChoices, _host.CurrentModel);
            Child(0, "STATIC", L.T("cw.history"), 0, 10, 46, 70, 20);
            Child(ID_HISTORY, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 82, 42, 360, 300);
            Child(ID_NEW, "BUTTON", L.T("cw.new"), BS_PUSHBUTTON | WS_TABSTOP, 450, 41, 110, 28);
            Child(ID_DELETE, "BUTTON", L.T("cw.delete"), BS_PUSHBUTTON | WS_TABSTOP, 566, 41, 104, 28);
            Child(ID_LOG, "EDIT", "", ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL | WS_VSCROLL | WS_TABSTOP, 10, 78, 660, 346, WS_EX_CLIENTEDGE);
            Child(ID_STATUS, "STATIC", "", SS_NOPREFIX, 10, 430, 660, 20);
            Child(ID_INPUT, "EDIT", "", ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN | WS_VSCROLL | WS_TABSTOP, 10, 452, 660, 130, WS_EX_CLIENTEDGE);
            Child(ID_SEND, "BUTTON", L.T("cw.send"), BS_DEFPUSHBUTTON | WS_TABSTOP, 10, 592, 220, 32);
            Child(ID_COPY, "BUTTON", L.T("cw.copy"), BS_PUSHBUTTON | WS_TABSTOP, 236, 592, 160, 32);
            Child(ID_CLOSE, "BUTTON", L.T("aw.close"), BS_PUSHBUTTON | WS_TABSTOP, 570, 592, 100, 32);
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

    // ------------------------------------------------------------------ chats

    /// <summary>Fill the list: "(new chat)" first, then the saved chats.</summary>
    private void LoadHistory(bool selectCurrent)
    {
        _saved = ChatStore.List();
        var cmb = _ctl[ID_HISTORY];
        SendMessage(cmb, CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        SendMessage(cmb, CB_ADDSTRING, IntPtr.Zero, L.T("cw.new_item"));
        foreach (var c in _saved) SendMessage(cmb, CB_ADDSTRING, IntPtr.Zero, $"{c.Updated:dd.MM HH:mm}  {c.Title}");
        int sel = selectCurrent ? _saved.FindIndex(c => c.Id == _chat.Id) + 1 : 0;
        SendMessage(cmb, CB_SETCURSEL, (IntPtr)Math.Max(0, sel), IntPtr.Zero);
    }

    private void ShowChat()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in _chat.Messages)
            sb.Append(m.Role == "user" ? L.T("cw.you") : L.T("cw.model")).Append("\n").Append(m.Content.Trim()).Append("\n\n");
        SetWindowText(_ctl[ID_LOG], ToEdit(sb.ToString()));
        // Scroll to the end
        SendMessage(_ctl[ID_LOG], EM_SETSEL, (IntPtr)int.MaxValue, (IntPtr)int.MaxValue);
        SendMessage(_ctl[ID_LOG], EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
        SetStatus(_chat.Messages.Count == 0 ? L.T("cw.hint") : "");
        UpdateButtons();
    }

    private void OnHistorySelected()
    {
        if (_busy) return;
        int i = (int)SendMessage(_ctl[ID_HISTORY], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        _chat = i <= 0 || i > _saved.Count ? new ChatSession() : _saved[i - 1];
        ShowChat();
    }

    private void NewChat()
    {
        if (_busy) return;
        _chat = new ChatSession();
        LoadHistory(selectCurrent: false);
        ShowChat();
        SetFocus(_ctl[ID_INPUT]);
    }

    private void DeleteChat()
    {
        if (_busy || _chat.Messages.Count == 0) return;
        if (MessageBox(_hwnd, L.T("cw.delete_confirm", _chat.Title), L.T("cw.title"), 0x24 /* YESNO | QUESTION */) != 6) return;
        ChatStore.Delete(_chat);
        NewChat();
    }

    private void Send()
    {
        if (_hwnd == IntPtr.Zero || _busy) return;
        if (_host.AiBusy) { SetStatus(L.T("note.busy")); return; }
        var text = GetText(_ctl[ID_INPUT]).Replace("\r\n", "\n").Trim();
        if (text.Length == 0) { SetStatus(L.T("aw.enter_text")); return; }

        var chat = _chat;
        chat.Messages.Add(new ChatMessage("user", text));
        SetWindowText(_ctl[ID_INPUT], "");
        ShowChat();
        SetBusy(true, L.T("cw.thinking"));
        _host.SendChat(chat.Messages.ToList(), (answer, error) =>
        {
            if (error != null || string.IsNullOrWhiteSpace(answer))
            {
                // Put the question back so it isn't lost
                chat.Messages.RemoveAt(chat.Messages.Count - 1);
                if (_hwnd == IntPtr.Zero) return;
                if (chat == _chat) { ShowChat(); SetWindowText(_ctl[ID_INPUT], ToEdit(text)); }
                SetBusy(false, L.T("aw.failed", error ?? L.T("err.empty")));
                return;
            }
            chat.Messages.Add(new ChatMessage("assistant", answer!.Trim()));
            ChatStore.Save(chat);
            if (_hwnd == IntPtr.Zero) return;
            SetBusy(false);
            if (chat == _chat) { LoadHistory(selectCurrent: true); ShowChat(); }
        });
    }

    // ------------------------------------------------------------------ called by the app

    /// <summary>The model list or the selected model changed (settings, the assistant window).</summary>
    public void RefreshModels()
    {
        if (_hwnd == IntPtr.Zero || !_ctl.TryGetValue(ID_MODEL, out var mc)) return;
        ModelChoices.Fill(mc, _choices = _host.ModelChoices, _host.CurrentModel);
    }

    public void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (_hwnd == IntPtr.Zero) return;
        EnableWindow(_ctl[ID_SEND], !busy);
        EnableWindow(_ctl[ID_HISTORY], !busy);
        EnableWindow(_ctl[ID_NEW], !busy);
        UpdateButtons();
        SetStatus(status ?? "");
    }

    private void SetStatus(string text)
    {
        if (_hwnd != IntPtr.Zero) SetWindowText(_ctl[ID_STATUS], text);
    }

    private void UpdateButtons()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool has = _chat.Messages.Count > 0;
        EnableWindow(_ctl[ID_DELETE], has && !_busy);
        EnableWindow(_ctl[ID_COPY], has);
    }

    // ------------------------------------------------------------------ keyboard and events

    public bool PreTranslate(ref MSG msg)
    {
        if (_hwnd == IntPtr.Zero || msg.message != WM_KEYDOWN) return false;
        int vk = (int)msg.wParam;
        bool ctrl = IsDown(VK_CONTROL);
        if (vk == VK_ESCAPE) { Close(); return true; }
        if (vk == VK_RETURN && ctrl) { Send(); return true; }
        var focus = GetFocus();
        if (ctrl && vk == 'A' && ClassOf(focus).Equals("Edit", StringComparison.OrdinalIgnoreCase))
        {
            SendMessage(focus, EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
            return true;
        }
        if (vk == VK_RETURN && focus != _ctl.GetValueOrDefault(ID_INPUT))
        {
            if (ClassOf(focus).Equals("Button", StringComparison.OrdinalIgnoreCase)) SendMessage(focus, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        return false;
    }

    private void OnCommand(int id, int code)
    {
        switch (id)
        {
            case ID_HISTORY when code == CBN_SELCHANGE_CODE: OnHistorySelected(); break;
            case ID_MODEL when code == CBN_SELCHANGE_CODE:
                {
                    var model = ModelChoices.Selected(_ctl[ID_MODEL], _choices);
                    if (model != null && model != _host.CurrentModel) _host.SelectModel(model);
                    break;
                }
            case ID_NEW when code == BN_CLICKED: NewChat(); break;
            case ID_DELETE when code == BN_CLICKED: DeleteChat(); break;
            case ID_SEND when code == BN_CLICKED: Send(); break;
            case ID_COPY when code == BN_CLICKED:
                if (_chat.Messages.Count > 0)
                {
                    Input.SetClipboardText(string.Join("\n\n", _chat.Messages.Select(m =>
                        (m.Role == "user" ? L.T("cw.you") : L.T("cw.model")) + "\n" + m.Content.Trim())));
                    SetStatus(L.T("aw.copied"));
                }
                break;
            case ID_CLOSE when code == BN_CLICKED: Close(); break;
        }
    }

    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(IntPtr hdc, uint color);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);

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
                        // The conversation (read-only) is white like a regular field; labels are on the window background
                        if (lParam == self._ctl.GetValueOrDefault(ID_LOG))
                        {
                            SetBkColor(wParam, GetSysColor(5)); // COLOR_WINDOW
                            return GetSysColorBrush(5);
                        }
                        SetBkMode(wParam, 1);
                        return GetSysColorBrush(COLOR_BTNFACE);
                    case WM_COMMAND:
                        self.OnCommand((int)wParam & 0xFFFF, ((int)wParam >> 16) & 0xFFFF);
                        return IntPtr.Zero;
                    case WM_CLOSE:
                        self.Close();
                        return IntPtr.Zero;
                    case WM_DESTROY:
                        self._hwnd = IntPtr.Zero;
                        self._ctl.Clear();
                        self._host.ChatClosed();
                        return IntPtr.Zero;
                    case WM_NCDESTROY:
                        if (self._font != IntPtr.Zero) { DeleteObject(self._font); self._font = IntPtr.Zero; }
                        break;
                }
            }
            catch (Exception ex) { Log.Write("Error in the chat window: " + ex); }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
