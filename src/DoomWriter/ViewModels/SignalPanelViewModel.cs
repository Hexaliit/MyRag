using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoomWriter.Models;

namespace DoomWriter.ViewModels;

/// <summary>
/// ViewModel for the left signal panel (TOC, Segments, Entities, Warnings).
/// </summary>
public partial class SignalPanelViewModel : ObservableObject
{
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _activeHeadingIndex = -1;

    public ObservableCollection<HeadingItem> Headings { get; } = [];
    public ObservableCollection<AnalyzedSegment> Segments { get; } = [];
    public ObservableCollection<TrackedEntity> Entities { get; } = [];
    public ObservableCollection<Suggestion> Warnings { get; } = [];

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
}
