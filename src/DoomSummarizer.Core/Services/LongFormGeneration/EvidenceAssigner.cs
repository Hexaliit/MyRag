using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
/// Phase 3: Evidence Assignment (Deterministic — No LLM).
/// Assigns evidence segments to planned sections using embedding similarity,
/// salience scores, and article relevance. Greedy selection with MMR diversity
/// and deduplication.
///
/// Quality-aware constraints:
/// - Minimum salience threshold filters low-quality segments upfront
/// - Per-section evidence quality adjusts target word count
/// - Top-K per document limits reduces LLM context noise
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

    // Quality-aware thresholds
    private const float MinSalienceThreshold = 0.35f;      // Skip low-salience segments entirely
    private const int MaxSegmentsPerSection = 12;          // Cap segments to reduce LLM load
    private const float StrongEvidenceThreshold = 0.6f;    // Salience above this = strong
    private const float WeakEvidenceThreshold = 0.45f;     // Salience below this = weak
    private const float MinThemeSimilarity = 0.45f;        // Minimum theme similarity to include evidence (raised for tighter focus)

    // Cross-section flow control
    private const float CrossSectionDedupThreshold = 0.80f; // Higher than within-section to allow related content
    private const float IntroDepthPenalty = 0.3f;           // Penalize deep/technical content in intro sections
    private const float LaterSectionBonus = 0.15f;          // Bonus for detailed content in later sections
    private const float BioSegmentPenalty = 0.5f;           // Penalty for segments containing bio content

    // Segment-level bio indicators (person-focused content)
    private static readonly string[] SegmentBioIndicators =
    [
        "my experience", "my background", "i have worked", "years of experience",
        "head of engineering", "cto", "chief technology officer", "program manager",
        "leading teams", "my work", "i've built", "i've led", "my role",
        "as a developer", "as an engineer", "as a consultant",
        "my career", "i joined", "i left", "i started"
    ];

    // Topic cohesion: evidence must relate to both section AND query
    private const float MinQueryCohesion = 0.30f;  // Minimum similarity to query embedding

    /// <summary>
    /// Assign evidence segments to sections based on embedding similarity + salience + relevance.
    /// Each section gets its own curated slice of the corpus.
    /// Quality-aware: filters low-salience, caps per-section, adjusts target words.
    /// Flow-aware: tracks cross-section content to avoid repetition and ensure progression.
    /// Cohesion-aware: filters evidence that doesn't relate to overall query topic.
    /// </summary>
    public static void AssignEvidence(DocumentPlan plan, EvidenceCorpus corpus, int contextTokenBudget = 5700, float[]? queryEmbedding = null)
    {
        // Evidence budget per section in chars
        var budgetChars = (int)(contextTokenBudget * 3.5);

        // Pre-filter: only consider segments above minimum salience threshold
        // This reduces noise and gives LLM higher-quality evidence to reason about
        var eligibleSegments = corpus.Segments
            .Where(s => s.Segment.Embedding != null && s.Segment.SalienceScore >= MinSalienceThreshold)
            .ToList();

        // Track all assigned segments across sections for cross-section deduplication
        var globalAssigned = new List<EvidenceSegment>();

        // Determine section roles based on position
        var totalSections = plan.Sections.Count;

        foreach (var (section, sectionIndex) in plan.Sections.Select((s, i) => (s, i)))
        {
            if (section.ThemeEmbedding == null) continue;

            // Section role: intro (first 20%), body (middle 60%), conclusion (last 20%)
            var sectionPosition = (float)sectionIndex / Math.Max(1, totalSections - 1);
            var isIntroSection = sectionPosition < 0.25f;
            var isLaterSection = sectionPosition > 0.6f;

            // Score all unassigned segments for this section
            var scored = eligibleSegments
                .Where(s => !s.IsAssigned)
                .Select(s => new
                {
                    Segment = s,
                    BaseScore = ComputeScore(section.ThemeEmbedding, s),
                    ThemeSim = VectorMath.CosineSimilarity(section.ThemeEmbedding, s.Segment.Embedding!),
                    QuerySim = queryEmbedding != null
                        ? VectorMath.CosineSimilarity(queryEmbedding, s.Segment.Embedding!)
                        : 1.0f, // If no query embedding, skip this check
                    IsDeepContent = IsDetailedContent(s.Segment.Text)
                })
                // COHESION GATE: Evidence must relate to overall query, not just section
                // This prevents drift into loosely-related topics
                .Where(s => s.QuerySim >= MinQueryCohesion)
                .Select(s => new
                {
                    s.Segment,
                    // Apply flow-based score adjustments
                    Score = s.BaseScore
                        + (isIntroSection && s.IsDeepContent ? -IntroDepthPenalty : 0)
                        + (isLaterSection && s.IsDeepContent ? LaterSectionBonus : 0),
                    s.ThemeSim
                })
                .OrderByDescending(s => s.Score)
                .ToList();

            var selected = new List<EvidenceSegment>();
            var selectedArticleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var totalChars = 0;

            foreach (var candidate in scored)
            {
                // Cap segments per section to reduce LLM context load
                if (selected.Count >= MaxSegmentsPerSection) break;
                if (totalChars >= budgetChars) break;

                // HARD GATE: Skip evidence below minimum theme relevance
                // This prevents off-topic content from entering the LLM context
                if (candidate.ThemeSim < MinThemeSimilarity)
                    continue;

                var seg = candidate.Segment;
                var segChars = seg.Segment.Text.Length;

                // MMR diversity: skip if article already has max segments AND theme similarity is low
                var articleUrl = seg.ArticleUrl;
                selectedArticleCounts.TryGetValue(articleUrl, out var articleCount);
                if (articleCount >= MaxSegmentsPerArticlePerSection && candidate.ThemeSim < MmrDiversityThreshold)
                    continue;

                // Dedup within section: skip if too similar to any already-selected segment
                if (IsDuplicateOf(seg, selected, DedupThreshold))
                    continue;

                // Cross-section dedup: skip if too similar to content in previous sections
                // This prevents the same facts/quotes from appearing multiple times
                if (IsDuplicateOf(seg, globalAssigned, CrossSectionDedupThreshold))
                    continue;

                // Budget check
                if (totalChars + segChars > budgetChars && selected.Count > 0)
                    continue;

                selected.Add(seg);
                globalAssigned.Add(seg);
                seg.IsAssigned = true;
                seg.AssignedSection = sectionIndex;
                selectedArticleCounts[articleUrl] = articleCount + 1;
                totalChars += segChars;
            }

            section.AssignedEvidence = selected;

            // Compute evidence quality and adjust target word count
            AdjustTargetWordsForQuality(section);
        }

        // Orphan rescue: unassigned high-salience segments go to best-matching section
        RescueOrphans(plan, corpus);
    }

    /// <summary>
    /// Detect if content is detailed/technical (code, specific implementations, etc.)
    /// vs. overview/conceptual content better suited for introductions.
    /// </summary>
    private static bool IsDetailedContent(string text)
    {
        // Indicators of detailed/technical content
        var detailIndicators = new[]
        {
            "```",           // Code blocks
            "=>",            // Lambda/arrow syntax
            "class ",        // Class definitions
            "function ",     // Function definitions
            "async ",        // Async patterns
            "await ",        // Await patterns
            "var ",          // Variable declarations
            "const ",        // Const declarations
            "import ",       // Import statements
            "using ",        // Using statements
            "version ",      // Version numbers
            " v1.", " v2.",  // Version patterns
            "npm install",   // Package commands
            "dotnet add",    // Package commands
            "Step 1", "Step 2", // Numbered steps
        };

        var lowerText = text.ToLowerInvariant();
        var matches = detailIndicators.Count(ind => lowerText.Contains(ind.ToLowerInvariant()));

        // Also check for high density of technical terms
        var techTermDensity = CountTechTerms(text) / (float)Math.Max(1, text.Split(' ').Length);

        return matches >= 2 || techTermDensity > 0.15f;
    }

    private static int CountTechTerms(string text)
    {
        var techTerms = new[] { "api", "http", "json", "xml", "sql", "html", "css", "dom", "ajax", "rest" };
        var lowerText = text.ToLowerInvariant();
        return techTerms.Count(t => lowerText.Contains(t));
    }

    /// <summary>
    /// Adjust section's target word count based on evidence quality.
    /// Strong evidence → full word count. Weak evidence → reduced (prevent hallucination).
    /// </summary>
    private static void AdjustTargetWordsForQuality(PlannedSection section)
    {
        if (section.AssignedEvidence.Count == 0)
        {
            section.AdjustedTargetWords = 100; // Minimal if no evidence
            return;
        }

        var avgSalience = section.AssignedEvidence.Average(e => e.Segment.SalienceScore);
        var sourceCount = section.AssignedEvidence.Select(e => e.ArticleUrl).Distinct().Count();
        var segmentCount = section.AssignedEvidence.Count;

        // Quality multiplier: 0.5 to 1.0 based on evidence strength
        double qualityMultiplier;
        if (avgSalience >= StrongEvidenceThreshold && sourceCount >= 2 && segmentCount >= 4)
        {
            // Strong evidence: full word count
            qualityMultiplier = 1.0;
        }
        else if (avgSalience >= WeakEvidenceThreshold && segmentCount >= 2)
        {
            // Medium evidence: slight reduction
            qualityMultiplier = 0.8;
        }
        else
        {
            // Weak evidence: significant reduction to prevent hallucination
            qualityMultiplier = 0.6;
        }

        var originalTarget = section.TargetWords;
        section.AdjustedTargetWords = Math.Max(100, (int)(originalTarget * qualityMultiplier));
    }

    private static double ComputeScore(float[] themeEmbedding, EvidenceSegment seg)
    {
        var themeSim = VectorMath.CosineSimilarity(themeEmbedding, seg.Segment.Embedding!);
        var salience = seg.Segment.SalienceScore;
        var relevance = seg.ArticleRelevance;

        // Base score from theme similarity, salience, and relevance
        var baseScore = ThemeSimilarityWeight * themeSim
                      + SalienceWeight * salience
                      + ArticleRelevanceWeight * relevance;

        // Apply content type penalty from article-level classification
        // ContentTypeWeight is 1.0 for tech docs, 0.2 for bio content
        var score = baseScore * seg.ContentTypeWeight;

        // Additional segment-level bio detection
        // Even tech articles can have bio snippets that should be penalized
        if (ContainsBioContent(seg.Segment.Text))
            score *= BioSegmentPenalty;

        return score;
    }

    /// <summary>
    /// Detect if a segment contains biographical/personal content.
    /// This catches bio references even in technical articles.
    /// </summary>
    private static bool ContainsBioContent(string text)
    {
        var lowerText = text.ToLowerInvariant();
        var matches = SegmentBioIndicators.Count(indicator => lowerText.Contains(indicator));
        return matches >= 2; // Need at least 2 indicators to avoid false positives
    }

    private static bool IsDuplicateOf(EvidenceSegment candidate, List<EvidenceSegment> selected, float threshold = DedupThreshold)
    {
        if (candidate.Segment.Embedding == null) return false;

        foreach (var existing in selected)
        {
            if (existing.Segment.Embedding == null) continue;
            var sim = VectorMath.CosineSimilarity(candidate.Segment.Embedding, existing.Segment.Embedding);
            if (sim > threshold)
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
                .OrderByDescending(s => VectorMath.CosineSimilarity(s.ThemeEmbedding!, orphan.Segment.Embedding!))
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
