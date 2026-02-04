using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoomWriter.Models;

/// <summary>
///     Persisted application settings for DoomWriter.
///     Stored at %APPDATA%/DoomWriter/settings.json
/// </summary>
public class WriterConfig
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DoomWriter");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    // Ollama
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string MainModel { get; set; } = "llama3.2";
    public string SentinelModel { get; set; } = "llama3.2:1b";
    public int ContextWindow { get; set; } = 8192;

    // Editor
    public string EditorMode { get; set; } = "ir"; // ir (instant rendering), wysiwyg, sv (split view)
    public int FontSize { get; set; } = 16;
    public string Theme { get; set; } = "dark";
    public string CodeTheme { get; set; } = "github-dark";

    // Analysis
    public int DebounceMs { get; set; } = 500;
    public bool EnableNer { get; set; } = true;
    public float MinSalienceThreshold { get; set; } = 0.1f;
    public bool EnableDriftDetection { get; set; } = true;

    // Corpus
    public List<string> CorpusDirectories { get; set; } = [];
    public bool AutoIndexOnChange { get; set; } = true;

    // Web crawl sources
    public List<CrawlSource> CrawlSources { get; set; } = [];

    // Spell check
    public string SpellCheckLanguage { get; set; } = "en_US";
    public bool EnableSpellCheck { get; set; } = true;

    // Auto-save
    public int AutoSaveIntervalSeconds { get; set; } = 30;

    // Recent files
    public List<string> RecentFiles { get; set; } = [];
    public int MaxRecentFiles { get; set; } = 20;

    // Window state
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool SignalPanelVisible { get; set; } = true;
    public double SignalPanelWidth { get; set; } = 280;

    public static WriterConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new WriterConfig();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<WriterConfig>(json, JsonOptions) ?? new WriterConfig();
        }
        catch
        {
            return new WriterConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    public void AddRecentFile(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }
}