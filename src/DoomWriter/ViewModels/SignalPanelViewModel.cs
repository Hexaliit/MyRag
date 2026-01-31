using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoomWriter.Models;
using DoomWriter.Services;

namespace DoomWriter.ViewModels;

/// <summary>
/// ViewModel for the left signal panel (TOC, Segments, Entities, Warnings, Search/Ask).
/// </summary>
public partial class SignalPanelViewModel : ObservableObject
{
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
        // Switch to Search tab (index 4)
        SelectedTabIndex = 4;
    }

    public void ShowAskResponse(string response)
    {
        SearchResults.Clear();
        AskResponse = response;
        HasAskResponse = true;
        SelectedTabIndex = 4;
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
