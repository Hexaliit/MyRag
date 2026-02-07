using System.Text.RegularExpressions;
using DomainClassifier.Core.Interfaces;
using DomainClassifier.Core.Models;
using DomainClassifier.Narrative.Extractors;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Narrative;

/// <summary>
///     Narrative domain plugin. Extracts characters, dialogue, settings, genre,
///     and perspective from fiction/literary content.
/// </summary>
public partial class NarrativeDomainPlugin : IDomainPlugin
{
    private readonly ILogger<NarrativeDomainPlugin> _logger;

    public NarrativeDomainPlugin(ILogger<NarrativeDomainPlugin> logger)
    {
        _logger = logger;
    }

    public string DomainId => "narrative";
    public string Name => "Narrative Domain";

    public IReadOnlyList<string> EntityTypes =>
    [
        "character", "dialogue", "setting_location",
        "time_period", "atmosphere"
    ];

    public Task<DomainClassification> ClassifyAsync(
        string text,
        CancellationToken ct = default)
    {
        var result = NarrativeKeywordClassifier.Classify(text);
        return Task.FromResult(result);
    }

    public Task<DomainEnrichmentResult> EnrichAsync(
        IReadOnlyList<ContentChunk> chunks,
        CancellationToken ct = default)
    {
        var allEntities = new List<DomainEntity>();
        var signals = new List<DomainSignal>();

        var combinedText = string.Join("\n", chunks.Select(c => c.Text));

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            allEntities.AddRange(CharacterExtractor.Extract(chunk.Text));
            allEntities.AddRange(DialogueExtractor.Extract(chunk.Text));
            allEntities.AddRange(SettingExtractor.Extract(chunk.Text));
        }

        // Aggregate signals from combined text
        var (perspective, perspectiveConfidence) = PerspectiveDetector.Detect(combinedText);
        if (perspective != "unknown")
        {
            signals.Add(new DomainSignal(
                "narrative.perspective",
                perspective,
                perspectiveConfidence,
                DomainId));
        }

        var (genre, genreConfidence) = GenreClassifier.Classify(combinedText);
        if (genre != "unknown")
        {
            signals.Add(new DomainSignal(
                "narrative.genre",
                genre,
                genreConfidence,
                DomainId));

            signals.Add(new DomainSignal(
                "narrative.genre_confidence",
                genreConfidence,
                genreConfidence,
                DomainId));
        }

        // Character count and primary characters
        var characters = allEntities
            .Where(e => e.EntityType == "character")
            .DistinctBy(e => e.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();

        signals.Add(new DomainSignal(
            "narrative.character_count",
            characters.Count,
            0.9,
            DomainId));

        if (characters.Count > 0)
        {
            var primaryCharacters = characters
                .OrderByDescending(c => GetMentionCount(c))
                .Take(10)
                .Select(c => c.Text)
                .ToList();

            signals.Add(new DomainSignal(
                "narrative.primary_characters",
                primaryCharacters,
                0.9,
                DomainId));
        }

        // Dialogue density
        var dialogueDensity = DialogueExtractor.ComputeDialogueDensity(combinedText);
        signals.Add(new DomainSignal(
            "narrative.dialogue_density",
            Math.Round(dialogueDensity, 4),
            0.9,
            DomainId));

        // Settings
        var settings = allEntities
            .Where(e => e.EntityType == "setting_location")
            .Select(e => e.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (settings.Count > 0)
        {
            signals.Add(new DomainSignal(
                "narrative.settings",
                settings,
                0.85,
                DomainId));
        }

        // Time period
        var timePeriods = allEntities
            .Where(e => e.EntityType == "time_period")
            .Select(e => e.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (timePeriods.Count > 0)
        {
            signals.Add(new DomainSignal(
                "narrative.time_period",
                timePeriods.First(),
                0.8,
                DomainId));
        }

        // Document subtype inference
        var docSubtype = InferDocumentSubtype(combinedText, dialogueDensity);
        signals.Add(new DomainSignal(
            "narrative.document_subtype",
            docSubtype,
            0.7,
            DomainId));

        var classification = NarrativeKeywordClassifier.Classify(
            string.Join("\n", chunks.Take(3).Select(c => c.Text)));

        _logger.LogDebug(
            "Narrative enrichment: {EntityCount} entities, {SignalCount} signals from {ChunkCount} chunks",
            allEntities.Count, signals.Count, chunks.Count);

        return Task.FromResult(new DomainEnrichmentResult(
            Classifications: [classification],
            Entities: allEntities,
            Signals: signals));
    }

    // Stage directions for play detection
    [GeneratedRegex(@"\b(?:Enter|Exit|Exeunt|Re-enter)\b", RegexOptions.Compiled)]
    private static partial Regex StageDirectionRegex();

    // "Dear " pattern for letter/epistolary detection
    [GeneratedRegex(@"(?m)^Dear\s+", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DearPatternRegex();

    private static int GetMentionCount(DomainEntity entity)
    {
        if (entity.Metadata?.TryGetValue("mentions", out var val) == true && val is int count)
            return count;
        return 0;
    }

    private static string InferDocumentSubtype(string text, double dialogueDensity)
    {
        // Play: high dialogue density + stage directions
        if (dialogueDensity > 0.6 && StageDirectionRegex().Matches(text).Count >= 2)
            return "play";

        // Poetry: majority of lines < 60 chars with irregular lengths
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 10)
        {
            var shortLines = lines.Count(l => l.TrimEnd().Length < 60);
            if ((double)shortLines / lines.Length > 0.7)
            {
                // Check for irregular lengths (not just code or lists)
                var lengths = lines.Select(l => l.TrimEnd().Length).ToArray();
                var variance = lengths.Select(l => Math.Pow(l - lengths.Average(), 2)).Average();
                if (variance > 100) // High variance = likely poetry
                    return "poetry";
            }
        }

        // Letter/epistolary: "Dear " patterns
        if (DearPatternRegex().Matches(text).Count >= 2)
            return "letter";

        // Short story: < 20,000 chars
        if (text.Length < 20_000)
            return "short_story";

        // Default: novel
        return "novel";
    }
}
