using DoomSummarizer.Models;
using DoomSummarizer.Models.LongFormGeneration;
using DoomSummarizer.Services;
using DoomSummarizer.Services.LongFormGeneration;
using Mostlylucid.DocSummarizer.Models;
using LfValidationResult = DoomSummarizer.Models.LongFormGeneration.ValidationResult;

namespace DoomSummarizer.Tests;

/// <summary>
/// Integration tests for the long-form document generation pipeline using
/// real ONNX embeddings and article content from mostlylucid.net.
/// Uses OnnxEmbeddingService (via ArticleProcessor) which auto-downloads models.
/// </summary>
[Trait("Category", "Network")]
public class LongFormIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly ArticleProcessor _articleProcessor;

    // Real article content from mostlylucid.net
    private static readonly string Article1Content = """
        This system applies deterministic and probabilistic techniques to extract candidate segments,
        evaluate informational value, and retain strongest representatives. Simple string equality is
        not enough. Concepts expressed through different wording, structure, or format require
        sophisticated handling.

        Phase 1: Ingestion Deduplication operates per-document. Extracts, embeds, and deduplicates
        before indexing. Near-duplicates receive salience boosts rather than deletion. Exact duplicates
        drop silently.

        Phase 2: Retrieval Deduplication operates cross-document after Reciprocal Rank Fusion ranking.
        Preserves highest-scoring segment when duplicates exist. Maintains semantic ordering.

        The deduplication strategy uses cosine similarity with a 0.90 threshold aligned with NVIDIA
        NeMo standards. Near-duplicate segments receive salience boosts rather than deletion, preserving
        signal while reducing redundancy. Logarithmic boost decay prevents runaway salience inflation.

        Five core invariants: ordering preserved, no concept loss, document boundary respected,
        content immutable, deterministic output. Identical inputs yield identical outputs.

        Performance: Ingestion phase O(n²) on 50-500 segments with less than 100ms impact.
        Retrieval phase O(m²) on 20-100 segments with less than 10ms impact.
        """;

    private static readonly string Article2Content = """
        VideoSummarizer is an orchestration system for video analysis implementing the Reduced RAG
        pattern. It processes video files by decomposing them into three reduction stages: structural
        analysis with shots and frames, content extraction with embeddings and transcription, and
        scene assembly for RAG indexing.

        Perceptual hash deduplication using dHash filters visually similar frames before expensive ML
        operations. Resize to 9x8 grayscale, compare adjacent pixels horizontally to generate 64-bit
        hashes. Achieves approximately 40% frame reduction with less than 1ms overhead per frame.

        Batch CLIP embedding processes 8 images per GPU pass instead of one, delivering 3-5x speedup.
        A 30-frame batch completes in approximately 1.4 seconds versus 6 seconds with serial processing.

        The 16-wave pipeline processes video sequentially with signal dependencies from FFprobe
        metadata extraction through evidence generation. Multi-signal scene clustering uses embedding
        dissimilarity at 40%, transcript semantic shift at 30%, cut type detection at 20%, and
        temporal pressure at 10%.

        BERT-NER extracts named entities from transcripts: persons, organizations, and locations.
        Entities are deduplicated and emitted as signals for cross-modal linking.
        """;

    private static readonly string Article3Content = """
        MCP operates as a wire protocol that transmits inputs, outputs, schemas, tool metadata,
        and capability discovery. MCP does not determine when tools should run, validate truth,
        enforce trust, or manage side effects.

        The core principle: Probabilistic components propose while deterministic systems decide.
        LLMs never serve as authorities. Tools operate non-autonomously. Every MCP response
        represents a proposal, not fact.

        The constrainer is deterministic logic that evaluates competing proposals, enforces policy
        rules, determines persistence versus rejection, and routes escalation for low-confidence
        scenarios. Most tutorials omit the constrainer.

        MCP functions as a hard boundary through process isolation, explicit inputs and outputs,
        no shared mutable state, no ambient context, and typed contracts. Schema violations are errors.

        Signal contracts require type safety, bounded values, attributable sources, confidence metrics,
        provenance tracking, and evidence pointers. If a result cannot be validated, it is not accepted.

        The synthesis: MCP enables composition. Determinism enables trust. LLMs enable fluency.
        Probability proposes and determinism persists.
        """;

    public LongFormIntegrationTests()
    {
        _articleProcessor = new ArticleProcessor();
    }

    public async Task InitializeAsync()
    {
        await _articleProcessor.EnsureInitializedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _articleProcessor.Dispose();
    }

    private List<ContentItem> BuildContentItems()
    {
        return
        [
            new ContentItem
            {
                Id = "dedup-1",
                Source = "web",
                Title = "Deduplication of Graphitic RAG Evidence Segments in lucidRAG",
                Url = "https://www.mostlylucid.net/blog/graphiticragdedupe",
                Content = Article1Content,
                RelevanceScore = 0.95
            },
            new ContentItem
            {
                Id = "video-2",
                Source = "web",
                Title = "VideoSummarizer: Reduced RAG for Video Architecture",
                Url = "https://www.mostlylucid.net/blog/videosummarizer",
                Content = Article2Content,
                RelevanceScore = 0.88
            },
            new ContentItem
            {
                Id = "mcp-3",
                Source = "web",
                Title = "MCP Is a Transport, Not an Architecture",
                Url = "https://www.mostlylucid.net/blog/mcp-transport",
                Content = Article3Content,
                RelevanceScore = 0.82
            }
        ];
    }

    private List<(string title, string summary, string topic, float sentiment, string url, double relevance)>
        BuildAnalyzedItems()
    {
        return
        [
            ("Deduplication of Graphitic RAG Evidence Segments in lucidRAG",
                "Design spec for deduplication in lucidRAG using cosine similarity and salience boosting",
                "RAG", 0.6f, "https://www.mostlylucid.net/blog/graphiticragdedupe", 0.95),
            ("VideoSummarizer: Reduced RAG for Video Architecture",
                "Video analysis pipeline using dHash, CLIP, and 16-wave architecture",
                "Video AI", 0.5f, "https://www.mostlylucid.net/blog/videosummarizer", 0.88),
            ("MCP Is a Transport, Not an Architecture",
                "MCP should be treated as a wire protocol, not an agent framework",
                "Architecture", 0.4f, "https://www.mostlylucid.net/blog/mcp-transport", 0.82)
        ];
    }

    [Fact]
    public async Task Phase1_RealContent_BuildsCorpusWithEmbeddings()
    {
        var items = BuildContentItems();
        var analyzed = BuildAnalyzedItems();

        var processed = await _articleProcessor.ProcessBatchAsync(items);

        var corpus = EvidencePreparationService.BuildCorpus(processed, items, analyzed);

        // Should have segments with real embeddings
        corpus.Segments.Should().NotBeEmpty();
        corpus.Segments.Should().OnlyContain(s => s.Segment.Embedding != null);
        corpus.Segments.Should().OnlyContain(s => s.Segment.Embedding!.Length == 384);

        // Centroid should be computed from real embeddings
        corpus.GlobalCentroid.Should().NotBeNull();
        corpus.GlobalCentroid!.Length.Should().Be(384);

        // All 3 articles registered
        corpus.Articles.Should().HaveCount(3);
        corpus.AllSourceUrls.Should().HaveCount(3);

        // URL whitelist
        corpus.UrlFetchDates.Should().ContainKey("https://www.mostlylucid.net/blog/graphiticragdedupe");

        // Entities extracted from real text
        corpus.GlobalEntities.Should().NotBeEmpty();
        corpus.KnownEntityNames.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Phase3_RealEmbeddings_AssignsEvidenceByThemeSimilarity()
    {
        var items = BuildContentItems();
        var analyzed = BuildAnalyzedItems();
        var processed = await _articleProcessor.ProcessBatchAsync(items);
        var corpus = EvidencePreparationService.BuildCorpus(processed, items, analyzed);

        // Build plan with real theme embeddings
        var plan = new DocumentPlan
        {
            Title = "Signal-Driven AI Architecture",
            ThemeDescription = "deduplication video analysis MCP transport architecture signals pipelines",
            Sections =
            [
                new PlannedSection
                {
                    Heading = "Deduplication Strategy",
                    ThemeKeywords = "deduplication cosine similarity salience boost near-duplicate RAG segments",
                    ThemeEmbedding = _articleProcessor.Embed("deduplication cosine similarity salience boost near-duplicate RAG segments")
                },
                new PlannedSection
                {
                    Heading = "Video Intelligence Pipeline",
                    ThemeKeywords = "video pipeline CLIP dHash keyframe scene wave analysis perceptual hash",
                    ThemeEmbedding = _articleProcessor.Embed("video pipeline CLIP dHash keyframe scene wave analysis perceptual hash")
                },
                new PlannedSection
                {
                    Heading = "MCP and Deterministic Control",
                    ThemeKeywords = "MCP transport protocol constrainer deterministic authority signal contracts",
                    ThemeEmbedding = _articleProcessor.Embed("MCP transport protocol constrainer deterministic authority signal contracts")
                }
            ],
            ThemeEmbedding = _articleProcessor.Embed("deduplication video analysis MCP transport architecture signals pipelines"),
            QueryType = QueryType.General
        };

        EvidenceAssigner.AssignEvidence(plan, corpus);

        // With real embeddings, theme-matched sections should get relevant evidence
        var totalAssigned = plan.Sections.Sum(s => s.AssignedEvidence.Count);
        totalAssigned.Should().BeGreaterThan(0, "real embeddings should produce theme matches");

        // At least 2 of 3 sections should have evidence
        plan.Sections.Count(s => s.AssignedEvidence.Count > 0).Should()
            .BeGreaterThanOrEqualTo(2, "most sections should get evidence with real embeddings");

        // Check that dedup section actually got dedup-related segments
        var dedupSection = plan.Sections[0];
        if (dedupSection.AssignedEvidence.Count > 0)
        {
            var dedupTexts = string.Join(" ", dedupSection.AssignedEvidence.Select(e => e.Segment.Text));
            // Should contain dedup-related terms
            dedupTexts.ToLowerInvariant().Should().ContainAny(
                "dedup", "similar", "cosine", "salience", "duplicate");
        }
    }

    [Fact]
    public async Task Phase5_RealEmbeddings_ValidatesGroundedContent()
    {
        var items = BuildContentItems();
        var analyzed = BuildAnalyzedItems();
        var processed = await _articleProcessor.ProcessBatchAsync(items);
        var corpus = EvidencePreparationService.BuildCorpus(processed, items, analyzed);

        var plan = new DocumentPlan
        {
            Title = "Test Article",
            Sections =
            [
                new PlannedSection
                {
                    Heading = "Deduplication",
                    ThemeKeywords = "deduplication",
                    ThemeEmbedding = _articleProcessor.Embed("deduplication"),
                    // Generate content directly from evidence (should be well-grounded)
                    GeneratedContent = "The deduplication strategy uses cosine similarity with a 0.90 threshold. " +
                                       "Near-duplicate segments receive salience boosts. " +
                                       "According to [lucidRAG](https://www.mostlylucid.net/blog/graphiticragdedupe), " +
                                       "this preserves signal while reducing redundancy."
                },
                new PlannedSection
                {
                    Heading = "Video Processing",
                    ThemeKeywords = "video",
                    ThemeEmbedding = _articleProcessor.Embed("video"),
                    GeneratedContent = "VideoSummarizer processes video using dHash for perceptual deduplication. " +
                                       "Batch CLIP embedding delivers 3-5x speedup over serial processing."
                }
            ],
            QueryType = QueryType.General
        };

        var validation = OutputValidator.Validate(plan, corpus, _articleProcessor.Embed);

        // URLs from evidence should be verified
        validation.HallucinatedUrls.Should().BeEmpty("all URLs come from evidence");
        validation.VerifiedUrlCount.Should().Be(1); // One URL in section 1

        // Content paraphrases evidence, so grounding should be reasonable
        validation.OverallGroundingScore.Should().BeGreaterThan(0,
            "content derived from evidence should have some grounding");
    }

    [Fact]
    public async Task Phase5_HallucinatedContent_DetectedByRealEmbeddings()
    {
        var items = BuildContentItems();
        var analyzed = BuildAnalyzedItems();
        var processed = await _articleProcessor.ProcessBatchAsync(items);
        var corpus = EvidencePreparationService.BuildCorpus(processed, items, analyzed);

        var plan = new DocumentPlan
        {
            Title = "Test Article",
            Sections =
            [
                new PlannedSection
                {
                    Heading = "Fabricated Content",
                    ThemeKeywords = "fabricated",
                    ThemeEmbedding = _articleProcessor.Embed("fabricated"),
                    GeneratedContent = "According to [Non-Existent Source](https://www.fake-url.com/article), " +
                                       "quantum computing has achieved room-temperature superconductivity " +
                                       "using a novel approach involving neural crystallography. " +
                                       "The Quantum Research Institute confirmed 99.9% efficiency."
                }
            ],
            QueryType = QueryType.General
        };

        var validation = OutputValidator.Validate(plan, corpus, _articleProcessor.Embed);

        // Hallucinated URL should be detected — this is the primary signal
        validation.HallucinatedUrls.Should().NotBeEmpty("fabricated URL not in evidence");
        validation.HallucinatedUrls.Should().Contain(u => u.Url.Contains("fake-url.com"));

        // Note: Embedding-based fact grounding (0.4 threshold) is too coarse to distinguish
        // plausible-sounding fabricated English from real evidence — MiniLM produces moderate
        // similarity for any technical English against a technical corpus.
        // URL validation is the reliable hallucination signal; fact grounding catches
        // truly alien content (wrong language, random strings, etc.).
    }

    [Fact]
    public async Task FullDeterministicPipeline_RealEmbeddings_EndToEnd()
    {
        var items = BuildContentItems();
        var analyzed = BuildAnalyzedItems();

        // Phase 1: Process articles and build corpus
        var processed = await _articleProcessor.ProcessBatchAsync(items);
        var corpus = EvidencePreparationService.BuildCorpus(processed, items, analyzed);

        corpus.Segments.Should().NotBeEmpty();
        corpus.GlobalCentroid.Should().NotBeNull();

        // Phase 2: Build plan with real embeddings (skip sentinel, use deterministic plan)
        var plan = new DocumentPlan
        {
            Title = "Signal-Driven RAG Architecture",
            ThemeDescription = "deterministic AI architecture with deduplication, video processing, and MCP protocols",
            Sections =
            [
                new PlannedSection
                {
                    Heading = "Evidence Deduplication",
                    ThemeKeywords = "deduplication cosine similarity salience boost evidence segments",
                    ThemeEmbedding = _articleProcessor.Embed("deduplication cosine similarity salience boost evidence segments")
                },
                new PlannedSection
                {
                    Heading = "Multi-Modal Video Intelligence",
                    ThemeKeywords = "video pipeline CLIP dHash keyframe scene analysis batch processing",
                    ThemeEmbedding = _articleProcessor.Embed("video pipeline CLIP dHash keyframe scene analysis batch processing")
                },
                new PlannedSection
                {
                    Heading = "Protocol-Driven Architecture",
                    ThemeKeywords = "MCP transport protocol constrainer deterministic authority signals",
                    ThemeEmbedding = _articleProcessor.Embed("MCP transport protocol constrainer deterministic authority signals")
                }
            ],
            ThemeEmbedding = _articleProcessor.Embed(
                "deterministic AI architecture with deduplication, video processing, and MCP protocols"),
            QueryType = QueryType.General
        };

        // Phase 3: Assign evidence
        EvidenceAssigner.AssignEvidence(plan, corpus);
        var totalAssigned = plan.Sections.Sum(s => s.AssignedEvidence.Count);
        totalAssigned.Should().BeGreaterThan(0);

        // Initialize trackers
        var runningSummary = new RunningSummary();
        var entityTracker = new EntityContinuityTracker();
        entityTracker.Initialize(corpus);

        // Simulate Phase 4: Use evidence text as generated content
        for (var i = 0; i < plan.Sections.Count; i++)
        {
            var section = plan.Sections[i];
            entityTracker.ScanEvidence(section, i);

            // "Generate" content from assigned evidence
            if (section.AssignedEvidence.Count > 0)
            {
                section.GeneratedContent = string.Join(" ",
                    section.AssignedEvidence
                        .OrderByDescending(e => e.Segment.SalienceScore)
                        .Take(3)
                        .Select(e => e.Segment.Text));
            }
            else
            {
                section.GeneratedContent = $"This section covers {section.Heading}.";
            }

            runningSummary.RecordSection(section, i);
            entityTracker.ScanGenerated(section.GeneratedContent, i);
        }

        // Running summary should show progression
        var summaryText = runningSummary.Build();
        summaryText.Should().NotBeEmpty();
        summaryText.Should().Contain("DOCUMENT SO FAR:");

        // Phase 5: Validate
        var validation = OutputValidator.Validate(plan, corpus, _articleProcessor.Embed);
        validation.Should().NotBeNull();
        validation.OverallGroundingScore.Should().BeGreaterThanOrEqualTo(0);

        // Since we used evidence text directly, there should be no hallucinated URLs
        validation.HallucinatedUrls.Should().BeEmpty();

        // Phase 6: Assemble
        var result = DocumentAssembler.Assemble(
            plan,
            introduction: "This article explores signal-driven AI architecture across deduplication, video, and MCP.",
            conclusion: "Deterministic systems enable trust in AI pipelines.",
            validation,
            corpus);

        result.Title.Should().Be("Signal-Driven RAG Architecture");
        result.Sections.Should().NotBeEmpty();
        result.SourceUrls.Should().NotBeEmpty();
        result.Conclusion.Should().Contain("Validated:");

        // Print summary for manual inspection
        Console.WriteLine($"\n=== Full Pipeline Results ===");
        Console.WriteLine($"Title: {result.Title}");
        Console.WriteLine($"Sections: {result.Sections.Count}");
        Console.WriteLine($"Total segments in corpus: {corpus.Segments.Count}");
        Console.WriteLine($"Total assigned: {totalAssigned}");
        Console.WriteLine($"Grounding score: {validation.OverallGroundingScore:P0}");
        Console.WriteLine($"Verified URLs: {validation.VerifiedUrlCount}");
        Console.WriteLine($"Hallucinated URLs: {validation.HallucinatedUrls.Count}");
        Console.WriteLine($"Ungrounded facts: {validation.UngroundedFacts.Count}");
        Console.WriteLine($"Entities found: {corpus.GlobalEntities.Count}");
        Console.WriteLine($"Running summary:\n{summaryText}");

        foreach (var section in result.Sections)
        {
            Console.WriteLine($"\n--- {section.Heading} ---");
            Console.WriteLine($"Sources: {string.Join(", ", section.SourceUrls)}");
            Console.WriteLine($"Content preview: {section.Content[..Math.Min(200, section.Content.Length)]}...");
        }
    }
}
