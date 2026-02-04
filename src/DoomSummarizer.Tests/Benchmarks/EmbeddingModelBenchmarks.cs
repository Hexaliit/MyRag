using System.Diagnostics;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Xunit.Abstractions;
using EmbeddingConfig = DoomSummarizer.Models.EmbeddingConfig;

namespace DoomSummarizer.Tests.Benchmarks;

/// <summary>
///     Real ONNX embedding benchmarks: single/batch latency, throughput, and model comparison.
///     These use actual GPU/CPU inference — run locally, not in CI.
///     Skips gracefully if models aren't downloaded.
/// </summary>
[Trait("Category", "Integration")]
public class EmbeddingModelBenchmarks(ITestOutputHelper output)
{
    private static readonly string[] SampleTexts =
    [
        "Machine learning algorithms transform raw data into actionable predictions.",
        "The quick brown fox jumps over the lazy dog near the river bank.",
        "Retrieval-augmented generation combines search with language models for grounded answers.",
        "Shakespeare wrote Hamlet, one of the most famous tragedies in English literature.",
        "Kubernetes orchestrates container workloads across distributed clusters.",
        "Climate change impacts global food security through droughts and floods.",
        "Neural networks approximate complex functions through layers of weighted transformations.",
        "The Rust programming language prevents memory safety bugs at compile time."
    ];

    /// <summary>
    ///     Compare all available models: single embed latency, batch throughput, embedding quality.
    /// </summary>
    [Fact]
    public async Task CompareAllModels()
    {
        output.WriteLine("Model                        | Quant | Single (ms) | Batch/8 (ms) | Batch/32 (ms) | Dim | Seq");
        output.WriteLine("-----------------------------|-------|-------------|--------------|---------------|-----|----");

        var models = Enum.GetValues<OnnxEmbeddingModel>();
        foreach (var model in models)
        foreach (var quantized in new[] { true, false })
        {
            try
            {
                var config = new EmbeddingConfig
                {
                    Model = OnnxModelRegistry.GetEmbeddingModel(model, quantized).Name,
                    Quantized = quantized,
                    ExecutionProvider = "auto"
                };

                using var service = await CreateService(config);
                if (service is null) continue;

                var info = OnnxModelRegistry.GetEmbeddingModel(model, quantized);

                // Warm up
                await service.EmbedAsync(SampleTexts[0]);

                // Single embed latency (median of 10)
                var singleTimes = new List<double>();
                for (var i = 0; i < 10; i++)
                {
                    var sw = Stopwatch.StartNew();
                    await service.EmbedAsync(SampleTexts[i % SampleTexts.Length]);
                    sw.Stop();
                    singleTimes.Add(sw.Elapsed.TotalMilliseconds);
                }

                singleTimes.Sort();
                var singleMedian = singleTimes[singleTimes.Count / 2];

                // Batch/8 latency
                var sw8 = Stopwatch.StartNew();
                await service.EmbedBatchAsync(SampleTexts);
                sw8.Stop();
                var batch8Ms = sw8.Elapsed.TotalMilliseconds;

                // Batch/32 latency (repeat sample texts to get 32)
                var batch32 = Enumerable.Range(0, 4).SelectMany(_ => SampleTexts).ToArray();
                var sw32 = Stopwatch.StartNew();
                await service.EmbedBatchAsync(batch32);
                sw32.Stop();
                var batch32Ms = sw32.Elapsed.TotalMilliseconds;

                output.WriteLine(
                    $"{info.Name,-29}| {(quantized ? "INT8 " : "FP32 ")} | {singleMedian,10:F2} | {batch8Ms,12:F2} | {batch32Ms,13:F2} | {info.EmbeddingDimension,3} | {info.MaxSequenceLength,3}");
            }
            catch (Exception ex)
            {
                var info = OnnxModelRegistry.GetEmbeddingModel(model, quantized);
                output.WriteLine(
                    $"{info.Name,-29}| {(quantized ? "INT8 " : "FP32 ")} | {'—',10} | {'—',12} | {'—',13} | --- | --- ({ex.Message[..Math.Min(60, ex.Message.Length)]})");
            }
        }
    }

    /// <summary>
    ///     Default model throughput: embeddings per second for batch sizes 1, 8, 32, 64.
    /// </summary>
    [Fact]
    public async Task DefaultModel_BatchThroughput()
    {
        var config = new EmbeddingConfig { Quantized = true, ExecutionProvider = "auto" };
        using var service = await CreateService(config);
        if (service is null)
        {
            output.WriteLine("SKIP: default model not available");
            return;
        }

        // Warm up
        await service.EmbedBatchAsync(SampleTexts);

        output.WriteLine("Batch Size | Total (ms) | Per-Item (ms) | Items/sec");
        output.WriteLine("-----------|------------|---------------|----------");

        foreach (var batchSize in new[] { 1, 8, 32, 64 })
        {
            var texts = Enumerable.Range(0, batchSize)
                .Select(i => SampleTexts[i % SampleTexts.Length]).ToArray();

            var sw = Stopwatch.StartNew();
            await service.EmbedBatchAsync(texts);
            sw.Stop();

            var totalMs = sw.Elapsed.TotalMilliseconds;
            var perItem = totalMs / batchSize;
            var itemsPerSec = batchSize / sw.Elapsed.TotalSeconds;

            output.WriteLine($"{batchSize,10} | {totalMs,10:F2} | {perItem,13:F2} | {itemsPerSec,8:F0}");
        }
    }

    /// <summary>
    ///     LFU cache effectiveness: compare cached vs uncached throughput.
    /// </summary>
    [Fact]
    public async Task CachingService_HitRateAndSpeedup()
    {
        var config = new EmbeddingConfig { Quantized = true, ExecutionProvider = "auto" };
        using var rawService = await CreateService(config);
        if (rawService is null)
        {
            output.WriteLine("SKIP: default model not available");
            return;
        }

        var cached = new CachingEmbeddingService(rawService);

        // Cold: embed all samples (fills cache)
        var swCold = Stopwatch.StartNew();
        foreach (var text in SampleTexts)
            await cached.EmbedAsync(text);
        swCold.Stop();

        // Hot: re-embed all samples (should hit cache)
        var swHot = Stopwatch.StartNew();
        foreach (var text in SampleTexts)
            await cached.EmbedAsync(text);
        swHot.Stop();

        var stats = cached.GetStats();
        output.WriteLine($"Cold pass: {swCold.Elapsed.TotalMilliseconds:F2}ms");
        output.WriteLine($"Hot pass:  {swHot.Elapsed.TotalMilliseconds:F2}ms");
        output.WriteLine($"Speedup:   {swCold.Elapsed.TotalMilliseconds / Math.Max(swHot.Elapsed.TotalMilliseconds, 0.001):F1}x");
        output.WriteLine($"Hit rate:  {stats.HitRate:P0} ({stats.Hits}h/{stats.Misses}m)");

        Assert.Equal(SampleTexts.Length, stats.Hits);
        Assert.Equal(SampleTexts.Length, stats.Misses);
    }

    /// <summary>
    ///     Create an OnnxEmbeddingService, returning null if the model can't be loaded.
    /// </summary>
    private async Task<OnnxEmbeddingService?> CreateService(EmbeddingConfig config)
    {
        try
        {
            var model = EmbeddingFactory.ParseModel(config.Model);
            var quantized = config.Quantized;
            var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized);

            var onnxConfig = new OnnxConfig
            {
                EmbeddingModel = model,
                UseQuantized = quantized,
                MaxEmbeddingSequenceLength = modelInfo.MaxSequenceLength,
                ExecutionProvider = OnnxExecutionProvider.Auto,
                GpuDeviceId = 0,
                ModelDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".doomsummarizer", "models")
            };

            var service = new OnnxEmbeddingService(onnxConfig, false);
            await service.InitializeAsync();
            return service;
        }
        catch
        {
            return null;
        }
    }
}
