using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static KeyboardSwitcher.Native;

namespace KeyboardSwitcher;

/// <summary>Tray icon, menu and hotkeys. Runs on the app's main thread.</summary>
internal sealed class TrayApp : IDisposable, SettingsWindow.IHost, AssistantWindow.IHost
{
    private const uint WM_TRAY = WM_APP + 1, WM_INVOKE = WM_APP + 2;
    private const int HK_LAST = 1, HK_SELECTION = 2, HK_AI = 3, HK_TRANSLATE = 4;
    private static readonly int[] AllHotkeyIds = { HK_LAST, HK_SELECTION, HK_AI, HK_TRANSLATE };
    private const int CMD_AUTO = 1, CMD_LAST = 2, CMD_SEL = 3, CMD_AI = 4, CMD_TRANSLATE = 5, CMD_RELOAD = 6, CMD_UNLOAD = 7, CMD_ASSISTANT = 13,
        CMD_SETTINGS = 8, CMD_CANCEL_INSTALL = 9, CMD_LOG = 10, CMD_ABOUT = 11, CMD_EXIT = 12;
    private const string AppName = "KeyboardSwitcher";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly KeyboardMonitor _monitor;
    private readonly AiCorrector _ai;
    private Settings _settings;

    private IntPtr _hwnd;
    private WndProc? _wndProc;
    private uint _taskbarCreated;
    private IntPtr _iconOn, _iconOff, _iconBusy;
    private readonly ConcurrentQueue<Action> _queue = new();
    private volatile bool _aiBusy;

    private SettingsWindow? _settingsWindow;
    private AssistantWindow? _assistant;
    private IntPtr _assistantSourceWnd;      // window the assistant's text was taken from

    /// <summary>ToWindow - result to the assistant window, otherwise - replaces the selected text.</summary>
    private sealed record AiJob(AssistantTask Task, string Text, string? Prev, uint CopySeq, IntPtr Fg, bool ToWindow);

    /// <summary>Translation language pair for the menu: "RU <-> EN".</summary>
    private string PairCodes => $"{_settings.TranslateLangA.ToUpperInvariant()} <-> {_settings.TranslateLangB.ToUpperInvariant()}";
    private AiJob? _pending;                 // task waiting for the model to load
    private const int IdleTimerId = 1;
    private CancellationTokenSource? _installCts;
    private string _installStatus = "";
    private string? _loadedModelDir;

    public TrayApp(Settings settings, KeyboardMonitor monitor, AiCorrector ai)
    {
        _settings = settings;
        _monitor = monitor;
        _ai = ai;
        _ai.StatusChanged += () => Invoke(OnAiStatusChanged);
        _monitor.WordRejected += word => Invoke(() =>
        {
            if (!_settings.NeverSwitch.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                _settings.NeverSwitch.Add(word);
                _settings.Save();
            }
        });
    }

    public void Run()
    {
        CreateWindow();
        LoadIcons();
        AddTrayIcon();
        RegisterHotkeys();
        ApplySettings(first: true);
        SetTimer(_hwnd, (UIntPtr)IdleTimerId, 30_000, IntPtr.Zero); // model idle check

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            // Tab/Enter/Esc in the settings window
            var sw = _settingsWindow?.Hwnd ?? IntPtr.Zero;
            if (sw != IntPtr.Zero && (_settingsWindow!.PreTranslate(ref msg) || IsDialogMessage(sw, ref msg))) continue;
            var aw = _assistant?.Hwnd ?? IntPtr.Zero;
            if (aw != IntPtr.Zero && (_assistant!.PreTranslate(ref msg) || IsDialogMessage(aw, ref msg))) continue;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>Run an action on the main thread.</summary>
    public void Invoke(Action a)
    {
        _queue.Enqueue(a);
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    // ------------------------------------------------------------------ window and icon

    private void CreateWindow()
    {
        _wndProc = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "KeyboardSwitcherWindow",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, wc.lpszClassName, AppName, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException(L.T("err.window_create", Marshal.GetLastWin32Error()));
        Input.OwnerWindow = _hwnd;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    private void LoadIcons()
    {
        IntPtr Load(string file)
        {
            // The icons are embedded in the exe; Windows loads icons from files, so they are written to the local data folder
            var path = Path.Combine(AppContext.BaseDirectory, "Icons", file);
            if (!File.Exists(path))
            {
                path = Path.Combine(AiSetup.BaseDir, "icons", file);
                try
                {
                    using var res = typeof(TrayApp).Assembly.GetManifestResourceStream("KeyboardSwitcher.Icons." + file);
                    if (res != null)
                    {
                        using var ms = new MemoryStream();
                        res.CopyTo(ms);
                        var bytes = ms.ToArray();
                        if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            File.WriteAllBytes(path, bytes);
                        }
                    }
                }
                catch (Exception ex) { Log.Write("Icon " + file + ": " + ex.Message); }
            }
            var h = File.Exists(path) ? LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE) : IntPtr.Zero;
            return h != IntPtr.Zero ? h : LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        }
        _iconOn = Load("on.ico");
        _iconOff = Load("off.ico");
        _iconBusy = Load("busy.ico");
    }

    private NOTIFYICONDATA IconData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _aiBusy ? _iconBusy : _monitor.AutoSwitch ? _iconOn : _iconOff,
        szTip = Tooltip(),
        szInfo = "",
        szInfoTitle = "",
    };

    private string Tooltip()
    {
        var t = L.T("tip.auto", AppName, L.T(_monitor.AutoSwitch ? "tip.on" : "tip.off"));
        if (_settings.AiEnabled || InstallRunning) t += "\n" + L.T("tip.model", _aiBusy ? L.T("tip.working") : AiStatusText);
        return t.Length > 127 ? t[..127] : t;
    }

    private void AddTrayIcon()
    {
        var d = IconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIcon(NIM_ADD, ref d);
    }

    private void UpdateIcon()
    {
        var d = IconData(NIF_ICON | NIF_TIP);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    public void Notify(string title, string text, bool warning = false)
    {
        var d = IconData(NIF_INFO);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_TRAY:
                    int ev = (int)lParam & 0xFFFF;
                    if (ev == WM_RBUTTONUP || ev == WM_LBUTTONUP) ShowMenu();
                    return IntPtr.Zero;
                case WM_HOTKEY:
                    OnHotkey((int)wParam);
                    return IntPtr.Zero;
                case WM_INVOKE:
                    while (_queue.TryDequeue(out var a)) a();
                    return IntPtr.Zero;
                case WM_TIMER:
                    UnloadIfIdle();
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0) AddTrayIcon(); // Explorer restarted
        }
        catch (Exception ex)
        {
            Log.Write("Error: " + ex);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ menu

    private void ShowMenu()
    {
        var m = CreatePopupMenu();
        AppendMenu(m, MF_STRING | (_monitor.AutoSwitch ? MF_CHECKED : 0), (UIntPtr)CMD_AUTO, L.T("menu.auto"));
        AppendMenu(m, MF_STRING, (UIntPtr)CMD_LAST, L.T("menu.last") + "\t" + _settings.HotkeyConvertLastWord);
        AppendMenu(m, MF_STRING, (UIntPtr)CMD_SEL, L.T("menu.sel") + "\t" + _settings.HotkeyConvertSelection);
        if (_settings.AiEnabled || InstallRunning)
        {
            AppendMenu(m, MF_STRING, (UIntPtr)CMD_AI, L.T("menu.ai") + "\t" + _settings.HotkeyAiFix);
            AppendMenu(m, MF_STRING, (UIntPtr)CMD_TRANSLATE, L.T("menu.translate", PairCodes) + "\t" + _settings.HotkeyTranslate);
            AppendMenu(m, MF_STRING, (UIntPtr)CMD_ASSISTANT, L.T("menu.assistant"));
            AppendMenu(m, MF_SEPARATOR, UIntPtr.Zero, null);
            var status = L.T("tip.model", AiStatusText).Replace("\t", " ");
            AppendMenu(m, MF_STRING | MF_GRAYED, UIntPtr.Zero, status.Length > 90 ? status[..90] + "..." : status);
            if (InstallRunning) AppendMenu(m, MF_STRING, (UIntPtr)CMD_CANCEL_INSTALL, L.T("menu.cancel_install"));
            else
            {
                AppendMenu(m, MF_STRING, (UIntPtr)CMD_RELOAD, L.T("menu.reload"));
                AppendMenu(m, MF_STRING | (_ai.Status == AiCorrector.State.Ready ? 0 : MF_GRAYED), (UIntPtr)CMD_UNLOAD, L.T("menu.unload"));
            }
        }
        AppendMenu(m, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(m, MF_STRING, (UIntPtr)CMD_SETTINGS, L.T("menu.settings"));
        AppendMenu(m, MF_STRING, (UIntPtr)CMD_LOG, L.T("menu.log"));
        AppendMenu(m, MF_STRING, (UIntPtr)CMD_ABOUT, L.T("menu.about"));
        AppendMenu(m, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(m, MF_STRING, (UIntPtr)CMD_EXIT, L.T("menu.exit"));

        GetCursorPos(out var p);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(m, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(m);

        switch (cmd)
        {
            case CMD_AUTO:
                _settings.AutoSwitch = !_settings.AutoSwitch;
                _monitor.AutoSwitch = _settings.AutoSwitch;
                _settings.Save();
                UpdateIcon();
                break;
            case CMD_LAST: Notify(AppName, L.T("note.use_last", _settings.HotkeyConvertLastWord)); break;
            case CMD_SEL: Notify(AppName, L.T("note.select_press", _settings.HotkeyConvertSelection)); break;
            case CMD_AI: Notify(AppName, L.T("note.select_press", _settings.HotkeyAiFix)); break;
            case CMD_TRANSLATE: Notify(AppName, L.T("note.select_press", _settings.HotkeyTranslate)); break;
            case CMD_ASSISTANT:
                _assistantSourceWnd = IntPtr.Zero;
                ShowAssistant("", AssistantTask.FromId(_settings.AssistantLastTask), runNow: false);
                break;
            case CMD_UNLOAD:
                _ai.Unload();
                _loadedModelDir = null;
                Log.Write("Model unloaded from memory on command");
                break;
            case CMD_RELOAD:
                if (_ai.Status == AiCorrector.State.Loading) Notify(AppName, L.T("note.already_loading", _ai.Message));
                else LoadModel();
                break;
            case CMD_SETTINGS: OpenSettings(); break;
            case CMD_CANCEL_INSTALL: _installCts?.Cancel(); break;
            case CMD_LOG: if (File.Exists(Log.FilePath)) Open("notepad.exe", Log.FilePath); break;
            case CMD_ABOUT:
                MessageBox(_hwnd,
                    L.T("about.text", _settings.HotkeyConvertLastWord, _settings.HotkeyConvertSelection, _settings.HotkeyAiFix, _settings.HotkeyTranslate),
                    AppName, 0x40);
                break;
            case CMD_EXIT:
                _installCts?.Cancel();
                KillTimer(_hwnd, (UIntPtr)IdleTimerId);
                _assistant?.Close();
                _settingsWindow?.Close();
                DestroyWindow(_hwnd);
                break;
        }
    }

    private static void Open(string exe, string arg)
    {
        try { Process.Start(new ProcessStartInfo(exe, $"\"{arg}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Failed to open: " + ex.Message); }
    }

    // ------------------------------------------------------------------ settings

    private void ApplySettings(bool first)
    {
        _monitor.AutoSwitch = _settings.AutoSwitch;
        _monitor.SetExcludedApps(_settings.ExcludedApps);
        _monitor.SetNeverSwitch(_settings.NeverSwitch);
        _monitor.SetPriority(_settings.EffectiveLayoutPriority);
        if (!first && _settingsWindow == null) RegisterHotkeys();
        UpdateIcon();
        if (_settings.AiEnabled)
        {
            // At startup only load what is already installed; installation happens from the settings window
            if (first)
            {
                if (!_settings.AiLoadOnDemand && AiPlatform.Installed(_settings)) LoadModel();
            }
            else EnsureAi();
        }
        else
        {
            _installCts?.Cancel();
            _ai.Unload();
            _loadedModelDir = null;
        }
    }

    private void LoadModel()
    {
        var key = AiPlatform.InstalledKey(_settings);
        if (key == null)
        {
            _ai.Unload();
            _loadedModelDir = null;
            Notify(AppName, L.T("note.not_installed"), true);
            return;
        }
        _loadedModelDir = key;
        var s = _settings;
        // Device detection (OpenVINO plugins, Vulkan list) is done in the background together with loading
        _ai.LoadAsync(AiPlatform.ModelName(s), () => AiPlatform.Candidates(s));
    }

    // ------------------------------------------------------------------ settings window and model installation

    public Settings Settings => _settings;
    public bool Autostart => IsAutostart();
    public bool InstallRunning => _installCts != null;

    public string AiStatusText =>
        InstallRunning ? L.T("st.installing", _installStatus.Length > 0 ? _installStatus : L.T("st.preparing"))
        : !_settings.AiEnabled ? L.T("st.off")
        : _ai.Status == AiCorrector.State.Off
            ? (AiPlatform.Installed(_settings) ? L.T("st.not_in_memory") : L.T("st.not_installed"))
        : $"{_ai.ModelName}: {_ai.Message}" + MemoryText();

    private string MemoryText()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            long bytes = p.PrivateMemorySize64;
            // A local model server (AMD engines) keeps the model in its own process
            try { var c = _ai.Child; if (c != null) { c.Refresh(); bytes += c.PrivateMemorySize64; } } catch { }
            return L.T("st.memory", bytes / 1073741824.0);
        }
        catch { return ""; }
    }

    /// <summary>Unload the model if it hasn't been used for a while.</summary>
    private void UnloadIfIdle()
    {
        int minutes = _settings.AiUnloadAfterMinutes;
        if (minutes <= 0 || _ai.Status != AiCorrector.State.Ready || _aiBusy || _pending != null) return;
        if ((DateTime.Now - _ai.LastUsed).TotalMinutes < minutes) return;
        _ai.Unload();
        _loadedModelDir = null;
        Log.Write($"Model unloaded from memory after {minutes} min idle");
    }

    private void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            // While the window is open our hotkeys are not intercepted - they can be typed into the fields
            foreach (var id in AllHotkeyIds) UnregisterHotKey(_hwnd, id);
            _settingsWindow = new SettingsWindow(this);
        }
        if (!_settingsWindow.Show(_iconOn))
        {
            _settingsWindow = null;
            RegisterHotkeys();
            return;
        }
        if (AiPlatform.CanDetectDevices)
            Task.Run(AiPlatform.Devices).ContinueWith(t => Invoke(() => _settingsWindow?.UpdateDevices(t.IsCompletedSuccessfully ? t.Result : null)));
    }

    public void SettingsWindowClosed()
    {
        _settingsWindow = null;
        RegisterHotkeys();
    }

    private void OnAiStatusChanged()
    {
        UpdateIcon();
        _settingsWindow?.UpdateStatus(AiStatusText);

        // The task was waiting for the model to load
        if (_pending != null)
        {
            if (_ai.Status == AiCorrector.State.Ready)
            {
                var job = _pending;
                _pending = null;
                Execute(job);
            }
            else if (_ai.Status == AiCorrector.State.Failed)
            {
                var job = _pending;
                _pending = null;
                if (job.ToWindow) _assistant?.SetError(L.T("err.model_failed", _ai.Message));
                else Input.RestoreIfUnchanged(job.Prev, job.CopySeq);
                Notify(AppName, L.T("note.model_failed", _ai.Message), true);
            }
        }
    }

    public bool Apply(Settings s, bool autostart)
    {
        // Hotkey check
        var keys = new[] { s.HotkeyConvertLastWord, s.HotkeyConvertSelection, s.HotkeyAiFix, s.HotkeyTranslate }.Where(k => k.Length > 0).ToList();
        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count)
        {
            MessageBox(_settingsWindow?.Hwnd ?? _hwnd, L.T("msg.dup_hotkey"), AppName, 0x30);
            return false;
        }
        foreach (var k in keys)
            if (!Hotkey.TryParse(k, out _, out _))
            {
                MessageBox(_settingsWindow?.Hwnd ?? _hwnd, L.T("msg.bad_hotkey", k), AppName, 0x30);
                return false;
            }

        bool modelChanged = s.AiModel != _settings.AiModel || !string.Equals(s.AiDevice, _settings.AiDevice, StringComparison.OrdinalIgnoreCase);
        bool langChanged = s.UiLanguage != _settings.UiLanguage;
        _settings = s;
        _settings.Save();
        try { SetAutostart(autostart); } catch (Exception ex) { Log.Write("Autostart: " + ex.Message); }
        if (modelChanged && !InstallRunning) { _ai.Unload(); _loadedModelDir = null; }
        ApplySettings(first: false);
        if (langChanged)
        {
            // New interface language: recreate the windows
            L.SetLanguage(_settings.UiLanguage);
            _assistant?.Close();
            // On "Apply" the settings window stays open - recreate it in the new language
            Invoke(() => { if (_settingsWindow != null) { _settingsWindow.Close(); Invoke(OpenSettings); } });
            UpdateIcon();
        }
        return true;
    }

    /// <summary>Model correction is enabled: load the model or (with consent) install everything needed.</summary>
    private void EnsureAi()
    {
        if (InstallRunning) return;
        var dir = AiPlatform.InstalledKey(_settings);
        if (dir != null)
        {
            if (_settings.AiLoadOnDemand)
            {
                // Into memory only on first use; a different model - unload the old one
                if (_loadedModelDir != null && dir != _loadedModelDir) { _ai.Unload(); _loadedModelDir = null; }
            }
            else if (dir != _loadedModelDir || _ai.Status is AiCorrector.State.Off or AiCorrector.State.Failed) LoadModel();
            return;
        }

        var (steps, gb) = AiPlatform.Plan(_settings);
        var text = L.T("msg.install_plan", string.Join("\n- ", steps), gb, AiSetup.BaseDir);
        if (MessageBox(_settingsWindow?.Hwnd ?? _hwnd, text, AppName, 0x24 /* MB_YESNO | MB_ICONQUESTION */) != 6 /* IDYES */)
        {
            _settings.AiEnabled = false;
            _settings.Save();
            UpdateIcon();
            return;
        }
        StartInstall();
    }

    private sealed class UiProgress : IProgress<string>
    {
        private readonly TrayApp _app;
        private long _last;
        public UiProgress(TrayApp app) => _app = app;
        public void Report(string value)
        {
            // Frequent progress lines (with percentages) are thinned out; step changes are always shown
            long now = Environment.TickCount64;
            if (value.Contains('%') && now - Interlocked.Read(ref _last) < 300) return;
            Interlocked.Exchange(ref _last, now);
            _app.Invoke(() =>
            {
                _app._installStatus = value;
                _app.UpdateIcon();
                _app._settingsWindow?.UpdateStatus(_app.AiStatusText);
            });
        }
    }

    private void StartInstall()
    {
        var info = AiSetup.FindModelInfo(_settings.AiModel);
        var snapshot = _settings;
        var cts = new CancellationTokenSource();
        _installCts = cts;
        _installStatus = L.T("st.preparing");
        OnAiStatusChanged();
        Log.Write($"Installing for model {info.Id} ({(AiPlatform.Amd ? "AMD engines" : "OpenVINO")})");
        var progress = new UiProgress(this);
        Task.Run(async () =>
        {
            string? error = null;
            try { await AiPlatform.InstallAsync(snapshot, progress, cts.Token); }
            catch (OperationCanceledException) { error = L.T("st.install_cancelled"); }
            catch (Exception ex) { error = ex.Message; Log.Write("Installation error: " + ex); }

            Invoke(() =>
            {
                _installCts = null;
                _installStatus = "";
                if (error != null)
                {
                    Notify(AppName, L.T("note.install_failed", error), true);
                    _settings.AiEnabled = false;
                    _settings.Save();
                }
                else if (_settings.AiLoadOnDemand)
                {
                    Notify(AppName, L.T("note.installed_ondemand", _settings.HotkeyAiFix, _settings.HotkeyTranslate));
                }
                else
                {
                    Notify(AppName, L.T("note.installed_loading"));
                    if (_settings.AiEnabled) LoadModel();
                }
                RegisterHotkeysIfIdle();
                _settingsWindow?.SetAiChecked(_settings.AiEnabled);
                OnAiStatusChanged();
            });
        });
    }

    private void RegisterHotkeysIfIdle()
    {
        if (_settingsWindow == null) RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        foreach (var id in AllHotkeyIds) UnregisterHotKey(_hwnd, id);
        var set = new HashSet<(uint, int)>();
        var failed = new List<string>();
        void Reg(int id, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!Hotkey.TryParse(text, out uint mods, out int vk)) { failed.Add(L.T("hk.unrecognized", text)); return; }
            if (!RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, (uint)vk)) { failed.Add(L.T("hk.taken", text)); return; }
            set.Add((mods, vk));
        }
        Reg(HK_LAST, _settings.HotkeyConvertLastWord);
        Reg(HK_SELECTION, _settings.HotkeyConvertSelection);
        if (_settings.AiEnabled)
        {
            Reg(HK_AI, _settings.HotkeyAiFix);
            Reg(HK_TRANSLATE, _settings.HotkeyTranslate);
        }
        _monitor.Hotkeys = set;
        if (failed.Count > 0)
        {
            Log.Write("Hotkeys not registered: " + string.Join(", ", failed));
            Notify(AppName, L.T("note.hotkeys_failed", string.Join(", ", failed)), true);
        }
    }

    private static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(AppName) is string;
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(AppName, false);
    }

    // ------------------------------------------------------------------ hotkeys

    private void OnHotkey(int id)
    {
        // Right away, while Ctrl/Alt are still down: don't let the window open its menu when Alt is released
        Input.MaskAltMenu();
        switch (id)
        {
            case HK_LAST:
                // Don't copy without a selection: many editors copy the whole line on Ctrl+C
                _monitor.ConvertLastWord();
                break;
            case HK_SELECTION:
                ConvertSelection();
                break;
            case HK_AI:
                BeginAiJob(new AssistantTask(AiAction.Fix), toWindow: false);
                break;
            case HK_TRANSLATE:
                BeginAiJob(new AssistantTask(AiAction.TranslatePair), toWindow: _settings.TranslateToWindow);
                break;
        }
    }

    private void ConvertSelection()
    {
        var text = Input.CopySelection(out var prev, out _);
        if (text == null) return;
        var from = Layouts.GuessLang(text);
        var to = _monitor.BestTextTarget(text, from);
        var converted = Layouts.ConvertText(text, from, to);
        _monitor.ResetWord();
        Input.PasteText(converted, prev);
        Input.SwitchLayout(to);
    }

    /// <summary>Correct or translate the selected text with the model. If the model isn't in memory - load it and continue.</summary>
    private void BeginAiJob(AssistantTask task, bool toWindow)
    {
        if (_aiBusy || _pending != null) { Notify(AppName, L.T("note.busy")); return; }
        if (InstallRunning) { Notify(AppName, L.T("note.still_installing", _installStatus)); return; }
        bool installed = AiPlatform.Installed(_settings);
        if (_ai.Status != AiCorrector.State.Ready && !installed)
        {
            Notify(AppName, L.T("note.not_installed"), true);
            return;
        }

        var fg = GetForegroundWindow();
        var text = Input.CopySelection(out var prev, out uint copySeq);
        if (text == null)
        {
            if (toWindow) { _assistantSourceWnd = IntPtr.Zero; ShowAssistant("", task, runNow: false); return; }
            Notify(AppName, L.T(task.Action == AiAction.Fix ? "note.select_fix" : "note.select_translate"));
            return;
        }
        if (text.Length > _settings.AiMaxChars)
        {
            Input.RestoreIfUnchanged(prev, copySeq);
            Notify(AppName, L.T("note.too_long", text.Length, _settings.AiMaxChars), true);
            return;
        }

        if (toWindow)
        {
            // The result goes to the assistant window: restore the clipboard right away
            Input.RestoreIfUnchanged(prev, copySeq);
            _assistantSourceWnd = fg;
            ShowAssistant(text, task, runNow: true);
            return;
        }
        var job = new AiJob(task, text, prev, copySeq, fg, ToWindow: false);
        if (_ai.Status == AiCorrector.State.Ready) { Execute(job); return; }

        _pending = job;
        if (_ai.Status != AiCorrector.State.Loading) LoadModel();
        Notify(AppName, L.T("note.loading_then"));
    }

    private void Execute(AiJob job)
    {
        _aiBusy = true;
        UpdateIcon();
        var input = job.Task.Action == AiAction.Fix ? _monitor.FixLayoutInText(job.Text) : job.Text;
        string a = _settings.TranslateLangA, b = _settings.TranslateLangB, ui = _settings.UiLanguage;
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            string? result = null, error = null;
            try { result = _ai.Transform(job.Task, input, a, b, ui); }
            catch (Exception ex) { error = ex.Message; Log.Write("Model error: " + ex.Message); }
            Log.Write($"{job.Task.Id}: {job.Text.Length} characters in {sw.Elapsed.TotalSeconds:F1} s");
            Invoke(() => Finish(job, result, error));
        });
    }

    private void Finish(AiJob job, string? result, string? error)
    {
        _aiBusy = false;
        UpdateIcon();
        if (job.ToWindow)
        {
            if (_assistant == null) return; // the window has already been closed
            if (error != null || string.IsNullOrWhiteSpace(result)) _assistant.SetError(error ?? L.T("err.empty"));
            else _assistant.SetResult(result!);
            return;
        }
        if (error != null || string.IsNullOrWhiteSpace(result))
        {
            Input.RestoreIfUnchanged(job.Prev, job.CopySeq);
            Notify(AppName, L.T("note.failed", error ?? L.T("err.empty")), true);
            return;
        }
        if (job.Task.Action == AiAction.Fix && result == job.Text)
        {
            Input.RestoreIfUnchanged(job.Prev, job.CopySeq);
            Notify(AppName, L.T("note.no_errors"));
            return;
        }
        if (GetForegroundWindow() == job.Fg && GetClipboardSequenceNumber() == job.CopySeq)
        {
            Input.PasteText(result!, job.Prev);
        }
        else
        {
            // The user moved to another window - don't paste blindly
            Input.SetClipboardText(result!);
            Notify(AppName, L.T("note.copied_window_changed"));
        }
    }

    // ------------------------------------------------------------------ assistant window

    private void ShowAssistant(string text, AssistantTask task, bool runNow)
    {
        _assistant ??= new AssistantWindow(this);
        _assistant.Show(text, task, runNow, _iconOn);
    }

    public string LangA => _settings.TranslateLangA;
    public string LangB => _settings.TranslateLangB;

    public bool AiBusy => _aiBusy || _pending != null;

    public AssistantTask LastTask
    {
        get => AssistantTask.FromId(_settings.AssistantLastTask);
        set { if (_settings.AssistantLastTask != value.Id) { _settings.AssistantLastTask = value.Id; _settings.Save(); } }
    }

    public bool CanPasteToSource => _assistantSourceWnd != IntPtr.Zero && IsWindow(_assistantSourceWnd);

    public void RunAssistant(AssistantTask task, string text)
    {
        if (InstallRunning) { _assistant?.SetError(L.T("err.still_installing", _installStatus)); return; }
        bool installed = AiPlatform.Installed(_settings);
        if (!_settings.AiEnabled || (_ai.Status != AiCorrector.State.Ready && !installed))
        {
            _assistant?.SetError(L.T("err.not_installed"));
            return;
        }
        if (text.Length > _settings.AiMaxChars)
        {
            _assistant?.SetError(L.T("err.too_long", text.Length, _settings.AiMaxChars));
            return;
        }
        var job = new AiJob(task, text, null, 0, _assistantSourceWnd, ToWindow: true);
        if (_ai.Status == AiCorrector.State.Ready) { Execute(job); return; }
        _pending = job;
        if (_ai.Status != AiCorrector.State.Loading) LoadModel();
        _assistant?.SetBusy(true, L.T("st.loading_model"));
    }

    public bool PasteToSource(string text)
    {
        if (!CanPasteToSource) return false;
        var target = _assistantSourceWnd;
        _assistant?.Close();
        // Go back to the original window: the selection usually remains there - paste over it
        SetForegroundWindow(target);
        Thread.Sleep(150);
        if (GetForegroundWindow() != target) { Input.SetClipboardText(text); Notify(AppName, L.T("note.cant_return")); return true; }
        Input.PasteText(text, Input.GetClipboardText());
        return true;
    }

    public void AssistantClosed() => _assistant = null;

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            var d = IconData(0);
            Shell_NotifyIcon(NIM_DELETE, ref d);
        }
    }
}

/// <summary>Parses strings like "Ctrl+Alt+Z".</summary>
internal static class Hotkey
{
    public static bool TryParse(string text, out uint mods, out int vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = raw.ToUpperInvariant();
            switch (p)
            {
                case "CTRL": case "CONTROL": mods |= MOD_CONTROL; continue;
                case "ALT": mods |= MOD_ALT; continue;
                case "SHIFT": mods |= MOD_SHIFT; continue;
                case "WIN": mods |= MOD_WIN; continue;
            }
            if (vk != 0) return false;
            if (p.StartsWith("0X") && int.TryParse(p[2..], System.Globalization.NumberStyles.HexNumber, null, out int hex) && hex is > 0 and < 256) vk = hex;
            else if (p.Length == 1 && (char.IsAsciiLetterUpper(p[0]) || char.IsAsciiDigit(p[0]))) vk = p[0];
            else if (p.Length is 2 or 3 && p[0] == 'F' && int.TryParse(p[1..], out int f) && f is >= 1 and <= 24) vk = 0x70 + f - 1;
            else vk = p switch
            {
                "PAUSE" or "BREAK" => VK_PAUSE,
                "SPACE" => VK_SPACE,
                "INSERT" or "INS" => VK_INSERT,
                "SCROLLLOCK" => 0x91,
                "`" or "TILDE" => Layouts.VK_OEM_3,
                _ => 0
            };
            if (vk == 0) return false;
        }
        return vk != 0;
    }

    /// <summary>Reverse conversion: (MOD_*, key) -> "Ctrl+Alt+Z".</summary>
    public static string Format(uint mods, int vk)
    {
        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        string key = vk switch
        {
            >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),
            VK_PAUSE => "Pause",
            VK_SPACE => "Space",
            VK_INSERT => "Insert",
            0x91 => "ScrollLock",
            Layouts.VK_OEM_3 => "`",
            _ => $"0x{vk:X2}",
        };
        parts.Add(key);
        return string.Join("+", parts);
    }
}
