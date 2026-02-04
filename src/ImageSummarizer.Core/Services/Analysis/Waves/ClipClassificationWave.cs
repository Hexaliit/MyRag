using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Vision;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
///     CLIP Classification Wave - Zero-shot image classification using predetermined entity labels.
///     Uses CLIP text encoder to match image embeddings against semantic labels.
///     Produces entity-type signals for downstream NER integration.
///     Priority: 46 (immediately after CLIP embedding, before synthesis)
/// </summary>
public class ClipClassificationWave : IAnalysisWave
{
    private readonly ClipZeroShotService _clipService;
    private readonly ImageConfig _config;
    private readonly ILogger<ClipClassificationWave>? _logger;

    public ClipClassificationWave(
        ClipZeroShotService clipService,
        IOptions<ImageConfig> config,
        ILogger<ClipClassificationWave>? logger = null)
    {
        _clipService = clipService;
        _config = config.Value;
        _logger = logger;
    }

    public string Name => "ClipClassificationWave";
    public int Priority => 46; // Right after ClipEmbeddingWave (45)
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "classification", "clip", "ner" };

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Skip if CLIP embedding is disabled
        if (!_config.EnableClipEmbedding)
            return false;

        // Skip if auto-routing says to skip
        if (context.IsWaveSkippedByRouting(Name))
            return false;

        // Only run if we have a CLIP embedding from the previous wave
        var hasEmbedding = context.HasSignal("vision.clip.embedding");
        if (!hasEmbedding)
        {
            _logger?.LogDebug("Skipping ClipClassificationWave: no CLIP embedding available");
            return false;
        }

        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        try
        {
            // Get the CLIP embedding from context
            var embedding = context.GetValue<float[]>("vision.clip.embedding");
            if (embedding == null || embedding.Length == 0)
            {
                _logger?.LogDebug("No valid CLIP embedding in context");
                return signals;
            }

            // Check if service is available
            if (!await _clipService.IsAvailableAsync(ct))
            {
                signals.Add(new Signal
                {
                    Key = "clip.classification.unavailable",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "classification", "status" }
                });
                return signals;
            }

            // Classify against predetermined labels
            var classifications = await _clipService.ClassifyAsync(
                embedding,
                5,
                0.15,
                ct);

            if (classifications.Count == 0)
            {
                signals.Add(new Signal
                {
                    Key = "clip.classification.no_matches",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "classification" }
                });
                return signals;
            }

            // Emit top classification as primary entity type
            var topMatch = classifications.First();
            signals.Add(new Signal
            {
                Key = "clip.entity_type",
                Value = topMatch.EntityType,
                Confidence = topMatch.Confidence,
                Source = Name,
                Tags = new List<string> { SignalTags.Content, "entity", "primary" },
                Metadata = new Dictionary<string, object>
                {
                    ["label"] = topMatch.Label,
                    ["rank"] = topMatch.Rank
                }
            });

            // Emit all classifications for entity extraction
            var entityTypes = classifications
                .Select(c => new { c.EntityType, c.Confidence })
                .ToList();

            signals.Add(new Signal
            {
                Key = "clip.entity_types",
                Value = entityTypes.Select(e => e.EntityType).ToArray(),
                Confidence = classifications.Average(c => c.Confidence),
                Source = Name,
                Tags = new List<string> { SignalTags.Content, "entities", "classification" },
                Metadata = new Dictionary<string, object>
                {
                    ["classifications"] = classifications.Select(c => new
                    {
                        type = c.EntityType,
                        label = c.Label,
                        confidence = c.Confidence,
                        rank = c.Rank
                    }).ToList(),
                    ["count"] = classifications.Count
                }
            });

            // Emit individual entity signals for high-confidence matches
            foreach (var classification in classifications.Where(c => c.Confidence > 0.25))
                signals.Add(new Signal
                {
                    Key = $"clip.detected.{classification.EntityType}",
                    Value = true,
                    Confidence = classification.Confidence,
                    Source = Name,
                    Tags = new List<string> { "entity", "detected", classification.EntityType },
                    Metadata = new Dictionary<string, object>
                    {
                        ["label"] = classification.Label,
                        ["rank"] = classification.Rank
                    }
                });

            // Special signals for common entity types (for easier downstream consumption)
            if (classifications.Any(c => c.EntityType == "person" && c.Confidence > 0.3))
                signals.Add(new Signal
                {
                    Key = "content.has_person",
                    Value = true,
                    Confidence = classifications.First(c => c.EntityType == "person").Confidence,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "person", "ner" }
                });

            if (classifications.Any(c => c.EntityType is "diagram" or "chart" or "document" && c.Confidence > 0.3))
                signals.Add(new Signal
                {
                    Key = "content.is_document",
                    Value = true,
                    Confidence = classifications
                        .Where(c => c.EntityType is "diagram" or "chart" or "document")
                        .Max(c => c.Confidence),
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "document", "ner" }
                });

            _logger?.LogInformation(
                "CLIP classification: primary={Primary} ({Confidence:P0}), {Count} total entities",
                topMatch.EntityType,
                topMatch.Confidence,
                classifications.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "CLIP classification wave failed");
            signals.Add(new Signal
            {
                Key = "clip.classification.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "classification", "error" }
            });
        }

        return signals;
    }
}