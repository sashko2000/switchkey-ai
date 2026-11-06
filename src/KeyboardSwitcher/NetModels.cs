using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace KeyboardSwitcher;

/// <summary>
/// A model on a server: OpenAI, Anthropic (Claude), Google Gemini, or any OpenAI-compatible server (Ollama, LM Studio...).
/// The selected text and chat messages are sent to that server - only when the user chose such a model.
/// </summary>
public sealed class NetModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>openai, anthropic, gemini, ollama (any OpenAI-compatible server).</summary>
    public string Provider { get; set; } = "openai";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>API key encrypted for this Windows user (DPAPI), "dpapi:base64"; "" - no key.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Value of Settings.AiModel for this connection.</summary>
    public string Choice => NetModels.Prefix + Id;

    public string Title => (Name.Length > 0 ? Name : NetModels.Find(Provider).Title + ": " + Model);

    public NetModel Copy() => (NetModel)MemberwiseClone();
}

/// <summary>Providers and what they need.</summary>
internal sealed record NetProvider(string Id, string Title, string DefaultUrl, bool NeedsKey);

internal static class NetModels
{
    public const string Prefix = "net:";

    public static readonly IReadOnlyList<NetProvider> Providers = new[]
    {
        new NetProvider("openai", "OpenAI", "https://api.openai.com/v1", true),
        new NetProvider("anthropic", "Claude (Anthropic)", "https://api.anthropic.com/v1", true),
        new NetProvider("gemini", "Gemini (Google)", "https://generativelanguage.googleapis.com/v1beta/openai", true),
        new NetProvider("ollama", "Ollama / OpenAI-compatible", "http://localhost:11434/v1", false),
    };

    public static NetProvider Find(string id) => Providers.FirstOrDefault(p => p.Id == id) ?? Providers[^1];

    public static bool IsNet(string aiModel) => aiModel.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The connection selected in settings, or null (a local model, or the connection was deleted).</summary>
    public static NetModel? Selected(Settings s) =>
        IsNet(s.AiModel) ? s.NetModels.FirstOrDefault(n => n.Choice == s.AiModel) : null;

    /// <summary>Is the server on this computer or in the local network (the text doesn't leave it)?</summary>
    public static bool IsLocalServer(NetModel n) =>
        Uri.TryCreate(n.BaseUrl, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase));

    /// <summary>Model list from the server (GET /models) - to pick a model by name.</summary>
    public static async Task<List<string>> ListModelsAsync(NetModel n, string? key, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        using var http = NetBackend.NewHttp(n, key, handler, TimeSpan.FromSeconds(30));
        HttpResponseMessage r;
        try { r = await http.GetAsync(NetBackend.Url(n, "models"), ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new NetModelException(L.T("err.net_unreachable", NetBackend.Url(n, "models"), ex.Message)); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new NetModelException(L.T("err.net_timeout", Find(n.Provider).Title)); }
        using var _ = r;
        var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw NetBackend.Error(n, r.StatusCode, text);
        var list = new List<string>();
        var data = JsonNode.Parse(text)?["data"]?.AsArray();
        if (data != null)
            foreach (var m in data)
            {
                var id = m?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;
                list.Add(id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id);   // Gemini: "models/gemini-..."
            }
        return list.Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>API keys are stored encrypted with Windows DPAPI: only this Windows user on this computer can decrypt them.</summary>
internal static class Secret
{
    private const string Tag = "dpapi:";

    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        return Tag + Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(plain), protect: true));
    }

    public static string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        if (!stored.StartsWith(Tag, StringComparison.Ordinal)) return stored;   // typed into settings.json by hand
        return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(stored[Tag.Length..]), protect: false));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    private static byte[] Crypt(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI");
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            const int UiForbidden = 0x1;
            bool ok = protect
                ? CryptProtectData(ref input, "KeyboardSwitcher", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new InvalidDataException(L.T("err.net_key_unreadable"));
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { h.Free(); }
    }
}

/// <summary>An error from a network model (not InvalidOperationException: that one makes AiCorrector retry differently).</summary>
internal sealed class NetModelException(string message) : Exception(message);

/// <summary>
/// A model on a server. OpenAI, Gemini and Ollama: the OpenAI chat API (/chat/completions);
/// Anthropic: its Messages API (/messages, the system prompt is a separate field).
/// </summary>
internal sealed class NetBackend : ILlmBackend
{
    private readonly NetModel _n;
    private readonly HttpClient _http;

    public NetBackend(NetModel n, string? key, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(n.Model)) throw new NetModelException(L.T("err.net_no_model"));
        if (NetModels.Find(n.Provider).NeedsKey && string.IsNullOrEmpty(key)) throw new NetModelException(L.T("err.net_no_key"));
        _n = n;
        _http = NewHttp(n, key, handler, TimeSpan.FromMinutes(3));
    }

    public System.Diagnostics.Process? Child => null;

    internal static HttpClient NewHttp(NetModel n, string? key, HttpMessageHandler? handler, TimeSpan timeout)
    {
        var http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        http.Timeout = timeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KeyboardSwitcher/0.3");
        if (n.Provider == "anthropic")
        {
            if (!string.IsNullOrEmpty(key)) http.DefaultRequestHeaders.Add("x-api-key", key);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrEmpty(key))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    internal static string Url(NetModel n, string path)
    {
        var b = (n.BaseUrl.Length > 0 ? n.BaseUrl : NetModels.Find(n.Provider).DefaultUrl).TrimEnd('/');
        return b + "/" + path;
    }

    public string GenerateChat(string messagesJson, bool think, int maxNewTokens) =>
        GenerateChatAsync(messagesJson, maxNewTokens, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>The same with cancellation (the "Check" button of the network models window can be cancelled).</summary>
    internal Task<string> GenerateChatAsync(string messagesJson, int maxNewTokens, CancellationToken ct)
    {
        var messages = JsonNode.Parse(messagesJson)!.AsArray();
        // Cloud models think inside the token limit (OpenAI, Gemini): leave room so the answer is not cut off
        int max = Math.Max(maxNewTokens, _n.Provider is "openai" or "gemini" ? 16000 : 4096);
        return _n.Provider == "anthropic" ? Anthropic(messages, max, ct) : OpenAi(messages, max, ct);
    }

    public string GenerateText(string prompt, int maxNewTokens) =>
        GenerateChat(new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } }.ToJsonString(), false, maxNewTokens);

    private async Task<string> OpenAi(JsonArray messages, int max, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = _n.Model, ["messages"] = messages.DeepClone(), ["stream"] = false };
        if (_n.Provider == "openai")
            body["max_completion_tokens"] = max;   // newer OpenAI models: no max_tokens, only the default temperature
        else
        {
            body["max_tokens"] = max;
            body["temperature"] = 0;
        }
        var res = await PostAsync("chat/completions", body, ct);
        var content = res?["choices"]?[0]?["message"]?["content"];
        return content switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")),
            _ => "",
        };
    }

    private async Task<string> Anthropic(JsonArray messages, int max, CancellationToken ct)
    {
        var system = new StringBuilder();
        var rest = new JsonArray();
        foreach (var m in messages)
        {
            var role = m?["role"]?.GetValue<string>();
            var text = m?["content"]?.GetValue<string>() ?? "";
            if (role == "system") system.Append(system.Length > 0 ? "\n\n" : "").Append(text);
            else rest.Add(new JsonObject { ["role"] = role, ["content"] = text });
        }
        var body = new JsonObject { ["model"] = _n.Model, ["max_tokens"] = max, ["messages"] = rest };
        if (system.Length > 0) body["system"] = system.ToString();
        var res = await PostAsync("messages", body, ct);
        var sb = new StringBuilder();
        foreach (var part in res?["content"]?.AsArray() ?? new JsonArray())
            if (part?["type"]?.GetValue<string>() == "text") sb.Append(part["text"]?.GetValue<string>());
        return sb.ToString();
    }

    private async Task<JsonNode?> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        HttpResponseMessage r;
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            r = await _http.PostAsync(Url(_n, path), content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw new NetModelException(L.T("err.net_unreachable", Url(_n, path), ex.Message)); }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (TaskCanceledException) { throw new NetModelException(L.T("err.net_timeout", NetModels.Find(_n.Provider).Title)); }
        using (r)
        {
            var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) throw Error(_n, r.StatusCode, text);
            try { return JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException ex) { throw new NetModelException("Bad server answer: " + ex.Message); }
        }
    }

    /// <summary>A readable error: wrong key, limit, or the server's own message.</summary>
    internal static NetModelException Error(NetModel n, HttpStatusCode code, string body)
    {
        string? msg = null;
        try
        {
            var j = JsonNode.Parse(body);
            var e = j?["error"];
            msg = e is JsonValue ? e.GetValue<string>() : e?["message"]?.GetValue<string>();
            if (j is JsonArray a) msg = a[0]?["error"]?["message"]?.GetValue<string>();   // Gemini answers with a list
        }
        catch { }
        msg ??= body.Length > 200 ? body[..200] : body;
        var who = NetModels.Find(n.Provider).Title;
        return code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new NetModelException(L.T("err.net_auth", who, msg)),
            HttpStatusCode.TooManyRequests => new NetModelException(L.T("err.net_limit", who, msg)),
            HttpStatusCode.NotFound => new NetModelException(L.T("err.net_not_found", who, n.Model, msg)),
            _ => new NetModelException($"{who}: HTTP {(int)code}: {msg}"),
        };
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Models for the model lists in settings, the assistant and the chat: the catalog, then network models.</summary>
internal static class ModelChoices
{
    public static List<(string Id, string Title)> All(Settings s)
    {
        // Installed models are marked; the others show how much will be downloaded
        var list = AiSetup.Catalog.Select(m => (m.Id, Installed(m, s)
            ? L.T("sw.model_item_installed", m.Title)
            : L.T("sw.model_item", m.Title, AiPlatform.DownloadGb(m)))).ToList();
        foreach (var n in s.NetModels) list.Add((n.Choice, L.T("net.item", n.Title)));
        return list;
    }

    /// <summary>The model can run on this computer: it and an engine for it are downloaded.</summary>
    private static bool Installed(ModelInfo m, Settings s)
    {
        try { return AiPlatform.Installed(new Settings { AiModel = m.Id, AiDevice = s.AiDevice, AiNvidia = s.AiNvidia, ModelPath = s.ModelPath, AiDevices = s.AiDevices }); }
        catch (Exception ex) { Log.Write("Model state: " + ex.Message); return false; }
    }

    /// <summary>Fill a drop-down list and select <paramref name="selected"/>.</summary>
    public static void Fill(IntPtr combo, IReadOnlyList<(string Id, string Title)> choices, string selected)
    {
        Native.SendMessage(combo, Native.CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        foreach (var c in choices) Native.SendMessage(combo, Native.CB_ADDSTRING, IntPtr.Zero, c.Title);
        int i = choices.ToList().FindIndex(c => c.Id == selected);
        Native.SendMessage(combo, Native.CB_SETCURSEL, (IntPtr)Math.Max(0, i), IntPtr.Zero);
    }

    public static string? Selected(IntPtr combo, IReadOnlyList<(string Id, string Title)> choices)
    {
        int i = (int)Native.SendMessage(combo, Native.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return i >= 0 && i < choices.Count ? choices[i].Id : null;
    }
}
