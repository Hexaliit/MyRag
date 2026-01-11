using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VideoSummarizer.Core.Services;

/// <summary>
/// Batch CLIP embedding service for keyframe processing.
/// Processes multiple images in a single GPU pass for 3-5x speedup.
/// </summary>
public class BatchClipEmbeddingService
{
    private readonly ImageConfig _config;
    private readonly ModelDownloader? _modelDownloader;
    private readonly OnnxSessionFactory? _sessionFactory;
    private readonly ILogger<BatchClipEmbeddingService> _logger;

    private static InferenceSession? _clipSession;
    private static readonly object _modelLock = new();

    // CLIP ViT-B/32 parameters
    private const int ClipImageSize = 224;
    private const int ClipEmbeddingSize = 512;
    private const int DefaultBatchSize = 8; // Process 8 images at a time

    // CLIP normalization values
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
    private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

    public BatchClipEmbeddingService(
        IOptions<ImageConfig> config,
        ILogger<BatchClipEmbeddingService> logger,
        ModelDownloader? modelDownloader = null,
        OnnxSessionFactory? sessionFactory = null)
    {
        _config = config.Value;
        _logger = logger;
        _modelDownloader = modelDownloader;
        _sessionFactory = sessionFactory;
    }

    /// <summary>
    /// Generate CLIP embeddings for multiple images in batches.
    /// Significantly faster than processing one at a time.
    /// </summary>
    /// <param name="framePaths">Dictionary of frame index -> image path</param>
    /// <param name="batchSize">Number of images per batch (default 8)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary of frame index -> normalized embedding</returns>
    public async Task<Dictionary<int, float[]>> GenerateBatchEmbeddingsAsync(
        Dictionary<int, string> framePaths,
        int batchSize = DefaultBatchSize,
        CancellationToken ct = default)
    {
        var results = new Dictionary<int, float[]>();

        if (!_config.EnableClipEmbedding)
        {
            _logger.LogInformation("CLIP embedding is disabled, skipping batch generation");
            return results;
        }

        // Load CLIP model
        var session = await GetOrLoadClipModelAsync(ct);
        if (session == null)
        {
            _logger.LogWarning("CLIP model not available, skipping batch embedding");
            return results;
        }

        var frameList = framePaths.ToList();
        var totalFrames = frameList.Count;
        var batchCount = (totalFrames + batchSize - 1) / batchSize;

        _logger.LogInformation(
            "Processing {Count} frames in {Batches} batches (size {BatchSize})",
            totalFrames, batchCount, batchSize);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int batchIdx = 0; batchIdx < batchCount; batchIdx++)
        {
            ct.ThrowIfCancellationRequested();

            var batch = frameList
                .Skip(batchIdx * batchSize)
                .Take(batchSize)
                .ToList();

            try
            {
                var batchResults = await ProcessBatchAsync(batch, session, ct);

                foreach (var (frameIndex, embedding) in batchResults)
                {
                    results[frameIndex] = embedding;
                }

                _logger.LogDebug(
                    "Batch {Batch}/{Total}: processed {Count} frames",
                    batchIdx + 1, batchCount, batch.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch {Batch} failed, falling back to single-image processing", batchIdx);

                // Fallback: process failed batch images one at a time
                foreach (var (frameIndex, path) in batch)
                {
                    try
                    {
                        var embedding = await ProcessSingleImageAsync(path, session, ct);
                        if (embedding != null)
                        {
                            results[frameIndex] = embedding;
                        }
                    }
                    catch (Exception innerEx)
                    {
                        _logger.LogWarning(innerEx, "Failed to process frame {Index}", frameIndex);
                    }
                }
            }
        }

        stopwatch.Stop();
        var avgMs = totalFrames > 0 ? stopwatch.ElapsedMilliseconds / totalFrames : 0;

        _logger.LogInformation(
            "Batch CLIP complete: {Count} embeddings in {Time}ms ({Avg}ms/frame avg)",
            results.Count, stopwatch.ElapsedMilliseconds, avgMs);

        return results;
    }

    /// <summary>
    /// Process a batch of images in a single GPU pass.
    /// </summary>
    private async Task<Dictionary<int, float[]>> ProcessBatchAsync(
        List<KeyValuePair<int, string>> batch,
        InferenceSession session,
        CancellationToken ct)
    {
        var results = new Dictionary<int, float[]>();
        var batchSize = batch.Count;

        // Create batch tensor [batchSize, 3, 224, 224]
        var tensor = new DenseTensor<float>(new[] { batchSize, 3, ClipImageSize, ClipImageSize });

        // Load and preprocess all images in parallel
        var preprocessTasks = batch.Select(async (kvp, idx) =>
        {
            var (frameIndex, path) = kvp;
            await using var stream = File.OpenRead(path);
            using var image = await Image.LoadAsync<Rgb24>(stream, ct);

            // Resize to CLIP input size
            image.Mutate(x => x.Resize(ClipImageSize, ClipImageSize));

            // Fill tensor for this batch index
            for (int y = 0; y < ClipImageSize; y++)
            {
                for (int x = 0; x < ClipImageSize; x++)
                {
                    var pixel = image[x, y];
                    tensor[idx, 0, y, x] = (pixel.R / 255f - Mean[0]) / Std[0];
                    tensor[idx, 1, y, x] = (pixel.G / 255f - Mean[1]) / Std[1];
                    tensor[idx, 2, y, x] = (pixel.B / 255f - Mean[2]) / Std[2];
                }
            }

            return (idx, frameIndex);
        }).ToList();

        var indexMappings = await Task.WhenAll(preprocessTasks);

        // Run batch inference
        var inputName = session.InputNames.FirstOrDefault() ?? "input";
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var outputResults = session.Run(inputs);
        var output = outputResults.FirstOrDefault();

        if (output == null)
        {
            _logger.LogWarning("CLIP batch inference returned no output");
            return results;
        }

        // Extract embeddings for each image in batch
        var outputTensor = output.AsEnumerable<float>().ToArray();

        foreach (var (batchIdx, frameIndex) in indexMappings)
        {
            var embedding = new float[ClipEmbeddingSize];
            Array.Copy(outputTensor, batchIdx * ClipEmbeddingSize, embedding, 0, ClipEmbeddingSize);

            // L2 normalize
            var normalized = NormalizeEmbedding(embedding);
            results[frameIndex] = normalized;
        }

        return results;
    }

    /// <summary>
    /// Process a single image (fallback for batch failures).
    /// </summary>
    private async Task<float[]?> ProcessSingleImageAsync(
        string imagePath,
        InferenceSession session,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(imagePath);
        using var image = await Image.LoadAsync<Rgb24>(stream, ct);

        image.Mutate(x => x.Resize(ClipImageSize, ClipImageSize));

        var tensor = new DenseTensor<float>(new[] { 1, 3, ClipImageSize, ClipImageSize });

        for (int y = 0; y < ClipImageSize; y++)
        {
            for (int x = 0; x < ClipImageSize; x++)
            {
                var pixel = image[x, y];
                tensor[0, 0, y, x] = (pixel.R / 255f - Mean[0]) / Std[0];
                tensor[0, 1, y, x] = (pixel.G / 255f - Mean[1]) / Std[1];
                tensor[0, 2, y, x] = (pixel.B / 255f - Mean[2]) / Std[2];
            }
        }

        var inputName = session.InputNames.FirstOrDefault() ?? "input";
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var results = session.Run(inputs);
        var output = results.FirstOrDefault()?.AsEnumerable<float>()?.ToArray();

        return output != null ? NormalizeEmbedding(output) : null;
    }

    private async Task<InferenceSession?> GetOrLoadClipModelAsync(CancellationToken ct)
    {
        if (_clipSession != null) return _clipSession;

        var modelPath = await GetOrDownloadClipModelAsync(ct);
        if (modelPath == null) return null;

        lock (_modelLock)
        {
            if (_clipSession != null) return _clipSession;

            try
            {
                _logger.LogInformation("Loading CLIP model for batch processing: {Path}", modelPath);

                if (_sessionFactory != null)
                {
                    var provider = _sessionFactory.GetEffectiveProvider();
                    _logger.LogInformation("CLIP batch using ONNX provider: {Provider}", provider);
                    _clipSession = _sessionFactory.CreateSession(modelPath);
                }
                else
                {
                    var options = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                    };
                    _clipSession = new InferenceSession(modelPath, options);
                }

                return _clipSession;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load CLIP model");
                return null;
            }
        }
    }

    private async Task<string?> GetOrDownloadClipModelAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_config.ClipModelPath) && File.Exists(_config.ClipModelPath))
        {
            return _config.ClipModelPath;
        }

        if (_modelDownloader != null)
        {
            try
            {
                var path = await _modelDownloader.GetModelPathAsync(ModelType.ClipVisual, ct);
                if (path != null && File.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get CLIP model from ModelDownloader");
            }
        }

        // Fallback paths
        var paths = new[]
        {
            Path.Combine(_config.ModelsDirectory ?? "./models", "clip", "visual.onnx"),
            Path.Combine(_config.ModelsDirectory ?? "./models", "clip", "clip-vit-b-32-visual.onnx"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LucidRAG", "models", "clip", "visual.onnx")
        };

        return paths.FirstOrDefault(File.Exists);
    }

    private static float[] NormalizeEmbedding(float[] embedding)
    {
        var norm = Math.Sqrt(embedding.Sum(x => x * x));
        if (norm < 1e-10) return embedding;

        var normalized = new float[embedding.Length];
        for (int i = 0; i < embedding.Length; i++)
        {
            normalized[i] = embedding[i] / (float)norm;
        }

        return normalized;
    }
}
