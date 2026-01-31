using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoomWriter.Models;
using DoomWriter.Services;

namespace DoomWriter.ViewModels;

/// <summary>
/// ViewModel for the left signal panel (TOC, Segments, Entities, Warnings, Graph, Search/Ask).
/// </summary>
public partial class SignalPanelViewModel : ObservableObject
{
    private readonly EntityGraphService _entityGraph;
    private readonly GraphBridge _graphBridge;
    private string? _currentDocumentId;
    private string? _currentDocumentTitle;
    private bool _graphReady;
    private DocumentSignals? _pendingGraphSignals;

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _activeHeadingIndex = -1;

    // Search/Ask
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private SearchMode _searchMode = SearchMode.Corpus;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _askResponse = "";
    [ObservableProperty] private bool _hasAskResponse;

    public ObservableCollection<HeadingItem> Headings { get; } = [];
    public ObservableCollection<AnalyzedSegment> Segments { get; } = [];
    public ObservableCollection<TrackedEntity> Entities { get; } = [];
    public ObservableCollection<Suggestion> Warnings { get; } = [];
    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];

    public SignalPanelViewModel(EntityGraphService entityGraph, GraphBridge graphBridge)
    {
        _entityGraph = entityGraph;
        _graphBridge = graphBridge;

        _graphBridge.NodeClicked += OnGraphNodeClicked;
        _graphBridge.NodeDoubleClicked += OnGraphNodeDoubleClicked;
        _graphBridge.NodeExpanded += OnGraphNodeExpanded;
        _graphBridge.GraphReady += (_, _) =>
        {
            _graphReady = true;
            if (_pendingGraphSignals != null)
            {
                _ = UpdateGraphFromSignalsAsync(_pendingGraphSignals);
                _pendingGraphSignals = null;
            }
        };
    }

    public string[] SearchModes { get; } = ["Corpus", "Web", "Ask"];

    /// <summary>
    /// Raised when user clicks a heading to navigate in the editor.
    /// </summary>
    public event EventHandler<HeadingItem>? HeadingClicked;

    /// <summary>
    /// Raised when user clicks a segment to navigate in the editor.
    /// </summary>
    public event EventHandler<AnalyzedSegment>? SegmentClicked;

    /// <summary>
    /// Raised when user clicks an entity to highlight all mentions.
    /// </summary>
    public event EventHandler<TrackedEntity>? EntityClicked;

    [RelayCommand]
    private void NavigateToHeading(HeadingItem heading)
    {
        HeadingClicked?.Invoke(this, heading);
    }

    [RelayCommand]
    private void NavigateToSegment(AnalyzedSegment segment)
    {
        SegmentClicked?.Invoke(this, segment);
    }

    [RelayCommand]
    private void SelectEntity(TrackedEntity entity)
    {
        EntityClicked?.Invoke(this, entity);
    }

    /// <summary>
    /// Update all signal data from a DocumentSignals analysis result.
    /// </summary>
    public void UpdateFromSignals(DocumentSignals signals)
    {
        Headings.Clear();
        foreach (var h in signals.Headings) Headings.Add(h);

        Segments.Clear();
        foreach (var s in signals.Segments.OrderByDescending(s => s.Salience)) Segments.Add(s);

        Entities.Clear();
        foreach (var e in signals.Entities.OrderByDescending(e => e.MentionCount)) Entities.Add(e);
    }

    /// <summary>
    /// Update the warnings/suggestions tab from SuggestionService results.
    /// </summary>
    public void UpdateSuggestions(List<Suggestion> suggestions)
    {
        Warnings.Clear();
        foreach (var s in suggestions) Warnings.Add(s);
    }

    /// <summary>
    /// Raised when user clicks a suggestion to insert or navigate.
    /// </summary>
    public event EventHandler<Suggestion>? SuggestionClicked;

    [RelayCommand]
    private void SelectSuggestion(Suggestion suggestion)
    {
        SuggestionClicked?.Invoke(this, suggestion);
    }

    /// <summary>
    /// Set which heading is currently active based on cursor position.
    /// </summary>
    public void SetActiveHeading(int cursorOffset)
    {
        for (int i = Headings.Count - 1; i >= 0; i--)
        {
            if (Headings[i].CharOffset <= cursorOffset)
            {
                ActiveHeadingIndex = i;
                return;
            }
        }
        ActiveHeadingIndex = -1;
    }

    /// <summary>
    /// Raised when user submits a search/ask query.
    /// </summary>
    public event EventHandler<(string query, SearchMode mode)>? SearchSubmitted;

    /// <summary>
    /// Raised when user clicks a search result to insert text into the editor.
    /// </summary>
    public event EventHandler<SearchResultItem>? SearchResultClicked;

    [RelayCommand]
    private void SubmitSearch()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        SearchSubmitted?.Invoke(this, (SearchQuery, SearchMode));
    }

    [RelayCommand]
    private void SelectSearchResult(SearchResultItem result)
    {
        SearchResultClicked?.Invoke(this, result);
    }

    public void SetSearchMode(string mode)
    {
        SearchMode = mode switch
        {
            "Corpus" => SearchMode.Corpus,
            "Web" => SearchMode.Web,
            "Ask" => SearchMode.Ask,
            _ => SearchMode.Corpus
        };
    }

    public void ShowSearchResults(List<SearchResultItem> results)
    {
        SearchResults.Clear();
        HasAskResponse = false;
        AskResponse = "";
        foreach (var r in results) SearchResults.Add(r);
        // Switch to Search tab (index 6 - after Graph tab)
        SelectedTabIndex = 6;
    }

    public void ShowAskResponse(string response)
    {
        SearchResults.Clear();
        AskResponse = response;
        HasAskResponse = true;
        SelectedTabIndex = 6;
    }

    // --- Graph ---

    public event EventHandler<string>? GraphDocumentOpened;

    public void SetCurrentDocument(string documentId, string title)
    {
        _currentDocumentId = documentId;
        _currentDocumentTitle = title;
        _pendingGraphSignals = null;
    }

    public async Task UpdateGraphFromSignalsAsync(DocumentSignals signals)
    {
        if (!_graphReady)
        {
            _pendingGraphSignals = signals;
            return;
        }

        if (_currentDocumentId == null || signals.Entities.Count == 0)
            return;

        try
        {
            var graphData = await _entityGraph.BuildDocumentGraphAsync(
                _currentDocumentId, _currentDocumentTitle ?? "Untitled", signals.Entities.ToList());
            await _graphBridge.SetGraphDataAsync(graphData);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Graph update failed: {ex.Message}");
        }
    }

    public void OnGraphWebViewReady()
    {
        // Theme is set via GraphReady event handler
    }

    private async void OnGraphNodeClicked(object? sender, string nodeId)
    {
        // Show info - could highlight in entities list
        System.Diagnostics.Debug.WriteLine($"Graph node clicked: {nodeId}");
    }

    private void OnGraphNodeDoubleClicked(object? sender, string nodeId)
    {
        // Open document in editor if it's a document node
        if (nodeId.StartsWith("corpus:") || nodeId.Contains('/') || nodeId.Contains('\\'))
        {
            GraphDocumentOpened?.Invoke(this, nodeId);
        }
    }

    private async void OnGraphNodeExpanded(object? sender, string nodeId)
    {
        try
        {
            GraphData expandData;
            if (nodeId.StartsWith("corpus:") || nodeId.Contains('/') || nodeId.Contains('\\'))
            {
                expandData = await _entityGraph.ExpandDocumentAsync(nodeId);
            }
            else
            {
                expandData = await _entityGraph.ExpandEntityAsync(nodeId);
            }

            if (expandData.Nodes.Count > 0)
                await _graphBridge.AddNodesAsync(expandData.Nodes);
            if (expandData.Edges.Count > 0)
                await _graphBridge.AddEdgesAsync(expandData.Edges);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Graph expand failed: {ex.Message}");
        }
    }
}

public enum SearchMode
{
    Corpus,
    Web,
    Ask
}

public record SearchResultItem
{
    public required string Title { get; init; }
    public required string Snippet { get; init; }
    public required float Score { get; init; }
    public string? Source { get; init; }
    public string? InsertText { get; init; }
}
