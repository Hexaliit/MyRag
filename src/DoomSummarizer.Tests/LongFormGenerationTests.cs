using DoomSummarizer.Models;
using DoomSummarizer.Models.LongFormGeneration;
using DoomSummarizer.Services;
using DoomSummarizer.Services.LongFormGeneration;
using Mostlylucid.DocSummarizer.Models;
using LfValidationResult = DoomSummarizer.Models.LongFormGeneration.ValidationResult;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for the deterministic phases of the long-form document generation pipeline.
///     Phases 1, 3, 5, 6 and helper services are all deterministic and testable without LLM.
/// </summary>
public class LongFormGenerationTests
{
    #region Integration: Full Deterministic Pipeline

    [Fact]
    public void FullDeterministicPipeline_ProducesCoherentOutput()
    {
        // Phase 1: Build corpus
        var corpus = BuildTestCorpus();
        corpus.Segments.Should().HaveCount(9);
        corpus.Articles.Should().HaveCount(3);
        corpus.GlobalCentroid.Should().NotBeNull();
        corpus.GlobalEntities.Should().NotBeEmpty();

        // Phase 2: Build plan (deterministic — no sentinel needed for test)
        var plan = BuildTestPlan(corpus);
        plan.Sections.Should().HaveCount(3);

        // Phase 3: Assign evidence
        EvidenceAssigner.AssignEvidence(plan, corpus);
        var totalAssigned = plan.Sections.Sum(s => s.AssignedEvidence.Count);
        totalAssigned.Should().BeGreaterThan(0);
        totalAssigned.Should().BeLessThanOrEqualTo(9); // Can't assign more than total segments

        // Simulate Phase 4: Fake generated content using evidence text
        var runningSummary = new RunningSummary();
        var entityTracker = new EntityContinuityTracker();
        entityTracker.Initialize(corpus);

        for (var i = 0; i < plan.Sections.Count; i++)
        {
            var section = plan.Sections[i];
            entityTracker.ScanEvidence(section, i);

            // "Generate" content from evidence (simulating LLM)
            var evidenceText = string.Join(" ",
                section.AssignedEvidence.Select(e => e.Segment.Text));
            section.GeneratedContent = $"In this section about {section.Heading}: {evidenceText}";

            runningSummary.RecordSection(section, i);
            entityTracker.ScanGenerated(section.GeneratedContent, i);
        }

        // Running summary should have content
        var summaryText = runningSummary.Build();
        summaryText.Should().Contain("DOCUMENT SO FAR:");

        // Entity tracker should provide guidance for later sections
        var guidance = entityTracker.BuildGuidance(2);
        // May or may not have guidance depending on entity matches — just verify it runs
        guidance.Should().NotBeNull();

        // Phase 5: Validate output
        Func<string, float[]> embedder = text => MakeEmbedding(text.GetHashCode() * 0.001f);
        var validation = OutputValidator.Validate(plan, corpus, embedder);
        validation.Should().NotBeNull();
        validation.OverallGroundingScore.Should().BeGreaterThanOrEqualTo(0);

        // No hallucinated URLs since we only used evidence text
        validation.HallucinatedUrls.Should().BeEmpty();

        // Phase 6: Assemble
        var result = DocumentAssembler.Assemble(
            plan,
            "Introduction to signal-driven AI.",
            "The future is deterministic.",
            validation,
            corpus);

        result.Title.Should().NotBeEmpty();
        result.Sections.Should().HaveCount(3);
        result.SourceUrls.Should().NotBeEmpty();
        result.Conclusion.Should().Contain("Validated:");
    }

    #endregion

    #region Test Data Helpers

    private static float[] MakeEmbedding(float seed, int dim = 384)
    {
        // Create a deterministic pseudo-embedding from a seed value.
        // Different seeds produce different vectors with known cosine similarity.
        var emb = new float[dim];
        for (var i = 0; i < dim; i++)
            emb[i] = MathF.Sin(seed + i * 0.1f);
        // L2 normalize
        var norm = MathF.Sqrt(emb.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < dim; i++)
                emb[i] /= norm;
        return emb;
    }

    private static ArticleSegment MakeSegment(string id, string text, double salience, float embSeed)
    {
        return new ArticleSegment
        {
            Id = id,
            Text = text,
            Type = SegmentType.Sentence,
            SalienceScore = salience,
            PositionWeight = 1.0,
            Embedding = MakeEmbedding(embSeed)
        };
    }

    private static ContentItem MakeContentItem(string id, string title, string url, string content)
    {
        return new ContentItem
        {
            Id = id,
            Source = "test",
            Title = title,
            Url = url,
            Content = content,
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    private static ProcessedArticle MakeProcessedArticle(
        ContentItem item, List<ArticleSegment> segments)
    {
        return new ProcessedArticle
        {
            Item = item,
            Strategy = ProcessingStrategy.FullExtraction,
            ContentLength = item.Content?.Length ?? 0,
            Segments = segments,
            TopSegments = segments
        };
    }

    private static List<(string title, string summary, string topic, float sentiment, string url, double relevance)>
        MakeAnalyzedItems(params (string title, string url, double relevance)[] items)
    {
        return items.Select(i =>
            (i.title, summary: $"Summary of {i.title}", topic: "tech", sentiment: 0.5f, i.url, i.relevance)
        ).ToList();
    }

    private static EvidenceCorpus BuildTestCorpus()
    {
        var item1 = MakeContentItem("1", "Deduplication in lucidRAG",
            "https://www.mostlylucid.net/blog/graphiticragdedupe",
            "This system applies deterministic techniques to extract candidate segments.");
        var item2 = MakeContentItem("2", "VideoSummarizer Architecture",
            "https://www.mostlylucid.net/blog/videosummarizer",
            "VideoSummarizer implements the Reduced RAG pattern for video analysis.");
        var item3 = MakeContentItem("3", "MCP Is a Transport",
            "https://www.mostlylucid.net/blog/mcp-transport",
            "MCP handles transport; system architecture must address authority.");

        var seg1A = MakeSegment("1_0",
            "The deduplication strategy uses cosine similarity with a 0.90 threshold aligned with NVIDIA NeMo standards.",
            0.9, 1.0f);
        var seg1B = MakeSegment("1_1",
            "Near-duplicate segments receive salience boosts rather than deletion, preserving signal while reducing redundancy.",
            0.85, 1.5f);
        var seg1C = MakeSegment("1_2",
            "Phase 2 retrieval deduplication operates cross-document after Reciprocal Rank Fusion ranking.",
            0.7, 2.0f);

        var seg2A = MakeSegment("2_0",
            "Perceptual hash deduplication using dHash filters visually similar frames with less than 1ms overhead.",
            0.88, 3.0f);
        var seg2B = MakeSegment("2_1",
            "Batch CLIP embedding processes 8 images per GPU pass delivering 3-5x speedup over serial processing.",
            0.82, 3.5f);
        var seg2C = MakeSegment("2_2",
            "The 16-wave pipeline processes video sequentially with signal dependencies from FFprobe to evidence generation.",
            0.75, 4.0f);

        var seg3A = MakeSegment("3_0",
            "MCP operates as a wire protocol transmitting inputs, outputs, schemas, and tool metadata for capability discovery.",
            0.91, 5.0f);
        var seg3B = MakeSegment("3_1",
            "Probabilistic components propose while deterministic systems decide. LLMs never serve as authorities.",
            0.86, 5.5f);
        var seg3C = MakeSegment("3_2",
            "The constrainer evaluates competing proposals, enforces policy rules, and routes escalation for low confidence.",
            0.78, 6.0f);

        var articles = new List<ProcessedArticle>
        {
            MakeProcessedArticle(item1, [seg1A, seg1B, seg1C]),
            MakeProcessedArticle(item2, [seg2A, seg2B, seg2C]),
            MakeProcessedArticle(item3, [seg3A, seg3B, seg3C])
        };

        var analyzed = MakeAnalyzedItems(
            ("Deduplication in lucidRAG", "https://www.mostlylucid.net/blog/graphiticragdedupe", 0.95),
            ("VideoSummarizer Architecture", "https://www.mostlylucid.net/blog/videosummarizer", 0.88),
            ("MCP Is a Transport", "https://www.mostlylucid.net/blog/mcp-transport", 0.82));

        var contentItems = articles.Select(a => a.Item).ToList();

        return EvidencePreparationService.BuildCorpus(articles, contentItems, analyzed);
    }

    private static DocumentPlan BuildTestPlan(EvidenceCorpus corpus)
    {
        // Build a 3-section plan with different theme embeddings
        return new DocumentPlan
        {
            Title = "Signal-Driven AI Architecture",
            ThemeDescription = "deduplication video analysis MCP architecture signals",
            Sections =
            [
                new PlannedSection
                {
                    Heading = "Deduplication Strategy",
                    ThemeKeywords = "deduplication cosine similarity salience boost near-duplicate",
                    TargetWords = 300,
                    ThemeEmbedding = MakeEmbedding(1.2f) // close to seg1 embeddings
                },
                new PlannedSection
                {
                    Heading = "Video Intelligence Pipeline",
                    ThemeKeywords = "video pipeline CLIP dHash keyframe scene analysis",
                    TargetWords = 300,
                    ThemeEmbedding = MakeEmbedding(3.3f) // close to seg2 embeddings
                },
                new PlannedSection
                {
                    Heading = "MCP and Deterministic Control",
                    ThemeKeywords = "MCP transport protocol constrainer deterministic authority",
                    TargetWords = 300,
                    ThemeEmbedding = MakeEmbedding(5.2f) // close to seg3 embeddings
                }
            ],
            ConclusionAngle = "toward unified signal architecture",
            ThemeEmbedding = MakeEmbedding(3.0f),
            QueryType = QueryType.General
        };
    }

    #endregion

    #region Phase 1: Evidence Preparation

    [Fact]
    public void EvidencePreparation_BuildsCorpus_WithCorrectSegmentCount()
    {
        var corpus = BuildTestCorpus();

        corpus.Segments.Should().HaveCount(9); // 3 articles × 3 segments each
    }

    [Fact]
    public void EvidencePreparation_BuildsCorpus_WithGlobalCentroid()
    {
        var corpus = BuildTestCorpus();

        corpus.GlobalCentroid.Should().NotBeNull();
        corpus.GlobalCentroid!.Length.Should().Be(384);

        // Centroid should be L2-normalized (length ≈ 1)
        var norm = MathF.Sqrt(corpus.GlobalCentroid.Sum(x => x * x));
        norm.Should().BeApproximately(1.0f, 0.01f);
    }

    [Fact]
    public void EvidencePreparation_RegistersAllUrls()
    {
        var corpus = BuildTestCorpus();

        corpus.AllSourceUrls.Should().HaveCount(3);
        corpus.UrlFetchDates.Should().ContainKey("https://www.mostlylucid.net/blog/graphiticragdedupe");
        corpus.UrlFetchDates.Should().ContainKey("https://www.mostlylucid.net/blog/videosummarizer");
        corpus.UrlFetchDates.Should().ContainKey("https://www.mostlylucid.net/blog/mcp-transport");
    }

    [Fact]
    public void EvidencePreparation_RegistersKnownTitles()
    {
        var corpus = BuildTestCorpus();

        corpus.KnownTitles.Should().Contain("Deduplication in lucidRAG");
        corpus.KnownTitles.Should().Contain("VideoSummarizer Architecture");
        corpus.KnownTitles.Should().Contain("MCP Is a Transport");
    }

    [Fact]
    public void EvidencePreparation_ExtractsEntities()
    {
        var corpus = BuildTestCorpus();

        // Should find entities like "Reduced RAG", "Reciprocal Rank Fusion", etc.
        corpus.GlobalEntities.Should().NotBeEmpty();
        corpus.KnownEntityNames.Should().NotBeEmpty();
    }

    [Fact]
    public void EvidencePreparation_ArticleSummaries_HaveRelevanceScores()
    {
        var corpus = BuildTestCorpus();

        corpus.Articles.Should().HaveCount(3);
        corpus.Articles.Values.Should().OnlyContain(a => a.Relevance > 0);
    }

    #endregion

    #region Phase 3: Evidence Assignment

    [Fact]
    public void EvidenceAssigner_AssignsSegmentsToSections()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);

        EvidenceAssigner.AssignEvidence(plan, corpus);

        // At least one section should have evidence
        plan.Sections.Should().Contain(s => s.AssignedEvidence.Count > 0);
        // Total assigned should be reasonable (most segments get assigned)
        var totalAssigned = plan.Sections.Sum(s => s.AssignedEvidence.Count);
        totalAssigned.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EvidenceAssigner_MarksSegmentsAsAssigned()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);

        EvidenceAssigner.AssignEvidence(plan, corpus);

        var assignedCount = corpus.Segments.Count(s => s.IsAssigned);
        assignedCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EvidenceAssigner_NoSegmentAssignedTwice()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);

        EvidenceAssigner.AssignEvidence(plan, corpus);

        // Each segment should appear in at most one section
        var allAssigned = plan.Sections.SelectMany(s => s.AssignedEvidence).ToList();
        var uniqueIds = allAssigned.Select(e => e.Segment.Id).ToHashSet();
        uniqueIds.Count.Should().Be(allAssigned.Count);
    }

    [Fact]
    public void EvidenceAssigner_HighSalienceSegments_AreAssigned()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);

        EvidenceAssigner.AssignEvidence(plan, corpus);

        // Top salience segments (> 0.85) should all be assigned
        var highSalience = corpus.Segments
            .Where(s => s.Segment.SalienceScore > 0.85)
            .ToList();

        highSalience.Should().OnlyContain(s => s.IsAssigned,
            "high-salience segments should be assigned or rescued");
    }

    [Fact]
    public void EvidenceAssigner_AssignedSectionsHaveSourceUrls()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);

        EvidenceAssigner.AssignEvidence(plan, corpus);

        // Sections with assigned evidence should have source URLs
        plan.Sections
            .Where(s => s.AssignedEvidence.Count > 0)
            .Should().OnlyContain(s => s.SourceUrls.Count > 0);
    }

    #endregion

    #region Running Summary

    [Fact]
    public void RunningSummary_EmptyBuild_ReturnsEmpty()
    {
        var summary = new RunningSummary();
        summary.Build().Should().BeEmpty();
    }

    [Fact]
    public void RunningSummary_SingleSection_IncludesHeading()
    {
        var summary = new RunningSummary();
        var section = new PlannedSection
        {
            Heading = "Background",
            ThemeKeywords = "background",
            AssignedEvidence =
            [
                new EvidenceSegment
                {
                    Segment = MakeSegment("s1", "Important fact about AI.", 0.9, 1.0f),
                    ArticleTitle = "Test",
                    ArticleUrl = "https://test.com"
                }
            ],
            GeneratedContent = "AI has transformed computing."
        };

        summary.RecordSection(section, 0);
        var result = summary.Build();

        result.Should().Contain("Background");
        result.Should().Contain("DOCUMENT SO FAR:");
    }

    [Fact]
    public void RunningSummary_MultipleSections_ShowsProgression()
    {
        var summary = new RunningSummary();

        for (var i = 0; i < 5; i++)
        {
            var section = new PlannedSection
            {
                Heading = $"Section {i + 1}",
                ThemeKeywords = $"theme {i}",
                AssignedEvidence =
                [
                    new EvidenceSegment
                    {
                        Segment = MakeSegment($"s{i}", $"Fact {i} about topic.", 0.8 - i * 0.1, i),
                        ArticleTitle = "Test",
                        ArticleUrl = "https://test.com"
                    }
                ],
                GeneratedContent = $"Generated content for section {i + 1}."
            };
            summary.RecordSection(section, i);
        }

        var result = summary.Build();
        result.Should().Contain("DOCUMENT SO FAR:");
        // Should contain headings from recorded sections
        result.Should().Contain("Section 1");
    }

    #endregion

    #region Entity Continuity Tracker

    [Fact]
    public void EntityTracker_Initialize_LoadsEntities()
    {
        var corpus = BuildTestCorpus();
        var tracker = new EntityContinuityTracker();
        tracker.Initialize(corpus);

        // Tracker should have loaded entities from the corpus.
        // BuildGuidance at section 0 may show "active entities" since
        // entities with LastMentionedSection == -1 satisfy the >= -1 check.
        var guidance = tracker.BuildGuidance(0);
        guidance.Should().NotBeNull();
    }

    [Fact]
    public void EntityTracker_ScanEvidence_TracksEntities()
    {
        var corpus = BuildTestCorpus();
        var tracker = new EntityContinuityTracker();
        tracker.Initialize(corpus);

        var plan = BuildTestPlan(corpus);
        EvidenceAssigner.AssignEvidence(plan, corpus);

        // Scan first section's evidence
        tracker.ScanEvidence(plan.Sections[0], 0);

        // After scanning, guidance for section 2 should mention active entities
        // (depending on what entities were found)
        tracker.ScanEvidence(plan.Sections[1], 1);

        var guidance = tracker.BuildGuidance(2);
        // Guidance may or may not be empty depending on entity matches
        // but should not throw
        guidance.Should().NotBeNull();
    }

    [Fact]
    public void EntityTracker_ScanGenerated_UpdatesMentions()
    {
        var corpus = BuildTestCorpus();
        var tracker = new EntityContinuityTracker();
        tracker.Initialize(corpus);

        // If "Reduced RAG" is a known entity, scanning text that mentions it should track it
        var testText = "The Reduced RAG pattern enables efficient processing. " +
                       "Reciprocal Rank Fusion combines multiple signals.";
        tracker.ScanGenerated(testText, 0);

        // Guidance for section 2 might show recently mentioned entities
        var guidance = tracker.BuildGuidance(2);
        guidance.Should().NotBeNull();
    }

    #endregion

    #region Phase 5: Output Validation

    [Fact]
    public void OutputValidator_ValidUrls_NoIssues()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);
        EvidenceAssigner.AssignEvidence(plan, corpus);

        // Simulate generated content with valid URLs
        plan.Sections[0].GeneratedContent =
            "The system uses deduplication as described in [lucidRAG](https://www.mostlylucid.net/blog/graphiticragdedupe).";
        plan.Sections[1].GeneratedContent =
            "VideoSummarizer processes video files efficiently.";
        plan.Sections[2].GeneratedContent =
            "MCP provides transport capabilities as shown at [MCP post](https://www.mostlylucid.net/blog/mcp-transport).";

        Func<string, float[]> embedder = text => MakeEmbedding(text.GetHashCode() * 0.001f);
        var result = OutputValidator.Validate(plan, corpus, embedder);

        result.HallucinatedUrls.Should().BeEmpty();
        result.VerifiedUrlCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void OutputValidator_HallucinatedUrl_Detected()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);
        EvidenceAssigner.AssignEvidence(plan, corpus);

        // Inject a hallucinated URL
        plan.Sections[0].GeneratedContent =
            "According to [fake source](https://www.fake-news-site.com/article/123), deduplication is important.";
        plan.Sections[1].GeneratedContent = "Video processing is complex.";
        plan.Sections[2].GeneratedContent = "MCP is a transport protocol.";

        Func<string, float[]> embedder = text => MakeEmbedding(text.GetHashCode() * 0.001f);
        var result = OutputValidator.Validate(plan, corpus, embedder);

        result.HallucinatedUrls.Should().NotBeEmpty();
        result.HallucinatedUrls.Should().Contain(u => u.Url.Contains("fake-news-site.com"));
    }

    [Fact]
    public void OutputValidator_GoogleRedirectUrl_Detected()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);
        EvidenceAssigner.AssignEvidence(plan, corpus);

        plan.Sections[0].GeneratedContent =
            "See [this](https://news.google.com/rss/articles/CBMiXWh0dHBz) for details.";
        plan.Sections[1].GeneratedContent = "Testing.";
        plan.Sections[2].GeneratedContent = "Testing.";

        Func<string, float[]> embedder = text => MakeEmbedding(text.GetHashCode() * 0.001f);
        var result = OutputValidator.Validate(plan, corpus, embedder);

        result.HallucinatedUrls.Should().Contain(u => u.Type == UrlIssueType.GoogleRedirect);
    }

    [Fact]
    public void OutputValidator_AutoFix_RemovesHallucinatedUrls()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);
        EvidenceAssigner.AssignEvidence(plan, corpus);

        plan.Sections[0].GeneratedContent =
            "According to [fake source](https://www.hallucinated.com/article), this is true.";
        plan.Sections[1].GeneratedContent = "Good content.";
        plan.Sections[2].GeneratedContent = "Good content.";

        Func<string, float[]> embedder = text => MakeEmbedding(text.GetHashCode() * 0.001f);
        var validation = OutputValidator.Validate(plan, corpus, embedder);
        OutputValidator.AutoFix(plan, validation, corpus);

        // Hallucinated URL should be removed but text kept
        plan.Sections[0].GeneratedContent.Should().Contain("fake source");
        plan.Sections[0].GeneratedContent.Should().NotContain("https://www.hallucinated.com");
    }

    [Fact]
    public void OutputValidator_GroundingScore_BetweenZeroAndOne()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);
        EvidenceAssigner.AssignEvidence(plan, corpus);

        // Use evidence text as generated content for high grounding
        plan.Sections[0].GeneratedContent =
            "The deduplication strategy uses cosine similarity with a 0.90 threshold. " +
            "Near-duplicate segments receive salience boosts rather than deletion.";
        plan.Sections[1].GeneratedContent =
            "Perceptual hash deduplication using dHash filters visually similar frames.";
        plan.Sections[2].GeneratedContent =
            "MCP operates as a wire protocol transmitting inputs and outputs.";

        Func<string, float[]> embedder = text => MakeEmbedding(text.GetHashCode() * 0.001f);
        var result = OutputValidator.Validate(plan, corpus, embedder);

        result.OverallGroundingScore.Should().BeGreaterThanOrEqualTo(0);
        result.OverallGroundingScore.Should().BeLessThanOrEqualTo(1);
    }

    #endregion

    #region Phase 6: Document Assembly

    [Fact]
    public void DocumentAssembler_ProducesValidBlogArticleResult()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);
        EvidenceAssigner.AssignEvidence(plan, corpus);

        plan.Sections[0].GeneratedContent = "Content about deduplication.";
        plan.Sections[1].GeneratedContent = "Content about video processing.";
        plan.Sections[2].GeneratedContent = "Content about MCP.";

        var result = DocumentAssembler.Assemble(
            plan,
            "This article explores signal-driven AI architecture.",
            "The future is deterministic.",
            null,
            corpus);

        result.Title.Should().Be("Signal-Driven AI Architecture");
        result.Introduction.Should().Contain("signal-driven");
        result.Sections.Should().HaveCount(3);
        result.Sections[0].Heading.Should().Be("Deduplication Strategy");
        result.Sections[0].Content.Should().Contain("deduplication");
        result.Conclusion.Should().Contain("deterministic");
        result.SourceUrls.Should().NotBeEmpty();
        result.QueryType.Should().Be(QueryType.General);
    }

    [Fact]
    public void DocumentAssembler_WithValidation_AppendsMetadata()
    {
        var corpus = BuildTestCorpus();
        var plan = BuildTestPlan(corpus);
        EvidenceAssigner.AssignEvidence(plan, corpus);

        plan.Sections[0].GeneratedContent = "Content.";
        plan.Sections[1].GeneratedContent = "Content.";
        plan.Sections[2].GeneratedContent = "Content.";

        var validation = new LfValidationResult
        {
            OverallGroundingScore = 0.85,
            PassesValidation = true,
            VerifiedUrlCount = 3,
            GroundedEntityCount = 5
        };

        var result = DocumentAssembler.Assemble(
            plan, "Intro.", "Conclusion.", validation, corpus);

        result.Conclusion.Should().Contain("Validated:");
        result.Conclusion.Should().Contain("Grounding:");
    }

    #endregion

    #region Prompt Template Existence

    [Fact]
    public void LongFormTemplates_Exist()
    {
        PromptTemplateService.ClearCache();

        var outline = PromptTemplateService.LoadTemplate("longform-outline");
        outline.Should().NotBeEmpty();
        outline.Should().Contain("theme_keywords");
        outline.Should().Contain("QUERY");

        var section = PromptTemplateService.LoadTemplate("longform-section");
        section.Should().NotBeEmpty();
        section.Should().Contain("RUNNING_SUMMARY");
        section.Should().Contain("ENTITY_GUIDANCE");
        section.Should().Contain("DRIFT_GUIDANCE");
    }

    [Fact]
    public void LongFormOutlineTemplate_RendersCorrectly()
    {
        PromptTemplateService.ClearCache();

        var result = PromptTemplateService.Render("longform-outline", new Dictionary<string, object?>
        {
            ["QUERY"] = "AI architecture trends",
            ["EVIDENCE"] = "[0] Test article (https://test.com)",
            ["OUTLINE_INSTRUCTIONS"] = "Create a logical outline.",
            ["TOP_ENTITIES"] = "OpenAI, Google, Meta",
            ["SEGMENT_COUNT"] = "50"
        });

        result.Should().Contain("AI architecture trends");
        result.Should().Contain("OpenAI, Google, Meta");
        result.Should().Contain("50");
        result.Should().Contain("theme_keywords");
    }

    [Fact]
    public void LongFormSectionTemplate_RendersCorrectly()
    {
        PromptTemplateService.ClearCache();

        var result = PromptTemplateService.Render("longform-section", new Dictionary<string, object?>
        {
            ["HEADING"] = "Key Developments",
            ["QUERY"] = "AI trends",
            ["FOCUS"] = "Focus: Main breakthroughs",
            ["RUNNING_SUMMARY"] = "DOCUMENT SO FAR:\n- [Background]: AI emerged in the 1950s.",
            ["ENTITY_GUIDANCE"] = "KEY ENTITIES:\n  - OpenAI (last: section 1)",
            ["DRIFT_GUIDANCE"] = "",
            ["VIBE_PROMPT"] = "Be analytical",
            ["TIMELINE_EXTRA"] = "",
            ["EVIDENCE"] = "### Test Article\nURL: https://test.com\n  Key finding here.",
            ["WORD_RANGE"] = "200-400"
        });

        result.Should().Contain("Key Developments");
        result.Should().Contain("DOCUMENT SO FAR:");
        result.Should().Contain("KEY ENTITIES:");
        result.Should().Contain("200-400");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EvidenceAssigner_EmptyCorpus_NoAssignment()
    {
        var corpus = new EvidenceCorpus();
        var plan = new DocumentPlan
        {
            Title = "Test",
            Sections =
            [
                new PlannedSection
                {
                    Heading = "Empty",
                    ThemeKeywords = "test",
                    ThemeEmbedding = MakeEmbedding(1.0f)
                }
            ],
            QueryType = QueryType.General
        };

        EvidenceAssigner.AssignEvidence(plan, corpus);
        plan.Sections[0].AssignedEvidence.Should().BeEmpty();
    }

    [Fact]
    public void RunningSummary_VeryLongSection_TruncatesCorrectly()
    {
        var summary = new RunningSummary();
        var longText = new string('x', 5000);
        var section = new PlannedSection
        {
            Heading = "Long Section",
            ThemeKeywords = "long",
            AssignedEvidence =
            [
                new EvidenceSegment
                {
                    Segment = MakeSegment("s1", longText, 0.9, 1.0f),
                    ArticleTitle = "Test",
                    ArticleUrl = "https://test.com"
                }
            ],
            GeneratedContent = longText
        };

        summary.RecordSection(section, 0);
        var result = summary.Build();

        // Should not exceed the ~1400 char budget
        result.Length.Should().BeLessThan(2000);
    }

    [Fact]
    public void OutputValidator_NoSections_ReturnsValid()
    {
        var corpus = BuildTestCorpus();
        var plan = new DocumentPlan
        {
            Title = "Empty",
            Sections = [],
            QueryType = QueryType.General
        };

        Func<string, float[]> embedder = text => MakeEmbedding(text.GetHashCode() * 0.001f);
        var result = OutputValidator.Validate(plan, corpus, embedder);

        result.PassesValidation.Should().BeTrue();
        result.OverallGroundingScore.Should().Be(1.0);
    }

    [Fact]
    public void DocumentAssembler_EmptySections_ReturnsEmptyResult()
    {
        var corpus = BuildTestCorpus();
        var plan = new DocumentPlan
        {
            Title = "Empty",
            Sections = [],
            QueryType = QueryType.General
        };

        var result = DocumentAssembler.Assemble(plan, "Intro.", "Conclusion.", null, corpus);
        result.Sections.Should().BeEmpty();
        result.Title.Should().Be("Empty");
    }

    #endregion
}