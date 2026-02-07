using System.Text.Json.Serialization;

namespace LucidRAG.UltraResearch;

/// <summary>
///     Configuration for an UltraResearch session.
/// </summary>
public class UltraResearchConfig
{
    /// <summary>Research topic query.</summary>
    public required string Topic { get; set; }

    /// <summary>Max papers to fetch before stopping.</summary>
    public int MaxPapers { get; set; } = 200;

    /// <summary>Papers to fetch per iteration before analyzing.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>Max main loop iterations.</summary>
    public int MaxIterations { get; set; } = 50;

    /// <summary>Max wall-clock duration.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Run sentinel every N papers.</summary>
    public int SentinelInterval { get; set; } = 5;

    /// <summary>New-info ratio below this for 3 checkpoints triggers convergence.</summary>
    public double ConvergenceThreshold { get; set; } = 0.15;

    /// <summary>Seed arXiv IDs to start from.</summary>
    public List<string> SeedArxivIds { get; set; } = [];

    /// <summary>Seed DOIs to start from.</summary>
    public List<string> SeedDois { get; set; } = [];

    /// <summary>arXiv categories to search within.</summary>
    public List<string> ArxivCategories { get; set; } = [];

    /// <summary>Whether to use Semantic Scholar for reverse citations.</summary>
    public bool IncludeSemanticScholar { get; set; } = true;

    /// <summary>Optional collection name override. Auto-generated if null.</summary>
    public string? CollectionName { get; set; }

    /// <summary>Data directory for saving fetched papers.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Dry-run mode: search + discover without ingesting.</summary>
    public bool DryRun { get; set; }
}

/// <summary>
///     Status of an UltraResearch session.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UltraResearchStatus
{
    Running,
    Paused,
    Completed,
    Stopped,
    Failed
}

/// <summary>
///     Full state of an UltraResearch session. Serialized to CollectionEntity.Settings for crash recovery.
/// </summary>
public class UltraResearchState
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public Guid CollectionId { get; set; }
    public UltraResearchStatus Status { get; set; } = UltraResearchStatus.Running;
    public required string Topic { get; set; }
    public int Iteration { get; set; }
    public int PapersFetched { get; set; }
    public int PapersIngested { get; set; }
    public int PapersSkipped { get; set; }
    public int PapersFailed { get; set; }

    /// <summary>Normalized IDs of all seen papers (arxiv:XXXX, doi:XXXX).</summary>
    public HashSet<string> SeenIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Priority-ordered fetch candidates.</summary>
    public List<FetchCandidate> Frontier { get; set; } = [];

    /// <summary>Search queries already executed.</summary>
    public HashSet<string> SearchQueriesUsed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sentinel checkpoint history.</summary>
    public List<SentinelCheckpoint> Checkpoints { get; set; } = [];

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? StopReason { get; set; }
}

/// <summary>
///     Source of a fetch candidate.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CandidateSource
{
    Search,
    Citation,
    SemanticScholar,
    Orphan,
    Sentinel
}

/// <summary>
///     A paper discovered during the research process, queued for fetching.
/// </summary>
public class FetchCandidate
{
    /// <summary>Identifier (arXiv ID or DOI).</summary>
    public required string Id { get; set; }

    /// <summary>"arxiv" or "doi".</summary>
    public required string Type { get; set; }

    /// <summary>How this candidate was discovered.</summary>
    public CandidateSource Source { get; set; }

    /// <summary>Composite priority score (0-1, higher = fetch sooner).</summary>
    public double Priority { get; set; }

    /// <summary>Citation count (from S2 or graph).</summary>
    public int CitedByCount { get; set; }

    /// <summary>Paper title if known.</summary>
    public string? Title { get; set; }

    /// <summary>ID of the paper that led to discovering this candidate.</summary>
    public string? DiscoveredFrom { get; set; }
}

/// <summary>
///     Snapshot from a sentinel evaluation checkpoint.
/// </summary>
public class SentinelCheckpoint
{
    public int Iteration { get; set; }
    public int TotalPapers { get; set; }
    public int TotalEntities { get; set; }
    public int OrphanCitations { get; set; }

    /// <summary>Fraction of new entities in this batch vs total.</summary>
    public double NewInfoRatio { get; set; }

    /// <summary>Conceptual gaps identified by the sentinel.</summary>
    public List<string> IdentifiedGaps { get; set; } = [];

    /// <summary>New queries suggested by the sentinel.</summary>
    public List<string> SuggestedQueries { get; set; } = [];

    /// <summary>Full sentinel analysis text.</summary>
    public string? SentinelAnalysis { get; set; }

    /// <summary>Whether the sentinel recommends continuing.</summary>
    public bool ShouldContinue { get; set; } = true;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
///     Current stage of the research loop.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchStage
{
    Searching,
    Fetching,
    Ingesting,
    Analyzing,
    Sentinel,
    Finalizing
}

/// <summary>
///     Progress update emitted during the research loop.
/// </summary>
public record UltraResearchProgress(
    ResearchStage Stage,
    string Message,
    int Iteration,
    int PapersFetched,
    int PapersIngested,
    int FrontierSize,
    double? NewInfoRatio);
