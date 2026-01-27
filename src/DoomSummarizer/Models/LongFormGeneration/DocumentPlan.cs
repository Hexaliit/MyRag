using DoomSummarizer.Services;

namespace DoomSummarizer.Models.LongFormGeneration;

/// <summary>
/// A planned section in the document outline.
/// Contains the heading, theme keywords, and a theme embedding for deterministic
/// evidence assignment via cosine similarity.
/// </summary>
public class PlannedSection
{
    public required string Heading { get; init; }
    public required string ThemeKeywords { get; init; }
    public int TargetWords { get; init; } = 300;
    public string? Notes { get; init; }

    /// <summary>
    /// Embedding of the theme keywords — used for cosine similarity
    /// matching against evidence segments during assignment.
    /// </summary>
    public float[]? ThemeEmbedding { get; set; }

    /// <summary>Evidence segments assigned to this section (populated in Phase 3).</summary>
    public List<EvidenceSegment> AssignedEvidence { get; set; } = [];

    /// <summary>Generated text content (populated in Phase 4).</summary>
    public string? GeneratedContent { get; set; }

    /// <summary>Source URLs from assigned evidence.</summary>
    public List<string> SourceUrls => AssignedEvidence
        .Select(e => e.ArticleUrl)
        .Where(u => !string.IsNullOrEmpty(u))
        .Distinct()
        .ToList();
}

/// <summary>
/// The full document plan produced by the sentinel LLM (Phase 2).
/// Contains the title, section outlines with theme embeddings, and a global
/// theme embedding used for drift detection.
/// </summary>
public class DocumentPlan
{
    public required string Title { get; init; }
    public string? ThemeDescription { get; init; }
    public List<PlannedSection> Sections { get; init; } = [];
    public string? ConclusionAngle { get; init; }
    public List<string> KeyEntities { get; init; } = [];

    /// <summary>
    /// Embedding of the theme description — used as the drift detection anchor.
    /// Sections whose generated content drifts too far from this get correction guidance.
    /// </summary>
    public float[]? ThemeEmbedding { get; set; }

    /// <summary>QueryType detected for this document.</summary>
    public QueryType QueryType { get; init; }
}

/// <summary>
/// Raw sentinel outline JSON model (before embedding).
/// Parsed from sentinel LLM output then converted to DocumentPlan.
/// </summary>
public class SentinelOutline
{
    public string Title { get; set; } = "";
    public string ThemeDescription { get; set; } = "";
    public List<SentinelSection> Sections { get; set; } = [];
    public string? ConclusionAngle { get; set; }
    public List<string> KeyEntities { get; set; } = [];
}

public class SentinelSection
{
    public string Heading { get; set; } = "";
    public string ThemeKeywords { get; set; } = "";
    public int TargetWords { get; set; } = 300;
}
