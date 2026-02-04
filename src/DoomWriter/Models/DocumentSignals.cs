using CommunityToolkit.Mvvm.ComponentModel;

namespace DoomWriter.Models;

/// <summary>
///     Live analysis state for the current document.
///     Updated by DocumentAnalysisService on every content change.
/// </summary>
public partial class DocumentSignals : ObservableObject
{
    [ObservableProperty] private float _coherenceScore;
    [ObservableProperty] private float[] _documentCentroid = [];
    [ObservableProperty] private string _dominantTopic = "";
    [ObservableProperty] private float _driftScore;
    [ObservableProperty] private List<TrackedEntity> _entities = [];
    [ObservableProperty] private int _entityCount;
    [ObservableProperty] private List<HeadingItem> _headings = [];
    [ObservableProperty] private int _segmentCount;
    [ObservableProperty] private List<AnalyzedSegment> _segments = [];
    [ObservableProperty] private List<TopicScore> _topics = [];
    [ObservableProperty] private int _wordCount;
}

/// <summary>
///     A heading extracted from the markdown document for TOC display.
/// </summary>
public record HeadingItem
{
    public required string Text { get; init; }
    public required int Level { get; init; }
    public required int LineNumber { get; init; }
    public required int CharOffset { get; init; }
}

/// <summary>
///     A text segment with analysis signals.
/// </summary>
public record AnalyzedSegment
{
    public required string Text { get; init; }
    public required string FirstLine { get; init; }
    public required float Salience { get; init; }
    public required int Position { get; init; }
    public required int CharOffset { get; init; }
    public float[]? Embedding { get; init; }
    public List<string> EntityNames { get; init; } = [];
    public string? Topic { get; init; }
    public string? ContentHash { get; init; }
    public SegmentKind Kind { get; init; } = SegmentKind.Prose;
}

/// <summary>
///     Classification of a segment for display and scoring purposes.
/// </summary>
public enum SegmentKind
{
    /// <summary>Regular prose paragraph.</summary>
    Prose,

    /// <summary>Fenced code block.</summary>
    Code,

    /// <summary>Mermaid or other diagram.</summary>
    Diagram,

    /// <summary>Section heading.</summary>
    Heading
}

/// <summary>
///     A tracked entity with mention and section information.
/// </summary>
public record TrackedEntity
{
    public required string Name { get; init; }
    public required string Type { get; init; } // ORG, PER, LOC, MISC
    public required int MentionCount { get; init; }
    public required List<int> SectionIndices { get; init; }
    public float IdfWeight { get; init; }
}

/// <summary>
///     Topic inference result for a section.
/// </summary>
public record TopicScore
{
    public required string Topic { get; init; }
    public required float Score { get; init; }
    public required int SectionIndex { get; init; }
}