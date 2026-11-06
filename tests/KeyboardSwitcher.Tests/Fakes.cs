using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using KeyboardSwitcher;

namespace KeyboardSwitcher.Tests;

/// <summary>A model that answers from a script and records the requests.</summary>
internal sealed class FakeBackend : ILlmBackend
{
    public sealed record Call(string System, string User, int Messages, bool Think, int MaxTokens);
    public List<Call> Calls { get; } = new();
    public List<string> TextCalls { get; } = new();
    public Func<string, string, bool, string> Reply { get; set; } = (_, _, _) => "";
    public Func<string, string> TextReply { get; set; } = _ => "";
    public bool ChatThrows { get; set; }
    public bool Disposed { get; private set; }
    public Process? Child => null;

    public string GenerateChat(string messagesJson, bool think, int maxNewTokens)
    {
        var msgs = JsonNode.Parse(messagesJson)!.AsArray();
        var sys = msgs.FirstOrDefault(m => m!["role"]!.GetValue<string>() == "system")?["content"]?.GetValue<string>() ?? "";
        var user = msgs[^1]!["content"]!.GetValue<string>();
        Calls.Add(new Call(sys, user, msgs.Count, think, maxNewTokens));
        if (ChatThrows) throw new InvalidOperationException("template does not support the system role");
        return Reply(sys, user, think);
    }

    public string GenerateText(string prompt, int maxNewTokens) { TextCalls.Add(prompt); return TextReply(prompt); }
    public void Dispose() => Disposed = true;
}

/// <summary>
/// A tiny OpenAI-compatible server in the test process: /health, /v1/chat/completions, /v1/completions.
/// readyAfter - /health answers 503 until then (like llama-server while loading).
/// </summary>
internal sealed class FakeServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly DateTime _started = DateTime.Now;
    private readonly TimeSpan _readyAfter;
    private volatile bool _stopped;

    public string Url { get; }

    public FakeServer(TimeSpan readyAfter)
    {
        _readyAfter = readyAfter;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        new Thread(Loop) { IsBackground = true }.Start();
    }

    private void Loop()
    {
        while (!_stopped)
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                Serve(client.GetStream());
            }
            catch { if (_stopped) return; }
        }
    }

    private void Serve(Stream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n") && stream.Read(one, 0, 1) == 1) head.Append((char)one[0]);
        var lines = head.ToString().Split("\r\n");
        var parts = lines[0].Split(' ');
        string method = parts[0], path = parts.Length > 1 ? parts[1] : "/";
        int length = 0;
        string? auth = null;
        foreach (var l in lines.Skip(1))
        {
            int c = l.IndexOf(':');
            if (c <= 0) continue;
            var name = l[..c].Trim().ToLowerInvariant();
            var value = l[(c + 1)..].Trim();
            if (name == "content-length") length = int.Parse(value);
            if (name == "authorization") auth = value;
        }
        var buf = new byte[length];
        for (int read = 0; read < length;) read += stream.Read(buf, read, length - read);
        var (code, body) = Handle(method, path, auth, Encoding.UTF8.GetString(buf));
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        var header = $"HTTP/1.1 {code} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(header));
        stream.Write(bytes);
        stream.Flush();
    }

    private (int, JsonNode) Handle(string method, string path, string? auth, string text)
    {
        if (method == "GET" && path == "/health")
        {
            bool ready = DateTime.Now - _started >= _readyAfter;
            return (ready ? 200 : 503, new JsonObject { ["status"] = ready ? "ok" : "loading" });
        }
        if (auth != null && auth != "Bearer KEY") return (401, new JsonObject { ["error"] = "key" });
        var body = JsonNode.Parse(text)!;
        if (path == "/v1/chat/completions")
        {
            var msgs = body["messages"]!.AsArray();
            if (msgs[0]!["content"]!.GetValue<string>() == "FAIL") return (500, new JsonObject { ["error"] = "roles" });
            var think = body["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>();
            var answer = "echo:" + msgs[^1]!["content"]!.GetValue<string>() + "|think=" + think;
            return (200, new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = answer } }) });
        }
        return (200, new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["text"] = "txt:" + body["prompt"]!.GetValue<string>() }) });
    }

    public void Dispose() { _stopped = true; _listener.Stop(); }
}
