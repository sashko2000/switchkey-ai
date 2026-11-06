using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace KeyboardSwitcher;

/// <summary>A model from the catalog offered in settings.</summary>
/// <param name="GgufRepo">llama.cpp version of the model (AMD: Radeon GPU and CPU).</param>
/// <param name="NpuRepo">AMD Ryzen AI NPU version (ONNX); for some models a close relative (Qwen2.5 instead of Qwen3).</param>
/// <param name="OpenVinoNpu">The OpenVINO version runs on the Intel NPU (symmetric per-channel INT4); otherwise GPU/CPU only.</param>
public sealed record ModelInfo(string Id, string Repo, string FolderName, bool NeedsConversion, double SizeGb,
    string? GgufRepo = null, string? GgufFile = null, long GgufBytes = 0, string? NpuRepo = null, double NpuGb = 0,
    bool OpenVinoNpu = true)
{
    public double GgufGb => GgufBytes / 1073741824.0;

    public string Title => L.T("model." + Id + ".title");
    public string Note => L.T("model." + Id + ".note");
}

/// <summary>
/// Installs everything needed for text correction by a model: OpenVINO libraries, the model,
/// and, if needed, Python and conversion tools. Everything goes to %LOCALAPPDATA%\KeyboardSwitcher.
/// </summary>
internal static class AiSetup
{
    public const string OpenVinoVersion = "2026.4.1";
    private const string OpenVinoZip = "openvino_genai_windows_" + OpenVinoVersion + ".0_x86_64.zip";
    private const string OpenVinoUrl = "https://storage.openvinotoolkit.org/repositories/openvino_genai/packages/"
                                       + OpenVinoVersion + "/windows/" + OpenVinoZip;
    private const double OpenVinoSizeGb = 0.25;
    private const string PythonInstallerUrl = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe";
    private const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    public static readonly IReadOnlyList<ModelInfo> Catalog = new[]
    {
        new ModelInfo("qwen3-8b", "OpenVINO/Qwen3-8B-int4-cw-ov", "Qwen3-8B-int4-cw-ov", false, 4.75,
            "Qwen/Qwen3-8B-GGUF", "Qwen3-8B-Q4_K_M.gguf", 5027783488, "amd/Qwen2.5-7B-Instruct-onnx-ryzenai-npu", 8.2),
        // Mistral-7B and Phi-3.5-mini were removed after the model tests (06.11.2026): weak in Russian, Ukrainian and Hindi.
        // Qwen3-4B: the locally converted NPU version did not load, so the ready OpenVINO version is used (GPU and CPU only).
        new ModelInfo("qwen3-4b", "OpenVINO/Qwen3-4B-int4-ov", "Qwen3-4B-int4-ov", false, 2.13,
            "Qwen/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf", 2497280256, "amd/Qwen2.5-3B-Instruct-onnx-ryzenai-npu", 3.81,
            OpenVinoNpu: false),
        new ModelInfo("qwen3-1.7b", "Qwen/Qwen3-1.7B", "Qwen3-1.7B-int4-npu", true, 1.2,
            "Qwen/Qwen3-1.7B-GGUF", "Qwen3-1.7B-Q8_0.gguf", 1834426016, "amd/Qwen-2.5_1.5B_Instruct-onnx-ryzenai-npu", 2.1),
    };

    public static ModelInfo FindModelInfo(string id) => Catalog.FirstOrDefault(m => m.Id == id) ?? Catalog[0];

    /// <summary>%LOCALAPPDATA%\KeyboardSwitcher; tests can move it with the KEYBOARDSWITCHER_HOME environment variable.</summary>
    public static string BaseDir =>
        Environment.GetEnvironmentVariable("KEYBOARDSWITCHER_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyboardSwitcher");
    public static string RuntimeDir => Path.Combine(BaseDir, "openvino", OpenVinoVersion);
    public static string ModelsDir => Path.Combine(BaseDir, "models");
    internal static string DownloadsDir => Path.Combine(BaseDir, "downloads");
    private static string VenvDir => Path.Combine(BaseDir, "convert-venv");
    private static string OwnPythonDir => Path.Combine(BaseDir, "python312");

    // ------------------------------------------------------------------ checks

    /// <summary>Folder with openvino_genai_c.dll: next to the app (if included in the build) or the installed one.</summary>
    public static string? FindRuntimeDir()
    {
        foreach (var d in new[] { AppContext.BaseDirectory, RuntimeDir })
            if (File.Exists(Path.Combine(d, "openvino_genai_c.dll"))) return d;
        return null;
    }

    public static bool VcRuntimeInstalled()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" }.All(f => File.Exists(Path.Combine(sys, f)))) return false;
        // OpenVINO 2026 is built with a recent Visual Studio: it crashes with an old msvcp140 (< 14.40)
        try
        {
            var v = FileVersionInfo.GetVersionInfo(Path.Combine(sys, "msvcp140.dll"));
            return v.FileMajorPart > 14 || (v.FileMajorPart == 14 && v.FileMinorPart >= 40);
        }
        catch { return false; }
    }

    public static string ModelDir(ModelInfo m) => Path.Combine(ModelsDir, m.FolderName);

    public static bool ModelInstalled(ModelInfo m) =>
        File.Exists(Path.Combine(ModelDir(m), "openvino_model.xml")) && File.Exists(Path.Combine(ModelDir(m), ".complete"));

    /// <summary>What needs to be done for this model - list of steps and approximate download size (GB).</summary>
    public static (List<string> steps, double gb) Plan(ModelInfo m)
    {
        var steps = new List<string>();
        double gb = 0;
        if (!VcRuntimeInstalled()) { steps.Add(L.T("plan.vc")); gb += 0.03; }
        if (FindRuntimeDir() == null) { steps.Add(L.T("plan.openvino", OpenVinoVersion)); gb += OpenVinoSizeGb; }
        if (!ModelInstalled(m))
        {
            if (m.NeedsConversion)
            {
                if (!VenvReady())
                {
                    if (FindPython() == null) { steps.Add(L.T("plan.python")); gb += 0.03; }
                    steps.Add(L.T("plan.tools"));
                    gb += 3;
                }
                steps.Add(L.T("plan.convert", m.Repo));
                gb += m.SizeGb * 3;
            }
            else
            {
                steps.Add(L.T("plan.download", m.Repo, m.SizeGb));
                gb += m.SizeGb;
            }
        }
        return (steps, gb);
    }

    // ------------------------------------------------------------------ installation

    public static async Task<string> InstallAsync(ModelInfo m, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(BaseDir);
        if (!VcRuntimeInstalled()) await InstallVcRuntimeAsync(progress, ct);
        if (FindRuntimeDir() == null) await InstallOpenVinoAsync(progress, ct);
        if (!ModelInstalled(m))
        {
            if (m.NeedsConversion) await ConvertModelAsync(m, progress, ct);
            else await DownloadModelAsync(m, progress, ct);
        }
        progress.Report(L.T("pr.done"));
        return ModelDir(m);
    }

    private static HttpClient NewHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KeyboardSwitcher", "0.2"));
        return h;
    }

    /// <summary>Download a file with resume support and progress reporting.</summary>
    private static async Task DownloadFileAsync(HttpClient http, string url, string target, string label,
        IProgress<string> progress, CancellationToken ct, long expectedSize = -1)
    {
        if (File.Exists(target) && (expectedSize < 0 || new FileInfo(target).Length == expectedSize)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expectedSize >= 0 && have > expectedSize) { File.Delete(part); have = 0; }
        if (expectedSize >= 0 && have == expectedSize) { MoveIntoPlace(part, target); return; }

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is unusable - download again
            resp.Dispose(); req.Dispose();
            File.Delete(part); have = 0;
            req = new HttpRequestMessage(HttpMethod.Get, url);
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        using var _req = req;
        using var _resp = resp;
        if (have > 0 && (resp.StatusCode != System.Net.HttpStatusCode.PartialContent || resp.Content.Headers.ContentRange?.From != have))
            have = 0; // the server doesn't support resume - start over
        resp.EnsureSuccessStatusCode();
        long? len = resp.Content.Headers.ContentLength;
        long total = len.HasValue ? len.Value + have : -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            long done = have;
            var sw = Stopwatch.StartNew();
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (sw.ElapsedMilliseconds > 500)
                {
                    sw.Restart();
                    progress.Report(total > 0
                        ? L.T("pr.progress", label, done / 1048576.0, total / 1048576.0, 100.0 * done / total)
                        : L.T("pr.progress_nototal", label, done / 1048576.0));
                }
            }
        }
        if (expectedSize >= 0 && new FileInfo(part).Length != expectedSize)
        {
            File.Delete(part);
            throw new InvalidOperationException(L.T("err.incomplete", Path.GetFileName(target)));
        }
        MoveIntoPlace(part, target);
    }

    private static void MoveIntoPlace(string part, string target)
    {
        if (File.Exists(target)) File.Delete(target);
        File.Move(part, target);
    }

    /// <summary>Wait for the installer; on cancel - stop it.</summary>
    private static async Task WaitInstallerAsync(Process p, CancellationToken ct)
    {
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); p.WaitForExit(10000); } catch { }
            throw;
        }
    }

    private static async Task InstallVcRuntimeAsync(IProgress<string> progress, CancellationToken ct)
    {
        using var http = NewHttp();
        var exe = Path.Combine(DownloadsDir, "vc_redist.x64.exe");
        await DownloadFileAsync(http, VcRedistUrl, exe, "Visual C++ Runtime", progress, ct);
        progress.Report(L.T("pr.vc_install"));
        var p = Process.Start(new ProcessStartInfo(exe, "/install /passive /norestart") { UseShellExecute = true, Verb = "runas" })!;
        await WaitInstallerAsync(p, ct);
        // 0 - success, 1638 - a newer version is already installed, 3010 - reboot required
        if (p.ExitCode == 1618)
            throw new InvalidOperationException(L.T("err.other_install"));
        if (p.ExitCode is not (0 or 1638 or 3010))
            throw new InvalidOperationException(L.T("err.vc_code", p.ExitCode));
    }

    private static async Task InstallOpenVinoAsync(IProgress<string> progress, CancellationToken ct)
    {
        using var http = NewHttp();
        var zip = Path.Combine(DownloadsDir, OpenVinoZip);
        await DownloadFileAsync(http, OpenVinoUrl, zip, "OpenVINO", progress, ct);

        progress.Report(L.T("pr.ov_check"));
        try
        {
            var expected = (await http.GetStringAsync(OpenVinoUrl + ".sha256", ct)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            await using var fs = File.OpenRead(zip);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (expected.Length == 64 && expected != actual)
            {
                fs.Close();
                File.Delete(zip);
                throw new InvalidOperationException(L.T("err.ov_corrupt"));
            }
        }
        catch (HttpRequestException) { /* no checksum file - skip verification */ }

        progress.Report(L.T("pr.ov_unpack"));
        var tmp = RuntimeDir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var e in archive.Entries)
            {
                var path = e.FullName.Replace('\\', '/');
                bool bin = path.Contains("/runtime/bin/intel64/Release/") || path.Contains("/runtime/3rdparty/tbb/bin/");
                if (!bin || string.IsNullOrEmpty(e.Name)) continue;
                if (e.Name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)) continue;
                e.ExtractToFile(Path.Combine(tmp, e.Name), true);
            }
        }, ct);
        if (!File.Exists(Path.Combine(tmp, "openvino_genai_c.dll")))
            throw new InvalidOperationException(L.T("err.ov_nodll"));
        if (Directory.Exists(RuntimeDir)) Directory.Delete(RuntimeDir, true);
        Directory.Move(tmp, RuntimeDir);
        try { File.Delete(zip); } catch { }
    }

    private static Task DownloadModelAsync(ModelInfo m, IProgress<string> progress, CancellationToken ct) =>
        DownloadRepoAsync(m.Repo, ModelDir(m), progress, ct);

    /// <summary>Download all files of a Hugging Face repository into a folder (with resume), then mark it complete.</summary>
    internal static async Task DownloadRepoAsync(string repo, string dir, IProgress<string> progress, CancellationToken ct)
    {
        using var http = NewHttp();
        progress.Report(L.T("pr.model_list"));
        var json = await http.GetStringAsync($"https://huggingface.co/api/models/{repo}/tree/main?recursive=true", ct);
        var files = JsonSerializer.Deserialize<List<HfFile>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .Where(f => f.Type == "file" && !f.Path.StartsWith('.') && !f.Path.Contains("/.")).ToList();
        Directory.CreateDirectory(dir);
        long total = files.Sum(f => f.Size), done = 0;
        var name = repo[(repo.IndexOf('/') + 1)..];
        foreach (var f in files.OrderBy(f => f.Size))
        {
            var target = Path.Combine(dir, f.Path.Replace('/', '\\'));
            long before = done;
            var label = L.T("pr.model_file", name, before / 1073741824.0, total / 1073741824.0, f.Path);
            await DownloadFileAsync(http, $"https://huggingface.co/{repo}/resolve/main/{f.Path}", target, label, progress, ct, f.Size);
            done += f.Size;
        }
        File.WriteAllText(Path.Combine(dir, ".complete"), repo);
    }

    /// <summary>Download a single file (with resume and progress).</summary>
    internal static async Task DownloadAsync(string url, string target, string label, IProgress<string> progress,
        CancellationToken ct, long expectedSize = -1)
    {
        using var http = NewHttp();
        await DownloadFileAsync(http, url, target, label, progress, ct, expectedSize);
    }

    internal static Task EnsureVcRuntimeAsync(IProgress<string> progress, CancellationToken ct) =>
        VcRuntimeInstalled() ? Task.CompletedTask : InstallVcRuntimeAsync(progress, ct);

    private sealed class HfFile
    {
        public string Type { get; set; } = "";
        public string Path { get; set; } = "";
        public long Size { get; set; }
    }

    // ------------------------------------------------------------------ Python and conversion

    private static string VenvPython => Path.Combine(VenvDir, "Scripts", "python.exe");
    private static bool VenvReady() => File.Exists(Path.Combine(VenvDir, "Scripts", "optimum-cli.exe"));

    /// <summary>Find Python 3.10-3.12: our own, via the py launcher, or on PATH.</summary>
    private static string? FindPython()
    {
        var own = Path.Combine(OwnPythonDir, "python.exe");
        if (File.Exists(own)) return own;
        foreach (var v in new[] { "3.12", "3.11", "3.10" })
        {
            var exe = RunCapture("py", $"-{v} -c \"import sys;print(sys.executable)\"");
            if (Usable(exe)) return exe;
        }
        var p = RunCapture("python", "-c \"import sys;print(sys.executable if (3,10)<=sys.version_info[:2]<=(3,12) else '')\"");
        return Usable(p) ? p : null;
    }

    /// <summary>Microsoft Store Python is not suitable: it hides created files in its package folder.</summary>
    private static bool Usable(string? exe) =>
        !string.IsNullOrEmpty(exe) && File.Exists(exe)
        && !exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
        && !exe.Contains("PythonSoftwareFoundation.Python", StringComparison.OrdinalIgnoreCase);

    private static string? RunCapture(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? read.Result.Trim() : null;
        }
        catch { return null; }
    }

    private static async Task<string> EnsurePythonAsync(IProgress<string> progress, CancellationToken ct)
    {
        var py = FindPython();
        if (py != null) return py;
        using var http = NewHttp();
        var exe = Path.Combine(DownloadsDir, "python-3.12.10-amd64.exe");
        await DownloadFileAsync(http, PythonInstallerUrl, exe, "Python 3.12", progress, ct);
        progress.Report(L.T("pr.python_install"));
        // Current user only, into our own folder, without changing PATH
        var args = $"/quiet InstallAllUsers=0 TargetDir=\"{OwnPythonDir}\" PrependPath=0 Include_launcher=0 Shortcuts=0 " +
                   "Include_test=0 Include_doc=0 Include_tcltk=0 AssociateFiles=0";
        var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!;
        await WaitInstallerAsync(p, ct);
        if (p.ExitCode == 1618)
            throw new InvalidOperationException(L.T("err.other_install"));
        var own = Path.Combine(OwnPythonDir, "python.exe");
        if (!File.Exists(own)) throw new InvalidOperationException(L.T("err.python_failed", p.ExitCode));
        return own;
    }

    /// <summary>Run a process, showing the last line of its output.</summary>
    private static async Task RunAsync(string exe, string args, string label, IProgress<string> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        using var p = new Process { StartInfo = psi };
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (tail) { tail.Enqueue(line); if (tail.Count > 30) tail.Dequeue(); }
            var l = line.Trim();
            progress.Report($"{label}: {(l.Length > 120 ? l[..120] : l)}");
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            string last;
            lock (tail) last = string.Join(Environment.NewLine, tail);
            Log.Write($"{label}: error {p.ExitCode}{Environment.NewLine}{last}");
            throw new InvalidOperationException(L.T("err.step_failed", label, p.ExitCode));
        }
    }

    private static async Task ConvertModelAsync(ModelInfo m, IProgress<string> progress, CancellationToken ct)
    {
        if (!VenvReady())
        {
            var py = await EnsurePythonAsync(progress, ct);
            if (!File.Exists(VenvPython))
                await RunAsync(py, $"-m venv \"{VenvDir}\"", L.T("pr.venv"), progress, ct);
            await RunAsync(VenvPython, "-m pip install --upgrade pip", "pip", progress, ct);
            await RunAsync(VenvPython, "-m pip install torch --index-url https://download.pytorch.org/whl/cpu", L.T("pr.torch"), progress, ct);
            try
            {
                // Converter versions match the app's OpenVINO libraries
                await RunAsync(VenvPython, $"-m pip install \"openvino~={OpenVinoVersion}\" \"openvino-tokenizers~={OpenVinoVersion}.0\" \"optimum-intel[openvino]\" nncf",
                    L.T("pr.optimum"), progress, ct);
            }
            catch (InvalidOperationException)
            {
                await RunAsync(VenvPython, "-m pip install \"optimum-intel[openvino]\" nncf", L.T("pr.optimum"), progress, ct);
            }
        }

        var dir = ModelDir(m);
        var tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        var cli = Path.Combine(VenvDir, "Scripts", "optimum-cli.exe");
        // INT4, symmetric, per-channel quantization - as Intel recommends for NPU
        await RunAsync(cli, $"export openvino -m {m.Repo} --task text-generation-with-past --weight-format int4 --sym " +
                            $"--ratio 1.0 --group-size -1 --trust-remote-code \"{tmp}\"", L.T("pr.convert"), progress, ct);
        if (!File.Exists(Path.Combine(tmp, "openvino_model.xml"))) throw new InvalidOperationException(L.T("err.convert_failed"));
        File.WriteAllText(Path.Combine(tmp, ".complete"), m.Repo);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
    }

    // ------------------------------------------------------------------ library loading

    private static IntPtr _genaiHandle, _coreHandle;

    /// <summary>Make openvino_genai_c.dll resolvable from the folder where we installed OpenVINO.</summary>
    public static void RegisterNativeResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(AiSetup).Assembly, (name, _, _) =>
        {
            if (name == "openvino_c")
            {
                // OpenVINO's own C API (device list) lies next to openvino_genai_c.dll
                if (_coreHandle != IntPtr.Zero) return _coreHandle;
                var d = FindRuntimeDir();
                if (d == null) return IntPtr.Zero;
                Native.SetDllDirectory(d);
                return _coreHandle = NativeLibrary.Load(Path.Combine(d, "openvino_c.dll"));
            }
            if (name != "openvino_genai_c") return IntPtr.Zero;
            if (_genaiHandle != IntPtr.Zero) return _genaiHandle;
            var dir = FindRuntimeDir();
            if (dir == null) return IntPtr.Zero;
            // Windows looks for dependent libraries (openvino.dll, tbb12.dll...) in the same folder
            Native.SetDllDirectory(dir);
            _genaiHandle = NativeLibrary.Load(Path.Combine(dir, "openvino_genai_c.dll"));
            return _genaiHandle;
        });
    }
}
