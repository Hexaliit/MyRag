using DoomSummarizer.Services;
using DoomWriter.Models;

namespace DoomWriter.Services;

/// <summary>
/// Generates intelligent suggestions by comparing the current document
/// against the corpus knowledge base.
/// - Cross-link suggestions ("you wrote about this")
/// - Similar content detection ("you mentioned this before")
/// - Drift warnings ("this section has drifted from your main topic")
/// </summary>
public class SuggestionService
{
    private readonly CorpusService _corpus;
    private readonly EmbeddingService _embedding;
    private readonly WriterSettingsService _settings;

    private const float EntityMatchThreshold = 0.4f;
    private const float SegmentMatchThreshold = 0.5f;
    private const float DriftWarningThreshold = 0.35f;

    public SuggestionService(
        CorpusService corpus,
        EmbeddingService embedding,
        WriterSettingsService settings)
    {
        _corpus = corpus;
        _embedding = embedding;
        _settings = settings;
    }

    /// <summary>
    /// Generate all suggestions for the current document signals.
    /// </summary>
    public async Task<List<Suggestion>> GenerateSuggestionsAsync(
        DocumentSignals signals,
        string currentContent,
        CancellationToken ct = default)
    {
        var suggestions = new List<Suggestion>();

        if (!_corpus.IsInitialized || !_embedding.IsSetup)
            return suggestions;

        // 1. Entity-based suggestions ("you have an article about this")
        var entitySuggestions = await FindEntityMatchesAsync(signals.Entities, ct);
        suggestions.AddRange(entitySuggestions);

        // 2. Segment similarity suggestions ("you mentioned this before")
        var segmentSuggestions = await FindSimilarSegmentsAsync(signals.Segments, ct);
        suggestions.AddRange(segmentSuggestions);

        // 3. Drift warnings
        if (_settings.Config.EnableDriftDetection)
        {
            var driftWarnings = DetectDrift(signals);
            suggestions.AddRange(driftWarnings);
        }

        return suggestions
            .OrderByDescending(s => s.Confidence)
            .Take(20)
            .ToList();
    }

    /// <summary>
    /// Find corpus articles that match entities in the current document.
    /// "You mention X — you covered this in detail in [Article Y]."
    /// </summary>
    private async Task<List<Suggestion>> FindEntityMatchesAsync(
        List<TrackedEntity> entities,
        CancellationToken ct)
    {
        var suggestions = new List<Suggestion>();

        foreach (var entity in entities.Take(10)) // Top 10 by mention count
        {
            if (ct.IsCancellationRequested) break;

            var matches = await _corpus.SearchByEntityAsync(entity.Name, topK: 3);

            foreach (var match in matches.Where(m => m.Score >= EntityMatchThreshold))
            {
                // Avoid suggesting the current document
                if (match.Title.Equals(entity.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                suggestions.Add(new Suggestion
                {
                    Type = SuggestionType.SuggestedLink,
                    Title = $"You mention \"{entity.Name}\"",
                    Description = $"You covered this in: {match.Title}",
                    TargetUrl = match.Source,
                    TargetTitle = match.Title,
                    InsertText = $"[{entity.Name}]({match.Source})",
                    Confidence = match.Score
                });
            }
        }

        return suggestions;
    }

    /// <summary>
    /// Find corpus segments that are similar to current document segments.
    /// "You wrote something similar in [Article Y, paragraph Z]."
    /// </summary>
    private async Task<List<Suggestion>> FindSimilarSegmentsAsync(
        List<AnalyzedSegment> segments,
        CancellationToken ct)
    {
        var suggestions = new List<Suggestion>();

        // Sample segments — don't search every single one
        var sampleSegments = segments
            .OrderByDescending(s => s.Salience)
            .Take(5);

        foreach (var segment in sampleSegments)
        {
            if (ct.IsCancellationRequested) break;

            var matches = await _corpus.SearchAsync(segment.Text, topK: 2);

            foreach (var match in matches.Where(m => m.Score >= SegmentMatchThreshold))
            {
                suggestions.Add(new Suggestion
                {
                    Type = SuggestionType.SimilarContent,
                    Title = "Similar passage found",
                    Description = $"In \"{match.Title}\": \"{Truncate(match.Text, 120)}\"",
                    TargetUrl = match.Source,
                    TargetTitle = match.Title,
                    Confidence = match.Score
                });
            }
        }

        return suggestions;
    }

    /// <summary>
    /// Detect topic drift using embedding cosine similarity.
    /// Sections that drift far from the document centroid get warnings.
    /// </summary>
    private List<Suggestion> DetectDrift(DocumentSignals signals)
    {
        var warnings = new List<Suggestion>();

        if (signals.DocumentCentroid.Length == 0)
            return warnings;

        for (int i = 0; i < signals.Segments.Count; i++)
        {
            var segment = signals.Segments[i];
            if (segment.Embedding == null) continue;

            var similarity = VectorMath.CosineSimilarity(segment.Embedding, signals.DocumentCentroid);

            if (similarity < DriftWarningThreshold)
            {
                var topic = segment.Topic ?? "unknown";
                warnings.Add(new Suggestion
                {
                    Type = SuggestionType.DriftWarning,
                    Title = $"Section {i + 1} has drifted",
                    Description = $"This section (topic: {topic}) has low coherence ({similarity:F2}) with the rest of your document. Consider refocusing or moving to a separate section.",
                    Confidence = 1f - similarity // Higher confidence = worse drift
                });
            }
        }

        // Check consecutive drift
        var consecutiveDrift = 0;
        for (int i = 0; i < signals.Segments.Count; i++)
        {
            var segment = signals.Segments[i];
            if (segment.Embedding == null) continue;

            var sim = VectorMath.CosineSimilarity(segment.Embedding, signals.DocumentCentroid);
            if (sim < 0.5f)
            {
                consecutiveDrift++;
                if (consecutiveDrift >= 3)
                {
                    warnings.Add(new Suggestion
                    {
                        Type = SuggestionType.CoherenceWarning,
                        Title = "Extended topic drift detected",
                        Description = $"Sections {i - 1} through {i + 1} have all drifted from your main topic. Your document may benefit from restructuring.",
                        Confidence = 0.9f
                    });
                    break;
                }
            }
            else
            {
                consecutiveDrift = 0;
            }
        }

        return warnings;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}
