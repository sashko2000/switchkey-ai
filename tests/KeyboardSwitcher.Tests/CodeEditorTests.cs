using KeyboardSwitcher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyboardSwitcher.Tests;

/// <summary>Which focused element is a code editor (no auto-correction there).</summary>
[TestClass]
public class CodeEditorTests
{
    [DataTestMethod]
    [DataRow("devenv.exe", "WpfTextView", true)]               // Visual Studio editor
    [DataRow("DEVENV.EXE", "WpfTextView", true)]
    [DataRow("devenv.exe", "TextBox", false)]                   // search, rename in Solution Explorer
    [DataRow("devenv.exe", "SearchTextBox", false)]
    [DataRow("devenv.exe", null, false)]
    [DataRow("Code.exe", "inputarea monaco-mouse-cursor-text", false)]  // VS Code is not asked (Chromium, see SafeToAsk)
    [DataRow("Code.exe", "xterm-helper-textarea", false)]      // VS Code terminal
    [DataRow("Code.exe", "input", false)]                      // VS Code search box
    [DataRow("notepad.exe", "WpfTextView", false)]
    [DataRow("WINWORD.EXE", "inputarea", false)]
    public void IsCodeEditor(string exe, string? cls, bool expected) =>
        Assert.AreEqual(expected, CodeEditorProbe.IsCodeEditor(exe, cls));

    [DataTestMethod]
    [DataRow("HwndWrapper[DefaultDomain;;6f2c1e7a-0b8e-4b0f-9e5c-2d7f1a3b4c5d]", true)]   // WPF: the VS main window, floating tool windows
    [DataRow("Chrome_RenderWidgetHostHWND", false)]   // WebView2 tool window (chat panels) - asking hung Visual Studio
    [DataRow("Chrome_WidgetWin_1", false)]
    [DataRow("Edit", false)]
    [DataRow("", false)]                              // the focus is in another thread / process
    public void SafeToAsk_OnlyWpfWindows(string cls, bool expected) =>
        Assert.AreEqual(expected, CodeEditorProbe.SafeToAsk(cls));

    [TestMethod]
    public void OnByDefault() => Assert.IsTrue(new Settings().SkipCodeEditors);
}
