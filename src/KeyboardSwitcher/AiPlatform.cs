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
/// Chooses engines by processor vendor and model, in the same order NPU -> GPU -> CPU:
/// Intel - OpenVINO (NPU, Intel GPU, CPU); GGUF-only models - llama.cpp Vulkan (Arc GPU) and CPU;
/// AMD - ryzenai-server (Ryzen AI NPU, XDNA 2), llama.cpp Vulkan (Radeon GPU), llama.cpp (CPU).
/// An NVIDIA card (llama.cpp CUDA) goes first when the user allows it and the model fits into its free memory.
/// </summary>
internal static class AiPlatform
{
    /// <summary>
    /// Use the AMD engines (llama.cpp, ryzenai-server). Not when Smart App Control is on: Windows blocks these unsigned
    /// programs, so AMD computers then use OpenVINO (signed by Intel) on the CPU, like the Intel path.
    /// </summary>
    public static bool Amd => Hardware.IsAmdCpu && !Hardware.SmartAppControlOn;

    /// <summary>AMD processor, but Smart App Control forces the OpenVINO CPU path.</summary>
    public static bool AmdBlockedBySac => Hardware.IsAmdCpu && Hardware.SmartAppControlOn;

    internal static readonly string[] Kinds = { "NPU", "GPU", "CPU" };

    /// <summary>The model runs in llama.cpp (and ryzenai-server): on AMD, and for GGUF-only models on any processor.</summary>
    public static bool UsesLlama(ModelInfo m) => Amd || m.GgufOnly;

    /// <summary>llama.cpp is unsigned: with Smart App Control on, Windows doesn't let it start.</summary>
    public static bool LlamaBlocked => Hardware.SmartAppControlOn;

    /// <summary>The user allowed the NVIDIA card and there is one (with a driver new enough for CUDA 12.4).</summary>
    public static bool UseNvidia(Settings s) => s.AiNvidia && !LlamaBlocked && Nvidia.Present;

    /// <summary>The NVIDIA card is allowed, but llama.cpp CUDA or the model's GGUF file is not installed yet.</summary>
    public static bool NvidiaMissing(Settings s)
    {
        var m = AiSetup.FindModelInfo(s.AiModel);
        return UseNvidia(s) && m.GgufFile != null && (!Nvidia.CudaInstalled || !AmdEngines.GgufInstalled(m));
    }

    /// <summary>Device kinds in the order to try: the chosen one first, then the rest in NPU, GPU, CPU order.</summary>
    internal static List<string> KindOrder(string choice)
    {
        var order = Kinds.ToList();
        var c = choice.Split('.')[0].ToUpperInvariant();   // "GPU.0" -> "GPU"
        if (order.Remove(c)) order.Insert(0, c);
        return order;
    }

    /// <summary>llama.cpp path: device kinds that exist and can be used (NPU only AMD XDNA 2 with a good driver).</summary>
    /// <param name="probeGpu">Also ask llama-server for Vulkan GPUs (takes a second or two; not on the UI thread).</param>
    private static List<string> LlamaOrder(Settings s, ModelInfo m, bool probeGpu = true)
    {
        var order = KindOrder(s.AiDevice);
        if (!Amd || m.NpuRepo == null || !Hardware.AmdNpuUsable(Hardware.FindAmdNpu())) order.Remove("NPU");
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
        var m = AiSetup.FindModelInfo(s.AiModel);
        var parts = new List<string>();
        bool gguf = AmdEngines.GgufInstalled(m);
        if (!UsesLlama(m))
        {
            var dir = AiCorrector.FindModel(s);
            // The model selected in the catalog wins over a model that happens to lie next to the app
            if (string.IsNullOrWhiteSpace(s.ModelPath) && !AiSetup.ModelInstalled(m)) dir = null;
            if (dir != null && AiSetup.FindRuntimeDir() != null) parts.Add(dir);
        }
        else
        {
            if (Amd && AmdEngines.RyzenAiInstalled && AmdEngines.NpuModelInstalled(m)) parts.Add("npu");
            if (AmdEngines.LlamaInstalled && gguf) parts.Add("llama");
        }
        if (UseNvidia(s) && Nvidia.CudaInstalled && gguf) parts.Add("cuda");
        return parts.Count == 0 ? null : $"{m.Id}|{s.AiDevice}|{string.Join("|", parts)}";
    }

    public static string ModelName(Settings s)
    {
        var m = AiSetup.FindModelInfo(s.AiModel);
        if (!UsesLlama(m)) return Path.GetFileName((AiCorrector.FindModel(s) ?? m.FolderName).TrimEnd('\\', '/'));
        return m.GgufFile != null ? Path.GetFileNameWithoutExtension(m.GgufFile) : m.Id;
    }

    /// <summary>
    /// Size of the model files this computer downloads, in GB (10^9 bytes, as Hugging Face shows): the OpenVINO version
    /// on Intel (Qwen3-1.7B: after conversion), the GGUF file on AMD and for GGUF-only models.
    /// </summary>
    public static double DownloadGb(ModelInfo m) => UsesLlama(m) ? m.GgufBytes / 1e9 : m.SizeGb;

    /// <summary>For the model note in settings: installed, or how much will be downloaded and whether it is converted.</summary>
    public static (bool installed, double gb, bool convert) ModelState(ModelInfo m, Settings s)
    {
        if (!UsesLlama(m) && !UseNvidia(s)) return (AiSetup.ModelInstalled(m), m.SizeGb, m.NeedsConversion);
        var (steps, gb) = Plan(s, m);
        return (steps.Count == 0, gb, m.NeedsConversion && !UsesLlama(m) && !AiSetup.ModelInstalled(m));
    }

    // ------------------------------------------------------------------ installation

    public static (List<string> steps, double gb) Plan(Settings s) => Plan(s, AiSetup.FindModelInfo(s.AiModel));

    internal static (List<string> steps, double gb) Plan(Settings s, ModelInfo m)
    {
        List<string> steps;
        double gb;
        bool ggufPlanned = false;
        if (!UsesLlama(m)) (steps, gb) = AiSetup.Plan(m);
        else
        {
            steps = new List<string>();
            gb = 0;
            var need = LlamaNeeds(s, m);
            if (!AiSetup.VcRuntimeInstalled()) { steps.Add(L.T("plan.vc")); gb += 0.03; }
            if (need.ryzen && !AmdEngines.RyzenAiInstalled) { steps.Add(L.T("plan.ryzenai", AmdEngines.RyzenAiMinVersion + "+", AmdEngines.RyzenAiGb)); gb += AmdEngines.RyzenAiGb; }
            if (need.ryzen && !AmdEngines.NpuModelInstalled(m)) { steps.Add(L.T("plan.npu_model", m.NpuRepo, m.NpuGb)); gb += m.NpuGb; }
            if (!AmdEngines.LlamaInstalled) { steps.Add(L.T("plan.llama", AmdEngines.LlamaVersion, AmdEngines.LlamaGb)); gb += AmdEngines.LlamaGb; }
            if (!AmdEngines.GgufInstalled(m)) { steps.Add(L.T("plan.gguf", m.GgufFile, m.GgufGb)); gb += m.GgufGb; ggufPlanned = true; }
        }
        if (UseNvidia(s) && m.GgufFile != null)
        {
            if (!Nvidia.CudaInstalled) { steps.Add(L.T("plan.cuda", Nvidia.CudaVersion, Nvidia.CudaGb)); gb += Nvidia.CudaGb; }
            if (!ggufPlanned && !AmdEngines.GgufInstalled(m)) { steps.Add(L.T("plan.gguf", m.GgufFile, m.GgufGb)); gb += m.GgufGb; }
        }
        return (steps, gb);
    }

    /// <summary>
    /// llama.cpp path: is ryzenai-server needed. The first device in the order decides; llama.cpp is always installed,
    /// so that the GPU/CPU can take over when the NPU fails (the same fallback as on Intel).
    /// </summary>
    private static (bool ryzen, bool llama) LlamaNeeds(Settings s, ModelInfo m)
    {
        var order = LlamaOrder(s, m, probeGpu: false);
        bool ryzen = Amd && order.FirstOrDefault() == "NPU" && m.NpuRepo != null;
        return (ryzen, true);
    }

    public static async Task InstallAsync(Settings s, IProgress<string> progress, CancellationToken ct)
    {
        var m = AiSetup.FindModelInfo(s.AiModel);
        Directory.CreateDirectory(AiSetup.BaseDir);
        // The NVIDIA card first: it is the fastest device, so the model can start there while the rest downloads later
        if (UseNvidia(s) && m.GgufFile != null)
        {
            await AiSetup.EnsureVcRuntimeAsync(progress, ct);
            if (!Nvidia.CudaInstalled) await Nvidia.InstallCudaAsync(progress, ct);
            if (!AmdEngines.GgufInstalled(m)) await AmdEngines.DownloadGgufAsync(m, progress, ct);
        }
        if (!UsesLlama(m)) { await AiSetup.InstallAsync(m, progress, ct); return; }
        var need = LlamaNeeds(s, m);
        await AiSetup.EnsureVcRuntimeAsync(progress, ct);
        if (!AmdEngines.LlamaInstalled) await AmdEngines.InstallLlamaAsync(progress, ct);
        if (!AmdEngines.GgufInstalled(m)) await AmdEngines.DownloadGgufAsync(m, progress, ct);
        if (need.ryzen && !AmdEngines.RyzenAiInstalled) await AmdEngines.InstallRyzenAiAsync(progress, ct);
        if (need.ryzen && !AmdEngines.NpuModelInstalled(m)) await AmdEngines.DownloadNpuModelAsync(m, progress, ct);
        progress.Report(L.T("pr.done"));
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Ways to load the model, in order. Runs on a background thread (device detection takes time).</summary>
    public static List<LoadCandidate> Candidates(Settings s)
    {
        var list = new List<LoadCandidate>();
        var m = AiSetup.FindModelInfo(s.AiModel);

        // NVIDIA (CUDA) first, if allowed: the whole model if it fits into the card's free memory right now,
        // otherwise as many layers as fit (the rest on the CPU)
        if (UseNvidia(s) && Nvidia.CudaInstalled && AmdEngines.GgufInstalled(m))
        {
            var g = Nvidia.Best();
            var off = g == null ? null : Nvidia.PlanOffload(m, g, GgufInfo.Read(AmdEngines.GgufPath(m)));
            if (g != null && off != null)
            {
                var cuda = "CUDA " + Nvidia.BuildFor(g).Version;
                var label = off.Full ? $"GPU ({ShortName(g.Name)}, {cuda})"
                                     : $"GPU ({ShortName(g.Name)}, {cuda}, {L.T("dev.layers", off.Layers, off.Total)})";
                list.Add(new LoadCandidate(label, () => Nvidia.StartLlama(m, g, off)));
                if (!off.Full) Log.Write($"NVIDIA {g.Name}: partial offload {off.Layers}/{off.Total} layers, free {g.FreeBytes / 1073741824.0:F1} GB");
            }
            else if (g != null)
                Log.Write($"NVIDIA {g.Name}: the model needs ~{Nvidia.NeededBytes(m) / 1073741824.0:F1} GB, " +
                          $"free {g.FreeBytes / 1073741824.0:F1} GB, not even a quarter of the layers fits - skipped");
        }

        if (!UsesLlama(m))
        {
            var dir = AiCorrector.FindModel(s);
            if (dir == null) return list;
            var cache = Path.Combine(AiSetup.BaseDir, "ovcache"); // local folder: the NPU cache can be large
            try { Directory.CreateDirectory(cache); } catch (Exception ex) { Log.Write("No cache folder: " + ex.Message); }
            foreach (var dev in OvDevices.Order(s.AiDevice, s.AiDevices))
            {
                // Some models have no NPU version (asymmetric INT4): skip the NPU, take the GPU/CPU
                if (!m.OpenVinoNpu && string.IsNullOrWhiteSpace(s.ModelPath) && OvDevices.Kind(dev) == "NPU") continue;
                var d = dev;
                list.Add(new LoadCandidate(d, () => new OvBackend(AiCorrector.SafePath(dir), d, AiCorrector.SafePath(cache))));
            }
            return list;
        }

        // GGUF-only model on a computer where Windows blocks llama.cpp (Smart App Control)
        if (LlamaBlocked) throw new InvalidOperationException(L.T("err.blocked_by_windows", "llama-server"));

        foreach (var kind in LlamaOrder(s, m))
        {
            switch (kind)
            {
                case "NPU" when AmdEngines.RyzenAiInstalled && AmdEngines.NpuModelInstalled(m):
                    list.Add(new LoadCandidate($"NPU (Ryzen AI {AmdEngines.RyzenAiVersion})", () => AmdEngines.StartRyzenAi(m)));
                    break;
                case "GPU" when AmdEngines.LlamaInstalled && AmdEngines.GgufInstalled(m):
                    var gpu = AmdEngines.PickGpu(AmdEngines.VulkanDevices() ?? new());
                    if (gpu != null) list.Add(new LoadCandidate($"GPU ({ShortName(gpu.Name)})", () => AmdEngines.StartLlama(m, gpu.Id)));
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
        List<DeviceInfo>? list;
        if (!Amd)
        {
            var found = OvDevices.Detect();
            // NPU, then GPUs, then CPU - the order the model is tried in
            // OpenVINO's GPU plugin also lists an NVIDIA card (via OpenCL) - it is shown once below, as the CUDA device
            list = found?.Where(d => !d.FullName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                .Select(d => new DeviceInfo(OvDevices.Kind(d.Name), ShortName(d.FullName), true, null, d.Name)).ToList();
            if (list == null && AmdEngines.LlamaInstalled)
            {
                // No OpenVINO yet (a GGUF-only model was installed): the GPU as llama.cpp sees it
                list = new List<DeviceInfo>();
                var gpu = AmdEngines.PickGpu(AmdEngines.VulkanDevices() ?? new());
                if (gpu != null) list.Add(new DeviceInfo("GPU", ShortName(gpu.Name), true, null, gpu.Id));
                list.Add(new DeviceInfo("CPU", ShortName(Hardware.CpuName), true));
            }
        }
        else
        {
            list = new List<DeviceInfo>();
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
        }
        // NVIDIA cards (used through CUDA when allowed in settings)
        foreach (var g in Nvidia.Gpus())
        {
            list ??= new List<DeviceInfo>();
            list.Add(new DeviceInfo("GPU", $"{ShortName(g.Name)}, {g.TotalBytes / 1073741824.0:F0} GB", Nvidia.DriverOk(g),
                Nvidia.DriverOk(g) ? null : L.T("sw.dev.nvidia_driver_old", g.Driver, Nvidia.MinDriver), "CUDA" + g.Index));
        }
        return list?.OrderBy(d => Array.IndexOf(Kinds, d.Kind)).ThenBy(d => d.Label).ToList();
    }

    /// <summary>"Intel(R) Core(TM) Ultra 7 255H" -> "Intel Core Ultra 7 255H".</summary>
    internal static string ShortName(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", ""), @"\s+", " ").Trim();

    /// <summary>Can the device list be shown now (Intel: OpenVINO or llama.cpp installed; AMD: always).</summary>
    public static bool CanDetectDevices => Amd || AiSetup.FindRuntimeDir() != null || AmdEngines.LlamaInstalled || Nvidia.SmiExists;
}
