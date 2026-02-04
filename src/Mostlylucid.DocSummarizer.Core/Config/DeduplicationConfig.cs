namespace Mostlylucid.DocSummarizer.Config;

/// <summary>
///     Deduplication configuration for both ingestion and retrieval phases.
///     Controls segment deduplication behavior across the RAG pipeline.
/// </summary>
public class DeduplicationConfig
{
    /// <summary>
    ///     Ingestion-phase deduplication settings (intra-document only).
    /// </summary>
    public IngestionDeduplicationConfig Ingestion { get; set; } = new();

    /// <summary>
    ///     Retrieval-phase deduplication settings (cross-document, post-RRF).
    /// </summary>
    public RetrievalDeduplicationConfig Retrieval { get; set; } = new();

    /// <summary>
    ///     Analytics and observability settings.
    /// </summary>
    public DeduplicationAnalyticsConfig Analytics { get; set; } = new();
}

/// <summary>
///     Configuration for ingestion-phase (intra-document) deduplication.
///     Runs during document processing before indexing to vector store.
/// </summary>
public class IngestionDeduplicationConfig
{
    /// <summary>
    ///     Enable deduplication during ingestion.
    ///     Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Cosine similarity threshold for considering segments as duplicates.
    ///     0.90 aligns with industry standards (NVIDIA NeMo, SemHash).
    ///     Range: 0.0-1.0. Higher = more conservative (fewer false positives).
    ///     Default: 0.90
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.90;

    /// <summary>
    ///     Minimum salience score to consider a segment for deduplication.
    ///     Segments below this threshold are filtered out as noise.
    ///     Range: 0.0-1.0. Default: 0.05
    /// </summary>
    public double SalienceThreshold { get; set; } = 0.05;

    /// <summary>
    ///     Whether to boost salience for near-duplicate segments.
    ///     Near-duplicates (different text, similar meaning) signal author emphasis.
    ///     Default: true
    /// </summary>
    public bool EnableSalienceBoost { get; set; } = true;

    /// <summary>
    ///     Boost multiplier per near-duplicate merged (linear mode).
    ///     Only used when BoostDecayMode = Linear.
    ///     Range: 0.0-1.0. Default: 0.15 (+15% per near-duplicate)
    /// </summary>
    public double BoostPerNearDuplicate { get; set; } = 0.15;

    /// <summary>
    ///     Maximum total salience boost that can be applied.
    ///     Prevents any single concept from dominating.
    ///     Range: 1.0-2.0. Default: 1.0 (max salience score)
    /// </summary>
    public double MaxSalienceBoost { get; set; } = 1.0;

    /// <summary>
    ///     Boost decay mode: Linear or Logarithmic.
    ///     Linear: constant boost per duplicate (boostPerNearDuplicate * count)
    ///     Logarithmic: diminishing returns (boostPerNearDuplicate * log2(1 + count))
    ///     Default: Logarithmic
    /// </summary>
    public BoostDecayMode BoostDecayMode { get; set; } = BoostDecayMode.Logarithmic;

    /// <summary>
    ///     Base for logarithmic decay calculation.
    ///     Higher base = slower decay (more boost per duplicate).
    ///     Default: 2.0 (log2)
    /// </summary>
    public double LogBase { get; set; } = 2.0;
}

/// <summary>
///     Configuration for retrieval-phase (cross-document) deduplication.
///     Runs after RRF ranking to prevent redundant segments in LLM context.
/// </summary>
public class RetrievalDeduplicationConfig
{
    /// <summary>
    ///     Enable deduplication during retrieval.
    ///     Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Cosine similarity threshold for cross-document deduplication.
    ///     Range: 0.0-1.0. Default: 0.90
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.90;

    /// <summary>
    ///     Minimum RRF/relevance score to include in results.
    ///     Segments below this are considered not relevant enough.
    ///     Range: 0.0-1.0. Default: 0.25
    /// </summary>
    public double MinRelevanceScore { get; set; } = 0.25;
}

/// <summary>
///     Configuration for deduplication analytics and observability.
/// </summary>
public class DeduplicationAnalyticsConfig
{
    /// <summary>
    ///     Enable detailed logging of deduplication operations.
    ///     Default: true
    /// </summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>
    ///     Enable metrics collection for monitoring dashboards.
    ///     Default: true
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    ///     Alert threshold for ingestion dedup ratio.
    ///     If more than this percentage is deduplicated, log a warning.
    ///     May indicate excessive boilerplate or auto-generated content.
    ///     Range: 0.0-1.0. Default: 0.50 (50%)
    /// </summary>
    public double HighIngestionDedupThreshold { get; set; } = 0.50;

    /// <summary>
    ///     Alert threshold for retrieval dedup ratio.
    ///     If more than this percentage is deduplicated, log a warning.
    ///     May indicate overly broad query or highly similar corpus.
    ///     Range: 0.0-1.0. Default: 0.30 (30%)
    /// </summary>
    public double HighRetrievalDedupThreshold { get; set; } = 0.30;

    /// <summary>
    ///     Alert threshold for max salience boost.
    ///     If any segment's boost approaches this, log a warning.
    ///     May indicate legitimate emphasis or potential spam.
    ///     Range: 0.0-1.0. Default: 0.60
    /// </summary>
    public double HighSalienceBoostThreshold { get; set; } = 0.60;
}

/// <summary>
///     Boost decay mode for near-duplicate salience boosting.
/// </summary>
public enum BoostDecayMode
{
    /// <summary>
    ///     Linear boost: boostPerNearDuplicate * count.
    ///     Each near-duplicate adds the same amount of boost.
    /// </summary>
    Linear,

    /// <summary>
    ///     Logarithmic boost: boostPerNearDuplicate * log(1 + count).
    ///     Diminishing returns - first duplicates matter most.
    /// </summary>
    Logarithmic
}