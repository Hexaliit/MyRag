namespace DoomWriter.Models;

/// <summary>
/// A suggestion from the intelligence pipeline (link, mention, warning).
/// </summary>
public record Suggestion
{
    public required SuggestionType Type { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? TargetUrl { get; init; }
    public string? TargetTitle { get; init; }
    public string? InsertText { get; init; }
    public float Confidence { get; init; }
}

public enum SuggestionType
{
    SuggestedLink,
    SimilarContent,
    DriftWarning,
    CoherenceWarning,
    EntityMention
}
