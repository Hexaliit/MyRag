using LucidRAG.Services;
using Microsoft.Extensions.Logging;

namespace LucidRAG.UltraResearch;

/// <summary>
///     Priority queue for research paper candidates. Uses citation graph topology,
///     entity overlap, sentinel suggestions, and recency to score and rank candidates.
/// </summary>
public class ResearchFrontierManager
{
    private readonly CitationGraphQueries _graphQueries;
    private readonly ILogger<ResearchFrontierManager> _logger;

    // Priority weights
    private const double WeightCitedBy = 0.40;
    private const double WeightEntityOverlap = 0.25;
    private const double WeightSentinelBoost = 0.20;
    private const double WeightRecency = 0.15;

    public ResearchFrontierManager(
        CitationGraphQueries graphQueries,
        ILogger<ResearchFrontierManager> logger)
    {
        _graphQueries = graphQueries;
        _logger = logger;
    }

    /// <summary>
    ///     Rebuild frontier priorities using current index state.
    ///     Identifies orphan citations and foundational references as high-priority candidates.
    /// </summary>
    public async Task RefreshFrontierAsync(
        UltraResearchState state,
        Guid collectionId,
        CancellationToken ct = default)
    {
        // Get orphan citations — references cited by ingested docs but not themselves ingested
        var orphans = await _graphQueries.FindOrphanCitationsAsync(collectionId, limit: 50, ct: ct);

        foreach (var orphan in orphans)
        {
            var (id, type) = ParseEntityName(orphan.CanonicalName, orphan.EntityType);
            if (id == null) continue;

            var key = ResearchPaperFetcher.NormalizeSeenKey(type, id);
            if (state.SeenIds.Contains(key)) continue;

            // Check if already in frontier
            var existing = state.Frontier.FirstOrDefault(f =>
                ResearchPaperFetcher.NormalizeSeenKey(f.Type, f.Id) == key);

            if (existing != null)
            {
                // Boost existing candidate
                existing.CitedByCount = Math.Max(existing.CitedByCount, orphan.CitingDocumentCount);
                existing.Source = CandidateSource.Orphan;
            }
            else
            {
                state.Frontier.Add(new FetchCandidate
                {
                    Id = id,
                    Type = type,
                    Source = CandidateSource.Orphan,
                    CitedByCount = orphan.CitingDocumentCount,
                    Title = orphan.Description
                });
            }
        }

        // Rescore all candidates
        RescoreFrontier(state);

        _logger.LogDebug("Frontier refreshed: {Count} candidates, {Orphans} orphans found",
            state.Frontier.Count, orphans.Count);
    }

    /// <summary>
    ///     Add newly discovered candidates to the frontier (e.g., from citation extraction or S2 lookups).
    /// </summary>
    public void AddDiscoveredCandidates(List<FetchCandidate> candidates, UltraResearchState state)
    {
        foreach (var candidate in candidates)
        {
            var key = ResearchPaperFetcher.NormalizeSeenKey(candidate.Type, candidate.Id);
            if (state.SeenIds.Contains(key)) continue;

            var existing = state.Frontier.FirstOrDefault(f =>
                ResearchPaperFetcher.NormalizeSeenKey(f.Type, f.Id) == key);

            if (existing != null)
            {
                existing.CitedByCount = Math.Max(existing.CitedByCount, candidate.CitedByCount);
                if (candidate.Title != null) existing.Title ??= candidate.Title;
            }
            else
            {
                state.Frontier.Add(candidate);
            }
        }

        RescoreFrontier(state);
    }

    /// <summary>
    ///     Get the next batch of highest-priority candidates and remove them from the frontier.
    /// </summary>
    public List<FetchCandidate> GetNextBatch(UltraResearchState state, int batchSize)
    {
        RescoreFrontier(state);

        var batch = state.Frontier
            .OrderByDescending(c => c.Priority)
            .Take(batchSize)
            .ToList();

        foreach (var candidate in batch)
            state.Frontier.Remove(candidate);

        return batch;
    }

    private void RescoreFrontier(UltraResearchState state)
    {
        if (state.Frontier.Count == 0) return;

        var maxCitations = Math.Max(1, state.Frontier.Max(c => c.CitedByCount));
        var sentinelTopics = GetSentinelTopics(state);

        foreach (var candidate in state.Frontier)
        {
            var citedByScore = maxCitations > 0
                ? Math.Min(1.0, (double)candidate.CitedByCount / maxCitations)
                : 0.0;

            // Sentinel boost: does this candidate match sentinel-suggested queries?
            var sentinelScore = ComputeSentinelBoost(candidate, sentinelTopics);

            // Recency: arXiv IDs encode year (YYMM.XXXXX)
            var recencyScore = ComputeRecencyScore(candidate);

            // Entity overlap: approximate via source type
            var entityScore = candidate.Source switch
            {
                CandidateSource.Orphan => 0.8, // Cited by ingested docs → high overlap
                CandidateSource.Citation => 0.6,
                CandidateSource.SemanticScholar => 0.4,
                CandidateSource.Sentinel => 0.7,
                _ => 0.3
            };

            candidate.Priority =
                (citedByScore * WeightCitedBy) +
                (entityScore * WeightEntityOverlap) +
                (sentinelScore * WeightSentinelBoost) +
                (recencyScore * WeightRecency);
        }
    }

    private static HashSet<string> GetSentinelTopics(UltraResearchState state)
    {
        var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastCheckpoint = state.Checkpoints.LastOrDefault();
        if (lastCheckpoint == null) return topics;

        foreach (var query in lastCheckpoint.SuggestedQueries)
        {
            foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 3)
                    topics.Add(word);
            }
        }

        foreach (var gap in lastCheckpoint.IdentifiedGaps)
        {
            foreach (var word in gap.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 3)
                    topics.Add(word);
            }
        }

        return topics;
    }

    private static double ComputeSentinelBoost(FetchCandidate candidate, HashSet<string> sentinelTopics)
    {
        if (sentinelTopics.Count == 0 || string.IsNullOrEmpty(candidate.Title))
            return candidate.Source == CandidateSource.Sentinel ? 0.8 : 0.0;

        var titleWords = candidate.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = titleWords.Count(w => sentinelTopics.Contains(w));
        return Math.Min(1.0, (double)matches / Math.Max(1, sentinelTopics.Count) * 2);
    }

    private static double ComputeRecencyScore(FetchCandidate candidate)
    {
        if (candidate.Type != "arxiv") return 0.5; // Unknown recency for DOIs

        // arXiv IDs: YYMM.XXXXX → extract year
        var parts = candidate.Id.Split('.');
        if (parts.Length < 2 || parts[0].Length < 4) return 0.5;

        if (!int.TryParse(parts[0][..2], out var yy)) return 0.5;
        var year = yy < 90 ? 2000 + yy : 1900 + yy;
        var currentYear = DateTimeOffset.UtcNow.Year;
        var age = currentYear - year;

        return age switch
        {
            <= 1 => 1.0,
            <= 3 => 0.8,
            <= 5 => 0.6,
            <= 10 => 0.4,
            _ => 0.2
        };
    }

    private static (string? id, string type) ParseEntityName(string canonicalName, string entityType)
    {
        // Entity canonical names are like "doi:10.xxxx/yyyy" or "arxiv:2301.12345"
        if (entityType == "doi_reference")
        {
            var doi = canonicalName;
            if (doi.StartsWith("doi:", StringComparison.OrdinalIgnoreCase))
                doi = doi[4..];
            return string.IsNullOrEmpty(doi) ? (null, "doi") : (doi, "doi");
        }

        if (entityType == "arxiv_reference")
        {
            var arxivId = canonicalName;
            if (arxivId.StartsWith("arxiv:", StringComparison.OrdinalIgnoreCase))
                arxivId = arxivId[6..];
            return string.IsNullOrEmpty(arxivId) ? (null, "arxiv") : (arxivId, "arxiv");
        }

        return (null, "unknown");
    }
}
