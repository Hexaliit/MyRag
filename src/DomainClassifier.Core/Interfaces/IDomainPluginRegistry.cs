using DomainClassifier.Core.Models;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Core.Interfaces;

/// <summary>
///     Discovery and dispatch for domain plugins.
///     Mirrors the IPipelineRegistry pattern.
/// </summary>
public interface IDomainPluginRegistry
{
    /// <summary>
    ///     Get all registered domain plugins.
    /// </summary>
    IReadOnlyList<IDomainPlugin> GetAll();

    /// <summary>
    ///     Get a specific plugin by domain ID.
    /// </summary>
    IDomainPlugin? GetById(string domainId);

    /// <summary>
    ///     Classify text against all registered plugins, return ranked matches.
    /// </summary>
    Task<IReadOnlyList<DomainClassification>> ClassifyAsync(
        string text,
        CancellationToken ct = default);

    /// <summary>
    ///     Auto-detect domain and run matching plugin(s) for enrichment.
    ///     If domainHint is provided, skips classification and uses the hinted plugin directly.
    /// </summary>
    Task<DomainEnrichmentResult> EnrichAsync(
        IReadOnlyList<ContentChunk> chunks,
        double classificationThreshold = 0.3,
        string? domainHint = null,
        CancellationToken ct = default);
}
