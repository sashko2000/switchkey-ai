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
    [DataRow("Code.exe", "inputarea monaco-mouse-cursor-text", true)]   // VS Code editor
    [DataRow("Code.exe", "xterm-helper-textarea", false)]      // VS Code terminal
    [DataRow("Code.exe", "input", false)]                      // VS Code search box
    [DataRow("notepad.exe", "WpfTextView", false)]
    [DataRow("WINWORD.EXE", "inputarea", false)]
    public void IsCodeEditor(string exe, string? cls, bool expected) =>
        Assert.AreEqual(expected, CodeEditorProbe.IsCodeEditor(exe, cls));

    [TestMethod]
    public void OnByDefault() => Assert.IsTrue(new Settings().SkipCodeEditors);
}
