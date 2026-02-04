using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
///     Phase 4: Section Generation (Main LLM).
///     Supports both sequential and parallel modes:
///     - Sequential: Full negative prompt support, slower
///     - Parallel: Intro → parallel body → conclusion, faster
///     Inspired by ComoRAG's cognitive approach:
///     - Dynamic memory workspace (covered concepts tracking)
///     - Progressive evidence consolidation (propositions)
///     - Iterative refinement (negative prompts prevent repetition)
///     LLM is called once per section — no compression calls.
/// </summary>
public static partial class SectionGenerator
{
    private const float DriftThreshold = 0.35f;
    private const int ConsecutiveDriftRecalibrationThreshold = 3;
    private const int MaxPropositionsPerSection = 15;
    private const float PropositionSimilarityThreshold = 0.85f;
    private const int MaxParallelSections = 3; // Limit concurrent LLM calls

    // Max segments to include in LLM context per section (reduces reasoning load)
    private const int MaxEvidenceSegmentsForLlm = 8;
    private const float TopSalienceThreshold = 0.5f;

    // Pattern to extract key concepts from generated content
    [GeneratedRegex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)*)\b")]
    private static partial Regex KeyConceptRx();

    /// <summary>
    ///     Generate all sections with optional parallelization.
    ///     Pattern: Intro (sequential) → Body (parallel) → Conclusion (sequential)
    ///     This balances speed with quality — intro sets context, body runs fast, conclusion wraps up.
    /// </summary>
    public static async Task GenerateAllSectionsAsync(
        DocumentPlan plan,
        EvidenceCorpus corpus,
        RunningSummary runningSummary,
        EntityContinuityTracker entityTracker,
        string query,
        string vibe,
        string vibePrompt,
        OllamaService ollama,
        Func<string, float[]> embedder,
        TemplateDefinition? templateDef,
        CancellationToken ct,
        bool parallel = true)
    {
        // Extract propositions from all evidence upfront (Dense-X Retrieval)
        var allPropositions = ExtractAllPropositions(corpus, embedder);
        var usedPropositions = new List<Proposition>();

        if (!parallel || plan.Sections.Count <= 3)
        {
            // Sequential mode for small documents or when disabled
            await GenerateSequentialAsync(plan, corpus, runningSummary, entityTracker,
                query, vibePrompt, ollama, embedder, templateDef, allPropositions, usedPropositions, ct);
            return;
        }

        // Parallel mode: Intro → Body (parallel) → Conclusion
        await GenerateParallelAsync(plan, corpus, runningSummary, entityTracker,
            query, vibePrompt, ollama, embedder, templateDef, allPropositions, ct);
    }

    /// <summary>
    ///     Parallel generation: Intro first, then body sections in parallel, then conclusion.
    /// </summary>
    private static async Task GenerateParallelAsync(
        DocumentPlan plan,
        EvidenceCorpus corpus,
        RunningSummary runningSummary,
        EntityContinuityTracker entityTracker,
        string query,
        string vibePrompt,
        OllamaService ollama,
        Func<string, float[]> embedder,
        TemplateDefinition? templateDef,
        List<Proposition> allPropositions,
        CancellationToken ct)
    {
        var sections = plan.Sections;
        if (sections.Count == 0) return;

        // Phase 1: Generate intro (first section) sequentially
        var introSection = sections[0];
        entityTracker.ScanEvidence(introSection, 0);
        var introProps = FilterPropositionsForSection(allPropositions, introSection, [], embedder);
        introSection.Propositions = introProps;

        var introPrompt = BuildSectionPrompt(introSection, plan, corpus, runningSummary, entityTracker,
            query, vibePrompt, 0, 0, templateDef, embedder);
        introSection.GeneratedContent = await ollama.GenerateAsync(introPrompt, null, 0.5, ct);
        introSection.CoveredConcepts = ExtractCoveredConcepts(introSection.GeneratedContent, introSection.Heading);
        runningSummary.RecordSection(introSection, 0);
        entityTracker.ScanGenerated(introSection.GeneratedContent, 0);

        // Phase 2: Generate body sections (middle) in parallel
        var bodyStartIdx = 1;
        var bodyEndIdx = sections.Count - 1; // Exclude last (conclusion)
        var bodySections = sections.Skip(bodyStartIdx).Take(bodyEndIdx - bodyStartIdx).ToList();

        if (bodySections.Count > 0)
        {
            // Build exclude topics from intro
            var introExcludes = introSection.CoveredConcepts;

            // Use semaphore to limit concurrent LLM calls
            using var semaphore = new SemaphoreSlim(MaxParallelSections);

            var bodyTasks = bodySections.Select(async (section, relativeIdx) =>
            {
                var sectionIdx = bodyStartIdx + relativeIdx;
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    entityTracker.ScanEvidence(section, sectionIdx);
                    section.ExcludeTopics = introExcludes.ToList(); // All body sections exclude intro topics

                    var props = FilterPropositionsForSection(allPropositions, section, [], embedder);
                    section.Propositions = props;

                    var prompt = BuildSectionPrompt(section, plan, corpus, runningSummary, entityTracker,
                        query, vibePrompt, sectionIdx, 0, templateDef, embedder);
                    section.GeneratedContent = await ollama.GenerateAsync(prompt, null, 0.5, ct);
                    section.CoveredConcepts = ExtractCoveredConcepts(section.GeneratedContent, section.Heading);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(bodyTasks);

            // Update running summary and entity tracker after all body sections complete
            foreach (var (section, relativeIdx) in bodySections.Select((s, i) => (s, i)))
            {
                var sectionIdx = bodyStartIdx + relativeIdx;
                runningSummary.RecordSection(section, sectionIdx);
                entityTracker.ScanGenerated(section.GeneratedContent ?? "", sectionIdx);
            }
        }

        // Phase 3: Generate conclusion (last section) sequentially
        if (sections.Count > 1)
        {
            var conclusionIdx = sections.Count - 1;
            var conclusionSection = sections[conclusionIdx];
            entityTracker.ScanEvidence(conclusionSection, conclusionIdx);

            // Conclusion excludes topics from all previous sections
            conclusionSection.ExcludeTopics = sections
                .Take(conclusionIdx)
                .SelectMany(s => s.CoveredConcepts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var conclusionProps = FilterPropositionsForSection(allPropositions, conclusionSection, [], embedder);
            conclusionSection.Propositions = conclusionProps;

            var conclusionPrompt = BuildSectionPrompt(conclusionSection, plan, corpus, runningSummary, entityTracker,
                query, vibePrompt, conclusionIdx, 0, templateDef, embedder);
            conclusionSection.GeneratedContent = await ollama.GenerateAsync(conclusionPrompt, null, 0.5, ct);
            conclusionSection.CoveredConcepts =
                ExtractCoveredConcepts(conclusionSection.GeneratedContent, conclusionSection.Heading);
            runningSummary.RecordSection(conclusionSection, conclusionIdx);
            entityTracker.ScanGenerated(conclusionSection.GeneratedContent, conclusionIdx);
        }
    }

    /// <summary>
    ///     Sequential generation with full negative prompt support (original behavior).
    /// </summary>
    private static async Task GenerateSequentialAsync(
        DocumentPlan plan,
        EvidenceCorpus corpus,
        RunningSummary runningSummary,
        EntityContinuityTracker entityTracker,
        string query,
        string vibePrompt,
        OllamaService ollama,
        Func<string, float[]> embedder,
        TemplateDefinition? templateDef,
        List<Proposition> allPropositions,
        List<Proposition> usedPropositions,
        CancellationToken ct)
    {
        var consecutiveDrifts = 0;

        for (var i = 0; i < plan.Sections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var section = plan.Sections[i];

            // Scan evidence for entity mentions before generation
            entityTracker.ScanEvidence(section, i);

            // Build ExcludeTopics from previous sections' covered concepts
            if (i > 0)
                section.ExcludeTopics = plan.Sections
                    .Take(i)
                    .SelectMany(s => s.CoveredConcepts)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            // Filter propositions for this section (theme-relevant, not yet used)
            var sectionPropositions = FilterPropositionsForSection(
                allPropositions, section, usedPropositions, embedder);
            section.Propositions = sectionPropositions;

            // Build the prompt with propositions and negative prompts
            var prompt = BuildSectionPrompt(
                section, plan, corpus, runningSummary, entityTracker,
                query, vibePrompt, i, consecutiveDrifts, templateDef, embedder);

            // Generate via main LLM
            var content = await ollama.GenerateAsync(prompt, null, 0.5, ct);
            section.GeneratedContent = content;

            // Extract covered concepts from generated content (for negative prompts)
            section.CoveredConcepts = ExtractCoveredConcepts(content, section.Heading);

            // Mark used propositions (ComoRAG memory consolidation)
            usedPropositions.AddRange(sectionPropositions);

            // Update running summary with top-salience evidence (deterministic, no LLM)
            runningSummary.RecordSection(section, i);

            // Update entity tracker with generated text
            entityTracker.ScanGenerated(content, i);

            // Drift detection (deterministic)
            if (plan.ThemeEmbedding != null)
            {
                var sectionEmbedding = embedder(content.Length > 1000 ? content[..1000] : content);
                var driftScore = VectorMath.CosineSimilarity(sectionEmbedding, plan.ThemeEmbedding);

                if (driftScore < DriftThreshold)
                {
                    consecutiveDrifts++;

                    // Recalibrate if too many consecutive drifts
                    if (consecutiveDrifts >= ConsecutiveDriftRecalibrationThreshold && i >= 2)
                    {
                        // Recalibrate theme embedding from first 2 sections
                        var recalText = string.Join(" ",
                            plan.Sections.Take(2)
                                .Select(s => s.GeneratedContent)
                                .OfType<string>()
                                .Select(c => c[..Math.Min(500, c.Length)]));
                        plan.ThemeEmbedding = embedder(recalText);
                        consecutiveDrifts = 0;
                    }
                }
                else
                {
                    consecutiveDrifts = 0;
                }
            }
        }
    }

    /// <summary>
    ///     Extract propositions from all evidence segments (Dense-X Retrieval).
    ///     Computes embeddings for semantic matching.
    /// </summary>
    private static List<Proposition> ExtractAllPropositions(
        EvidenceCorpus corpus,
        Func<string, float[]> embedder)
    {
        var allPropositions = PropositionExtractor.ExtractFromCorpus(corpus);

        // Compute embeddings for propositions (needed for semantic dedup)
        foreach (var prop in allPropositions)
            if (prop.Text.Length >= 20)
                prop.Embedding = embedder(prop.Text);

        return allPropositions;
    }

    /// <summary>
    ///     Filter propositions for a specific section:
    ///     1. Match by theme embedding similarity
    ///     2. Exclude already-used propositions (cross-section dedup)
    ///     3. Take top-K most relevant
    /// </summary>
    private static List<Proposition> FilterPropositionsForSection(
        List<Proposition> allPropositions,
        PlannedSection section,
        List<Proposition> usedPropositions,
        Func<string, float[]> embedder)
    {
        // Remove propositions already used in previous sections
        var available = PropositionExtractor.RemoveUsedPropositions(
            allPropositions, usedPropositions);

        // Filter for section theme relevance
        var filtered = PropositionExtractor.FilterForSection(
            available, section);

        return filtered;
    }

    /// <summary>
    ///     Extract key concepts from generated content for negative prompts.
    ///     Uses named entity patterns and section-specific terms.
    /// </summary>
    private static List<string> ExtractCoveredConcepts(string content, string heading)
    {
        var concepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add heading keywords (these are definitely covered)
        var headingWords = heading.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Select(w => w.Trim(',', '.', ':', ';'));
        foreach (var word in headingWords)
            concepts.Add(word);

        // Extract proper nouns / named entities from content
        var matches = KeyConceptRx().Matches(content);
        foreach (Match match in matches)
        {
            var concept = match.Groups[1].Value;
            // Only keep multi-word concepts or significant single words
            if (concept.Contains(' ') || concept.Length > 5)
                concepts.Add(concept);
        }

        // Extract technical terms (often in specific patterns)
        var technicalPatterns = new[] { "HTMX", "ASP.NET", "JavaScript", "HTML", "CSS", "API", "HTTP", "DOM" };
        foreach (var pattern in technicalPatterns)
            if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                concepts.Add(pattern);

        return concepts.Take(15).ToList(); // Limit to prevent prompt bloat
    }

    private static string BuildSectionPrompt(
        PlannedSection section,
        DocumentPlan plan,
        EvidenceCorpus corpus,
        RunningSummary runningSummary,
        EntityContinuityTracker entityTracker,
        string query,
        string vibePrompt,
        int sectionIndex,
        int consecutiveDrifts,
        TemplateDefinition? templateDef,
        Func<string, float[]> embedder)
    {
        // Filter to top-salience segments to reduce LLM reasoning load
        // Only include the most salient evidence — LLM doesn't need to sift through noise
        var topEvidence = section.AssignedEvidence
            .Where(e => e.Segment.SalienceScore >= TopSalienceThreshold)
            .OrderByDescending(e => e.Segment.SalienceScore)
            .Take(MaxEvidenceSegmentsForLlm)
            .ToList();

        // If we filtered too aggressively, include at least some evidence
        if (topEvidence.Count < 3 && section.AssignedEvidence.Count > 0)
            topEvidence = section.AssignedEvidence
                .OrderByDescending(e => e.Segment.SalienceScore)
                .Take(Math.Min(MaxEvidenceSegmentsForLlm, section.AssignedEvidence.Count))
                .ToList();

        // Build evidence block from filtered segments
        var evidence = new StringBuilder();
        var articleGroups = topEvidence
            .GroupBy(e => e.ArticleTitle)
            .OrderByDescending(g => g.Max(e => e.Segment.SalienceScore));

        foreach (var group in articleGroups)
        {
            var first = group.First();
            evidence.AppendLine($"### {first.ArticleTitle}");
            if (!string.IsNullOrEmpty(first.ArticleUrl))
                evidence.AppendLine($"URL: {first.ArticleUrl}");

            foreach (var seg in group.OrderByDescending(e => e.Segment.SalienceScore))
            {
                var marker = seg.Segment.SalienceScore > 0.8 ? "[KEY] " : "";
                evidence.AppendLine($"  {marker}{seg.Segment.Text}");
            }

            evidence.AppendLine();
        }

        // Running summary (deterministic — top-salience segments from previous sections)
        var summaryContext = runningSummary.Build();

        // Entity continuity guidance (deterministic — string matching)
        var entityGuidance = entityTracker.BuildGuidance(sectionIndex);

        // Drift correction
        var driftGuidance = "";
        if (consecutiveDrifts > 0 && plan.ThemeDescription != null)
            driftGuidance = $"IMPORTANT: Return focus to the main theme: {plan.ThemeDescription}";

        // Timeline extra
        var timelineExtra = plan.QueryType == QueryType.Timeline
            ? """
              Structure as a timeline. For key milestones use:
              **Year — What happened** — Why it mattered (cite source)
              Use concrete names, dates, paper titles, and model names.
              """
            : "";

        // Template section prompt
        var templateSections = templateDef?.Sections;
        var sectionDef = templateSections != null && sectionIndex < templateSections.Count
            ? templateSections[sectionIndex]
            : null;
        var sectionFocus = sectionDef?.Prompt ?? section.Notes ?? "";
        // Use quality-adjusted target words (EffectiveTargetWords respects evidence quality)
        var targetWords = sectionDef?.TargetWords ?? section.EffectiveTargetWords;
        var wordRange = $"{Math.Max(100, targetWords - 100)}-{targetWords + 100}";

        // Format propositions as bullet points (Dense-X style)
        var propositionsText = section.Propositions.Count > 0
            ? PropositionExtractor.FormatAsBullets(section.Propositions)
            : "";

        // Build negative prompts from excluded topics (ComoRAG memory)
        var excludeTopicsText = section.ExcludeTopics.Count > 0
            ? $"Do NOT discuss these topics (already covered): {string.Join(", ", section.ExcludeTopics)}"
            : "";

        // Evidence density check: provide guidance when evidence is sparse
        var evidenceDensityGuidance = "";
        var sourceCount = topEvidence.Select(e => e.ArticleUrl).Distinct().Count();
        var avgSalience = topEvidence.Count > 0 ? topEvidence.Average(e => e.Segment.SalienceScore) : 0;
        var highQualitySegments = topEvidence.Count(e => e.Segment.SalienceScore > 0.7);

        if (topEvidence.Count <= 2 || sourceCount <= 1 || highQualitySegments == 0)
            // Sparse evidence: be honest, keep it short
            evidenceDensityGuidance = """
                                      EVIDENCE NOTE: Limited concrete evidence available for this section.
                                      Write a shorter, focused section (100-150 words max).
                                      Only include claims directly supported by the evidence above.
                                      If you cannot provide specific technical details, acknowledge the scope is limited.
                                      Do NOT pad with generic statements or conceptual filler.
                                      """;
        else if (avgSalience < 0.6 || highQualitySegments < 3)
            // Medium evidence: be concise
            evidenceDensityGuidance = """
                                      EVIDENCE NOTE: Moderate evidence density.
                                      Prioritize concrete technical details over general concepts.
                                      Keep section focused and avoid padding.
                                      """;

        return PromptTemplateService.Render("longform-section", new Dictionary<string, object?>
        {
            ["HEADING"] = section.Heading,
            ["QUERY"] = query,
            ["FOCUS"] = !string.IsNullOrEmpty(sectionFocus) ? $"Focus: {sectionFocus}" : "",
            ["RUNNING_SUMMARY"] = summaryContext,
            ["ENTITY_GUIDANCE"] = entityGuidance,
            ["DRIFT_GUIDANCE"] = driftGuidance,
            ["EXCLUDE_TOPICS"] = excludeTopicsText,
            ["EVIDENCE_DENSITY"] = evidenceDensityGuidance,
            ["VIBE_PROMPT"] = vibePrompt,
            ["TIMELINE_EXTRA"] = timelineExtra,
            ["PROPOSITIONS"] = propositionsText,
            ["EVIDENCE"] = evidence.ToString(),
            ["WORD_RANGE"] = wordRange
        });
    }
}