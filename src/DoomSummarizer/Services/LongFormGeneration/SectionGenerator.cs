using System.Text;
using DoomSummarizer.Models;
using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
/// Phase 4: Section Generation (Main LLM, Sequential).
/// For each section, builds evidence context from assigned segments and
/// generates text via the main LLM. Drift detection uses cosine similarity
/// to the plan's theme embedding.
/// LLM is called once per section — no compression calls.
/// </summary>
public static class SectionGenerator
{
    private const float DriftThreshold = 0.35f;
    private const int ConsecutiveDriftRecalibrationThreshold = 3;

    /// <summary>
    /// Generate all sections sequentially.
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
        CancellationToken ct)
    {
        var consecutiveDrifts = 0;

        for (var i = 0; i < plan.Sections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var section = plan.Sections[i];

            // Scan evidence for entity mentions before generation
            entityTracker.ScanEvidence(section, i);

            // Build the prompt
            var prompt = BuildSectionPrompt(
                section, plan, corpus, runningSummary, entityTracker,
                query, vibePrompt, i, consecutiveDrifts, templateDef, embedder);

            // Generate via main LLM
            var content = await ollama.GenerateAsync(prompt, null, 0.5, ct);
            section.GeneratedContent = content;

            // Update running summary with top-salience evidence (deterministic, no LLM)
            runningSummary.RecordSection(section, i);

            // Update entity tracker with generated text
            entityTracker.ScanGenerated(content, i);

            // Drift detection (deterministic)
            if (plan.ThemeEmbedding != null)
            {
                var sectionEmbedding = embedder(content.Length > 1000 ? content[..1000] : content);
                var driftScore = EmbeddingService.CosineSimilarity(sectionEmbedding, plan.ThemeEmbedding);

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
        // Build evidence block from assigned segments
        var evidence = new StringBuilder();
        var articleGroups = section.AssignedEvidence
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
        var targetWords = sectionDef?.TargetWords ?? section.TargetWords;
        var wordRange = $"{Math.Max(100, targetWords - 100)}-{targetWords + 100}";

        return PromptTemplateService.Render("longform-section", new Dictionary<string, object?>
        {
            ["HEADING"] = section.Heading,
            ["QUERY"] = query,
            ["FOCUS"] = !string.IsNullOrEmpty(sectionFocus) ? $"Focus: {sectionFocus}" : "",
            ["RUNNING_SUMMARY"] = summaryContext,
            ["ENTITY_GUIDANCE"] = entityGuidance,
            ["DRIFT_GUIDANCE"] = driftGuidance,
            ["VIBE_PROMPT"] = vibePrompt,
            ["TIMELINE_EXTRA"] = timelineExtra,
            ["EVIDENCE"] = evidence.ToString(),
            ["WORD_RANGE"] = wordRange
        });
    }
}
