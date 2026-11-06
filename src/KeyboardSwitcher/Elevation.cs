using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace KeyboardSwitcher;

/// <summary>
/// Working in programs that run as administrator. Windows doesn't pass the keystrokes of such windows to a program
/// without admin rights, so KeyboardSwitcher itself must run elevated. To avoid a UAC prompt at every start, a Task
/// Scheduler task "with highest privileges" starts it (at sign-in, if autostart is on). The task is deleted when the
/// option is turned off, and by "KeyboardSwitcher.exe --uninstall".
/// </summary>
internal static class Elevation
{
    public const string TaskName = "KeyboardSwitcher (administrator)";

    public static bool IsElevated
    {
        get
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    /// <summary>The task XML: our exe, highest privileges, only for this user, no time limit, also on battery.</summary>
    internal static string TaskXml(string exe, string user, bool atLogon)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        var trigger = atLogon
            ? $"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{X(user)}</UserId></LogonTrigger></Triggers>"
            : "<Triggers />";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>KeyboardSwitcher with administrator rights (works in programs run as administrator).</Description></RegistrationInfo>
              {trigger}
              <Principals>
                <Principal id="Author">
                  <UserId>{X(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{X(exe)}</Command></Exec>
              </Actions>
            </Task>
            """;
    }

    private static (int code, string output) Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, o.Result + e.Result);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    public static bool TaskExists() => Schtasks($"/Query /TN \"{TaskName}\"").code == 0;

    /// <summary>Does the task start the app at sign-in.</summary>
    public static bool TaskAtLogon()
    {
        var (code, xml) = Schtasks($"/Query /TN \"{TaskName}\" /XML");
        return code == 0 && xml.Contains("<LogonTrigger", StringComparison.Ordinal);
    }

    /// <summary>Create or update the task (needs admin rights). false - failed (see the log).</summary>
    public static bool CreateTask(bool atLogon)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var file = Path.Combine(Path.GetTempPath(), "KeyboardSwitcher-task.xml");
        try
        {
            File.WriteAllText(file, TaskXml(Environment.ProcessPath!, user, atLogon), System.Text.Encoding.Unicode);
            var (code, output) = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            Log.Write(code == 0 ? $"Task Scheduler: task created (at sign-in: {atLogon})" : "Task Scheduler: " + output.Trim());
            return code == 0;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Start the app through the task: elevated without a UAC prompt.</summary>
    public static bool RunTask()
    {
        var (code, output) = Schtasks($"/Run /TN \"{TaskName}\"");
        if (code != 0) Log.Write("Task Scheduler run: " + output.Trim());
        return code == 0;
    }

    /// <summary>Delete the task. Without admin rights Windows asks for confirmation (UAC).</summary>
    public static bool DeleteTask()
    {
        if (!TaskExists()) return true;
        if (IsElevated)
        {
            var (code, output) = Schtasks($"/Delete /TN \"{TaskName}\" /F");
            Log.Write(code == 0 ? "Task Scheduler: task deleted" : "Task Scheduler delete: " + output.Trim());
            return code == 0;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit(30000);
            Log.Write("Task Scheduler: task deleted (with confirmation), exit code " + p.ExitCode);
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) { Log.Write("Task Scheduler delete cancelled: " + ex.Message); return false; }
    }

    /// <summary>Start a new copy with admin rights (UAC prompt). false - the user said no.</summary>
    public static bool RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) { Log.Write("Elevation cancelled: " + ex.Message); return false; }
    }

    /// <summary>Start a new copy without admin rights (Explorer starts programs with normal rights).</summary>
    public static void RestartNormal()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Environment.ProcessPath}\"") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Write("Restart without admin rights: " + ex.Message); }
    }
}
