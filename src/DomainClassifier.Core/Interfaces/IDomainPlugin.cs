using DomainClassifier.Core.Models;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Core.Interfaces;

/// <summary>
///     Central contract for domain-specific content enrichment plugins.
///     Each domain (financial, legal, medical, etc.) implements this interface.
/// </summary>
public interface IDomainPlugin
{
    /// <summary>
    ///     Unique identifier, e.g. "financial", "legal", "medical".
    /// </summary>
    string DomainId { get; }

    /// <summary>
    ///     Human-readable name.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Domain-specific entity types this plugin can extract,
    ///     e.g. ["ticker", "amount", "instrument", "financial_date"].
    /// </summary>
    IReadOnlyList<string> EntityTypes { get; }

    /// <summary>
    ///     Query terms that indicate a user query is relevant to this domain.
    ///     Used by retrieval to boost domain-matched segments.
    ///     Override per-language or per-domain to avoid hardcoding.
    /// </summary>
    IReadOnlyList<string> QueryRelevanceTerms => [];

    /// <summary>
    ///     How confident this plugin is that the content belongs to its domain.
    ///     Called on a sample of chunks to decide whether to run full extraction.
    ///     Returns 0.0 (not my domain) to 1.0 (definitely my domain).
    /// </summary>
    Task<DomainClassification> ClassifyAsync(
        string text,
        CancellationToken ct = default);

    /// <summary>
    ///     Extract domain-specific entities and signals from content chunks.
    ///     Only called if ClassifyAsync returned above threshold.
    /// </summary>
    Task<DomainEnrichmentResult> EnrichAsync(
        IReadOnlyList<ContentChunk> chunks,
        CancellationToken ct = default);
}
