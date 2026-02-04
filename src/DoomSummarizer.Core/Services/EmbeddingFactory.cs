using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using EmbeddingConfig = DoomSummarizer.Models.EmbeddingConfig;

namespace DoomSummarizer.Services;

/// <summary>
///     Factory for creating IEmbeddingService instances from DoomSummarizer config.
///     Bridges DoomSummarizer's EmbeddingConfig to DocSummarizer.Core's OnnxEmbeddingService.
/// </summary>
public static class EmbeddingFactory
{
    /// <summary>
    ///     Create an initialized IEmbeddingService using ONNX (default, local, no API keys).
    ///     Uses the same all-MiniLM-L6-v2 model as the old EmbeddingService.
    /// </summary>
    public static async Task<IEmbeddingService> CreateAsync(
        EmbeddingConfig? config = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var onnxConfig = new OnnxConfig
        {
            EmbeddingModel = OnnxEmbeddingModel.AllMiniLmL6V2,
            UseQuantized = false, // Match old EmbeddingService behavior (non-quantized)
            MaxEmbeddingSequenceLength = 256,
            ExecutionProvider = OnnxExecutionProvider.Auto,
            ModelDirectory = GetModelDirectory()
        };

        var service = new OnnxEmbeddingService(onnxConfig, false);
        onStatus?.Invoke("Initializing embedding model...");
        await service.InitializeAsync(ct);
        onStatus?.Invoke("Embedding model ready");
        return service;
    }

    private static string GetModelDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".doomsummarizer",
            "models");
    }
}