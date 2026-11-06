using System.Runtime.InteropServices;
using static KeyboardSwitcher.Native;

namespace KeyboardSwitcher;

/// <summary>
/// Network models: list of connections on the left; provider, name, address, API key and model on the right.
/// "Load list" asks the server for its models, "Check" sends a short test request. Changes are saved at once.
/// </summary>
internal sealed class NetModelsWindow
{
    public interface IHost
    {
        Settings Settings { get; }
        /// <summary>Save the connections (the app updates the model lists in all windows).</summary>
        void SaveNetModels(List<NetModel> list);
        /// <summary>Run on the main thread.</summary>
        void Invoke(Action a);
    }

    private const string ClassName = "KeyboardSwitcherNetModels";
    private const int ID_LIST = 300, ID_ADD = 301, ID_DELETE = 302, ID_PROVIDER = 303, ID_NAME = 304, ID_URL = 305,
        ID_KEY = 306, ID_MODEL = 307, ID_LOAD = 308, ID_CHECK = 309, ID_SAVE = 310, ID_STATUS = 311, ID_CLOSE = 2;
    private const uint ES_PASSWORD = 0x20, ES_AUTOHSCROLL = 0x80, LB_SETCURSEL = 0x0186, LBN_SELCHANGE = 1,
        EM_SETCUEBANNER = 0x1501, CB_SETEDITSEL = 0x0142;
    private static WndProc? _classProc;

    /// <summary>The open window (one at a time) - for the message loop.</summary>
    public static NetModelsWindow? Current { get; private set; }

    private readonly IHost _host;
    private IntPtr _hwnd, _font;
    private readonly Dictionary<int, IntPtr> _ctl = new();
    private double _scale = 1;
    private List<NetModel> _list = new();
    private NetModel? _edit;          // the connection in the fields (a copy); null - nothing selected
    private bool _busy;
    private CancellationTokenSource? _cts;   // the running "List" / "Check" - cancelled by the button or on close
    private DateTime _started;
    private bool _checking;
    private const int TimerId = 1;

    /// <summary>"Check" waits at most this long (a thinking model may take a while, but not minutes).</summary>
    internal static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(45);

    public IntPtr Hwnd => _hwnd;
    public NetModelsWindow(IHost host) => _host = host;
    private int S(int v) => (int)Math.Round(v * _scale);

    public static void Open(IHost host, IntPtr owner, IntPtr icon)
    {
        if (Current is { } c && c._hwnd != IntPtr.Zero) { ShowWindow(c._hwnd, SW_RESTORE); SetForegroundWindow(c._hwnd); return; }
        var w = new NetModelsWindow(host);
        Current = w;
        w.Create(owner, icon);
        if (w._hwnd == IntPtr.Zero) Current = null;
    }

    private void Create(IntPtr owner, IntPtr icon)
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
        _font = CreateFont(-(int)Math.Round(9 * GetDpiForSystem() / 72.0), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        const uint style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU;
        const uint exStyle = WS_EX_CONTROLPARENT;
        var rc = new RECT { Right = S(660), Bottom = S(420) };
        AdjustWindowRectEx(ref rc, style, false, exStyle);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        int x = (GetSystemMetrics(0) - w) / 2, y = (GetSystemMetrics(1) - h) / 2;

        var oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
        try
        {
            _hwnd = CreateWindowEx(exStyle, ClassName, L.T("nw.title"), style, x, y, w, h, owner, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { Log.Write("Network models window was not created: " + Marshal.GetLastWin32Error()); return; }

            Ctl(0, "STATIC", L.T("nw.list"), SS_NOPREFIX, 10, 10, 200, 20);
            Ctl(ID_LIST, "LISTBOX", "", LBS_NOTIFY | LBS_NOINTEGRALHEIGHT | WS_VSCROLL | WS_TABSTOP | WS_BORDER, 10, 30, 210, 304);
            Ctl(ID_ADD, "BUTTON", L.T("nw.add"), BS_PUSHBUTTON | WS_TABSTOP, 10, 342, 102, 28);
            Ctl(ID_DELETE, "BUTTON", L.T("nw.delete"), BS_PUSHBUTTON | WS_TABSTOP, 118, 342, 102, 28);

            int lx = 236, fx = 340, fw = 310;
            Ctl(0, "STATIC", L.T("nw.provider"), SS_NOPREFIX, lx, 34, 100, 20);
            var prov = Ctl(ID_PROVIDER, "COMBOBOX", "", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, fx, 30, fw, 200);
            foreach (var p in NetModels.Providers) SendMessage(prov, CB_ADDSTRING, IntPtr.Zero, p.Title);
            Ctl(0, "STATIC", L.T("nw.name"), SS_NOPREFIX, lx, 68, 100, 20);
            Ctl(ID_NAME, "EDIT", "", ES_AUTOHSCROLL | WS_TABSTOP, fx, 64, fw, 24, WS_EX_CLIENTEDGE);
            Ctl(0, "STATIC", L.T("nw.url"), SS_NOPREFIX, lx, 102, 100, 20);
            Ctl(ID_URL, "EDIT", "", ES_AUTOHSCROLL | WS_TABSTOP, fx, 98, fw, 24, WS_EX_CLIENTEDGE);
            Ctl(0, "STATIC", L.T("nw.key"), SS_NOPREFIX, lx, 136, 100, 20);
            Ctl(ID_KEY, "EDIT", "", ES_AUTOHSCROLL | ES_PASSWORD | WS_TABSTOP, fx, 132, fw, 24, WS_EX_CLIENTEDGE);
            Ctl(0, "STATIC", L.T("nw.model"), SS_NOPREFIX, lx, 170, 100, 20);
            var model = Ctl(ID_MODEL, "COMBOBOX", "", CBS_DROPDOWN | CBS_AUTOHSCROLL | WS_VSCROLL | WS_TABSTOP, fx, 166, 200, 300);
            SendMessage(model, CB_SETDROPPEDWIDTH, (IntPtr)S(320), IntPtr.Zero);
            Ctl(ID_LOAD, "BUTTON", L.T("nw.load"), BS_PUSHBUTTON | WS_TABSTOP, fx + 206, 165, fw - 206, 26);
            Ctl(ID_CHECK, "BUTTON", L.T("nw.check"), BS_PUSHBUTTON | WS_TABSTOP, fx, 204, 150, 28);
            Ctl(ID_SAVE, "BUTTON", L.T("nw.save"), BS_DEFPUSHBUTTON | WS_TABSTOP, fx + 160, 204, fw - 160, 28);
            Ctl(ID_STATUS, "STATIC", "", SS_NOPREFIX, lx, 242, 414, 48);
            Ctl(0, "STATIC", L.T("nw.privacy"), SS_NOPREFIX, lx, 294, 414, 64);
            Ctl(ID_CLOSE, "BUTTON", L.T("aw.close"), BS_PUSHBUTTON | WS_TABSTOP, 560, 382, 90, 28);
        }
        finally { if (oldCtx != IntPtr.Zero) SetThreadDpiAwarenessContext(oldCtx); }

        _list = _host.Settings.NetModels.Select(n => n.Copy()).ToList();
        FillList(_list.Count > 0 ? 0 : -1);
        if (_list.Count == 0) NewConnection();
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
    }

    private IntPtr Ctl(int id, string cls, string text, uint style, int x, int y, int w, int h, uint ex = 0)
    {
        var c = CreateWindowEx(ex, cls, text, WS_CHILD | WS_VISIBLE | style, S(x), S(y), S(w), S(h), _hwnd, (IntPtr)id, GetModuleHandle(null), IntPtr.Zero);
        SendMessage(c, WM_SETFONT, _font, (IntPtr)1);
        if (id != 0) _ctl[id] = c;
        return c;
    }

    // ------------------------------------------------------------------ list and fields

    private void FillList(int select)
    {
        var lb = _ctl[ID_LIST];
        SendMessage(lb, LB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        foreach (var n in _list) SendMessage(lb, LB_ADDSTRING, IntPtr.Zero, n.Title);
        SendMessage(lb, LB_SETCURSEL, (IntPtr)select, IntPtr.Zero);
        if (select >= 0 && select < _list.Count) ShowFields(_list[select].Copy());
        EnableWindow(_ctl[ID_DELETE], select >= 0);
    }

    private void ShowFields(NetModel n)
    {
        _edit = n;
        SendMessage(_ctl[ID_PROVIDER], CB_SETCURSEL, (IntPtr)Math.Max(0, NetModels.Providers.ToList().FindIndex(p => p.Id == n.Provider)), IntPtr.Zero);
        SetWindowText(_ctl[ID_NAME], n.Name);
        SetWindowText(_ctl[ID_URL], n.BaseUrl.Length > 0 ? n.BaseUrl : NetModels.Find(n.Provider).DefaultUrl);
        SetWindowText(_ctl[ID_KEY], "");
        // An empty key field keeps the saved key; the hint says whether one is saved
        SendMessage(_ctl[ID_KEY], EM_SETCUEBANNER, (IntPtr)1, L.T(n.ApiKey.Length > 0 ? "nw.key_saved" : NetModels.Find(n.Provider).NeedsKey ? "nw.key_needed" : "nw.key_optional"));
        SendMessage(_ctl[ID_MODEL], CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        SetWindowText(_ctl[ID_MODEL], n.Model);
        SetStatus("");
    }

    private void NewConnection()
    {
        SendMessage(_ctl[ID_LIST], LB_SETCURSEL, (IntPtr)(-1), IntPtr.Zero);
        EnableWindow(_ctl[ID_DELETE], false);
        ShowFields(new NetModel { Provider = "openai", BaseUrl = NetModels.Find("openai").DefaultUrl });
        SetFocus(_ctl[ID_PROVIDER]);
    }

    private NetProvider SelectedProvider()
    {
        int i = (int)SendMessage(_ctl[ID_PROVIDER], CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return NetModels.Providers[Math.Clamp(i, 0, NetModels.Providers.Count - 1)];
    }

    /// <summary>The connection from the fields; key - the typed key or the saved one.</summary>
    private (NetModel n, string key) FromFields()
    {
        var n = (_edit ?? new NetModel()).Copy();
        n.Provider = SelectedProvider().Id;
        n.Name = GetText(_ctl[ID_NAME]).Trim();
        n.BaseUrl = GetText(_ctl[ID_URL]).Trim();
        n.Model = GetText(_ctl[ID_MODEL]).Trim();
        var typed = GetText(_ctl[ID_KEY]).Trim();
        string key;
        try { key = typed.Length > 0 ? typed : Secret.Unprotect(n.ApiKey); }
        catch (Exception ex) { key = ""; SetStatus(ex.Message); }
        return (n, key);
    }

    private void OnProviderChanged()
    {
        var p = SelectedProvider();
        // Replace the address only if it was the default of another provider (keep a typed one)
        var url = GetText(_ctl[ID_URL]).Trim();
        if (url.Length == 0 || NetModels.Providers.Any(x => x.DefaultUrl == url)) SetWindowText(_ctl[ID_URL], p.DefaultUrl);
        SendMessage(_ctl[ID_MODEL], CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        SetWindowText(_ctl[ID_MODEL], "");
        if (_edit?.ApiKey.Length == 0 || _edit == null)
            SendMessage(_ctl[ID_KEY], EM_SETCUEBANNER, (IntPtr)1, L.T(p.NeedsKey ? "nw.key_needed" : "nw.key_optional"));
    }

    private void Save()
    {
        var (n, key) = FromFields();
        if (!Uri.TryCreate(n.BaseUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
        { SetStatus(L.T("nw.bad_url")); return; }
        // A cloud API key must not go over plain http to another computer
        if (u.Scheme == "http" && !NetModels.IsLocalServer(n) && key.Length > 0 && !u.IsLoopback && !IsPrivateHost(u))
        { SetStatus(L.T("nw.http_key")); return; }
        if (n.Model.Length == 0) { SetStatus(L.T("err.net_no_model")); return; }
        if (NetModels.Find(n.Provider).NeedsKey && key.Length == 0) { SetStatus(L.T("err.net_no_key")); return; }
        try { n.ApiKey = Secret.Protect(key); }
        catch (Exception ex) { SetStatus(ex.Message); return; }

        int i = _list.FindIndex(x => x.Id == n.Id);
        if (i >= 0) _list[i] = n; else { _list.Add(n); i = _list.Count - 1; }
        _host.SaveNetModels(_list.Select(x => x.Copy()).ToList());
        FillList(i);
        SetStatus(L.T("nw.saved"));
    }

    private static bool IsPrivateHost(Uri u) =>
        System.Net.IPAddress.TryParse(u.Host, out var ip) && ip.GetAddressBytes() is var b && b.Length == 4
        && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));

    private void Delete()
    {
        int i = (int)SendMessage(_ctl[ID_LIST], LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        if (i < 0 || i >= _list.Count) return;
        if (MessageBox(_hwnd, L.T("nw.delete_confirm", _list[i].Title), L.T("nw.title"), 0x24) != 6) return;
        _list.RemoveAt(i);
        _host.SaveNetModels(_list.Select(x => x.Copy()).ToList());
        FillList(_list.Count > 0 ? 0 : -1);
        if (_list.Count == 0) NewConnection();
    }

    /// <summary>
    /// Ask the server for its models (List) or send a test request (Check) in the background. While it runs, the Check
    /// button becomes Cancel and the status shows the seconds; everything else in the window keeps working.
    /// </summary>
    private void RunAsync(bool check)
    {
        if (_busy) { if (check) Cancel(); return; }   // while busy, the Check button is Cancel
        var (n, key) = FromFields();
        if (!check && n.Model.Length == 0) n.Model = "x";   // the list doesn't need a model
        _busy = true;
        _checking = check;
        _started = DateTime.Now;
        var cts = _cts = new CancellationTokenSource(check ? CheckTimeout : TimeSpan.FromSeconds(30));
        EnableWindow(_ctl[ID_LOAD], false);
        SetWindowText(_ctl[ID_CHECK], L.T("nw.cancel"));
        SetStatus(L.T(check ? "nw.checking" : "nw.loading"));
        SetTimer(_hwnd, (UIntPtr)TimerId, 1000, IntPtr.Zero);
        Log.Write($"Network model {(check ? "check" : "list")}: {n.Provider} {NetBackend.Url(n, "")} {n.Model}");
        Task.Run(async () =>
        {
            string status;
            List<string>? models = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (check)
                {
                    using var be = new NetBackend(n, key);
                    var answer = (await be.GenerateChatAsync("[{\"role\":\"user\",\"content\":\"Reply with one word: OK\"}]", 16, cts.Token)).Trim();
                    if (answer.Length == 0) answer = "-";
                    status = L.T("nw.check_ok", answer.Length > 40 ? answer[..40] : answer, sw.Elapsed.TotalSeconds);
                }
                else
                {
                    models = await NetModels.ListModelsAsync(n, key, cts.Token);
                    status = L.T("nw.loaded", models.Count);
                }
            }
            catch (OperationCanceledException)
            {
                // Our own timeout or the Cancel button
                status = sw.Elapsed >= (check ? CheckTimeout : TimeSpan.FromSeconds(30)) - TimeSpan.FromSeconds(1)
                    ? L.T("err.net_timeout", NetModels.Find(n.Provider).Title) : L.T("nw.cancelled");
            }
            catch (Exception ex) { status = ex is NetModelException ? ex.Message : L.T("err.net_unreachable", n.BaseUrl, ex.Message); }
            Log.Write($"Network model {(check ? "check" : "list")}: {status} ({sw.Elapsed.TotalSeconds:F1} s)");
            _host.Invoke(() =>
            {
                if (_cts == cts) Finish();
                cts.Dispose();
                if (_hwnd == IntPtr.Zero) return;
                if (models != null)
                {
                    var cmb = _ctl[ID_MODEL];
                    var current = GetText(cmb);
                    SendMessage(cmb, CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
                    foreach (var m in models) SendMessage(cmb, CB_ADDSTRING, IntPtr.Zero, m);
                    SetWindowText(cmb, current);
                    if (models.Count > 0 && current.Length == 0) SendMessage(cmb, 0x014F /* CB_SHOWDROPDOWN */, (IntPtr)1, IntPtr.Zero);
                }
                SetStatus(status);
            });
        });
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Back to the normal state (the request finished, was cancelled or the window closes).</summary>
    private void Finish()
    {
        _busy = false;
        _cts = null;
        if (_hwnd == IntPtr.Zero) return;
        KillTimer(_hwnd, (UIntPtr)TimerId);
        EnableWindow(_ctl[ID_LOAD], true);
        SetWindowText(_ctl[ID_CHECK], L.T("nw.check"));
    }

    /// <summary>Once a second while waiting: "Checking... 12 s".</summary>
    private void OnTimer()
    {
        if (!_busy) return;
        SetStatus(L.T(_checking ? "nw.checking" : "nw.loading") + $" {(int)(DateTime.Now - _started).TotalSeconds} " + L.T("nw.sec"));
    }

    private void SetStatus(string text)
    {
        if (_hwnd != IntPtr.Zero && _ctl.TryGetValue(ID_STATUS, out var h)) SetWindowText(h, text);
    }

    // ------------------------------------------------------------------ messages

    /// <summary>Enter on a button presses it, elsewhere saves; Esc closes.</summary>
    public bool PreTranslate(ref MSG msg)
    {
        if (msg.message != WM_KEYDOWN || _hwnd == IntPtr.Zero) return false;
        int vk = (int)msg.wParam;
        if (vk == VK_ESCAPE) { Close(); return true; }
        if (vk != VK_RETURN) return false;
        var focus = GetFocus();
        if (ClassOf(focus).Equals("Button", StringComparison.OrdinalIgnoreCase) && GetParent(focus) == _hwnd)
            SendMessage(focus, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        else Save();
        return true;
    }

    private void OnCommand(int id, int code)
    {
        switch (id)
        {
            case ID_LIST when code == LBN_SELCHANGE:
                {
                    int i = (int)SendMessage(_ctl[ID_LIST], LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                    if (i >= 0 && i < _list.Count) { ShowFields(_list[i].Copy()); EnableWindow(_ctl[ID_DELETE], true); }
                    break;
                }
            case ID_PROVIDER when code == CBN_SELCHANGE: OnProviderChanged(); break;
            case ID_ADD when code == BN_CLICKED: NewConnection(); break;
            case ID_DELETE when code == BN_CLICKED: Delete(); break;
            case ID_LOAD when code == BN_CLICKED: RunAsync(check: false); break;
            case ID_CHECK when code == BN_CLICKED: RunAsync(check: true); break;
            case ID_SAVE when code == BN_CLICKED: Save(); break;
            case ID_CLOSE when code == BN_CLICKED: Close(); break;
        }
    }

    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static IntPtr StaticProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = Current;
        if (self != null && (self._hwnd == hWnd || self._hwnd == IntPtr.Zero))
        {
            try
            {
                switch (msg)
                {
                    case WM_CTLCOLORSTATIC:
                        SetBkMode(wParam, 1);
                        return GetSysColorBrush(COLOR_BTNFACE);
                    case WM_COMMAND:
                        self.OnCommand((int)wParam & 0xFFFF, ((int)wParam >> 16) & 0xFFFF);
                        return IntPtr.Zero;
                    case WM_TIMER:
                        self.OnTimer();
                        return IntPtr.Zero;
                    case WM_CLOSE:
                        self.Close();
                        return IntPtr.Zero;
                    case WM_DESTROY:
                        self.Cancel();   // a running request is not needed any more
                        KillTimer(hWnd, (UIntPtr)TimerId);
                        self._hwnd = IntPtr.Zero;
                        self._ctl.Clear();
                        if (Current == self) Current = null;
                        return IntPtr.Zero;
                    case WM_NCDESTROY:
                        if (self._font != IntPtr.Zero) { DeleteObject(self._font); self._font = IntPtr.Zero; }
                        break;
                }
            }
            catch (Exception ex) { Log.Write("Error in the network models window: " + ex); }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
