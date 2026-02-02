using System.Diagnostics;
using LucidRAG.Decomposer.Analysis;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;
using LucidRAG.Decomposer.Refinement;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests.Benchmarks;

/// <summary>
/// Hot-path benchmarks for the decomposition pipeline.
/// Uses xUnit + Stopwatch + GC allocation tracking (same pattern as LoadBalancingBenchmarks).
/// </summary>
public class HotPathBenchmarks(Xunit.Abstractions.ITestOutputHelper output)
{
    // ── B1: Archetype embedding batch call counts ────────────────────────

    [Fact]
    public async Task B1_ConceptClassifier_BatchCallCount()
    {
        var counting = new CountingEmbeddingService();
        var classifier = new ConceptClassifier(counting);

        await classifier.ClassifyAsync("How do I configure a RAG pipeline?");

        output.WriteLine($"ConceptClassifier: {counting.BatchEmbedCalls} batch calls, " +
                         $"{counting.SingleEmbedCalls} single calls, " +
                         $"{counting.TotalTextsEmbedded} total texts");

        // After optimization: should be 1 batch call (was 9)
        counting.BatchEmbedCalls.Should().Be(1,
            "all concept archetypes should be embedded in a single batch call");
    }

    [Fact]
    public async Task B1_ToolUseAnalyzer_BatchCallCount()
    {
        var counting = new CountingEmbeddingService();
        var analyzer = new ToolUseAnalyzer(counting);

        var signals = new QuerySignals
        {
            OriginalQuery = "search the knowledge base for information about embeddings"
        };

        await analyzer.AnalyzeAsync(signals.OriginalQuery, signals);

        output.WriteLine($"ToolUseAnalyzer: {counting.BatchEmbedCalls} batch calls, " +
                         $"{counting.SingleEmbedCalls} single calls, " +
                         $"{counting.TotalTextsEmbedded} total texts");

        // After optimization: should be 1 batch call for archetypes (was 6)
        // Plus 1 single call for the query clause embedding
        counting.BatchEmbedCalls.Should().Be(1,
            "all tool archetypes should be embedded in a single batch call");
    }

    [Fact]
    public async Task B1_ComplexityClassifier_BatchCallCount()
    {
        var counting = new CountingEmbeddingService();
        var classifier = new ComplexityClassifier(counting);

        // Need 2+ clauses and no other triggers to reach archetype embedding path
        await classifier.ClassifyAsync(
            "How does X compare to Y, and what changed over time?",
            entityCount: 0, entityTypeCount: 0, hasUrls: false, hasDateTimes: true);

        output.WriteLine($"ComplexityClassifier: {counting.BatchEmbedCalls} batch calls, " +
                         $"{counting.SingleEmbedCalls} single calls, " +
                         $"{counting.TotalTextsEmbedded} total texts");

        // After optimization: should be 1 batch call (was 2)
        counting.BatchEmbedCalls.Should().Be(1,
            "comparison + temporal archetypes should be embedded in a single batch call");
    }

    // ── B2: DecompositionPipeline throughput ──────────────────────────────

    [Fact]
    public async Task B2_DecompositionPipeline_SimpleQuery()
    {
        var pipeline = CreatePipelineWithEmbedding();

        // Warm up
        await pipeline.DecomposeAsync("What is a transformer?");

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        var result = await pipeline.DecomposeAsync("What is a transformer?");

        sw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        output.WriteLine($"Simple query: {sw.ElapsedMilliseconds}ms, " +
                         $"{allocAfter - allocBefore:N0} bytes allocated");

        result.IsFastPath.Should().BeTrue();
    }

    [Fact]
    public async Task B2_DecompositionPipeline_ComplexQuery()
    {
        var pipeline = CreatePipelineWithEmbedding();

        // Warm up
        await pipeline.DecomposeAsync("test warm up", hasUrls: true);

        var sw = Stopwatch.StartNew();

        var result = await pipeline.DecomposeAsync(
            "Go to C:/test, find markdown files, index them into a KB called 'docs', then search for RAG pipelines",
            hasUrls: true);

        sw.Stop();

        output.WriteLine($"Complex query: {sw.ElapsedMilliseconds}ms, " +
                         $"nodes={result.Nodes.Count}, tools={result.Plan.ToolActionNodes.Count}");

        result.IsFastPath.Should().BeFalse();
    }

    [Fact]
    public async Task B2_DecompositionPipeline_100Queries()
    {
        const int iterations = 100;
        var pipeline = CreatePipelineWithEmbedding();

        // Warm up
        await pipeline.DecomposeAsync("warm up query");

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            await pipeline.DecomposeAsync($"What is concept {i}?");

        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"Throughput: {opsPerSec:N0} simple queries/sec ({sw.ElapsedMilliseconds}ms total)");

        opsPerSec.Should().BeGreaterThan(100, "simple queries should be fast with fake embeddings");
    }

    // ── B3: Analyzer pipeline wall time ──────────────────────────────────

    [Fact]
    public async Task B3_AnalyzerPipeline_SequentialBaseline()
    {
        var counting = new CountingEmbeddingService();
        var analyzers = CreateAnalyzers(counting);

        var signals = new QuerySignals
        {
            OriginalQuery = "Compare Azure and AWS pricing models and show me the latest trends",
            Complexity = QueryComplexity.Complex
        };

        var sw = Stopwatch.StartNew();

        var current = signals;
        foreach (var analyzer in analyzers)
            current = await analyzer.AnalyzeAsync(signals.OriginalQuery, current);

        sw.Stop();

        output.WriteLine($"All 6 analyzers sequential: {sw.ElapsedMilliseconds}ms, " +
                         $"nodes={current.ProposedNodes.Count}, " +
                         $"embed calls: {counting.BatchEmbedCalls} batch + {counting.SingleEmbedCalls} single");
    }

    // ── B5: BuildContentItem throughput & word count allocation ───────────

    [Fact]
    public async Task B5_BuildContentItem_Throughput()
    {
        var testDir = Path.Combine(AppContext.BaseDirectory, "TestData", "Markdown");
        var files = Directory.GetFiles(testDir, "*.md");
        files.Length.Should().BeGreaterThan(0, "test data files should exist");

        // Warm up
        await DoomSummarizer.Services.RetrievalSubQueryExecutor
            .BuildContentItemFromFileAsync(files[0], CancellationToken.None);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < files.Length; i++)
        {
            await DoomSummarizer.Services.RetrievalSubQueryExecutor
                .BuildContentItemFromFileAsync(files[i], CancellationToken.None);
        }

        sw.Stop();

        var opsPerSec = files.Length / sw.Elapsed.TotalSeconds;
        output.WriteLine($"BuildContentItem: {opsPerSec:N0} files/sec ({files.Length} files in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void B5_WordCount_Allocation()
    {
        // Create a ~50KB text string
        var text = string.Join(" ", Enumerable.Range(0, 8000).Select(i => $"word{i}"));
        text.Length.Should().BeGreaterThan(40_000, "test text should be ~50KB");

        // Warm up
        CountWordsSpan(text.AsSpan());

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();

        var count = CountWordsSpan(text.AsSpan());

        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var allocBytes = allocAfter - allocBefore;

        output.WriteLine($"Span word count: {count} words, {allocBytes:N0} bytes allocated");

        count.Should().Be(8000);
        allocBytes.Should().BeLessThan(1024, "span-based word count should be near-zero allocation");
    }

    // ── B8: CosineSimilarity SIMD vs scalar ─────────────────────────────

    [Fact]
    public void B8_CosineSimilarity_SIMD_Throughput()
    {
        const int iterations = 100_000;
        var a = new float[384];
        var b = new float[384];
        var rng = new Random(42);
        for (var i = 0; i < 384; i++)
        {
            a[i] = (float)(rng.NextDouble() * 2 - 1);
            b[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        // Warm up
        ComplexityClassifier.CosineSimilarity(a, b);

        var sw = Stopwatch.StartNew();
        float result = 0;
        for (var i = 0; i < iterations; i++)
            result = ComplexityClassifier.CosineSimilarity(a, b);
        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"TensorPrimitives CosineSimilarity: {opsPerSec:N0} ops/sec (384-dim), result={result:F4}");

        opsPerSec.Should().BeGreaterThan(200_000,
            "SIMD cosine similarity on 384-dim vectors should exceed 200K ops/sec");
    }

    // ── B9: Duplicate query embedding check ──────────────────────────────

    [Fact]
    public async Task B9_Pipeline_SharedEmbeddingCache()
    {
        var counting = new CountingEmbeddingService();
        var classifier = new ComplexityClassifier(counting);
        var conceptClassifier = new ConceptClassifier(counting);
        var analyzers = CreateAnalyzers(counting);
        var refiner = new SentinelRefiner();
        var pipeline = new DecompositionPipeline(classifier, conceptClassifier, analyzers, refiner, counting);

        // Run a query that triggers both ComplexityClassifier and ConceptClassifier embedding
        // Need 2+ clauses to trigger archetype matching in ComplexityClassifier
        await pipeline.DecomposeAsync(
            "How does X compare to Y, and what changed over time?",
            hasDateTimes: true);

        output.WriteLine($"Pipeline embed calls: {counting.SingleEmbedCalls} single, " +
                         $"{counting.BatchEmbedCalls} batch, {counting.TotalTextsEmbedded} total");

        // After optimization: query should be embedded at most once (via shared embeddingCache),
        // not separately in ComplexityClassifier + ConceptClassifier
        counting.SingleEmbedCalls.Should().BeLessThanOrEqualTo(2,
            "query embedding should be shared via embeddingCache between classifiers");
    }

    // ── B11: ToLowerInvariant elimination ─────────────────────────────────

    [Fact]
    public void B11_CountClauses_NoAllocation()
    {
        var query = "Compare Azure and AWS pricing models and also show me the latest trends";

        // Warm up
        ComplexityClassifier.CountClauses(query);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
            ComplexityClassifier.CountClauses(query);

        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var allocBytes = allocAfter - allocBefore;

        output.WriteLine($"CountClauses x1000: {allocBytes:N0} bytes allocated");

        allocBytes.Should().BeLessThan(1024,
            "CountClauses should not allocate via ToLowerInvariant");
    }

    [Fact]
    public void B11_HasToolUseSignals_NoAllocation()
    {
        var query = "Go to C:/test, index all the markdown files, build a knowledge base";

        // Warm up
        ComplexityClassifier.HasToolUseSignals(query);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
            ComplexityClassifier.HasToolUseSignals(query);

        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var allocBytes = allocAfter - allocBefore;

        output.WriteLine($"HasToolUseSignals x1000: {allocBytes:N0} bytes allocated");

        allocBytes.Should().BeLessThan(1024,
            "HasToolUseSignals should not allocate via ToLowerInvariant");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DecompositionPipeline CreatePipelineWithEmbedding()
    {
        var embedding = new CountingEmbeddingService();
        var classifier = new ComplexityClassifier(embedding);
        var conceptClassifier = new ConceptClassifier(embedding);
        var analyzers = CreateAnalyzers(embedding);
        var refiner = new SentinelRefiner();

        return new DecompositionPipeline(classifier, conceptClassifier, analyzers, refiner, embedding);
    }

    private static IQueryAnalyzer[] CreateAnalyzers(IEmbeddingService embedding) =>
    [
        new ReferenceExtractor(),
        new StructuralAnalyzer(embedding),
        new EntityRelationAnalyzer(embedding),
        new TemporalAnalyzer(),
        new SemanticClusterAnalyzer(embedding),
        new ToolUseAnalyzer(embedding)
    ];

    /// <summary>
    /// Zero-allocation word count using ReadOnlySpan.
    /// This is the target implementation for B6 optimization.
    /// </summary>
    internal static int CountWordsSpan(ReadOnlySpan<char> text)
    {
        var count = 0;
        var inWord = false;

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }
}
