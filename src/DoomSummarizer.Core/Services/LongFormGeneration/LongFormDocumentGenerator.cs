using DoomSummarizer.Models;
using DoomSummarizer.Models.LongFormGeneration;
namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
/// Top-level orchestrator for long-form document generation.
/// Chains six phases:
///   1. Evidence Preparation (deterministic)
///   2. Document Planning (sentinel LLM — 1 call)
///   3. Evidence Assignment (deterministic)
///   4. Section Generation (main LLM — N calls for N sections)
///   5. Output Validation (deterministic)
///   6. Assembly (deterministic)
///
/// LLM calls: 1 (outline) + N+2 (intro + sections + conclusion) = N+3 total.
/// Everything else is deterministic using existing segment embeddings/salience.
/// </summary>
public class LongFormDocumentGenerator
{
    private readonly OllamaService _ollama;
    private readonly ArticleProcessor _articleProcessor;

    public LongFormDocumentGenerator(
        OllamaService ollama,
        ArticleProcessor articleProcessor)
    {
        _ollama = ollama;
        _articleProcessor = articleProcessor;
    }

    /// <summary>
    /// Generate a long-form document from analyzed items.
    /// Returns BlogArticleResult for compatibility with existing rendering pipeline.
    /// </summary>
    /// <param name="parallel">Enable parallel section generation (faster but less cross-section coherence)</param>
    public async Task<BlogArticleResult> GenerateAsync(
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> analyzedItems,
        List<ContentItem> contentItems,
        string query,
        string vibe,
        string vibePrompt,
        QueryType queryType,
        TemplateDefinition? templateDef = null,
        CancellationToken ct = default,
        bool parallel = true)
    {
        var today = DateTime.Now.ToString("MMMM d, yyyy");

        // ── Phase 1: Evidence Preparation (deterministic) ──
        System.Diagnostics.Debug.WriteLine("Long-form: Phase 1 -- Preparing evidence corpus...");

        var topItems = contentItems
            .OrderByDescending(i => i.RelevanceScore)
            .Take(20)
            .ToList();

        var processedArticles = await _articleProcessor.ProcessBatchAsync(topItems, null, ct);

        var corpus = EvidencePreparationService.BuildCorpus(
            processedArticles, contentItems, analyzedItems);

        System.Diagnostics.Debug.WriteLine(
            $"  Corpus: {corpus.Segments.Count} segments from {corpus.Articles.Count} articles, " +
            $"{corpus.GlobalEntities.Count} entities");

        // ── Phase 2: Document Planning (sentinel LLM -- 1 call) ──
        System.Diagnostics.Debug.WriteLine("Long-form: Phase 2 -- Planning document structure...");

        var plan = await DocumentPlanner.CreatePlanAsync(
            corpus, query, queryType, vibe, _ollama, _articleProcessor.Embed, templateDef, ct);

        System.Diagnostics.Debug.WriteLine(
            $"  Plan: \"{plan.Title}\" -- {plan.Sections.Count} sections");

        // ── Phase 3: Evidence Assignment (deterministic) ──
        System.Diagnostics.Debug.WriteLine("Long-form: Phase 3 -- Assigning evidence to sections...");

        var contextBudget = _ollama.Router?.GetContextSize("main")
            ?? 5700;
        // Reserve ~1500 tokens for prompt overhead, convert rest to evidence budget
        var evidenceTokenBudget = Math.Max(2000, contextBudget - 1500);

        // Embed query for cohesion checking (evidence must relate to query, not just section)
        var queryEmbedding = _articleProcessor.Embed(query);
        EvidenceAssigner.AssignEvidence(plan, corpus, evidenceTokenBudget, queryEmbedding);

        foreach (var section in plan.Sections)
        {
            System.Diagnostics.Debug.WriteLine(
                $"  [{section.Heading}]: {section.AssignedEvidence.Count} segments " +
                $"from {section.SourceUrls.Count} sources");
        }

        // ── Phase 4: Section Generation (main LLM -- N+2 calls) ──
        System.Diagnostics.Debug.WriteLine("Long-form: Phase 4 -- Generating sections...");

        // Initialize trackers
        var runningSummary = new RunningSummary();
        var entityTracker = new EntityContinuityTracker();
        entityTracker.Initialize(corpus);

        // Generate introduction (LLM call)
        var introduction = await GenerateIntroductionAsync(
            plan, corpus, query, vibePrompt, today, templateDef, ct);

        // Generate body sections (parallel or sequential based on setting)
        await SectionGenerator.GenerateAllSectionsAsync(
            plan, corpus, runningSummary, entityTracker,
            query, vibe, vibePrompt, _ollama, _articleProcessor.Embed, templateDef, ct,
            parallel: parallel);

        // Generate conclusion (LLM call)
        var lastSection = plan.Sections.LastOrDefault();
        var previousContext = "";
        if (lastSection?.GeneratedContent != null)
        {
            var sentences = lastSection.GeneratedContent.Split('.', StringSplitOptions.RemoveEmptyEntries);
            previousContext = sentences.Length >= 2
                ? string.Join(".", sentences[^2..]).Trim() + "."
                : lastSection.GeneratedContent.Length > 200
                    ? lastSection.GeneratedContent[^200..]
                    : lastSection.GeneratedContent;
        }

        var conclusion = await GenerateConclusionAsync(
            plan, vibePrompt, previousContext, templateDef, ct);

        // ── Phase 5: Output Validation (deterministic) ──
        System.Diagnostics.Debug.WriteLine("Long-form: Phase 5 -- Validating output...");

        var validation = OutputValidator.Validate(plan, corpus, _articleProcessor.Embed);

        System.Diagnostics.Debug.WriteLine(
            $"  Grounding: {validation.OverallGroundingScore:P0}, " +
            $"URLs verified: {validation.VerifiedUrlCount}, " +
            $"Hallucinated: {validation.HallucinatedUrls.Count}, " +
            $"Ungrounded entities: {validation.UngroundedEntities.Count}");

        if (!validation.PassesValidation)
        {
            System.Diagnostics.Debug.WriteLine("  Applying auto-fix for validation issues...");
            OutputValidator.AutoFix(plan, validation, corpus);
        }

        // ── Phase 6: Assembly (deterministic) ──
        System.Diagnostics.Debug.WriteLine("Long-form: Phase 6 -- Assembling document...");

        return DocumentAssembler.Assemble(plan, introduction, conclusion, validation, corpus);
    }

    private async Task<string> GenerateIntroductionAsync(
        DocumentPlan plan,
        EvidenceCorpus corpus,
        string query,
        string vibePrompt,
        string today,
        TemplateDefinition? templateDef,
        CancellationToken ct)
    {
        // Build intro evidence from top-salience segments across the corpus
        // Filter out bio/about content that shouldn't appear in technical intros
        var introEvidence = new System.Text.StringBuilder();
        var topSegments = corpus.Segments
            .Where(s => s.ContentTypeWeight >= 0.5f) // Exclude bio content (weight 0.2)
            .OrderByDescending(s => s.Segment.SalienceScore * s.ContentTypeWeight)
            .Take(5);
        foreach (var seg in topSegments)
        {
            introEvidence.AppendLine($"- {seg.ArticleTitle}: {seg.Segment.Text}");
        }

        var introExtra = templateDef?.Introduction?.Prompt
            ?? (plan.QueryType == QueryType.Timeline
                ? "Include the time span covered (e.g., 'from the 1920s to today')."
                : "");
        var introWords = templateDef?.Introduction?.TargetWords ?? 100;

        var introPrompt = PromptTemplateService.Render("blog-intro", new Dictionary<string, object?>
        {
            ["INTRO_WORDS"] = introWords.ToString(),
            ["TITLE"] = plan.Title,
            ["QUERY"] = query,
            ["TODAY"] = today,
            ["VIBE_PROMPT"] = vibePrompt,
            ["INTRO_EXTRA"] = introExtra,
            ["EVIDENCE"] = introEvidence.ToString()
        });

        return await _ollama.GenerateAsync(introPrompt, null, 0.5, ct);
    }

    private async Task<string> GenerateConclusionAsync(
        DocumentPlan plan,
        string vibePrompt,
        string previousContext,
        TemplateDefinition? templateDef,
        CancellationToken ct)
    {
        var conclusionExtra = templateDef?.Conclusion?.Prompt ?? "";
        var conclusionWords = templateDef?.Conclusion?.TargetWords ?? 80;

        var conclusionPrompt = PromptTemplateService.Render("blog-conclusion", new Dictionary<string, object?>
        {
            ["CONCLUSION_WORDS"] = conclusionWords.ToString(),
            ["TITLE"] = plan.Title,
            ["CONCLUSION_ANGLE"] = plan.ConclusionAngle ?? "forward-looking insights",
            ["VIBE_PROMPT"] = vibePrompt,
            ["PREVIOUS_CONTEXT"] = previousContext,
            ["CONCLUSION_EXTRA"] = conclusionExtra
        });

        return await _ollama.GenerateAsync(conclusionPrompt, null, 0.5, ct);
    }
}
