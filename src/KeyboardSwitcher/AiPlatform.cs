namespace KeyboardSwitcher;

/// <summary>A device shown in settings: kind (NPU/GPU/CPU), name, whether the model can run there, and a note.</summary>
/// <param name="Id">Device id shown to the user (NPU, GPU.0, GPU.1, CPU).</param>
internal sealed record DeviceInfo(string Kind, string Name, bool Usable, string? Note = null, string? Id = null)
{
    public string Label => Id ?? Kind;
}

/// <summary>A way to load the model: device label for the status line and a function that loads it.</summary>
internal sealed record LoadCandidate(string Device, Func<ILlmBackend> Create);

/// <summary>
/// Chooses engines by processor vendor, in the same order NPU -> GPU -> CPU:
/// Intel - OpenVINO (NPU, Intel GPU, CPU);
/// AMD - ryzenai-server (Ryzen AI NPU, XDNA 2), llama.cpp Vulkan (Radeon GPU), llama.cpp (CPU).
/// </summary>
internal static class AiPlatform
{
    public static bool Amd => Hardware.IsAmdCpu;

    private static readonly string[] Kinds = { "NPU", "GPU", "CPU" };

    /// <summary>Device kinds in the order to try: the chosen one first, then the rest in NPU, GPU, CPU order.</summary>
    private static List<string> KindOrder(string choice)
    {
        var order = Kinds.ToList();
        var c = choice.ToUpperInvariant();
        if (order.Remove(c)) order.Insert(0, c);
        return order;
    }

    /// <summary>AMD: device kinds that exist and can be used (NPU only XDNA 2 with a good driver).</summary>
    /// <param name="probeGpu">Also ask llama-server for Vulkan GPUs (takes a second or two; not on the UI thread).</param>
    private static List<string> AmdOrder(Settings s, bool probeGpu = true)
    {
        var order = KindOrder(s.AiDevice);
        if (!Hardware.AmdNpuUsable(Hardware.FindAmdNpu())) order.Remove("NPU");
        var vk = probeGpu ? AmdEngines.VulkanDevices() : null;
        if (vk != null && AmdEngines.PickGpu(vk) == null) order.Remove("GPU");
        return order;
    }

    // ------------------------------------------------------------------ state

    /// <summary>Is everything needed for at least one device installed?</summary>
    public static bool Installed(Settings s) => InstalledKey(s) != null;

    /// <summary>Identity of what will be loaded (to notice that the model or device changed). null - not installed.</summary>
    public static string? InstalledKey(Settings s)
    {
        if (!Amd)
        {
            var info = AiSetup.FindModelInfo(s.AiModel);
            var dir = AiCorrector.FindModel(s);
            // The model selected in the catalog wins over a model that happens to lie next to the app
            if (string.IsNullOrWhiteSpace(s.ModelPath) && !AiSetup.ModelInstalled(info)) dir = null;
            return dir != null && AiSetup.FindRuntimeDir() != null ? dir + "|" + s.AiDevice : null;
        }
        var m = AiSetup.FindModelInfo(s.AiModel);
        bool npu = AmdEngines.RyzenAiInstalled && AmdEngines.NpuModelInstalled(m);
        bool llama = AmdEngines.LlamaInstalled && AmdEngines.GgufInstalled(m);
        return npu || llama ? $"amd|{m.Id}|{s.AiDevice}|{npu}|{llama}" : null;
    }

    public static string ModelName(Settings s)
    {
        var m = AiSetup.FindModelInfo(s.AiModel);
        if (!Amd) return Path.GetFileName((AiCorrector.FindModel(s) ?? m.FolderName).TrimEnd('\\', '/'));
        return m.GgufFile != null ? Path.GetFileNameWithoutExtension(m.GgufFile) : m.Id;
    }

    /// <summary>For the model note in settings: installed, or how much will be downloaded and whether it is converted.</summary>
    public static (bool installed, double gb, bool convert) ModelState(ModelInfo m, Settings s)
    {
        if (!Amd) return (AiSetup.ModelInstalled(m), m.SizeGb, m.NeedsConversion);
        var (steps, gb) = Plan(s, m);
        return (steps.Count == 0, gb, false);
    }

    // ------------------------------------------------------------------ installation

    public static (List<string> steps, double gb) Plan(Settings s) => Plan(s, AiSetup.FindModelInfo(s.AiModel));

    private static (List<string> steps, double gb) Plan(Settings s, ModelInfo m)
    {
        if (!Amd) return AiSetup.Plan(m);
        var steps = new List<string>();
        double gb = 0;
        var need = AmdNeeds(s, m);
        if (!AiSetup.VcRuntimeInstalled()) { steps.Add(L.T("plan.vc")); gb += 0.03; }
        if (need.ryzen && !AmdEngines.RyzenAiInstalled) { steps.Add(L.T("plan.ryzenai", AmdEngines.RyzenAiMinVersion + "+", AmdEngines.RyzenAiGb)); gb += AmdEngines.RyzenAiGb; }
        if (need.ryzen && !AmdEngines.NpuModelInstalled(m)) { steps.Add(L.T("plan.npu_model", m.NpuRepo, m.NpuGb)); gb += m.NpuGb; }
        if (need.llama && !AmdEngines.LlamaInstalled) { steps.Add(L.T("plan.llama", AmdEngines.LlamaVersion, AmdEngines.LlamaGb)); gb += AmdEngines.LlamaGb; }
        if (need.llama && !AmdEngines.GgufInstalled(m)) { steps.Add(L.T("plan.gguf", m.GgufFile, m.GgufGb)); gb += m.GgufGb; }
        return (steps, gb);
    }

    /// <summary>
    /// AMD: which engines to install. The first device in the order decides; if it is the NPU, llama.cpp is installed
    /// too, so that the GPU/CPU can take over when the NPU fails (the same fallback as on Intel).
    /// </summary>
    private static (bool ryzen, bool llama) AmdNeeds(Settings s, ModelInfo m)
    {
        var order = AmdOrder(s, probeGpu: false);
        bool ryzen = order.FirstOrDefault() == "NPU" && m.NpuRepo != null;
        return (ryzen, true);
    }

    public static async Task InstallAsync(Settings s, IProgress<string> progress, CancellationToken ct)
    {
        var m = AiSetup.FindModelInfo(s.AiModel);
        if (!Amd) { await AiSetup.InstallAsync(m, progress, ct); return; }
        Directory.CreateDirectory(AiSetup.BaseDir);
        var need = AmdNeeds(s, m);
        await AiSetup.EnsureVcRuntimeAsync(progress, ct);
        if (need.llama && !AmdEngines.LlamaInstalled) await AmdEngines.InstallLlamaAsync(progress, ct);
        if (need.llama && !AmdEngines.GgufInstalled(m)) await AmdEngines.DownloadGgufAsync(m, progress, ct);
        if (need.ryzen && !AmdEngines.RyzenAiInstalled) await AmdEngines.InstallRyzenAiAsync(progress, ct);
        if (need.ryzen && !AmdEngines.NpuModelInstalled(m)) await AmdEngines.DownloadNpuModelAsync(m, progress, ct);
        progress.Report(L.T("pr.done"));
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Ways to load the model, in order. Runs on a background thread (device detection takes time).</summary>
    public static List<LoadCandidate> Candidates(Settings s)
    {
        var list = new List<LoadCandidate>();
        if (!Amd)
        {
            var dir = AiCorrector.FindModel(s);
            if (dir == null) return list;
            var cache = Path.Combine(AiSetup.BaseDir, "ovcache"); // local folder: the NPU cache can be large
            try { Directory.CreateDirectory(cache); } catch (Exception ex) { Log.Write("No cache folder: " + ex.Message); }
            foreach (var dev in OvDevices.Order(s.AiDevice, s.AiDevices))
            {
                var d = dev;
                list.Add(new LoadCandidate(d, () => new OvBackend(AiCorrector.SafePath(dir), d, AiCorrector.SafePath(cache))));
            }
            return list;
        }

        var m = AiSetup.FindModelInfo(s.AiModel);
        foreach (var kind in AmdOrder(s))
        {
            switch (kind)
            {
                case "NPU" when AmdEngines.RyzenAiInstalled && AmdEngines.NpuModelInstalled(m):
                    list.Add(new LoadCandidate($"NPU (Ryzen AI {AmdEngines.RyzenAiVersion})", () => AmdEngines.StartRyzenAi(m)));
                    break;
                case "GPU" when AmdEngines.LlamaInstalled && AmdEngines.GgufInstalled(m):
                    var gpu = AmdEngines.PickGpu(AmdEngines.VulkanDevices() ?? new());
                    if (gpu != null) list.Add(new LoadCandidate($"GPU ({gpu.Name})", () => AmdEngines.StartLlama(m, gpu.Id)));
                    break;
                case "CPU" when AmdEngines.LlamaInstalled && AmdEngines.GgufInstalled(m):
                    list.Add(new LoadCandidate("CPU", () => AmdEngines.StartLlama(m, null)));
                    break;
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ devices for settings

    /// <summary>Devices for the settings window (slow: loads OpenVINO plugins or runs llama-server). null - unknown yet.</summary>
    public static IReadOnlyList<DeviceInfo>? Devices()
    {
        if (!Amd)
        {
            var found = OvDevices.Detect();
            // NPU, then GPUs, then CPU - the order the model is tried in
            return found?.Select(d => new DeviceInfo(OvDevices.Kind(d.Name), ShortName(d.FullName), true, null, d.Name))
                .OrderBy(d => Array.IndexOf(Kinds, d.Kind)).ThenBy(d => d.Label).ToList();
        }
        var list = new List<DeviceInfo>();
        var npu = Hardware.FindAmdNpu();
        if (npu.Kind == Hardware.NpuKind.Xdna2)
            list.Add(new DeviceInfo("NPU", ShortName(npu.Name), Hardware.AmdNpuUsable(npu),
                Hardware.AmdNpuUsable(npu) ? null : L.T("sw.dev.npu_driver_old", npu.Driver, Hardware.MinNpuDriver)));
        else if (npu.Kind == Hardware.NpuKind.Xdna1)
            list.Add(new DeviceInfo("NPU", npu.Name, false, L.T("sw.dev.npu_xdna1")));
        var vk = AmdEngines.VulkanDevices();
        if (vk == null) list.Add(new DeviceInfo("GPU", "Vulkan", true, L.T("sw.dev.gpu_later")));
        else
        {
            var gpu = AmdEngines.PickGpu(vk);
            if (gpu != null) list.Add(new DeviceInfo("GPU", ShortName(gpu.Name), true, null, gpu.Id));
        }
        list.Add(new DeviceInfo("CPU", ShortName(Hardware.CpuName), true));
        return list;
    }

    /// <summary>"Intel(R) Core(TM) Ultra 7 255H" -> "Intel Core Ultra 7 255H".</summary>
    private static string ShortName(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", ""), @"\s+", " ").Trim();

    /// <summary>Can the device list be shown now (Intel: OpenVINO installed; AMD: always).</summary>
    public static bool CanDetectDevices => Amd || AiSetup.FindRuntimeDir() != null;
}
