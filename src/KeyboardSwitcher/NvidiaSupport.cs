using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;

namespace KeyboardSwitcher;

/// <summary>
/// NVIDIA graphics cards: found with nvidia-smi (installed with the NVIDIA driver), the model runs in llama.cpp built
/// for CUDA. Used only when the user turns it on in settings. If the model doesn't fit into the card's free memory,
/// as many layers as fit go to the card and the rest are computed on the CPU (partial offload).
/// </summary>
internal static class Nvidia
{
    /// <summary>An NVIDIA card: index for CUDA, name, memory in bytes, driver version, compute capability ("12.0").</summary>
    public sealed record Gpu(int Index, string Name, long TotalBytes, long FreeBytes, string Driver, string ComputeCap = "8.9");

    /// <summary>A llama.cpp CUDA build from the ggml-org release + the CUDA runtime DLLs for it.</summary>
    public sealed record CudaBuild(string Version, long ZipBytes, string ZipSha256, long RuntimeBytes, string RuntimeSha256)
    {
        public string Zip => "llama-" + AmdEngines.LlamaVersion + "-bin-win-cuda-" + Version + "-x64.zip";
        public string RuntimeZip => "cudart-llama-bin-win-cuda-" + Version + "-x64.zip";
        public double Gb => (ZipBytes + RuntimeBytes) / 1073741824.0;
        public string Dir => Path.Combine(AiSetup.BaseDir, "llamacpp", AmdEngines.LlamaVersion + "-cuda" + Version);
        public string Server => Path.Combine(Dir, "llama-server.exe");
        public bool Installed => File.Exists(Server) && Directory.GetFiles(Dir, "cudart64_*.dll").Length > 0;
    }

    /// <summary>CUDA 12.4: any card from GeForce 900 on, NVIDIA driver 551.61 or newer.</summary>
    public static readonly CudaBuild Cuda12 = new("12.4", 279441792,
        "1fce8596b732964e7763b0b7c65cff7c812670601f2c432f3dd2bc25d205d1ba", 391443627,
        "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6");

    /// <summary>
    /// CUDA 13.4: has native code for Blackwell (RTX 50, RTX PRO ...: compute capability 10.x/12.x). CUDA 12.4 doesn't know
    /// these cards, so its code is compiled by the driver on the first start (tests: 41 s load + 52 s first answer).
    /// Needs an NVIDIA driver 580 or newer.
    /// </summary>
    public static readonly CudaBuild Cuda13 = new("13.4", 164743095,
        "d0d5c73120a669b4e25c99bf21a8b2c6c3273527d5b50965865cd019cd4deb11", 423535356,
        "738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668");

    public static readonly Version MinDriver = new(551, 61);
    public static readonly Version MinDriverCuda13 = new(580, 0);
    private const string ReleaseUrl = "https://github.com/ggml-org/llama.cpp/releases/download/" + AmdEngines.LlamaVersion + "/";

    /// <summary>Blackwell or newer with a driver for CUDA 13 - take the CUDA 13 build.</summary>
    public static CudaBuild BuildFor(Gpu? g)
    {
        if (g == null) return Cuda12;
        bool newCard = Version.TryParse(g.ComputeCap, out var cc) && cc.Major >= 10;
        bool driver13 = Version.TryParse(g.Driver, out var d) && d >= MinDriverCuda13;
        return newCard && driver13 ? Cuda13 : Cuda12;
    }

    /// <summary>The build for the cards of this computer (the newest card decides).</summary>
    public static CudaBuild Build =>
        BuildFor(Gpus().Where(DriverOk).OrderByDescending(g => Version.TryParse(g.ComputeCap, out var v) ? v : new Version(0, 0)).FirstOrDefault());

    public static string CudaVersion => Build.Version;
    public static double CudaGb => Build.Gb;
    public static string CudaDir => Build.Dir;
    public static string CudaServer => Build.Server;
    public static bool CudaInstalled => Build.Installed;

    /// <summary>Tests: a fixed list of cards instead of asking nvidia-smi.</summary>
    internal static Func<List<Gpu>>? Override { get; set; }

    private static List<Gpu>? _found;

    /// <summary>NVIDIA cards (cached; free memory is from the time of the first query).</summary>
    public static List<Gpu> Gpus() => Override?.Invoke() ?? (_found ??= Query());

    /// <summary>Is there an NVIDIA card with a driver that can run CUDA 12.4?</summary>
    public static bool Present => Gpus().Any(DriverOk);

    public static bool DriverOk(Gpu g) => Version.TryParse(g.Driver, out var v) && v >= MinDriver;

    /// <summary>The card with the most free memory right now (asks nvidia-smi again: memory use changes).</summary>
    public static Gpu? Best()
    {
        var list = Override?.Invoke() ?? (_found = Query());
        return list.Where(DriverOk).OrderByDescending(g => g.FreeBytes).FirstOrDefault();
    }

    /// <summary>
    /// Video memory the model needs: the GGUF file (all layers go to the card) + the context (4096 tokens) and CUDA
    /// buffers. Rough, on the safe side: +10% and 1 GB.
    /// </summary>
    public static long NeededBytes(ModelInfo m) => m.GgufBytes + m.GgufBytes / 10 + (1L << 30);

    public static bool Fits(ModelInfo m, Gpu g) => m.GgufFile != null && g.FreeBytes >= NeededBytes(m);

    /// <summary>Kept free on the card for a partial offload: CUDA context, compute buffers, the context of the layers.</summary>
    public const long PartialReserve = 1L << 30;

    /// <summary>Partial offload is worth it only if at least this share of the layers goes to the card.</summary>
    public const double MinPartialShare = 0.25;

    /// <summary>How many of the model's layers go to the card: Layers == Total - the whole model (-ngl 99).</summary>
    public sealed record Offload(int Layers, int Total)
    {
        public bool Full => Layers >= Total;
        public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Math.Min(Layers, Total) / Total);
    }

    /// <summary>
    /// Whole model if it fits; otherwise as many layers as fit into the free memory (each layer +10% for its part of
    /// the context), keeping <see cref="PartialReserve"/> free. null - not even a quarter of the layers fits, or the
    /// layer sizes are unknown (the GGUF file can't be read).
    /// </summary>
    public static Offload? PlanOffload(ModelInfo m, Gpu g, GgufInfo? gguf)
    {
        if (m.GgufFile == null) return null;
        if (Fits(m, g)) return new Offload(gguf?.BlockCount ?? 1, gguf?.BlockCount ?? 1);
        if (gguf == null || gguf.BlockCount == 0 || gguf.LayerBytes == 0) return null;
        double perLayer = gguf.LayerBytes / (double)gguf.BlockCount * 1.1;
        int n = (int)Math.Floor((g.FreeBytes - PartialReserve) / perLayer);
        n = Math.Min(n, gguf.BlockCount - 1);   // the whole model was checked above
        return n >= Math.Max(4, (int)Math.Ceiling(gguf.BlockCount * MinPartialShare)) ? new Offload(n, gguf.BlockCount) : null;
    }

    /// <summary>
    /// For the settings note, before the model is downloaded: the share of the model that will be on the card
    /// (by total memory, from the file size), 0..100. 100 - the whole model fits.
    /// </summary>
    public static int EstimatePercent(ModelInfo m, long cardBytes)
    {
        if (NeededBytes(m) <= cardBytes) return 100;
        double share = (cardBytes - PartialReserve) / (m.GgufBytes * 1.1);   // embeddings are ~5% of the file - close enough
        return (int)Math.Clamp(Math.Floor(share * 100), 0, 99);
    }

    private static List<Gpu> Query()
    {
        var list = new List<Gpu>();
        var exe = FindSmi();
        if (exe == null) return list;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe,
                "--query-gpu=index,memory.total,memory.free,driver_version,compute_cap,name --format=csv,noheader,nounits")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var text = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10000)) { try { p.Kill(true); } catch { } return list; }
            list = Parse(text.Result);
        }
        catch (Exception ex) { Log.Write("nvidia-smi failed: " + ex.Message); }
        Log.Write("NVIDIA: " + (list.Count == 0 ? "none" : string.Join("; ", list.Select(g =>
            $"{g.Index} = {g.Name}, {g.FreeBytes >> 20} of {g.TotalBytes >> 20} MiB free, driver {g.Driver}, compute {g.ComputeCap}"))));
        return list;
    }

    /// <summary>"0, 8188, 7820, 566.36, 8.9, NVIDIA GeForce RTX 4060 Laptop GPU" per line (MiB); the name is last (it may contain commas).</summary>
    internal static List<Gpu> Parse(string csv)
    {
        var list = new List<Gpu>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length < 6) continue;
            if (!int.TryParse(f[0], out int idx)) continue;
            if (!long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long total)) continue;
            if (!long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long free)) continue;
            list.Add(new Gpu(idx, string.Join(", ", f[5..]), total << 20, free << 20, f[3], f[4]));
        }
        return list;
    }

    /// <summary>nvidia-smi is there (the NVIDIA driver is installed) - cheap, without running it.</summary>
    public static bool SmiExists => Override != null || FindSmi() != null;

    private static string? FindSmi()
    {
        var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(sys)) return sys;
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        return File.Exists(old) ? old : null;
    }

    // ------------------------------------------------------------------ running and installing

    public static ILlmBackend StartLlama(ModelInfo m, Gpu g, Offload offload)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        // Partial: the first layers stay on the CPU, the last N go to the card (llama.cpp offloads from the end)
        var ngl = offload.Full ? 99 : offload.Layers;
        var args = $"-m \"{AmdEngines.GgufPath(m)}\" --host 127.0.0.1 --port {{port}} -c 4096 --jinja -ngl {ngl} " +
                   $"--device CUDA{g.Index} --api-key {key}";
        // Number the cards the way nvidia-smi does (CUDA puts the fastest first by default)
        var build = BuildFor(g);
        return new ServerBackend(build.Server, args, key, thinkingKwargs: true, TimeSpan.FromMinutes(5),
            new Dictionary<string, string>
            {
                ["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID",
                // If the driver has to compile kernels (a card newer than the build), keep them all in its cache
                ["CUDA_CACHE_MAXSIZE"] = "4294967296",
            });
    }

    public static async Task InstallCudaAsync(IProgress<string> progress, CancellationToken ct)
    {
        var b = Build;
        Log.Write("llama.cpp CUDA " + b.Version);
        var zip = Path.Combine(AiSetup.DownloadsDir, b.Zip);
        var rt = Path.Combine(AiSetup.DownloadsDir, b.RuntimeZip);
        await AiSetup.DownloadAsync(ReleaseUrl + b.Zip, zip, "llama.cpp CUDA " + b.Version, progress, ct, b.ZipBytes);
        await AiSetup.DownloadAsync(ReleaseUrl + b.RuntimeZip, rt, "CUDA runtime " + b.Version, progress, ct, b.RuntimeBytes);
        progress.Report(L.T("pr.check", "llama.cpp CUDA"));
        await CheckSha256Async(zip, b.ZipSha256, ct);
        await CheckSha256Async(rt, b.RuntimeSha256, ct);
        progress.Report(L.T("pr.unpack", "llama.cpp CUDA"));
        await AmdEngines.UnpackAsync(zip, b.Dir, "llama-server.exe", ct);
        // The runtime archive holds only DLLs: put them next to llama-server.exe
        var tmp = b.Dir + ".rt";
        await Task.Run(() =>
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(rt, tmp, true);
            foreach (var dll in Directory.GetFiles(tmp, "*.dll", SearchOption.AllDirectories))
                File.Copy(dll, Path.Combine(b.Dir, Path.GetFileName(dll)), true);
            Directory.Delete(tmp, true);
        }, ct);
        try { File.Delete(rt); } catch { }
    }

    private static async Task CheckSha256Async(string file, string sha, CancellationToken ct)
    {
        string actual;
        await using (var fs = File.OpenRead(file))
            actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (actual == sha) return;
        File.Delete(file);
        throw new InvalidOperationException(L.T("err.corrupt", Path.GetFileName(file)));
    }
}

/// <summary>
/// What we need from a GGUF file header: number of layers (blocks) and their total size. The header is read up to the
/// tensor list (a few MB: the tokenizer vocabulary is in it); the weights are not read.
/// </summary>
internal sealed record GgufInfo(string Architecture, int BlockCount, long LayerBytes, long OtherBytes)
{
    private static readonly Dictionary<string, (long size, DateTime time, GgufInfo? info)> Cache = new();

    /// <summary>Cached by path, size and time. null - not a GGUF file or a format we don't understand.</summary>
    public static GgufInfo? Read(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            lock (Cache)
                if (Cache.TryGetValue(path, out var c) && c.size == fi.Length && c.time == fi.LastWriteTimeUtc) return c.info;
            GgufInfo? info;
            try { info = Parse(path); }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or OverflowException or ArgumentException)
            {
                Log.Write("GGUF header of " + Path.GetFileName(path) + ": " + ex.Message);
                info = null;
            }
            lock (Cache) Cache[path] = (fi.Length, fi.LastWriteTimeUtc, info);
            return info;
        }
        catch (Exception ex) { Log.Write("GGUF: " + ex.Message); return null; }
    }

    private static GgufInfo? Parse(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var r = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != 0x46554747) return null;                 // "GGUF"
        uint version = r.ReadUInt32();
        if (version < 2) return null;
        long tensors = checked((long)r.ReadUInt64());
        long kvs = checked((long)r.ReadUInt64());
        if (tensors > 1_000_000 || kvs > 1_000_000) throw new InvalidDataException("bad counts");

        var meta = new Dictionary<string, object?>();
        for (long i = 0; i < kvs; i++)
        {
            var key = Str(r);
            meta[key] = Value(r, r.ReadUInt32());
        }
        var arch = meta.GetValueOrDefault("general.architecture") as string ?? "";
        int blocks = (int)ToLong(meta.GetValueOrDefault(arch + ".block_count"));
        long align = ToLong(meta.GetValueOrDefault("general.alignment"));
        if (align <= 0) align = 32;

        var list = new List<(string name, long offset)>();
        for (long i = 0; i < tensors; i++)
        {
            var name = Str(r);
            uint dims = r.ReadUInt32();
            if (dims > 8) throw new InvalidDataException("bad tensor dims");
            for (int d = 0; d < dims; d++) r.ReadUInt64();
            r.ReadUInt32();                                                // type
            list.Add((name, checked((long)r.ReadUInt64())));
        }
        long dataStart = (fs.Position + align - 1) / align * align;
        long dataSize = fs.Length - dataStart;
        // A tensor's size = the distance to the next one (the data are packed in offset order, with padding)
        var sorted = list.OrderBy(t => t.offset).ToList();
        long layer = 0, other = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            long end = i + 1 < sorted.Count ? sorted[i + 1].offset : dataSize;
            long size = Math.Max(0, end - sorted[i].offset);
            if (sorted[i].name.StartsWith("blk.", StringComparison.Ordinal)) layer += size; else other += size;
        }
        return new GgufInfo(arch, blocks, layer, other);
    }

    private static string Str(BinaryReader r)
    {
        ulong n = r.ReadUInt64();
        if (n > 1 << 20) throw new InvalidDataException("string too long");
        return System.Text.Encoding.UTF8.GetString(r.ReadBytes((int)n));
    }

    private static object? Value(BinaryReader r, uint type)
    {
        switch (type)
        {
            case 0: return (long)r.ReadByte();
            case 1: return (long)r.ReadSByte();
            case 2: return (long)r.ReadUInt16();
            case 3: return (long)r.ReadInt16();
            case 4: return (long)r.ReadUInt32();
            case 5: return (long)r.ReadInt32();
            case 6: return (double)r.ReadSingle();
            case 7: return r.ReadByte() != 0;
            case 8: return Str(r);
            case 9:
                {
                    uint itemType = r.ReadUInt32();
                    long count = checked((long)r.ReadUInt64());
                    if (count > 50_000_000) throw new InvalidDataException("array too long");
                    // Fixed-size items are skipped in one go (the tokenizer's scores and types are long arrays)
                    int fixedSize = itemType switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };
                    if (fixedSize > 0) { r.BaseStream.Seek(checked(count * fixedSize), SeekOrigin.Current); return null; }
                    for (long i = 0; i < count; i++) Value(r, itemType);
                    return null;
                }
            case 10: return checked((long)r.ReadUInt64());
            case 11: return r.ReadInt64();
            case 12: return r.ReadDouble();
            default: throw new InvalidDataException("unknown GGUF value type " + type);
        }
    }

    private static long ToLong(object? v) => v switch { long l => l, double d => (long)d, _ => 0 };
}
