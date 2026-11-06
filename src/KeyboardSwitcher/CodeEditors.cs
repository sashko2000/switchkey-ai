using System.Runtime.InteropServices;

namespace KeyboardSwitcher;

/// <summary>
/// Is the keyboard focus in a code editor (Visual Studio, VS Code)? There auto-correction is off, while the other fields
/// of the same program (search, Solution Explorer, chat...) keep it. The editor has no window of its own (it is drawn
/// inside the main window), so the focused element is asked through UI Automation.
/// The question is asked on a background thread when a word starts - never inside the keyboard hook; by the time the word
/// ends (Space) the answer is there.
/// </summary>
internal sealed class CodeEditorProbe : IDisposable
{
    /// <summary>Program (exe) and how its code editor looks to UI Automation (the focused element's ClassName).</summary>
    internal static bool IsCodeEditor(string exe, string? className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        return exe.ToLowerInvariant() switch
        {
            // Visual Studio: the WPF text view of the editor (also the Immediate / Output windows - code too)
            "devenv.exe" => className == "WpfTextView",
            // VS Code: the hidden text area of the Monaco editor ("inputarea monaco-mouse-cursor-text")
            "code.exe" or "code - insiders.exe" or "cursor.exe" or "windsurf.exe" => className.Contains("inputarea", StringComparison.Ordinal),
            _ => false,
        };
    }

    internal static bool IsCodeEditorProgram(string exe) =>
        exe.ToLowerInvariant() is "devenv.exe" or "code.exe" or "code - insiders.exe" or "cursor.exe" or "windsurf.exe";

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private IntPtr _requested;          // the foreground window to look at
    private volatile bool _inEditor;
    private string _lastLogged = "";
    private uint _cachedPid;
    private string _cachedExe = "";

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
                if (pid != _cachedPid) { _cachedPid = pid; _cachedExe = Native.ProcessFileName(pid); }
                if (broken || !IsCodeEditorProgram(_cachedExe)) { _inEditor = false; continue; }

                uia ??= (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;
                string? cls = null;
                if (uia.GetFocusedElement(out var el) == 0 && el != null)
                {
                    try
                    {
                        // Check that the interface layout is right: the element's process must be the foreground one
                        if (el.get_CurrentProcessId(out int elPid) != 0 || (uint)elPid != pid) cls = null;
                        else if (el.get_CurrentClassName(out var c) == 0) cls = c;
                    }
                    finally { Marshal.ReleaseComObject(el); }
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
