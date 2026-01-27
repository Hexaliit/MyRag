namespace DoomSummarizer.Models.LongFormGeneration;

/// <summary>
/// Result of output validation (Phase 5).
/// Every URL, entity, and factual claim is checked against the evidence corpus.
/// </summary>
public class ValidationResult
{
    public List<UrlIssue> HallucinatedUrls { get; init; } = [];
    public List<EntityIssue> UngroundedEntities { get; init; } = [];
    public List<TitleIssue> FabricatedTitles { get; init; } = [];
    public List<FactIssue> UngroundedFacts { get; init; } = [];

    /// <summary>0-1, percentage of grounded sentences.</summary>
    public double OverallGroundingScore { get; init; }

    /// <summary>True if grounding > 0.7 and no critical issues.</summary>
    public bool PassesValidation { get; init; }

    /// <summary>Number of URLs validated as present in evidence.</summary>
    public int VerifiedUrlCount { get; init; }

    /// <summary>Number of entities confirmed in evidence.</summary>
    public int GroundedEntityCount { get; init; }
}

public class UrlIssue
{
    public required string Url { get; init; }
    public required string Context { get; init; }
    public UrlIssueType Type { get; init; }
}

public enum UrlIssueType
{
    Hallucinated,
    GoogleRedirect,
    MalformedUrl
}

public class EntityIssue
{
    public required string EntityName { get; init; }
    public required string Context { get; init; }
    public double ClosestMatchScore { get; init; }
    public string? ClosestMatch { get; init; }
}

public class TitleIssue
{
    public required string ClaimedTitle { get; init; }
    public required string Context { get; init; }
    public string? ClosestRealTitle { get; init; }
    public double MatchScore { get; init; }
}

public class FactIssue
{
    public required string Sentence { get; init; }
    public int SectionIndex { get; init; }
    public double BestGroundingScore { get; init; }
    public FactGroundingLevel Level { get; init; }
}

public enum FactGroundingLevel
{
    Grounded,       // > 0.6 cosine similarity to evidence
    WeaklyGrounded, // 0.4-0.6
    Ungrounded      // < 0.4
}
