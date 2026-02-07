namespace DomainClassifier.Core.Models;

/// <summary>
///     Aggregated output from domain enrichment across one or more plugins.
/// </summary>
public record DomainEnrichmentResult(
    IReadOnlyList<DomainClassification> Classifications,
    IReadOnlyList<DomainEntity> Entities,
    IReadOnlyList<DomainSignal> Signals)
{
    public static DomainEnrichmentResult Empty { get; } = new([], [], []);

    /// <summary>
    ///     Merge enrichment results into a metadata dictionary suitable for ContentChunk.Metadata.
    /// </summary>
    public Dictionary<string, object?> ToMetadata()
    {
        var metadata = new Dictionary<string, object?>();

        if (Classifications.Count == 0)
            return metadata;

        var topClassification = Classifications
            .OrderByDescending(c => c.Confidence)
            .First();

        metadata["domain.detected"] = topClassification.DomainId;
        metadata["domain.confidence"] = topClassification.Confidence;

        // Add all classification scores
        foreach (var classification in Classifications)
            metadata[$"domain.{classification.DomainId}.confidence"] = classification.Confidence;

        // Add signals
        foreach (var signal in Signals)
            metadata[$"domain.{signal.Key}"] = signal.Value;

        // Add entities grouped by domain
        var entitiesByDomain = Entities
            .GroupBy(e => e.DomainId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => new Dictionary<string, object?>
                {
                    ["text"] = e.Text,
                    ["type"] = e.EntityType,
                    ["confidence"] = e.Confidence
                }).ToList() as object);

        foreach (var (domain, entities) in entitiesByDomain)
            metadata[$"domain.{domain}.entities"] = entities;

        return metadata;
    }

    /// <summary>
    ///     Build metadata specific to a single chunk, filtering entities by offset.
    /// </summary>
    public Dictionary<string, object?> ToMetadataForChunk(int chunkStartOffset, int chunkEndOffset)
    {
        var chunkEntities = Entities
            .Where(e => e.StartOffset >= chunkStartOffset && e.EndOffset <= chunkEndOffset)
            .ToList();

        var filtered = this with { Entities = chunkEntities };
        return filtered.ToMetadata();
    }
}
