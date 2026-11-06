using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeyboardSwitcher;

/// <summary>A loaded model that can answer chat requests (OpenVINO pipeline or a local server process).</summary>
internal interface ILlmBackend : IDisposable
{
    /// <summary>messagesJson - [{"role":..,"content":..}], think - allow reasoning (Qwen3).</summary>
    string GenerateChat(string messagesJson, bool think, int maxNewTokens);

    /// <summary>Plain completion of a prompt (fallback for chat templates without a system role).</summary>
    string GenerateText(string prompt, int maxNewTokens);

    /// <summary>Child process that holds the model (for the memory display), or null if it is in our process.</summary>
    Process? Child { get; }
}

/// <summary>OpenVINO GenAI pipeline in our process (Intel NPU / GPU / CPU, also any x86 CPU).</summary>
internal sealed class OvBackend : ILlmBackend
{
    private IntPtr _pipe;

    public const int NpuMaxPromptTokens = 1024;
    public const int NpuMinResponseTokens = 768;

    public OvBackend(string modelDir, string device, string cacheDir)
    {
        _pipe = device.StartsWith("NPU", StringComparison.OrdinalIgnoreCase)
            ? OvGenAI.CreatePipeline(modelDir, device,
                "MAX_PROMPT_LEN", NpuMaxPromptTokens.ToString(),
                "MIN_RESPONSE_LEN", NpuMinResponseTokens.ToString(),
                "CACHE_DIR", cacheDir)
            : OvGenAI.CreatePipeline(modelDir, device, "CACHE_DIR", cacheDir);
    }

    public Process? Child => null;

    public string GenerateChat(string messagesJson, bool think, int maxNewTokens) =>
        OvGenAI.GenerateChat(Pipe, messagesJson, think ? "{\"enable_thinking\": true}" : "{\"enable_thinking\": false}", maxNewTokens);

    public string GenerateText(string prompt, int maxNewTokens) => OvGenAI.GenerateText(Pipe, prompt, maxNewTokens);

    private IntPtr Pipe => _pipe != IntPtr.Zero ? _pipe : throw new InvalidOperationException(L.T("err.unloaded"));

    public void Dispose()
    {
        var p = Interlocked.Exchange(ref _pipe, IntPtr.Zero);
        if (p != IntPtr.Zero) OvGenAI.PipelineFree(p);
    }
}

/// <summary>
/// A local OpenAI-compatible server in a child process: llama-server (llama.cpp, Vulkan GPU or CPU) or
/// ryzenai-server (AMD Ryzen AI NPU). Listens only on 127.0.0.1; the process is killed together with our app.
/// </summary>
internal sealed class ServerBackend : ILlmBackend
{
    private readonly Process _process;
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly bool _thinkingKwargs;
    private readonly Queue<string> _tail = new();
    private static readonly IntPtr Job = CreateKillOnCloseJob();

    public Process? Child => _process.HasExited ? null : _process;

    /// <param name="arguments">Command line; {port} is replaced with a free port.</param>
    /// <param name="apiKey">Passed as a Bearer token if the server was started with one.</param>
    /// <param name="thinkingKwargs">Send chat_template_kwargs.enable_thinking (llama-server with --jinja).</param>
    public ServerBackend(string exe, string arguments, string? apiKey, bool thinkingKwargs, TimeSpan startTimeout)
    {
        int port = FreePort();
        _base = $"http://127.0.0.1:{port}";
        _thinkingKwargs = thinkingKwargs;
        _http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) };
        if (apiKey != null) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var psi = new ProcessStartInfo(exe, arguments.Replace("{port}", port.ToString()))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start: " + exe);
        if (Job != IntPtr.Zero) AssignProcessToJobObject(Job, _process.Handle);
        _process.OutputDataReceived += (_, e) => Remember(e.Data);
        _process.ErrorDataReceived += (_, e) => Remember(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        Log.Write($"Started {Path.GetFileName(exe)} (pid {_process.Id}, port {port})");

        // Wait until the model is loaded: /health answers 200 (llama-server answers 503 while loading)
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (_process.HasExited)
            {
                var why = LastLines();
                uint code = unchecked((uint)_process.ExitCode);
                Dispose();
                // 0xC0E90002: Windows code integrity (Smart App Control / App Control) refused to load an unsigned file
                if (code == 0xC0E90002)
                {
                    Hardware.MarkUnsignedBlocked();
                    throw new InvalidOperationException(L.T("err.blocked_by_windows", Path.GetFileName(exe)));
                }
                if (why.Length == 0) why = $"0x{code:X8}";
                throw new InvalidOperationException(L.T("err.server_exited", Path.GetFileName(exe), why));
            }
            try
            {
                using var r = _http.GetAsync(_base + "/health").GetAwaiter().GetResult();
                if (r.StatusCode == HttpStatusCode.OK) break;
            }
            catch (HttpRequestException) { /* not listening yet */ }
            catch (TaskCanceledException) { }
            if (sw.Elapsed > startTimeout)
            {
                Dispose();
                throw new InvalidOperationException(L.T("err.server_timeout", Path.GetFileName(exe), (int)startTimeout.TotalSeconds));
            }
            Thread.Sleep(500);
        }
    }

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_tail)
        {
            _tail.Enqueue(line.Length > 300 ? line[..300] : line);
            while (_tail.Count > 8) _tail.Dequeue();
        }
    }

    private string LastLines()
    {
        Thread.Sleep(200); // let the output readers catch up
        lock (_tail) return string.Join(" | ", _tail);
    }

    public string GenerateChat(string messagesJson, bool think, int maxNewTokens)
    {
        var body = new JsonObject
        {
            ["messages"] = JsonNode.Parse(messagesJson),
            ["max_tokens"] = maxNewTokens,
            ["temperature"] = 0,
            ["stream"] = false,
        };
        if (_thinkingKwargs) body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = think };
        var res = Post("/v1/chat/completions", body);
        var msg = res?["choices"]?[0]?["message"];
        return msg?["content"]?.GetValue<string>() ?? "";
    }

    public string GenerateText(string prompt, int maxNewTokens)
    {
        var body = new JsonObject { ["prompt"] = prompt, ["max_tokens"] = maxNewTokens, ["temperature"] = 0, ["stream"] = false };
        var res = Post("/v1/completions", body);
        return res?["choices"]?[0]?["text"]?.GetValue<string>() ?? "";
    }

    private JsonNode? Post(string path, JsonObject body)
    {
        if (_process.HasExited) throw new InvalidOperationException(L.T("err.server_exited", _process.StartInfo.FileName, LastLines()));
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = _http.PostAsync(_base + path, content).GetAwaiter().GetResult();
        var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        // InvalidOperationException lets the caller fall back to a plain request (e.g. a template without the system role)
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
        try { return JsonNode.Parse(text); }
        catch (JsonException ex) { throw new InvalidOperationException("Bad server answer: " + ex.Message); }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(5000); } } catch { }
        _process.Dispose();
        _http.Dispose();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---------- Job object: child servers die together with the app (even if it crashes) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private static IntPtr CreateKillOnCloseJob()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
            SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref info, Marshal.SizeOf<ExtendedLimits>());
            return job;
        }
        catch { return IntPtr.Zero; }
    }
}
