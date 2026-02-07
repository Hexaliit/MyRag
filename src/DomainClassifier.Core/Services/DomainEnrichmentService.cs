using DomainClassifier.Core.Configuration;
using DomainClassifier.Core.Interfaces;
using DomainClassifier.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Core.Services;

/// <summary>
///     Orchestrates domain classification and enrichment on content chunks.
///     This is the main entry point for the domain enrichment pipeline step.
/// </summary>
public class DomainEnrichmentService
{
    private readonly IDomainPluginRegistry _registry;
    private readonly DomainClassifierConfig _config;
    private readonly ILogger<DomainEnrichmentService> _logger;

    public DomainEnrichmentService(
        IDomainPluginRegistry registry,
        IOptions<DomainClassifierConfig> config,
        ILogger<DomainEnrichmentService> logger)
    {
        _registry = registry;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Enrich content chunks with domain-specific metadata.
    ///     No-op if no domain plugins are registered.
    ///     If domainHint is provided, skips classification and uses the hinted plugin directly.
    /// </summary>
    public async Task<DomainEnrichmentResult> EnrichChunksAsync(
        IReadOnlyList<ContentChunk> chunks,
        string? domainHint = null,
        CancellationToken ct = default)
    {
        if (_registry.GetAll().Count == 0)
        {
            _logger.LogDebug("No domain plugins registered, skipping enrichment");
            return DomainEnrichmentResult.Empty;
        }

        if (chunks.Count == 0)
            return DomainEnrichmentResult.Empty;

        _logger.LogDebug("Running domain enrichment on {ChunkCount} chunks with {PluginCount} plugins (hint={DomainHint})",
            chunks.Count, _registry.GetAll().Count, domainHint ?? "none");

        var result = await _registry.EnrichAsync(
            chunks,
            _config.ClassificationThreshold,
            domainHint,
            ct);

        if (result.Classifications.Count > 0)
        {
            var topDomain = result.Classifications
                .OrderByDescending(c => c.Confidence)
                .First();
            _logger.LogInformation(
                "Domain enrichment complete: detected={DomainId} (confidence={Confidence:P1}), " +
                "{EntityCount} entities, {SignalCount} signals",
                topDomain.DomainId, topDomain.Confidence,
                result.Entities.Count, result.Signals.Count);
        }

        return result;
    }

    /// <summary>
    ///     Enrich chunks and merge domain metadata into each chunk's Metadata dictionary.
    /// </summary>
    public async Task EnrichAndMergeAsync(
        IReadOnlyList<ContentChunk> chunks,
        string? domainHint = null,
        CancellationToken ct = default)
    {
        var result = await EnrichChunksAsync(chunks, domainHint, ct);

        if (result == DomainEnrichmentResult.Empty)
            return;

        var globalMetadata = result.ToMetadata();

        foreach (var chunk in chunks)
            foreach (var (key, value) in globalMetadata)
                chunk.Metadata[key] = value;
    }
}
