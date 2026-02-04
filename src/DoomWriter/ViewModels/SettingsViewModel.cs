using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoomSummarizer.Services;
using DoomWriter.Services;

namespace DoomWriter.ViewModels;

/// <summary>
///     ViewModel for the Settings dialog.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly OllamaService _ollama;
    private readonly WriterSettingsService _settings;
    [ObservableProperty] private int _autoSaveInterval;
    private Action? _closeAction;
    [ObservableProperty] private string _connectionStatus = "";
    [ObservableProperty] private int _contextWindow;

    // Analysis
    [ObservableProperty] private int _debounceMs;

    // Editor
    [ObservableProperty] private string _editorMode = "ir";
    [ObservableProperty] private bool _enableDriftDetection;
    [ObservableProperty] private bool _enableNer;
    [ObservableProperty] private int _fontSize;
    [ObservableProperty] private string _mainModel = "";
    [ObservableProperty] private double _minSalienceThreshold;

    // LLM
    [ObservableProperty] private string _ollamaBaseUrl = "";
    [ObservableProperty] private string _sentinelModel = "";
    [ObservableProperty] private string _theme = "dark";

    public SettingsViewModel(WriterSettingsService settings, OllamaService ollama)
    {
        _settings = settings;
        _ollama = ollama;
        LoadFromConfig();
    }

    public List<string> EditorModes { get; } = ["ir", "wysiwyg", "sv"];
    public List<string> Themes { get; } = ["dark", "classic"];

    public void SetCloseAction(Action closeAction)
    {
        _closeAction = closeAction;
    }

    private void LoadFromConfig()
    {
        var c = _settings.Config;
        OllamaBaseUrl = c.OllamaBaseUrl;
        MainModel = c.MainModel;
        SentinelModel = c.SentinelModel;
        ContextWindow = c.ContextWindow;
        EditorMode = c.EditorMode;
        FontSize = c.FontSize;
        Theme = c.Theme;
        AutoSaveInterval = c.AutoSaveIntervalSeconds;
        DebounceMs = c.DebounceMs;
        EnableNer = c.EnableNer;
        EnableDriftDetection = c.EnableDriftDetection;
        MinSalienceThreshold = c.MinSalienceThreshold;
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        ConnectionStatus = "Testing...";
        try
        {
            var available = await _ollama.IsAvailableAsync();
            if (available)
            {
                var models = await _ollama.GetAvailableModelsAsync();
                ConnectionStatus = $"Connected. {models.Count} models available.";
            }
            else
            {
                ConnectionStatus = "Not connected. Check the URL.";
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Save()
    {
        var c = _settings.Config;
        c.OllamaBaseUrl = OllamaBaseUrl;
        c.MainModel = MainModel;
        c.SentinelModel = SentinelModel;
        c.ContextWindow = ContextWindow;
        c.EditorMode = EditorMode;
        c.FontSize = FontSize;
        c.Theme = Theme;
        c.AutoSaveIntervalSeconds = AutoSaveInterval;
        c.DebounceMs = DebounceMs;
        c.EnableNer = EnableNer;
        c.EnableDriftDetection = EnableDriftDetection;
        c.MinSalienceThreshold = (float)MinSalienceThreshold;
        _settings.Save();
        _closeAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        _closeAction?.Invoke();
    }
}