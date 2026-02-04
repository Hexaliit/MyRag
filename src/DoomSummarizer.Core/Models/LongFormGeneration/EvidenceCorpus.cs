using DoomSummarizer.Services;

namespace DoomSummarizer.Models.LongFormGeneration;

/// <summary>
///     A segment from the evidence corpus with its source article metadata.
///     Wraps an ArticleSegment with article-level context for evidence assignment.
/// </summary>
public class EvidenceSegment
{
    public required ArticleSegment Segment { get; init; }
    public required string ArticleTitle { get; init; }
    public required string ArticleUrl { get; init; }
    public double ArticleRelevance { get; init; }
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>
    ///     Content type weight: 1.0 for tech docs, 0.2 for bio/about content.
    ///     Used to downweight personal/promotional content in technical articles.
    /// </summary>
    public float ContentTypeWeight { get; init; } = 1.0f;

    /// <summary>Whether this segment has been assigned to a section.</summary>
    public bool IsAssigned { get; set; }

    /// <summary>Section index this segment was assigned to, or -1.</summary>
    public int AssignedSection { get; set; } = -1;
}

/// <summary>
///     Summary-level metadata for a source article in the corpus.
/// </summary>
public class ArticleSummary
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Summary { get; init; }
    public double Relevance { get; init; }
    public int SegmentCount { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
}

/// <summary>
///     An entity extracted from the evidence corpus via regex/NER.
/// </summary>
public class TrackedEntity
{
    public required string Name { get; init; }
    public required string NormalizedName { get; init; }
    public int MentionCount { get; set; }
    public List<string> SourceArticles { get; init; } = [];
}

/// <summary>
///     The full evidence corpus built from processed articles.
///     Contains all segments with embeddings, global theme vector, URL whitelist,
///     and entity registry for downstream validation.
/// </summary>
public class EvidenceCorpus
{
    /// <summary>All segments with embeddings and source metadata.</summary>
    public List<EvidenceSegment> Segments { get; init; } = [];

    /// <summary>Mean of all segment embeddings — the document theme vector.</summary>
    public float[]? GlobalCentroid { get; init; }

    /// <summary>Per-article summaries keyed by URL.</summary>
    public Dictionary<string, ArticleSummary> Articles { get; init; } = new();

    /// <summary>All source URLs from fetched evidence.</summary>
    public List<string> AllSourceUrls { get; init; } = [];

    /// <summary>Entities extracted from evidence (capitalized multi-word phrases).</summary>
    public List<TrackedEntity> GlobalEntities { get; init; } = [];

    /// <summary>Validation whitelist: every URL we actually fetched, with timestamp.</summary>
    public Dictionary<string, DateTimeOffset> UrlFetchDates { get; init; } = new();

    /// <summary>All entity names from evidence (lowercased for lookup).</summary>
    public HashSet<string> KnownEntityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All article titles (for title-reference validation).</summary>
    public HashSet<string> KnownTitles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}