using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KeyboardSwitcher;

/// <summary>Processor vendor, processor name and the AMD NPU (found via the Windows device list).</summary>
internal static class Hardware
{
    private static readonly Lazy<string> Vendor = new(() =>
    {
        try
        {
            if (!X86Base.IsSupported) return "";
            var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
            var b = new byte[12];
            BitConverter.GetBytes(ebx).CopyTo(b, 0);
            BitConverter.GetBytes(edx).CopyTo(b, 4);
            BitConverter.GetBytes(ecx).CopyTo(b, 8);
            return System.Text.Encoding.ASCII.GetString(b);
        }
        catch { return ""; }
    });

    /// <summary>AMD processor ("AuthenticAMD"). Then AMD engines are used instead of OpenVINO.</summary>
    public static bool IsAmdCpu => Environment.GetEnvironmentVariable("KEYBOARDSWITCHER_FORCE_VENDOR") switch
    {
        "amd" => true,      // tests: pretend to be an AMD / Intel computer
        "intel" => false,
        _ => Vendor.Value == "AuthenticAMD",
    };

    public static string CpuName
    {
        get
        {
            try
            {
                return (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", null) as string)?.Trim() ?? "CPU";
            }
            catch { return "CPU"; }
        }
    }

    /// <summary>Smart App Control: "on", "evaluation", "off" or "" (unknown / older Windows).</summary>
    public static string SmartAppControlState
    {
        get
        {
            try
            {
                var v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState", null);
                return v is int i ? i switch { 0 => "off", 1 => "on", 2 => "evaluation", _ => i.ToString() } : "";
            }
            catch { return ""; }
        }
    }

    /// <summary>
    /// Smart App Control is on: Windows refuses unsigned programs and libraries such as llama.cpp and ryzenai-server
    /// (error 0xC0E90002, "Bad Image"). OpenVINO is signed by Intel and still works.
    /// </summary>
    public static bool SmartAppControlOn => SmartAppControlState == "on" || File.Exists(BlockedMarker);

    /// <summary>Set when Windows refused to start llama.cpp / ryzenai-server (0xC0E90002) even though Smart App Control
    /// does not look "on" (e.g. App Control for Business): from then on the signed OpenVINO path is used.</summary>
    private static string BlockedMarker => Path.Combine(AiSetup.BaseDir, "unsigned-blocked.txt");

    public static void MarkUnsignedBlocked()
    {
        try { Directory.CreateDirectory(AiSetup.BaseDir); File.WriteAllText(BlockedMarker, DateTime.Now.ToString("s")); }
        catch { }
        Log.Write("Windows blocked an unsigned engine (0xC0E90002); switching to OpenVINO");
    }

    public enum NpuKind { None, Xdna1, Xdna2 }

    /// <summary>AMD NPU: XDNA 2 (Ryzen AI 300/400, PCI device 17F0) runs language models, XDNA 1 (1502) does not.</summary>
    public sealed record AmdNpu(NpuKind Kind, string Name, Version? Driver);

    /// <summary>Minimum NPU driver for Ryzen AI 1.6+ (from the Lemonade documentation).</summary>
    public static readonly Version MinNpuDriver = new(32, 0, 203, 280);

    private static AmdNpu? _npu;

    public static AmdNpu FindAmdNpu()
    {
        if (_npu != null) return _npu;
        var result = new AmdNpu(NpuKind.None, "", null);
        try
        {
            var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set != new IntPtr(-1))
            {
                try
                {
                    var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                    {
                        var ids = Property(set, ref data, SPDRP_HARDWAREID) ?? "";
                        if (!ids.Contains(@"PCI\VEN_1022&", StringComparison.OrdinalIgnoreCase)) continue;
                        NpuKind kind = ids.Contains("DEV_17F0", StringComparison.OrdinalIgnoreCase) ? NpuKind.Xdna2
                                     : ids.Contains("DEV_1502", StringComparison.OrdinalIgnoreCase) ? NpuKind.Xdna1 : NpuKind.None;
                        if (kind == NpuKind.None) continue;
                        var name = Property(set, ref data, SPDRP_FRIENDLYNAME) ?? Property(set, ref data, SPDRP_DEVICEDESC) ?? "AMD NPU";
                        result = new AmdNpu(kind, name.Split('\0')[0], DriverVersion(Property(set, ref data, SPDRP_DRIVER)));
                        break;
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
        }
        catch (Exception ex) { Log.Write("AMD NPU detection failed: " + ex.Message); }
        Log.Write($"AMD NPU: {result.Kind} {result.Name} driver {result.Driver}");
        return _npu = result;
    }

    /// <summary>The NPU can run models: XDNA 2 with a recent enough driver.</summary>
    public static bool AmdNpuUsable(AmdNpu n) => n.Kind == NpuKind.Xdna2 && (n.Driver == null || n.Driver >= MinNpuDriver);

    private static Version? DriverVersion(string? driverKey)
    {
        if (string.IsNullOrEmpty(driverKey)) return null;
        try
        {
            var v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\" + driverKey.Split('\0')[0],
                "DriverVersion", null) as string;
            return Version.TryParse(v, out var ver) ? ver : null;
        }
        catch { return null; }
    }

    // ---------- SetupAPI ----------

    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    private const uint SPDRP_DEVICEDESC = 0x0, SPDRP_HARDWAREID = 0x1, SPDRP_DRIVER = 0x9, SPDRP_FRIENDLYNAME = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property,
        out uint regType, byte[]? buffer, uint size, out uint required);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private static string? Property(IntPtr set, ref SP_DEVINFO_DATA data, uint prop)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, prop, out _, null, 0, out uint need);
        if (need == 0) return null;
        var buf = new byte[need];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, prop, out _, buf, need, out _)) return null;
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }
}

/// <summary>
/// Engines for AMD processors: llama.cpp (llama-server, Vulkan) for the Radeon GPU and the CPU,
/// ryzenai-server (AMD Ryzen AI) for the XDNA 2 NPU. Both are local OpenAI-compatible servers.
/// llama.cpp (Vulkan) also runs the GGUF-only models on Intel computers (Arc GPU and CPU).
/// </summary>
internal static class AmdEngines
{
    public const string LlamaVersion = "b11936";
    private const string LlamaZip = "llama-" + LlamaVersion + "-bin-win-vulkan-x64.zip";
    private const string LlamaUrl = "https://github.com/ggml-org/llama.cpp/releases/download/" + LlamaVersion + "/" + LlamaZip;
    private const long LlamaZipBytes = 37178674;
    public const double LlamaGb = 0.04;

    /// <summary>
    /// ryzenai-server: the newest release is taken from GitHub at install time (1.8.0 and later);
    /// if GitHub can't be asked, the known 1.7.0 release is used.
    /// </summary>
    public const string RyzenAiMinVersion = "1.7.0";
    private const string RyzenAiReleasesApi = "https://api.github.com/repos/lemonade-sdk/ryzenai-server/releases/latest";
    private const string RyzenAiFallbackUrl = "https://github.com/lemonade-sdk/ryzenai-server/releases/download/v1.7.0/ryzenai-server.zip";
    private const long RyzenAiFallbackBytes = 569142342;
    private const string RyzenAiFallbackSha256 = "1b263dcd7c71bb51819dcda569a10647733da153c53891cfb12d2277466971cf";
    public const double RyzenAiGb = 0.6;

    public static string LlamaDir => Path.Combine(AiSetup.BaseDir, "llamacpp", LlamaVersion);
    private static string RyzenAiRoot => Path.Combine(AiSetup.BaseDir, "ryzenai-server");

    /// <summary>Folder of the newest installed ryzenai-server version (or where 1.7.0 would go).</summary>
    public static string RyzenAiDir
    {
        get
        {
            try
            {
                if (Directory.Exists(RyzenAiRoot))
                {
                    var best = Directory.GetDirectories(RyzenAiRoot)
                        .Where(d => File.Exists(Path.Combine(d, "ryzenai-server.exe")))
                        .Select(d => (dir: d, ver: Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0)))
                        .OrderByDescending(x => x.ver).FirstOrDefault();
                    if (best.dir != null) return best.dir;
                }
            }
            catch { }
            return Path.Combine(RyzenAiRoot, RyzenAiMinVersion);
        }
    }

    /// <summary>Installed ryzenai-server version (folder name), or "" if not installed.</summary>
    public static string RyzenAiVersion => RyzenAiInstalled ? Path.GetFileName(RyzenAiDir) : "";
    public static string LlamaServer => Path.Combine(LlamaDir, "llama-server.exe");
    public static string RyzenAiServer => Path.Combine(RyzenAiDir, "ryzenai-server.exe");
    public static bool LlamaInstalled => File.Exists(LlamaServer);
    public static bool RyzenAiInstalled => File.Exists(RyzenAiServer);

    public static string GgufPath(ModelInfo m) => Path.Combine(AiSetup.ModelsDir, "gguf", m.GgufFile!);
    public static bool GgufInstalled(ModelInfo m) =>
        m.GgufFile != null && File.Exists(GgufPath(m)) && new FileInfo(GgufPath(m)).Length == m.GgufBytes;

    public static string NpuModelDir(ModelInfo m) => Path.Combine(AiSetup.ModelsDir, "ryzenai", m.NpuRepo!.Replace('/', '_'));
    public static bool NpuModelInstalled(ModelInfo m) =>
        m.NpuRepo != null && File.Exists(Path.Combine(NpuModelDir(m), ".complete"));

    // ------------------------------------------------------------------ Vulkan devices

    public sealed record VulkanDevice(string Id, string Name);

    private static List<VulkanDevice>? _vulkan;

    /// <summary>Vulkan GPUs from "llama-server --list-devices" (null - llama.cpp is not installed yet).</summary>
    public static List<VulkanDevice>? VulkanDevices()
    {
        if (_vulkan != null) return _vulkan;
        if (!LlamaInstalled) return null;
        var list = new List<VulkanDevice>();
        try
        {
            using var p = Process.Start(new ProcessStartInfo(LlamaServer, "--list-devices")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = LlamaDir,
            })!;
            var outText = p.StandardOutput.ReadToEndAsync();
            var errText = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } }
            foreach (Match m in Regex.Matches(outText.Result + "\n" + errText.Result, @"^\s*(Vulkan\d+):\s*(.+?)(?:\s*\(\d+\s*MiB.*)?\s*$", RegexOptions.Multiline))
                list.Add(new VulkanDevice(m.Groups[1].Value, m.Groups[2].Value.Trim()));
        }
        catch (Exception ex) { Log.Write("Vulkan device list failed: " + ex.Message); }
        Log.Write("Vulkan devices: " + string.Join("; ", list.Select(d => $"{d.Id} = {d.Name}")));
        return _vulkan = list;
    }

    /// <summary>
    /// GPU for the model via Vulkan: AMD Radeon first, then Intel (Arc), then any other except NVIDIA - an NVIDIA card is
    /// used only through CUDA and only when the user turned it on (see <see cref="Nvidia"/>).
    /// </summary>
    public static VulkanDevice? PickGpu(List<VulkanDevice> list)
    {
        static bool Has(VulkanDevice d, string s) => d.Name.Contains(s, StringComparison.OrdinalIgnoreCase);
        var usable = list.Where(d => !Has(d, "NVIDIA") && !Has(d, "GeForce") && !Has(d, "Quadro")
                                     && !Has(d, "llvmpipe") && !Has(d, "Microsoft Basic")).ToList();
        return usable.FirstOrDefault(d => Has(d, "AMD") || Has(d, "Radeon"))
               ?? usable.FirstOrDefault(d => Has(d, "Intel"))
               ?? usable.FirstOrDefault();
    }

    // ------------------------------------------------------------------ starting servers

    public static ILlmBackend StartLlama(ModelInfo m, string? vulkanDevice)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var dev = vulkanDevice == null ? "-ngl 0 --device none" : $"-ngl 99 --device {vulkanDevice}";
        var args = $"-m \"{GgufPath(m)}\" --host 127.0.0.1 --port {{port}} -c 4096 --jinja {dev} --api-key {key}";
        return new ServerBackend(LlamaServer, args, key, thinkingKwargs: true, TimeSpan.FromMinutes(5));
    }

    public static ILlmBackend StartRyzenAi(ModelInfo m) =>
        // The first start compiles the model for the NPU - it can take several minutes
        new ServerBackend(RyzenAiServer, $"-m \"{NpuModelDir(m)}\" --host 127.0.0.1 -p {{port}} -c 2048", null,
            thinkingKwargs: false, TimeSpan.FromMinutes(10));

    // ------------------------------------------------------------------ installation

    public static async Task InstallLlamaAsync(IProgress<string> progress, CancellationToken ct)
    {
        var zip = Path.Combine(AiSetup.DownloadsDir, LlamaZip);
        await AiSetup.DownloadAsync(LlamaUrl, zip, "llama.cpp " + LlamaVersion, progress, ct, LlamaZipBytes);
        progress.Report(L.T("pr.unpack", "llama.cpp"));
        await UnpackAsync(zip, LlamaDir, "llama-server.exe", ct);
    }

    public static async Task InstallRyzenAiAsync(IProgress<string> progress, CancellationToken ct)
    {
        var (version, url, size, sha) = await LatestRyzenAiAsync(ct);
        Log.Write($"ryzenai-server {version}: {url} ({size} bytes)");
        var zip = Path.Combine(AiSetup.DownloadsDir, "ryzenai-server-" + version + ".zip");
        await AiSetup.DownloadAsync(url, zip, "ryzenai-server " + version, progress, ct, size);
        if (sha != null)
        {
            progress.Report(L.T("pr.check", "ryzenai-server"));
            await using (var fs = File.OpenRead(zip))
            {
                var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (actual != sha) { fs.Close(); File.Delete(zip); throw new InvalidOperationException(L.T("err.corrupt", "ryzenai-server")); }
            }
        }
        progress.Report(L.T("pr.unpack", "ryzenai-server " + version));
        await UnpackAsync(zip, Path.Combine(RyzenAiRoot, version), "ryzenai-server.exe", ct);
    }

    /// <summary>The newest ryzenai-server release on GitHub: version, zip URL, size and sha256 (if published).</summary>
    private static async Task<(string version, string url, long size, string? sha)> LatestRyzenAiAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KeyboardSwitcher/0.2");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var rel = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync(RyzenAiReleasesApi, ct))!;
            var tag = rel["tag_name"]!.GetValue<string>().TrimStart('v', 'V');
            foreach (var a in rel["assets"]!.AsArray())
            {
                if (a?["name"]?.GetValue<string>() != "ryzenai-server.zip") continue;
                var digest = a["digest"]?.GetValue<string>();
                var sha = digest != null && digest.StartsWith("sha256:") ? digest[7..].ToLowerInvariant() : null;
                if (Version.TryParse(tag, out var v) && v >= Version.Parse(RyzenAiMinVersion))
                    return (tag, a["browser_download_url"]!.GetValue<string>(), a["size"]!.GetValue<long>(), sha);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write("ryzenai-server release check failed: " + ex.Message); }
        return (RyzenAiMinVersion, RyzenAiFallbackUrl, RyzenAiFallbackBytes, RyzenAiFallbackSha256);
    }

    /// <summary>Unpack the archive; the folder with <paramref name="exeName"/> becomes the target folder.</summary>
    internal static async Task UnpackAsync(string zip, string target, string exeName, CancellationToken ct)
    {
        var tmp = target + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, tmp, true), ct);
        var exe = Directory.GetFiles(tmp, exeName, SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException(L.T("err.no_exe", exeName));
        var root = Path.GetDirectoryName(exe)!;
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(root, target);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        try { File.Delete(zip); } catch { }
    }

    public static Task DownloadGgufAsync(ModelInfo m, IProgress<string> progress, CancellationToken ct) =>
        AiSetup.DownloadAsync($"https://huggingface.co/{m.GgufRepo}/resolve/main/{m.GgufFile}", GgufPath(m),
            m.GgufFile!, progress, ct, m.GgufBytes);

    public static Task DownloadNpuModelAsync(ModelInfo m, IProgress<string> progress, CancellationToken ct) =>
        AiSetup.DownloadRepoAsync(m.NpuRepo!, NpuModelDir(m), progress, ct);
}
