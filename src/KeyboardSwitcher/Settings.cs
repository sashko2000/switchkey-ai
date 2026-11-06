using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace KeyboardSwitcher;

/// <summary>Settings. Stored in %APPDATA%\KeyboardSwitcher\settings.json</summary>
public sealed class Settings
{
    public bool AutoSwitch { get; set; } = true;

    /// <summary>
    /// Preferred layout ("en", "ru", "uk", "hi", empty - none): if a word looks the same in several layouts
    /// ("мама" in Russian and Ukrainian), switch to this one. Not set (null) - the interface language, if it is one of the layouts.
    /// </summary>
    public string? LayoutPriority { get; set; }

    /// <summary>Preferred layout, taking the default into account.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveLayoutPriority => LayoutPriority ?? (UiLanguage is "en" or "ru" or "uk" or "hi" ? UiLanguage : "");

    /// <summary>Hotkeys: e.g. "Ctrl+Alt+Z", "Ctrl+Shift+Pause", "Pause". Empty string - disabled.</summary>
    public string HotkeyConvertLastWord { get; set; } = "Ctrl+Alt+Z";
    public string HotkeyConvertSelection { get; set; } = "Ctrl+Alt+X";
    public string HotkeyAiFix { get; set; } = "Ctrl+Alt+A";
    public string HotkeyTranslate { get; set; } = "Ctrl+Alt+T";
    public string HotkeyChat { get; set; } = "Ctrl+Alt+C";

    /// <summary>Translation: true - show in a separate window, false - replace the selected text.</summary>
    public bool TranslateToWindow { get; set; } = true;

    /// <summary>Last task in the assistant window (e.g. "TranslatePair", "to:sk", "Fix").</summary>
    public string AssistantLastTask { get; set; } = "TranslatePair";

    /// <summary>Interface language: ru, en, uk, sk, es, hi.</summary>
    public string UiLanguage { get; set; } = Languages.DefaultUi();

    /// <summary>Language pair for quick translation (A to B; B and other languages to A).</summary>
    public string TranslateLangA { get; set; } = "ru";
    public string TranslateLangB { get; set; } = "en";

    /// <summary>Unload the model from memory after this many idle minutes (0 - never unload).</summary>
    public int AiUnloadAfterMinutes { get; set; } = 10;

    /// <summary>Load the model only on first use (not at app startup).</summary>
    public bool AiLoadOnDemand { get; set; } = true;

    /// <summary>Enable text correction by the model (turned on in the settings window, which also installs everything needed).</summary>
    public bool AiEnabled { get; set; } = false;

    /// <summary>Model from the catalog (see AiSetup.Catalog).</summary>
    public string AiModel { get; set; } = "qwen3-8b";

    /// <summary>Advanced: a custom OpenVINO model folder. Empty - model from the catalog.</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>
    /// Where to run the model: "auto" - the first available of AiDevices (NPU, then Intel GPU, then CPU),
    /// or "NPU" / "GPU" / "CPU" - that device first, the others only if it fails.
    /// </summary>
    public string AiDevice { get; set; } = "auto";

    /// <summary>
    /// Use an NVIDIA graphics card (llama.cpp with CUDA) first: the whole model if it fits into the free memory,
    /// otherwise as many layers as fit (the rest on the CPU); if not even a quarter fits - the usual order. Off by default.
    /// </summary>
    public bool AiNvidia { get; set; } = false;

    /// <summary>Devices in priority order: NPU, GPU (GPU.0 - first graphics adapter), CPU.</summary>
    public List<string> AiDevices { get; set; } = new() { "NPU", "GPU.0", "CPU" };

    /// <summary>Maximum length of selected text for model correction (characters).</summary>
    public int AiMaxChars { get; set; } = 1500;

    /// <summary>Apps in which auto-switching is disabled (exe file name).</summary>
    public List<string> ExcludedApps { get; set; } = new()
    {
        "mstsc.exe", "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe", "vmconnect.exe"
    };

    /// <summary>Words that are never switched (grows when you undo a correction).</summary>
    public List<string> NeverSwitch { get; set; } = new()
    {
        "https", "http", "www", "localhost"
    };

    // ---------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyboardSwitcher");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOpts) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write("Error reading settings: " + ex.Message);
        }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Write("Error writing settings: " + ex.Message);
        }
    }
}

/// <summary>Activity log. Keystrokes and text are NOT recorded.</summary>
public static class Log
{
    private static readonly object Sync = new();
    public static string FilePath => Path.Combine(AiSetup.BaseDir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
