using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace KeyboardSwitcher;

/// <summary>One message of a chat: role "user" or "assistant".</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>A saved conversation with the model.</summary>
public sealed class ChatSession
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
    public string Title { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = new();
}

/// <summary>Chats are stored as JSON files in %LOCALAPPDATA%\KeyboardSwitcher\chats (only on this computer).</summary>
internal static class ChatStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static string Dir => Path.Combine(AiSetup.BaseDir, "chats");

    /// <summary>Saved chats, newest first.</summary>
    public static List<ChatSession> List()
    {
        var list = new List<ChatSession>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var c = JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(f), Opts);
                    if (c != null && c.Messages.Count > 0) { c.Id = Path.GetFileNameWithoutExtension(f); list.Add(c); }
                }
                catch (Exception ex) { Log.Write("Chat file " + Path.GetFileName(f) + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { Log.Write("Chat list: " + ex.Message); }
        return list.OrderByDescending(c => c.Updated).ToList();
    }

    public static void Save(ChatSession c)
    {
        if (c.Messages.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Dir);
            c.Updated = DateTime.Now;
            if (c.Title.Length == 0) c.Title = MakeTitle(c.Messages[0].Content);
            File.WriteAllText(Path.Combine(Dir, c.Id + ".json"), JsonSerializer.Serialize(c, Opts));
        }
        catch (Exception ex) { Log.Write("Chat save: " + ex.Message); }
    }

    public static void Delete(ChatSession c)
    {
        try { File.Delete(Path.Combine(Dir, c.Id + ".json")); }
        catch (Exception ex) { Log.Write("Chat delete: " + ex.Message); }
    }

    /// <summary>The first line of the first message, up to 60 characters.</summary>
    public static string MakeTitle(string text)
    {
        var line = text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 60 ? line[..57] + "..." : line;
    }
}
