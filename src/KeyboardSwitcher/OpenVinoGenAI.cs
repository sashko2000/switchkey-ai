using System.Runtime.InteropServices;
using System.Text;

namespace KeyboardSwitcher;

/// <summary>
/// Wrapper over the OpenVINO GenAI C API (openvino_genai_c.dll, version 2026.4).
/// The same LLMPipeline as in the Python scripts of the npu folder, just without Python.
/// </summary>
internal static unsafe class OvGenAI
{
    private const string Dll = "openvino_genai_c";

    // ov_status_e: 0 - OK, negative - errors
    public static string StatusText(int s) => s switch
    {
        0 => "OK",
        -1 => "GENERAL_ERROR",
        -2 => "NOT_IMPLEMENTED",
        -3 => "NETWORK_NOT_LOADED",
        -4 => "PARAMETER_MISMATCH",
        -5 => "NOT_FOUND",
        -6 => "OUT_OF_BOUNDS",
        -7 => "UNEXPECTED",
        -14 => "INVALID_C_PARAM",
        -17 => "UNKNOWN_EXCEPTION (error inside OpenVINO)",
        _ => "code " + s
    };

    // ov_genai_llm_pipeline_create(path, device, property_args_size, &pipe, ...)
    // Variadic function. On Windows x64 pointer strings are passed the same way
    // as regular arguments, so the needed variants are declared as fixed overloads.
    [DllImport(Dll, EntryPoint = "ov_genai_llm_pipeline_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PipelineCreate0(byte* path, byte* device, nuint n, out IntPtr pipe);

    [DllImport(Dll, EntryPoint = "ov_genai_llm_pipeline_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PipelineCreate2(byte* path, byte* device, nuint n, out IntPtr pipe, byte* k1, byte* v1);

    [DllImport(Dll, EntryPoint = "ov_genai_llm_pipeline_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PipelineCreate6(byte* path, byte* device, nuint n, out IntPtr pipe,
        byte* k1, byte* v1, byte* k2, byte* v2, byte* k3, byte* v3);

    [DllImport(Dll, EntryPoint = "ov_genai_llm_pipeline_free", CallingConvention = CallingConvention.Cdecl)]
    public static extern void PipelineFree(IntPtr pipe);

    [DllImport(Dll, EntryPoint = "ov_genai_llm_pipeline_generate", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Generate(IntPtr pipe, byte* input, IntPtr config, IntPtr streamer, out IntPtr results);

    [DllImport(Dll, EntryPoint = "ov_genai_generation_config_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ConfigCreate(out IntPtr config);

    [DllImport(Dll, EntryPoint = "ov_genai_generation_config_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ConfigFree(IntPtr config);

    [DllImport(Dll, EntryPoint = "ov_genai_generation_config_set_max_new_tokens", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ConfigSetMaxNewTokens(IntPtr config, nuint value);

    [DllImport(Dll, EntryPoint = "ov_genai_generation_config_set_do_sample", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ConfigSetDoSample(IntPtr config, [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(Dll, EntryPoint = "ov_genai_decoded_results_get_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ResultsGetString(IntPtr results, byte* output, ref nuint size);

    [DllImport(Dll, EntryPoint = "ov_genai_decoded_results_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ResultsFree(IntPtr results);

    private static byte[] Utf8(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    /// <summary>Create a pipeline. properties - "key", "value" pairs (0, 1 or 3 pairs).</summary>
    public static IntPtr CreatePipeline(string modelDir, string device, params string[] properties)
    {
        byte[] p = Utf8(modelDir), d = Utf8(device);
        var props = properties.Select(Utf8).ToArray();
        int st;
        IntPtr pipe;
        fixed (byte* pp = p, pd = d)
        {
            switch (props.Length)
            {
                case 0:
                    st = PipelineCreate0(pp, pd, 0, out pipe);
                    break;
                case 2:
                    fixed (byte* k1 = props[0], v1 = props[1])
                        st = PipelineCreate2(pp, pd, 2, out pipe, k1, v1);
                    break;
                case 6:
                    fixed (byte* k1 = props[0], v1 = props[1], k2 = props[2], v2 = props[3], k3 = props[4], v3 = props[5])
                        st = PipelineCreate6(pp, pd, 6, out pipe, k1, v1, k2, v2, k3, v3);
                    break;
                default:
                    throw new ArgumentException("0, 1 or 3 property pairs are supported");
            }
        }
        if (st != 0 || pipe == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load the model on {device}: {StatusText(st)}");
        return pipe;
    }

    /// <summary>Generate an answer (greedy search, no randomness).</summary>
    public static string GenerateText(IntPtr pipe, string prompt, int maxNewTokens)
    {
        IntPtr cfg = IntPtr.Zero, res = IntPtr.Zero;
        try
        {
            Check(ConfigCreate(out cfg), "generation_config_create");
            Check(ConfigSetMaxNewTokens(cfg, (nuint)maxNewTokens), "set_max_new_tokens");
            Check(ConfigSetDoSample(cfg, false), "set_do_sample");

            var input = Utf8(prompt);
            fixed (byte* pi = input)
                Check(Generate(pipe, pi, cfg, IntPtr.Zero, out res), "generate");

            nuint size = 0;
            Check(ResultsGetString(res, null, ref size), "get_string(size)");
            var buf = new byte[(int)size];
            fixed (byte* pb = buf)
                Check(ResultsGetString(res, pb, ref size), "get_string");
            int len = Array.IndexOf(buf, (byte)0);
            return Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
        }
        finally
        {
            if (res != IntPtr.Zero) ResultsFree(res);
            if (cfg != IntPtr.Zero) ConfigFree(cfg);
        }
    }

    // ---------- Chat (ChatHistory): system instruction + examples + user text ----------

    [DllImport(Dll, EntryPoint = "ov_genai_json_container_create_from_json_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern int JsonCreate(out IntPtr container, byte* json);

    [DllImport(Dll, EntryPoint = "ov_genai_json_container_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void JsonFree(IntPtr container);

    [DllImport(Dll, EntryPoint = "ov_genai_chat_history_create_from_json_container", CallingConvention = CallingConvention.Cdecl)]
    private static extern int HistoryCreate(out IntPtr history, IntPtr messages);

    [DllImport(Dll, EntryPoint = "ov_genai_chat_history_set_extra_context", CallingConvention = CallingConvention.Cdecl)]
    private static extern int HistorySetExtra(IntPtr history, IntPtr extra);

    [DllImport(Dll, EntryPoint = "ov_genai_chat_history_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void HistoryFree(IntPtr history);

    [DllImport(Dll, EntryPoint = "ov_genai_llm_pipeline_generate_with_history", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GenerateWithHistory(IntPtr pipe, IntPtr history, IntPtr config, IntPtr streamer, out IntPtr results);

    private static IntPtr Json(string json)
    {
        var b = Utf8(json);
        IntPtr c;
        fixed (byte* pb = b)
            if (JsonCreate(out c, pb) != 0 || c == IntPtr.Zero) throw new InvalidOperationException("OpenVINO GenAI: invalid JSON");
        return c;
    }

    /// <summary>
    /// Chat answer. messages - JSON array [{"role":"system|user|assistant","content":"..."}],
    /// extraContext - template variables, e.g. {"enable_thinking": false} for Qwen3.
    /// </summary>
    public static string GenerateChat(IntPtr pipe, string messagesJson, string? extraContextJson, int maxNewTokens)
    {
        IntPtr cfg = IntPtr.Zero, res = IntPtr.Zero, msgs = IntPtr.Zero, extra = IntPtr.Zero, hist = IntPtr.Zero;
        try
        {
            Check(ConfigCreate(out cfg), "generation_config_create");
            Check(ConfigSetMaxNewTokens(cfg, (nuint)maxNewTokens), "set_max_new_tokens");
            Check(ConfigSetDoSample(cfg, false), "set_do_sample");
            msgs = Json(messagesJson);
            if (HistoryCreate(out hist, msgs) != 0 || hist == IntPtr.Zero) throw new InvalidOperationException("OpenVINO GenAI: chat_history_create");
            if (extraContextJson != null)
            {
                extra = Json(extraContextJson);
                HistorySetExtra(hist, extra); // if the model template doesn't know the variable - it is simply unused
            }
            Check(GenerateWithHistory(pipe, hist, cfg, IntPtr.Zero, out res), "generate_with_history");
            nuint size = 0;
            Check(ResultsGetString(res, null, ref size), "get_string(size)");
            var buf = new byte[(int)size];
            fixed (byte* pb = buf)
                Check(ResultsGetString(res, pb, ref size), "get_string");
            int len = Array.IndexOf(buf, (byte)0);
            return Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
        }
        finally
        {
            if (res != IntPtr.Zero) ResultsFree(res);
            if (hist != IntPtr.Zero) HistoryFree(hist);
            if (extra != IntPtr.Zero) JsonFree(extra);
            if (msgs != IntPtr.Zero) JsonFree(msgs);
            if (cfg != IntPtr.Zero) ConfigFree(cfg);
        }
    }

    private static void Check(int st, string what)
    {
        if (st != 0) throw new InvalidOperationException($"OpenVINO GenAI: {what} - {StatusText(st)}");
    }
}

/// <summary>
/// Devices OpenVINO can run the model on (openvino_c.dll from the same folder as openvino_genai_c.dll):
/// NPU, GPU (Intel graphics only - OpenVINO does not use NVIDIA/AMD cards) and CPU.
/// </summary>
internal static class OvDevices
{
    private const string Dll = "openvino_c";

    [StructLayout(LayoutKind.Sequential)]
    private struct AvailableDevices { public IntPtr Devices; public nuint Size; }

    [DllImport(Dll, EntryPoint = "ov_core_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CoreCreate(out IntPtr core);
    [DllImport(Dll, EntryPoint = "ov_core_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CoreFree(IntPtr core);
    [DllImport(Dll, EntryPoint = "ov_core_get_available_devices", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetAvailable(IntPtr core, ref AvailableDevices devices);
    [DllImport(Dll, EntryPoint = "ov_available_devices_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void AvailableFree(ref AvailableDevices devices);
    [DllImport(Dll, EntryPoint = "ov_core_get_property", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetProperty(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string device,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, out IntPtr value);
    [DllImport(Dll, EntryPoint = "ov_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Free(IntPtr p);

    public sealed record Device(string Name, string FullName);

    private static IReadOnlyList<Device>? _cache;
    private static readonly object Sync = new();

    /// <summary>Detected devices (cached after the first successful call). null - OpenVINO is not installed or failed.</summary>
    public static IReadOnlyList<Device>? Detect()
    {
        lock (Sync)
        {
            if (_cache != null) return _cache;
            if (AiSetup.FindRuntimeDir() == null) return null;
            IntPtr core = IntPtr.Zero;
            try
            {
                if (CoreCreate(out core) != 0 || core == IntPtr.Zero) return null;
                var list = new AvailableDevices();
                if (GetAvailable(core, ref list) != 0) return null;
                var result = new List<Device>();
                try
                {
                    for (int i = 0; i < (int)list.Size; i++)
                    {
                        var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(list.Devices, i * IntPtr.Size)) ?? "";
                        string full = name;
                        if (GetProperty(core, name, "FULL_DEVICE_NAME", out var v) == 0 && v != IntPtr.Zero)
                        {
                            full = Marshal.PtrToStringUTF8(v) ?? name;
                            Free(v);
                        }
                        result.Add(new Device(name, full.Trim()));
                    }
                }
                finally { AvailableFree(ref list); }
                Log.Write("OpenVINO devices: " + string.Join("; ", result.Select(d => $"{d.Name} = {d.FullName}")));
                return _cache = result;
            }
            catch (Exception ex)
            {
                Log.Write("Device detection failed: " + ex.Message);
                return null;
            }
            finally { if (core != IntPtr.Zero) CoreFree(core); }
        }
    }

    /// <summary>Device kind: "NPU", "GPU" or "CPU" ("GPU.0" -> "GPU").</summary>
    public static string Kind(string name)
    {
        int dot = name.IndexOf('.');
        return (dot < 0 ? name : name[..dot]).ToUpperInvariant();
    }

    /// <summary>
    /// Order of devices to try. choice: "auto" - the order from settings (NPU, GPU, CPU), otherwise the chosen kind first,
    /// then the rest as a fallback. Devices that OpenVINO did not find are skipped (if detection worked).
    /// </summary>
    public static List<string> Order(string choice, IReadOnlyList<string> configured)
    {
        var order = configured.ToList();
        if (!string.Equals(choice, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var chosen = order.FirstOrDefault(d => Kind(d) == choice.ToUpperInvariant()) ?? (choice.ToUpperInvariant() == "GPU" ? "GPU.0" : choice.ToUpperInvariant());
            order.RemoveAll(d => Kind(d) == Kind(chosen));
            order.Insert(0, chosen);
        }
        var found = Detect();
        if (found == null) return order;
        var kinds = found.Select(d => Kind(d.Name)).ToHashSet();
        var filtered = order.Where(d => kinds.Contains(Kind(d))).ToList();
        return filtered.Count > 0 ? filtered : order;
    }
}
