using System.Text.Json;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoomSummarizer.Services;
using DoomWriter.Models;
using DoomWriter.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DoomWriter.ViewModels;

/// <summary>
/// Root ViewModel for the main window shell.
/// Orchestrates editor, signal panel, file operations, AI services, and settings.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly WriterSettingsService _settings;
    private readonly DocumentAnalysisService _analysisService;
    private readonly WritingAssistantService _writingAssistant;
    private readonly AutocompleteService _autocomplete;
    private readonly SuggestionService _suggestions;
    private readonly CorpusService _corpus;
    private readonly SpellCheckService _spellCheck;
    private readonly EditorBridge _bridge;
    private readonly OllamaService _ollama;
    private System.Timers.Timer? _autoSaveTimer;
    private DocumentSignals? _lastSignals;
    private CancellationTokenSource? _generationCts;

    public EditorViewModel Editor { get; }
    public SignalPanelViewModel SignalPanel { get; }

    // Status bar
    [ObservableProperty] private int _wordCount;
    [ObservableProperty] private int _segmentCount;
    [ObservableProperty] private int _entityCount;
    [ObservableProperty] private string _dominantTopic = "";
    [ObservableProperty] private float _driftScore;
    [ObservableProperty] private bool _isOllamaConnected;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _generatingStatus = "";
    [ObservableProperty] private string _title = "DoomWriter";
    [ObservableProperty] private int _corpusDocumentCount;

    // Panel visibility
    [ObservableProperty] private bool _isSignalPanelVisible = true;
    [ObservableProperty] private double _signalPanelWidth = 280;

    // Settings
    public WriterConfig Config => _settings.Config;

    public MainWindowViewModel(
        EditorViewModel editor,
        SignalPanelViewModel signalPanel,
        DocumentAnalysisService analysisService,
        WriterSettingsService settings,
        WritingAssistantService writingAssistant,
        AutocompleteService autocomplete,
        SuggestionService suggestions,
        CorpusService corpus,
        SpellCheckService spellCheck,
        EditorBridge bridge,
        OllamaService ollama)
    {
        Editor = editor;
        SignalPanel = signalPanel;
        _analysisService = analysisService;
        _settings = settings;
        _writingAssistant = writingAssistant;
        _autocomplete = autocomplete;
        _suggestions = suggestions;
        _corpus = corpus;
        _spellCheck = spellCheck;
        _bridge = bridge;
        _ollama = ollama;

        IsSignalPanelVisible = Config.SignalPanelVisible;
        SignalPanelWidth = Config.SignalPanelWidth;

        // Wire content changes to analysis pipeline
        Editor.ContentChanged += OnContentChanged;

        // Wire toolbar actions to AI services
        Editor.ToolbarAction += OnToolbarAction;

        // Wire analysis results to signal panel + suggestions
        _analysisService.AnalysisCompleted += OnAnalysisCompleted;

        // Wire autocomplete
        _bridge.AutocompleteRequested += OnAutocompleteRequested;
        _autocomplete.SuggestionReady += OnAutocompleteSuggestionReady;

        // Wire spell check
        _bridge.SpellCheckRequested += OnSpellCheckRequested;

        // When editor becomes ready (Vditor loaded), push any pending content
        Editor.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.IsEditorReady) && Editor.IsEditorReady)
            {
                if (!string.IsNullOrEmpty(Editor.Content))
                    await _bridge.SetContentAsync(Editor.Content);
            }
        };

        // Wire writing assistant status events
        _writingAssistant.GenerationStarted += (_, msg) =>
        {
            IsGenerating = true;
            GeneratingStatus = msg;
            _ = _bridge.ShowAiGeneratingAsync(msg);
        };
        _writingAssistant.GenerationCompleted += (_, _) =>
        {
            IsGenerating = false;
            GeneratingStatus = "";
            _ = _bridge.HideAiGeneratingAsync();
        };
        _writingAssistant.GenerationFailed += (_, msg) =>
        {
            IsGenerating = false;
            GeneratingStatus = "";
            _ = _bridge.HideAiGeneratingAsync();
        };

        // Set up auto-save
        SetupAutoSave();

        // Check Ollama connectivity in background
        _ = CheckOllamaAsync();

        // Initialize spell check in background
        if (Config.EnableSpellCheck)
            _ = InitSpellCheckAsync();
    }

    private async Task InitSpellCheckAsync()
    {
        try
        {
            await _spellCheck.InitializeAsync(Config.SpellCheckLanguage);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SpellCheck init failed: {ex.Message}");
        }
    }

    private async Task CheckOllamaAsync()
    {
        try
        {
            IsOllamaConnected = await _ollama.IsAvailableAsync();
        }
        catch
        {
            IsOllamaConnected = false;
        }
    }

    private void OnContentChanged(object? sender, string content)
    {
        UpdateTitle();
        _ = _analysisService.AnalyzeAsync(content);
    }

    private async void OnToolbarAction(object? sender, string action)
    {
        // Cancel any in-flight generation
        _generationCts?.Cancel();
        _generationCts = new CancellationTokenSource();
        var ct = _generationCts.Token;

        var signals = _lastSignals ?? new DocumentSignals();

        try
        {
            switch (action)
            {
                case "nextParagraph":
                    var nextPara = await _writingAssistant.GenerateNextParagraphAsync(
                        signals, Editor.Content, Editor.CursorPosition, ct);
                    if (!string.IsNullOrEmpty(nextPara))
                        await _bridge.InsertAtCursorAsync("\n\n" + nextPara);
                    break;

                case "expand":
                    if (!string.IsNullOrEmpty(Editor.SelectedText))
                    {
                        var expanded = await _writingAssistant.ExpandTextAsync(
                            Editor.SelectedText, signals, Editor.Content, ct);
                        if (!string.IsNullOrEmpty(expanded))
                            await _bridge.InsertAtCursorAsync(expanded);
                    }
                    break;

                case "rewrite":
                    if (!string.IsNullOrEmpty(Editor.SelectedText))
                    {
                        var rewritten = await _writingAssistant.RewriteTextAsync(
                            Editor.SelectedText, signals, Editor.Content, ct);
                        if (!string.IsNullOrEmpty(rewritten))
                            await _bridge.InsertAtCursorAsync(rewritten);
                    }
                    break;

                case "simplify":
                    if (!string.IsNullOrEmpty(Editor.SelectedText))
                    {
                        var simplified = await _writingAssistant.SimplifyTextAsync(
                            Editor.SelectedText, ct);
                        if (!string.IsNullOrEmpty(simplified))
                            await _bridge.InsertAtCursorAsync(simplified);
                    }
                    break;

                case "check":
                    if (!string.IsNullOrEmpty(Editor.SelectedText))
                    {
                        var grammar = await _writingAssistant.CheckGrammarAsync(Editor.SelectedText, ct);
                        if (grammar.HasIssues && !string.IsNullOrEmpty(grammar.CorrectedText))
                            await _bridge.InsertAtCursorAsync(grammar.CorrectedText);
                    }
                    break;

                case "suggestLinks":
                    await GenerateSuggestionsAsync(signals, ct);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Toolbar action '{action}' failed: {ex.Message}");
            IsGenerating = false;
            _ = _bridge.HideAiGeneratingAsync();
        }
    }

    private async void OnAutocompleteRequested(object? sender, string textBeforeCursor)
    {
        var signals = _lastSignals ?? new DocumentSignals();
        await _autocomplete.RequestCompletionAsync(textBeforeCursor, signals);
    }

    private async void OnAutocompleteSuggestionReady(object? sender, AutocompleteResult result)
    {
        await _bridge.ShowAutocompleteSuggestionAsync(result);
    }

    private void OnAnalysisCompleted(object? sender, DocumentSignals signals)
    {
        _lastSignals = signals;

        WordCount = signals.WordCount;
        SegmentCount = signals.SegmentCount;
        EntityCount = signals.EntityCount;
        DominantTopic = signals.DominantTopic;
        DriftScore = signals.DriftScore;

        SignalPanel.UpdateFromSignals(signals);
        SignalPanel.SetActiveHeading(Editor.CursorPosition);
        IsAnalyzing = false;

        // Feed entity names to spell checker so they aren't flagged
        if (_spellCheck.IsLoaded && signals.Entities.Count > 0)
            _spellCheck.AddEntityNames(signals.Entities.Select(e => e.Name));

        // Generate suggestions in background after analysis
        _ = GenerateSuggestionsAsync(signals, CancellationToken.None);
    }

    private async void OnSpellCheckRequested(object? sender, string content)
    {
        if (!_spellCheck.IsLoaded) return;

        try
        {
            var diagnostics = _spellCheck.CheckDocument(content);
            if (diagnostics.Count > 0)
                await _bridge.ShowDiagnosticsAsync(diagnostics);
            else
                await _bridge.ClearDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SpellCheck failed: {ex.Message}");
        }
    }

    private async Task GenerateSuggestionsAsync(DocumentSignals signals, CancellationToken ct)
    {
        try
        {
            var suggestionsResult = await _suggestions.GenerateSuggestionsAsync(
                signals, Editor.Content, ct);
            SignalPanel.UpdateSuggestions(suggestionsResult);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Suggestion generation failed: {ex.Message}");
        }
    }

    private void UpdateTitle()
    {
        var dirty = Editor.IsDirty ? " *" : "";
        Title = $"{Editor.FileName}{dirty} — DoomWriter";
    }

    // --- File operations ---

    [RelayCommand]
    private async Task NewDocument()
    {
        Editor.NewDocument();
        await _bridge.SetContentAsync("");
        UpdateTitle();
    }

    public async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path)) return;

        var content = await File.ReadAllTextAsync(path);
        Editor.SetFile(path, content);
        await _bridge.SetContentAsync(content);
        Config.AddRecentFile(path);
        _settings.Save();
        UpdateTitle();
    }

    [RelayCommand]
    private async Task OpenFileDialog(IStorageProvider? storage)
    {
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Markdown File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown", "*.mdx"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path != null)
                await OpenFileAsync(path);
        }
    }

    [RelayCommand]
    private async Task SaveFile()
    {
        if (string.IsNullOrEmpty(Editor.FilePath))
            return; // Need SaveAs instead

        await File.WriteAllTextAsync(Editor.FilePath, Editor.Content);
        Editor.MarkClean();
        Config.AddRecentFile(Editor.FilePath);
        _settings.Save();
        UpdateTitle();
    }

    [RelayCommand]
    private async Task SaveFileAs(IStorageProvider? storage)
    {
        if (storage == null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Markdown File",
            DefaultExtension = "md",
            SuggestedFileName = Editor.FileName == "Untitled" ? "document.md" : Editor.FileName,
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt"] }
            ]
        });

        if (file != null)
        {
            var path = file.TryGetLocalPath();
            if (path != null)
            {
                await File.WriteAllTextAsync(path, Editor.Content);
                Editor.SetFile(path, Editor.Content);
                Config.AddRecentFile(path);
                _settings.Save();
                UpdateTitle();
            }
        }
    }

    // --- Panel toggle ---

    [RelayCommand]
    private void ToggleSignalPanel()
    {
        IsSignalPanelVisible = !IsSignalPanelVisible;
        Config.SignalPanelVisible = IsSignalPanelVisible;
        _settings.Save();
    }

    // --- Dialogs ---

    [RelayCommand]
    private void OpenSettingsDialog()
    {
        var owner = GetMainWindow();
        if (owner == null) return;

        var vm = App.Services.GetRequiredService<SettingsViewModel>();
        var dialog = new Views.SettingsDialog { DataContext = vm };
        vm.SetCloseAction(() => dialog.Close());
        dialog.ShowDialog(owner);
    }

    [RelayCommand]
    private void OpenCorpusDialog()
    {
        var owner = GetMainWindow();
        if (owner == null) return;

        var vm = App.Services.GetRequiredService<CorpusViewModel>();
        var dialog = new Views.CorpusDialog { DataContext = vm };
        vm.SetWindow(dialog);
        dialog.ShowDialog(owner);
    }

    private static Avalonia.Controls.Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    // --- Auto-save ---

    private void SetupAutoSave()
    {
        _autoSaveTimer = new System.Timers.Timer(Config.AutoSaveIntervalSeconds * 1000);
        _autoSaveTimer.Elapsed += async (_, _) =>
        {
            if (Editor.IsDirty && !string.IsNullOrEmpty(Editor.FilePath))
            {
                try
                {
                    await File.WriteAllTextAsync(Editor.FilePath, Editor.Content);
                }
                catch
                {
                    // Silently fail auto-save
                }
            }
        };
        _autoSaveTimer.Start();
    }
}
