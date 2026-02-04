using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using EmbeddingConfig = DoomSummarizer.Models.EmbeddingConfig;

namespace DoomSummarizer.Services;

/// <summary>
///     Factory for creating IEmbeddingService instances from DoomSummarizer config.
///     Bridges DoomSummarizer's EmbeddingConfig to DocSummarizer.Core's OnnxEmbeddingService.
///     Wraps the ONNX service in an LFU cache to avoid recomputing identical embeddings.
/// </summary>
public static class EmbeddingFactory
{
    /// <summary>
    ///     Default LFU cache capacity. 8192 entries × 384 floats × 4 bytes ≈ 12 MB —
    ///     negligible memory for significant compute savings on repeated queries,
    ///     anchor embeddings, and entity names.
    /// </summary>
    private const int DefaultCacheCapacity = 8192;

    /// <summary>
    ///     Create an initialized IEmbeddingService using ONNX (default, local, no API keys).
    ///     Model and quantization are configurable via <see cref="EmbeddingConfig.Model" /> and
    ///     <see cref="EmbeddingConfig.Quantized" />.
    ///     Automatically wraps in an LFU cache for deduplication of repeated embeddings.
    /// </summary>
    public static async Task<IEmbeddingService> CreateAsync(
        EmbeddingConfig? config = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var model = ParseModel(config?.Model);
        var quantized = config?.Quantized ?? true;

        // Look up model info to derive max sequence length
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized);

        var onnxConfig = new OnnxConfig
        {
            EmbeddingModel = model,
            UseQuantized = quantized,
            MaxEmbeddingSequenceLength = modelInfo.MaxSequenceLength,
            ExecutionProvider = ParseExecutionProvider(config?.ExecutionProvider),
            GpuDeviceId = config?.GpuDeviceId ?? 0,
            ModelDirectory = GetModelDirectory()
        };

        var service = new OnnxEmbeddingService(onnxConfig, false);
        onStatus?.Invoke($"Initializing {modelInfo.Name}{(quantized ? " (quantized)" : "")}...");
        await service.InitializeAsync(ct);
        onStatus?.Invoke($"{modelInfo.Name} ready ({modelInfo.EmbeddingDimension}d, {modelInfo.MaxSequenceLength} seq)");

        // Wrap in LFU cache — avoids recomputing embeddings for repeated queries,
        // quality/sentiment anchors, entity names, and dedup comparisons.
        return new CachingEmbeddingService(service, DefaultCacheCapacity);
    }

    /// <summary>
    ///     Resolve a model name string from config to the <see cref="OnnxEmbeddingModel" /> enum.
    ///     Accepts registry names (e.g. "all-MiniLM-L6-v2"), enum names, and common aliases.
    /// </summary>
    internal static OnnxEmbeddingModel ParseModel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return OnnxEmbeddingModel.AllMiniLmL6V2;

        return value.Trim().ToLowerInvariant() switch
        {
            "all-minilm-l6-v2" or "allminilml6v2" or "minilm" => OnnxEmbeddingModel.AllMiniLmL6V2,
            "bge-small-en-v1.5" or "bgesmalenv15" or "bge-small" or "bge" => OnnxEmbeddingModel.BgeSmallEnV15,
            "gte-small" or "gtesmall" or "gte" => OnnxEmbeddingModel.GteSmall,
            "multi-qa-minilm-l6-cos-v1" or "multiqaminilm" or "multi-qa" or "multiqa" =>
                OnnxEmbeddingModel.MultiQaMiniLm,
            "paraphrase-minilm-l3-v2" or "paraphraseminilml3" or "paraphrase" =>
                OnnxEmbeddingModel.ParaphraseMiniLmL3,
            _ => OnnxEmbeddingModel.AllMiniLmL6V2 // Fallback to default
        };
    }

    /// <summary>
    ///     Build an <see cref="OnnxConfig" /> from DoomSummarizer's <see cref="EmbeddingConfig" />.
    ///     Use this when you need a consistent OnnxConfig (e.g. for ArticleProcessor)
    ///     without creating a full IEmbeddingService.
    /// </summary>
    public static OnnxConfig BuildOnnxConfig(EmbeddingConfig? config = null)
    {
        var model = ParseModel(config?.Model);
        var quantized = config?.Quantized ?? true;
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized);

        return new OnnxConfig
        {
            EmbeddingModel = model,
            UseQuantized = quantized,
            MaxEmbeddingSequenceLength = modelInfo.MaxSequenceLength,
            ExecutionProvider = ParseExecutionProvider(config?.ExecutionProvider),
            GpuDeviceId = config?.GpuDeviceId ?? 0,
            ModelDirectory = GetModelDirectory()
        };
    }

    private static OnnxExecutionProvider ParseExecutionProvider(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "cpu" => OnnxExecutionProvider.Cpu,
            "cuda" => OnnxExecutionProvider.Cuda,
            "directml" or "dml" => OnnxExecutionProvider.DirectMl,
            _ => OnnxExecutionProvider.Auto
        };
    }

    private static string GetModelDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".doomsummarizer",
            "models");
    }
}