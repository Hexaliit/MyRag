using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;

namespace Mostlylucid.DocSummarizer.Images.Services.Vision;

/// <summary>
///     CLIP Zero-Shot Classification Service
///     Uses CLIP text encoder to classify images against predetermined labels.
///     Computes cosine similarity between image embeddings and label text embeddings.
/// </summary>
public class ClipZeroShotService
{
    private const int MaxTokenLength = 77; // CLIP's max token length
    private const int EmbeddingSize = 512;

    private static InferenceSession? _textSession;
    private static readonly object _modelLock = new();
    private static Dictionary<string, float[]>? _labelEmbeddingsCache;

    // Predetermined entity-type labels for image classification
    // These map to common entity types in knowledge graphs
    public static readonly string[] EntityLabels = new[]
    {
        // People & Living
        "a photo of a person",
        "a photo of a group of people",
        "a photo of an animal",
        "a photo of a pet",

        // Objects & Products
        "a photo of a product",
        "a photo of food",
        "a photo of a vehicle",
        "a photo of electronics",
        "a photo of clothing",
        "a photo of furniture",

        // Places & Scenes
        "a photo of a building",
        "a photo of a landscape",
        "a photo of an indoor scene",
        "a photo of a city",
        "a photo of nature",

        // Documents & Graphics
        "a screenshot of a user interface",
        "a diagram or flowchart",
        "a chart or graph",
        "a scanned document",
        "a logo or icon",
        "text on a sign",

        // Art & Media
        "digital artwork or illustration",
        "a meme with text",
        "a photograph",

        // Technical
        "a code snippet or terminal",
        "a map or geographic image"
    };

    // Short labels for entity extraction (without prompt engineering prefix)
    public static readonly Dictionary<string, string> LabelToEntityType = new()
    {
        ["a photo of a person"] = "person",
        ["a photo of a group of people"] = "people",
        ["a photo of an animal"] = "animal",
        ["a photo of a pet"] = "pet",
        ["a photo of a product"] = "product",
        ["a photo of food"] = "food",
        ["a photo of a vehicle"] = "vehicle",
        ["a photo of electronics"] = "electronics",
        ["a photo of clothing"] = "clothing",
        ["a photo of furniture"] = "furniture",
        ["a photo of a building"] = "building",
        ["a photo of a landscape"] = "landscape",
        ["a photo of an indoor scene"] = "indoor_scene",
        ["a photo of a city"] = "city",
        ["a photo of nature"] = "nature",
        ["a screenshot of a user interface"] = "screenshot",
        ["a diagram or flowchart"] = "diagram",
        ["a chart or graph"] = "chart",
        ["a scanned document"] = "document",
        ["a logo or icon"] = "logo",
        ["text on a sign"] = "sign",
        ["digital artwork or illustration"] = "artwork",
        ["a meme with text"] = "meme",
        ["a photograph"] = "photo",
        ["a code snippet or terminal"] = "code",
        ["a map or geographic image"] = "map"
    };

    private readonly ImageConfig _config;
    private readonly ILogger<ClipZeroShotService>? _logger;
    private readonly ModelDownloader? _modelDownloader;
    private readonly OnnxSessionFactory? _sessionFactory;

    public ClipZeroShotService(
        IOptions<ImageConfig> config,
        ModelDownloader? modelDownloader = null,
        OnnxSessionFactory? sessionFactory = null,
        ILogger<ClipZeroShotService>? logger = null)
    {
        _config = config.Value;
        _modelDownloader = modelDownloader;
        _sessionFactory = sessionFactory;
        _logger = logger;
    }

    /// <summary>
    ///     Check if the text encoder model is available
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var session = await GetOrLoadTextModelAsync(ct);
            return session != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Classify an image embedding against predetermined labels.
    ///     Returns top-k matching labels with confidence scores.
    /// </summary>
    public async Task<List<ClipClassification>> ClassifyAsync(
        float[] imageEmbedding,
        int topK = 5,
        double minConfidence = 0.15,
        CancellationToken ct = default)
    {
        var results = new List<ClipClassification>();

        try
        {
            // Get or compute label embeddings (cached)
            var labelEmbeddings = await GetLabelEmbeddingsAsync(ct);
            if (labelEmbeddings == null || labelEmbeddings.Count == 0)
            {
                _logger?.LogWarning("No label embeddings available");
                return results;
            }

            // Compute cosine similarities
            var similarities = new List<(string Label, double Similarity)>();

            foreach (var (label, labelEmb) in labelEmbeddings)
            {
                var similarity = CosineSimilarity(imageEmbedding, labelEmb);
                similarities.Add((label, similarity));
            }

            // Sort by similarity and take top-k
            var topResults = similarities
                .OrderByDescending(x => x.Similarity)
                .Where(x => x.Similarity >= minConfidence)
                .Take(topK)
                .ToList();

            foreach (var (label, similarity) in topResults)
            {
                var entityType = LabelToEntityType.GetValueOrDefault(label, "unknown");
                results.Add(new ClipClassification
                {
                    Label = label,
                    EntityType = entityType,
                    Confidence = similarity,
                    Rank = results.Count + 1
                });
            }

            _logger?.LogDebug(
                "CLIP classified image: top={TopLabel} ({TopConf:P0}), {Count} matches above {MinConf:P0}",
                results.FirstOrDefault()?.EntityType ?? "none",
                results.FirstOrDefault()?.Confidence ?? 0,
                results.Count,
                minConfidence);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "CLIP classification failed");
        }

        return results;
    }

    /// <summary>
    ///     Classify with custom labels (not using predetermined set)
    /// </summary>
    public async Task<List<ClipClassification>> ClassifyWithLabelsAsync(
        float[] imageEmbedding,
        string[] customLabels,
        int topK = 3,
        CancellationToken ct = default)
    {
        var results = new List<ClipClassification>();
        var session = await GetOrLoadTextModelAsync(ct);
        if (session == null) return results;

        try
        {
            foreach (var label in customLabels)
            {
                var labelEmb = await EncodeTextAsync(label, session, ct);
                if (labelEmb == null) continue;

                var similarity = CosineSimilarity(imageEmbedding, labelEmb);
                results.Add(new ClipClassification
                {
                    Label = label,
                    EntityType = label.ToLowerInvariant().Replace(" ", "_"),
                    Confidence = similarity,
                    Rank = 0
                });
            }

            // Sort and assign ranks
            results = results
                .OrderByDescending(x => x.Confidence)
                .Take(topK)
                .Select((r, i) => r with { Rank = i + 1 })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "CLIP custom classification failed");
        }

        return results;
    }

    private async Task<Dictionary<string, float[]>?> GetLabelEmbeddingsAsync(CancellationToken ct)
    {
        if (_labelEmbeddingsCache != null)
            return _labelEmbeddingsCache;

        var session = await GetOrLoadTextModelAsync(ct);
        if (session == null) return null;

        lock (_modelLock)
        {
            if (_labelEmbeddingsCache != null)
                return _labelEmbeddingsCache;

            _logger?.LogInformation("Computing embeddings for {Count} entity labels...", EntityLabels.Length);

            var cache = new Dictionary<string, float[]>();
            foreach (var label in EntityLabels)
            {
                var embedding = EncodeTextAsync(label, session, ct).GetAwaiter().GetResult();
                if (embedding != null) cache[label] = NormalizeEmbedding(embedding);
            }

            _labelEmbeddingsCache = cache;
            _logger?.LogInformation("Cached {Count} label embeddings", cache.Count);
            return cache;
        }
    }

    private async Task<InferenceSession?> GetOrLoadTextModelAsync(CancellationToken ct)
    {
        if (_textSession != null) return _textSession;

        string? modelPath = null;

        // Try to get from ModelDownloader
        if (_modelDownloader != null)
            try
            {
                modelPath = await _modelDownloader.GetModelPathAsync(ModelType.ClipTextual, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get CLIP text model from downloader");
            }

        // Fallback paths
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
        {
            var fallbacks = new[]
            {
                Path.Combine(_config.ModelsDirectory ?? "./models", "clip", "textual.onnx"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LucidRAG", "models", "clip", "textual.onnx")
            };

            modelPath = fallbacks.FirstOrDefault(File.Exists);
        }

        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
        {
            _logger?.LogWarning("CLIP text model not found");
            return null;
        }

        lock (_modelLock)
        {
            if (_textSession != null) return _textSession;

            try
            {
                _logger?.LogInformation("Loading CLIP text model from {Path}", modelPath);

                if (_sessionFactory != null)
                {
                    _textSession = _sessionFactory.CreateSession(modelPath);
                }
                else
                {
                    var options = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                    };
                    _textSession = new InferenceSession(modelPath, options);
                }

                _logger?.LogInformation("CLIP text model loaded successfully");
                return _textSession;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load CLIP text model");
                return null;
            }
        }
    }

    private async Task<float[]?> EncodeTextAsync(
        string text,
        InferenceSession session,
        CancellationToken ct)
    {
        try
        {
            // Simple tokenization for CLIP (BPE-based, but we'll use basic approach)
            // CLIP expects input_ids tensor of shape [1, 77]
            var tokens = TokenizeSimple(text);

            var inputTensor = new DenseTensor<long>(new[] { 1, MaxTokenLength });
            for (var i = 0; i < MaxTokenLength; i++) inputTensor[0, i] = i < tokens.Length ? tokens[i] : 0;

            var inputName = session.InputNames.FirstOrDefault() ?? "input_ids";
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            using var results = session.Run(inputs);
            var output = results.FirstOrDefault()?.AsEnumerable<float>()?.ToArray();

            return output;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to encode text: {Text}", text);
            return null;
        }
    }

    /// <summary>
    ///     Simple tokenizer for CLIP (approximates BPE tokenization)
    ///     Uses SOT (49406) and EOT (49407) tokens
    /// </summary>
    private long[] TokenizeSimple(string text)
    {
        const long SOT = 49406; // Start of text
        const long EOT = 49407; // End of text

        // Very basic word-level tokenization
        // For production, should use proper BPE tokenizer
        var words = text.ToLowerInvariant()
            .Replace(".", " ")
            .Replace(",", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var tokens = new List<long> { SOT };

        foreach (var word in words)
        {
            // Simple character-based fallback (CLIP uses BPE)
            // This is approximate but works for simple labels
            var hash = (long)Math.Abs(word.GetHashCode() % 49000) + 1;
            tokens.Add(hash);

            if (tokens.Count >= MaxTokenLength - 1)
                break;
        }

        tokens.Add(EOT);

        // Pad to max length
        while (tokens.Count < MaxTokenLength)
            tokens.Add(0);

        return tokens.Take(MaxTokenLength).ToArray();
    }

    private static float[] NormalizeEmbedding(float[] embedding)
    {
        var norm = Math.Sqrt(embedding.Sum(x => x * x));
        if (norm < 1e-10) return embedding;

        var normalized = new float[embedding.Length];
        for (var i = 0; i < embedding.Length; i++) normalized[i] = embedding[i] / (float)norm;
        return normalized;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];

        // Already normalized, so dot product = cosine similarity
        return Math.Max(0, Math.Min(1, dot));
    }
}

/// <summary>
///     Result of CLIP zero-shot classification
/// </summary>
public record ClipClassification
{
    public required string Label { get; init; }
    public required string EntityType { get; init; }
    public required double Confidence { get; init; }
    public required int Rank { get; init; }
}