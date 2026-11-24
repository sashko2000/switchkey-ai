using System.Runtime.InteropServices;

namespace KeyboardSwitcher;

/// <summary>
/// Is the keyboard focus in the code editor of Visual Studio? There auto-correction is off, while the other fields
/// (search, Solution Explorer, chat...) keep it. The editor has no window of its own (it is drawn inside the WPF main
/// window), so the focused element is asked through UI Automation - on a background thread when a word starts, never
/// inside the keyboard hook; by the time the word ends (Space) the answer is there.
/// UI Automation is asked only when the focused window is a WPF one. Web views (WebView2 tool windows such as chat
/// panels, Electron apps like VS Code) are never asked: a UI Automation client makes Chromium build its whole
/// accessibility tree, and Visual Studio hung while typing in a WebView2 chat window (20.11.2026).
/// </summary>
internal sealed class CodeEditorProbe : IDisposable
{
    /// <summary>Program (exe) and how its code editor looks to UI Automation (the focused element's ClassName).</summary>
    internal static bool IsCodeEditor(string exe, string? className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        // Visual Studio: the WPF text view of the editor (also the Immediate / Output windows - code too)
        return exe.Equals("devenv.exe", StringComparison.OrdinalIgnoreCase) && className == "WpfTextView";
    }

    internal static bool IsCodeEditorProgram(string exe) => exe.Equals("devenv.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// May UI Automation be asked about the focus in this window? Only WPF windows ("HwndWrapper[...]"): the editor of
    /// Visual Studio lives there. Not web views (Chrome_*, WebView2) and not other window kinds.
    /// </summary>
    internal static bool SafeToAsk(string focusClass) => focusClass.StartsWith("HwndWrapper[", StringComparison.Ordinal);

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private IntPtr _requested;          // the foreground window to look at
    private volatile bool _inEditor;
    private string _lastLogged = "";
    private uint _cachedPid;
    private string _cachedExe = "";
    private DateTime _cachedStart;                                  // when the program was started
    private readonly Dictionary<uint, DateTime> _pause = new();     // process -> don't ask UI Automation until

    /// <summary>
    /// Visual Studio is not asked during its first minute and a half: while it starts, a UI Automation client makes WPF
    /// build automation objects for the whole window, and Visual Studio hung (24.11.2026). Meanwhile its WPF windows
    /// count as the code editor (no auto-correction - better than changing code).
    /// </summary>
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(90);

    /// <summary>After the focus was in a web view inside a WPF window (WebView2 chat) or a slow answer - pause.</summary>
    internal static readonly TimeSpan PauseAfterWebView = TimeSpan.FromMinutes(2);

    public volatile bool Enabled = true;

    /// <summary>The focus was in a code editor when last checked.</summary>
    public bool InCodeEditor => Enabled && _inEditor;

    public CodeEditorProbe()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Code editor probe" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Called from the keyboard hook when a word starts: cheap, only wakes the background thread.</summary>
    public void Check(IntPtr foreground)
    {
        if (!Enabled) return;
        Interlocked.Exchange(ref _requested, foreground);
        _wake.Set();
    }

    private void Loop()
    {
        IUIAutomation? uia = null;
        bool broken = false;
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop) break;
            var hwnd = Interlocked.Exchange(ref _requested, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) continue;
            try
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != _cachedPid)
                {
                    _cachedPid = pid;
                    _cachedExe = Native.ProcessFileName(pid);
                    try { using var proc = System.Diagnostics.Process.GetProcessById((int)pid); _cachedStart = proc.StartTime; }
                    catch { _cachedStart = DateTime.MinValue; }
                }
                if (broken || !IsCodeEditorProgram(_cachedExe)) { _inEditor = false; continue; }

                // The window with the keyboard focus: only WPF windows are asked (see the class comment)
                var gi = new Native.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<Native.GUITHREADINFO>() };
                uint thread = Native.GetWindowThreadProcessId(hwnd, out _);
                var focusClass = Native.GetGUIThreadInfo(thread, ref gi) && gi.hwndFocus != IntPtr.Zero ? Native.ClassOf(gi.hwndFocus) : "";
                if (!SafeToAsk(focusClass))
                {
                    _inEditor = false;
                    var skip = $"{_cachedExe}: focus window {(focusClass.Length > 0 ? focusClass : "?")} -> auto-correction (not asked)";
                    if (skip != _lastLogged) { Log.Write(skip); _lastLogged = skip; }
                    continue;
                }

                // Starting up, busy, or recently answered from a web view: don't ask now
                var why = Hold(hwnd, pid);
                if (why != null)
                {
                    // Starting up: count as the editor; busy: keep the last answer; paused: auto-correct (the pause
                    // comes from a chat window, where the auto-correction is wanted)
                    _inEditor = why switch { "starting" => true, "busy" => _inEditor, _ => false };
                    var held = $"{_cachedExe}: {why} -> {(_inEditor ? "no auto-correction" : "auto-correction")} (not asked)";
                    if (held != _lastLogged) { Log.Write(held); _lastLogged = held; }
                    continue;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                uia ??= (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;
                string? cls = null;
                if (uia.GetFocusedElement(out var el) == 0 && el != null)
                {
                    try
                    {
                        // The element must belong to the program itself (this also checks the interface layout).
                        // Another process - a web view hosted inside the WPF window (WebView2 chat): pause, so that
                        // its browser engine is not asked again on every word
                        if (el.get_CurrentProcessId(out int elPid) != 0 || (uint)elPid != pid)
                        {
                            cls = null;
                            _pause[pid] = DateTime.Now + PauseAfterWebView;
                        }
                        else if (el.get_CurrentClassName(out var c) == 0) cls = c;
                    }
                    finally { Marshal.ReleaseComObject(el); }
                }
                if (sw.ElapsedMilliseconds > 500)
                {
                    _pause[pid] = DateTime.Now + PauseAfterWebView;
                    Log.Write($"{_cachedExe}: UI Automation answered in {sw.ElapsedMilliseconds} ms - pausing");
                }
                _inEditor = IsCodeEditor(_cachedExe, cls);
                var line = $"{_cachedExe}: focus {cls ?? "?"} -> {(_inEditor ? "code editor, no auto-correction" : "auto-correction")}";
                if (line != _lastLogged) { Log.Write(line); _lastLogged = line; }
            }
            catch (Exception ex)
            {
                // No UI Automation (or a broken registration): don't try again, auto-correct as before
                broken = true;
                _inEditor = false;
                Log.Write("UI Automation is not available, code editors are not detected: " + ex.Message);
            }
        }
        if (uia != null) Marshal.ReleaseComObject(uia);
    }

    /// <summary>Why the program must not be asked now (null - it may be asked).</summary>
    private string? Hold(IntPtr hwnd, uint pid)
    {
        if (_cachedStart != DateTime.MinValue && DateTime.Now - _cachedStart < StartupGrace) return "starting";
        if (_pause.TryGetValue(pid, out var until) && DateTime.Now < until) return "paused";
        // The window must answer a plain message quickly; a busy program is not loaded with UI Automation requests
        if (Native.SendMessageTimeout(hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0x0002 /* SMTO_ABORTIFHUNG */, 250, out _) == IntPtr.Zero)
            return "busy";
        return null;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }

    // ---------- UI Automation (UIAutomationClient.h); placeholders keep the vtable order, only the used methods are real

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        void ElementFromPoint();
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        void GetCurrentPropertyValue();
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        void GetCurrentPattern();
        void GetCachedPattern();
        void GetCachedParent();
        void GetCachedChildren();
        [PreserveSig] int get_CurrentProcessId(out int pid);
        void get_CurrentControlType();
        void get_CurrentLocalizedControlType();
        void get_CurrentName();
        void get_CurrentAcceleratorKey();
        void get_CurrentAccessKey();
        void get_CurrentHasKeyboardFocus();
        void get_CurrentIsKeyboardFocusable();
        void get_CurrentIsEnabled();
        void get_CurrentAutomationId();
        [PreserveSig] int get_CurrentClassName([MarshalAs(UnmanagedType.BStr)] out string name);
    }
}
