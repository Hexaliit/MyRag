namespace LucidRAG.UltraResearch.Signals;

/// <summary>
///     Signal key constants for the UltraResearch wave system.
///     Convention: "domain.category.metric"
/// </summary>
public static class ResearchSignalKeys
{
    // Paper metadata signals
    public const string PaperMetadataTitle = "paper.metadata.title";
    public const string PaperMetadataAuthors = "paper.metadata.authors";
    public const string PaperMetadataYear = "paper.metadata.year";
    public const string PaperMetadataDoi = "paper.metadata.doi";
    public const string PaperMetadataArxivId = "paper.metadata.arxiv_id";

    // Venue signals
    public const string PaperVenueQuality = "paper.venue.quality";
    public const string PaperVenueName = "paper.venue.name";

    // Citation signals
    public const string PaperCitationsCount = "paper.citations.count";
    public const string PaperCitationsInfluential = "paper.citations.influential";
    public const string PaperCitationsExtracted = "paper.citations.extracted";

    // Content signals
    public const string PaperContentText = "paper.content.text";
    public const string PaperContentLength = "paper.content.length";
    public const string PaperFetchSuccess = "paper.fetch.success";

    // File signals
    public const string PaperFilePath = "paper.file.path";

    // Ingestion signals
    public const string PaperIngestSuccess = "paper.ingest.success";
    public const string PaperIngestSegmentCount = "paper.ingest.segment_count";

    // Evidence extraction signals
    public const string PaperEvidenceCodeBlocks = "paper.evidence.code_blocks";
    public const string PaperEvidenceTables = "paper.evidence.tables";
    public const string PaperEvidenceEquations = "paper.evidence.equations";
    public const string PaperEvidenceTotal = "paper.evidence.total";

    // Author signals
    public const string PaperAuthorsPromoted = "paper.authors.promoted";

    // Session-level signals
    public const string SessionConvergenceNewInfoRatio = "session.convergence.new_info_ratio";
    public const string SessionAuthorFrequency = "session.author.frequency";

    // Sentinel signals
    public const string SentinelGaps = "sentinel.gaps";
    public const string SentinelShouldContinue = "sentinel.should_continue";
}
