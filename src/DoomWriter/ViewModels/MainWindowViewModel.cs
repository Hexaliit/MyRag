using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoomSummarizer.Services;
using DoomWriter.Models;
using DoomWriter.Services;
using DoomWriter.Views;
using Microsoft.Extensions.DependencyInjection;
using Timer = System.Timers.Timer;

namespace DoomWriter.ViewModels;

/// <summary>
///     Root ViewModel for the main window shell.
///     Orchestrates editor, signal panel, file operations, AI services, and settings.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly DocumentAnalysisService _analysisService;
    private readonly AutocompleteService _autocomplete;
    private readonly EditorBridge _bridge;
    private readonly CorpusService _corpus;
    private readonly EntityGraphService _entityGraphService;
    private readonly OllamaService _ollama;
    private readonly WriterSettingsService _settings;
    private readonly SpellCheckService _spellCheck;
    private readonly SuggestionService _suggestions;
    private readonly WritingAssistantService _writingAssistant;
    private Timer? _autoSaveTimer;
    [ObservableProperty] private int _corpusDocumentCount;
    [ObservableProperty] private string _dominantTopic = "";
    [ObservableProperty] private float _driftScore;
    [ObservableProperty] private int _entityCount;
    [ObservableProperty] private string _generatingStatus = "";
    private CancellationTokenSource? _generationCts;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private bool _isOllamaConnected;

    // Panel visibility
    [ObservableProperty] private bool _isSignalPanelVisible = true;
    private DocumentSignals? _lastSignals;
    [ObservableProperty] private int _segmentCount;
    [ObservableProperty] private double _signalPanelWidth = 280;
    [ObservableProperty] private string _title = "DoomWriter";

    // Status bar
    [ObservableProperty] private int _wordCount;

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
        OllamaService ollama,
        EntityGraphService entityGraphService)
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
        _entityGraphService = entityGraphService;

        IsSignalPanelVisible = Config.SignalPanelVisible;
        SignalPanelWidth = Config.SignalPanelWidth;

        // Wire content changes to analysis pipeline
        Editor.ContentChanged += OnContentChanged;

        // Wire toolbar actions to AI services
        Editor.ToolbarAction += OnToolbarAction;

        // Wire analysis results to signal panel + suggestions
        _analysisService.AnalysisCompleted += OnAnalysisCompleted;

        // Wire signal panel navigation (clicks → scroll editor)
        SignalPanel.HeadingClicked += async (_, h) => await _bridge.ScrollToHeadingAsync(h.Text);
        SignalPanel.SegmentClicked += async (_, s) => await _bridge.ScrollToTextAsync(s.FirstLine);
        SignalPanel.EntityClicked += async (_, e) => await _bridge.ScrollToTextAsync(e.Name);
        SignalPanel.SuggestionClicked += async (_, s) =>
        {
            if (!string.IsNullOrEmpty(s.InsertText))
                await _bridge.InsertAtCursorAsync(s.InsertText);
            else if (!string.IsNullOrEmpty(s.Title))
                await _bridge.ScrollToTextAsync(s.Title);
        };

        // Wire search/ask
        SignalPanel.SearchSubmitted += OnSearchSubmitted;
        SignalPanel.SearchResultClicked += OnSearchResultClicked;

        // Wire graph document navigation
        SignalPanel.GraphDocumentOpened += OnGraphDocumentOpened;

        // Wire autocomplete
        _bridge.AutocompleteRequested += OnAutocompleteRequested;
        _autocomplete.SuggestionReady += OnAutocompleteSuggestionReady;

        // Wire spell check
        _bridge.SpellCheckRequested += OnSpellCheckRequested;

        // When editor becomes ready (Vditor loaded), push any pending content and analyze
        Editor.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.IsEditorReady) && Editor.IsEditorReady)
                if (!string.IsNullOrEmpty(Editor.Content))
                {
                    await _bridge.SetContentAsync(Editor.Content);
                    // Analyze immediately so signal panel populates on load
                    IsAnalyzing = true;
                    await _analysisService.AnalyzeImmediateAsync(Editor.Content);
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

    public EditorViewModel Editor { get; }
    public SignalPanelViewModel SignalPanel { get; }

    // Settings
    public WriterConfig Config => _settings.Config;

    private async Task InitSpellCheckAsync()
    {
        try
        {
            await _spellCheck.InitializeAsync(Config.SpellCheckLanguage);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SpellCheck init failed: {ex.Message}");
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Toolbar action '{action}' failed: {ex.Message}");
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

        // Persist entities and update graph
        if (signals.Entities.Count > 0 && !string.IsNullOrEmpty(Editor.FilePath))
        {
            var docId = $"corpus:{Path.GetFileNameWithoutExtension(Editor.FilePath)}";
            SignalPanel.SetCurrentDocument(docId, Editor.FileName);
            _ = PersistAndUpdateGraphAsync(docId, signals);
        }
    }

    private async Task PersistAndUpdateGraphAsync(string docId, DocumentSignals signals)
    {
        try
        {
            await _entityGraphService.PersistDocumentEntitiesAsync(
                docId, Editor.FileName, signals.Entities.ToList());
            await SignalPanel.UpdateGraphFromSignalsAsync(signals);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Entity graph persist failed: {ex.Message}");
        }
    }

    private async void OnGraphDocumentOpened(object? sender, string documentId)
    {
        // documentId is like "corpus:filename" — resolve to file path
        if (documentId.StartsWith("corpus:"))
        {
            var slug = documentId["corpus:".Length..];
            // Search corpus directories for matching file
            foreach (var dir in Config.CorpusDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                var files = Directory.GetFiles(dir, $"{slug}.md", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    await OpenFileAsync(files[0]);
                    return;
                }

                // Try other extensions
                foreach (var ext in new[] { ".markdown", ".mdx", ".txt" })
                {
                    files = Directory.GetFiles(dir, $"{slug}{ext}", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        await OpenFileAsync(files[0]);
                        return;
                    }
                }
            }
        }
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
            Debug.WriteLine($"SpellCheck failed: {ex.Message}");
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
            Debug.WriteLine($"Suggestion generation failed: {ex.Message}");
        }
    }

    private void UpdateTitle()
    {
        var dirty = Editor.IsDirty ? " *" : "";
        Title = $"{Editor.FileName}{dirty} — DoomWriter";
    }

    /// <summary>
    ///     Public version of UpdateTitle for code-behind (SaveAs flow).
    /// </summary>
    public void UpdateTitlePublic()
    {
        UpdateTitle();
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

        // Immediately analyze the document to populate the signal panel
        IsAnalyzing = true;
        await _analysisService.AnalyzeImmediateAsync(content);
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
        var dialog = new SettingsDialog { DataContext = vm };
        vm.SetCloseAction(() => dialog.Close());
        dialog.ShowDialog(owner);
    }

    [RelayCommand]
    private void OpenCorpusDialog()
    {
        var owner = GetMainWindow();
        if (owner == null) return;

        var vm = App.Services.GetRequiredService<CorpusViewModel>();
        var dialog = new CorpusDialog { DataContext = vm };
        vm.SetWindow(dialog);
        dialog.ShowDialog(owner);
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    // --- Search / Ask ---

    private async void OnSearchSubmitted(object? sender, (string query, SearchMode mode) e)
    {
        var (query, mode) = e;
        SignalPanel.IsSearching = true;

        try
        {
            switch (mode)
            {
                case SearchMode.Corpus:
                    await SearchCorpusAsync(query);
                    break;
                case SearchMode.Web:
                    // Web search placeholder - show corpus results with note
                    await SearchCorpusAsync(query);
                    break;
                case SearchMode.Ask:
                    await AskAsync(query);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Search/Ask failed: {ex.Message}");
            SignalPanel.ShowAskResponse($"Error: {ex.Message}");
        }
        finally
        {
            SignalPanel.IsSearching = false;
        }
    }

    private async Task SearchCorpusAsync(string query)
    {
        if (!_corpus.IsInitialized) return;

        // Hybrid search: fast Lucene keyword matches + slower embedding similarity
        var keywordMatches = _corpus.KeywordSearch(query, 5);
        var embeddingMatches = await _corpus.SearchAsync(query, 10);

        // Merge results, preferring keyword matches for exact hits
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<SearchResultItem>();

        foreach (var m in keywordMatches)
            if (seen.Add(m.Id))
                merged.Add(new SearchResultItem
                {
                    Title = m.Title,
                    Snippet = m.Text.Length > 0 ? m.Text : m.Source,
                    Score = m.Score,
                    Source = m.Source,
                    InsertText = null
                });

        foreach (var m in embeddingMatches)
            if (seen.Add(m.Id))
                merged.Add(new SearchResultItem
                {
                    Title = m.Title,
                    Snippet = m.Text.Length > 0 ? m.Text : m.Source,
                    Score = m.Score,
                    Source = m.Source,
                    InsertText = null
                });

        SignalPanel.ShowSearchResults(merged);
    }

    private async Task AskAsync(string query)
    {
        if (!IsOllamaConnected)
        {
            SignalPanel.ShowAskResponse("Ollama is not connected. Check settings.");
            return;
        }

        var context = BuildAskContext(query);
        var systemPrompt =
            "You are a helpful writing assistant. Answer the user's question concisely based on " +
            "their document context. Keep responses brief and actionable.";

        var response = await _ollama.GenerateAsync(context, systemPrompt, 0.4);
        SignalPanel.ShowAskResponse(response.Trim());
    }

    private string BuildAskContext(string query)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Question: {query}");
        sb.AppendLine();

        // Include document summary context
        if (_lastSignals != null)
        {
            if (!string.IsNullOrEmpty(_lastSignals.DominantTopic))
                sb.AppendLine($"Document topic: {_lastSignals.DominantTopic}");
            if (_lastSignals.Entities.Count > 0)
                sb.AppendLine($"Key entities: {string.Join(", ", _lastSignals.Entities.Take(5).Select(e => e.Name))}");
        }

        // Include relevant content around cursor (max 600 chars)
        var content = Editor.Content;
        if (!string.IsNullOrEmpty(content))
        {
            var snippet = content.Length > 600
                ? content[..600] + "..."
                : content;
            sb.AppendLine();
            sb.AppendLine("Document excerpt:");
            sb.AppendLine(snippet);
        }

        return sb.ToString();
    }

    private async void OnSearchResultClicked(object? sender, SearchResultItem result)
    {
        if (!string.IsNullOrEmpty(result.InsertText))
            await _bridge.InsertAtCursorAsync(result.InsertText);
        else if (!string.IsNullOrEmpty(result.Source))
            await _bridge.ScrollToTextAsync(result.Title);
    }

    // --- Auto-save ---

    private void SetupAutoSave()
    {
        _autoSaveTimer = new Timer(Config.AutoSaveIntervalSeconds * 1000);
        _autoSaveTimer.Elapsed += async (_, _) =>
        {
            if (Editor.IsDirty && !string.IsNullOrEmpty(Editor.FilePath))
                try
                {
                    await File.WriteAllTextAsync(Editor.FilePath, Editor.Content);
                }
                catch
                {
                    // Silently fail auto-save
                }
        };
        _autoSaveTimer.Start();
    }
}