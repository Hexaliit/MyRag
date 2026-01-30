using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
/// Extracts temporal constraints from query using RecognizedSignals (TextRecognizerService).
/// Detects temporal comparisons ("X before vs after 2024") and splits into
/// time-bounded sub-queries.
/// </summary>
public class TemporalAnalyzer : IQueryAnalyzer
{
    private readonly ILogger<TemporalAnalyzer>? _logger;

    /// <summary>
    /// Patterns indicating temporal comparison intent.
    /// These are checked against the query to determine if the user wants
    /// a before/after or trend analysis.
    /// </summary>
    private static readonly string[] TemporalComparisonMarkers =
    [
        "before and after",
        "vs. earlier",
        "compared to last",
        "changed since",
        "evolved from",
        "shifted since",
        "how has",
        "over time",
        "trend in",
        "evolution of"
    ];

    public TemporalAnalyzer(ILogger<TemporalAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    public Task<QuerySignals> AnalyzeAsync(string query, QuerySignals existing, CancellationToken ct = default)
    {
        // RecognizerSignals is passed as object to avoid tight coupling
        // The integration adapter will provide the actual RecognizedSignals type
        var nodes = new List<QueryNode>(existing.ProposedNodes);
        var lower = query.ToLowerInvariant();

        // Check for temporal comparison markers
        var isTemporalComparison = TemporalComparisonMarkers.Any(m =>
            lower.Contains(m, StringComparison.Ordinal));

        if (isTemporalComparison)
        {
            _logger?.LogDebug("Temporal comparison detected in query: {Query}", query);

            // For temporal comparisons, we create two time-bounded sub-queries
            // The actual date ranges come from RecognizerSignals or sentinel
            nodes.Add(new QueryNode
            {
                Query = query,
                Type = QueryNodeType.Atomic,
                Concept = ConceptType.Timeline,
                TimeScope = new TemporalScope
                {
                    RequiresFresh = false,
                    TimeSensitivity = "historical"
                },
                Depth = 0
            });

            nodes.Add(new QueryNode
            {
                Query = query,
                Type = QueryNodeType.Atomic,
                Concept = ConceptType.Timeline,
                TimeScope = new TemporalScope
                {
                    RequiresFresh = true,
                    TimeSensitivity = "recent"
                },
                Depth = 0
            });

            return Task.FromResult(existing with
            {
                ProposedNodes = nodes,
                DetectedConcept = ConceptType.Timeline
            });
        }

        // Check for freshness requirements (without comparison)
        var freshMarkers = new[] { "latest", "recent", "today", "this week", "breaking", "current" };
        var requiresFresh = freshMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));

        if (requiresFresh)
        {
            // Annotate existing nodes with temporal scope (don't create new ones)
            var updatedNodes = nodes.Select(n => n with
            {
                TimeScope = n.TimeScope ?? new TemporalScope
                {
                    RequiresFresh = true,
                    TimeSensitivity = lower.Contains("breaking") ? "breaking" :
                        lower.Contains("today") ? "today" : "recent"
                }
            }).ToList();

            return Task.FromResult(existing with { ProposedNodes = updatedNodes });
        }

        return Task.FromResult(existing);
    }
}
