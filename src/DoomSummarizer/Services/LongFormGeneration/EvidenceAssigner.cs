using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
/// Phase 3: Evidence Assignment (Deterministic — No LLM).
/// Assigns evidence segments to planned sections using embedding similarity,
/// salience scores, and article relevance. Greedy selection with MMR diversity
/// and deduplication.
/// </summary>
public static class EvidenceAssigner
{
    private const double ThemeSimilarityWeight = 0.60;
    private const double SalienceWeight = 0.25;
    private const double ArticleRelevanceWeight = 0.15;

    private const int MaxSegmentsPerArticlePerSection = 2;
    private const float DedupThreshold = 0.85f;
    private const float MmrDiversityThreshold = 0.70f;
    private const float OrphanSalienceThreshold = 0.7f;

    /// <summary>
    /// Assign evidence segments to sections based on embedding similarity + salience + relevance.
    /// Each section gets its own curated slice of the corpus.
    /// </summary>
    public static void AssignEvidence(DocumentPlan plan, EvidenceCorpus corpus, int contextTokenBudget = 5700)
    {
        // Evidence budget per section in chars
        var budgetChars = (int)(contextTokenBudget * 3.5);

        foreach (var (section, sectionIndex) in plan.Sections.Select((s, i) => (s, i)))
        {
            if (section.ThemeEmbedding == null) continue;

            // Score all unassigned segments for this section
            var scored = corpus.Segments
                .Where(s => !s.IsAssigned && s.Segment.Embedding != null)
                .Select(s => new
                {
                    Segment = s,
                    Score = ComputeScore(section.ThemeEmbedding, s),
                    ThemeSim = EmbeddingService.CosineSimilarity(section.ThemeEmbedding, s.Segment.Embedding!)
                })
                .OrderByDescending(s => s.Score)
                .ToList();

            var selected = new List<EvidenceSegment>();
            var selectedArticleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var totalChars = 0;

            foreach (var candidate in scored)
            {
                if (totalChars >= budgetChars) break;

                var seg = candidate.Segment;
                var segChars = seg.Segment.Text.Length;

                // MMR diversity: skip if article already has max segments AND theme similarity is low
                var articleUrl = seg.ArticleUrl;
                selectedArticleCounts.TryGetValue(articleUrl, out var articleCount);
                if (articleCount >= MaxSegmentsPerArticlePerSection && candidate.ThemeSim < MmrDiversityThreshold)
                    continue;

                // Dedup: skip if too similar to any already-selected segment
                if (IsDuplicateOf(seg, selected))
                    continue;

                // Budget check
                if (totalChars + segChars > budgetChars && selected.Count > 0)
                    continue;

                selected.Add(seg);
                seg.IsAssigned = true;
                seg.AssignedSection = sectionIndex;
                selectedArticleCounts[articleUrl] = articleCount + 1;
                totalChars += segChars;
            }

            section.AssignedEvidence = selected;
        }

        // Orphan rescue: unassigned high-salience segments go to best-matching section
        RescueOrphans(plan, corpus);
    }

    private static double ComputeScore(float[] themeEmbedding, EvidenceSegment seg)
    {
        var themeSim = EmbeddingService.CosineSimilarity(themeEmbedding, seg.Segment.Embedding!);
        var salience = seg.Segment.SalienceScore;
        var relevance = seg.ArticleRelevance;

        return ThemeSimilarityWeight * themeSim
             + SalienceWeight * salience
             + ArticleRelevanceWeight * relevance;
    }

    private static bool IsDuplicateOf(EvidenceSegment candidate, List<EvidenceSegment> selected)
    {
        if (candidate.Segment.Embedding == null) return false;

        foreach (var existing in selected)
        {
            if (existing.Segment.Embedding == null) continue;
            var sim = EmbeddingService.CosineSimilarity(candidate.Segment.Embedding, existing.Segment.Embedding);
            if (sim > DedupThreshold)
                return true;
        }
        return false;
    }

    private static void RescueOrphans(DocumentPlan plan, EvidenceCorpus corpus)
    {
        var orphans = corpus.Segments
            .Where(s => !s.IsAssigned && s.Segment.SalienceScore > OrphanSalienceThreshold && s.Segment.Embedding != null)
            .ToList();

        foreach (var orphan in orphans)
        {
            // Find best-matching section
            var bestSection = plan.Sections
                .Where(s => s.ThemeEmbedding != null)
                .OrderByDescending(s => EmbeddingService.CosineSimilarity(s.ThemeEmbedding!, orphan.Segment.Embedding!))
                .FirstOrDefault();

            if (bestSection != null)
            {
                orphan.IsAssigned = true;
                orphan.AssignedSection = plan.Sections.IndexOf(bestSection);
                bestSection.AssignedEvidence.Add(orphan);
            }
        }
    }
}
