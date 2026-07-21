using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services.LmStudio;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace Mostlylucid.DocSummarizer.Services.Embeddings;

/// <summary>
///     Base class for embedding providers with common functionality
/// </summary>
public abstract class EmbeddingProviderBase : IEmbeddingClient
{
    protected readonly ILogger Logger;
    protected readonly UnifiedEmbeddingConfig Config;

    protected EmbeddingProviderBase(IOptions<UnifiedEmbeddingConfig> config, ILogger logger)
    {
        Config = config.Value;
        Logger = logger;
    }

    public abstract string ProviderName { get; }
    public abstract string ModelName { get; }
    public abstract int EmbeddingDimension { get; }

    public abstract Task InitializeAsync(CancellationToken ct = default);
    public abstract Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    public abstract Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
    public abstract Task<int> GetContextWindowAsync(CancellationToken ct = default);

    /// <summary>
    ///     Normalize embedding vector (L2 normalization)
    /// </summary>
    protected static float[] Normalize(float[] vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(x => x * x));
        if (magnitude == 0) return vector;

        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / magnitude);

        return vector;
    }

    /// <summary>
    ///     Average multiple embeddings
    /// </summary>
    protected static float[] AverageEmbeddings(float[][] embeddings)
    {
        if (embeddings.Length == 0) return [];
        if (embeddings.Length == 1) return embeddings[0];

        var dim = embeddings[0].Length;
        var result = new float[dim];

        foreach (var emb in embeddings)
            for (var i = 0; i < dim; i++)
                result[i] += emb[i];

        var count = embeddings.Length;
        for (var i = 0; i < dim; i++)
            result[i] /= count;

        return Normalize(result);
    }

    /// <summary>
    ///     Split text into chunks for embedding
    /// </summary>
    protected static List<string> SplitIntoChunks(string text, int maxChars, int overlap = 0)
    {
        var chunks = new List<string>();
        var stride = maxChars - overlap;

        for (var i = 0; i < text.Length; i += stride)
        {
            var len = Math.Min(maxChars, text.Length - i);
            chunks.Add(text.Substring(i, len));

            if (i + len >= text.Length) break;
        }

        return chunks;
    }
}

/// <summary>
///     LM Studio embedding provider (OpenAI-compatible API)
/// </summary>
public class LmStudioEmbeddingProvider : EmbeddingProviderBase
{
    private readonly LmStudioHttpClient _client;
    private readonly LmStudioConfig _lmStudioConfig;
    private int _dimension = -1;

    public override string ProviderName => "LM Studio";
    public override string ModelName => _lmStudioConfig.DefaultEmbeddingModel;

    public override int EmbeddingDimension
    {
        get
        {
            if (_dimension == -1)
                _dimension = EstimateDimension(ModelName);
            return _dimension;
        }
    }

    public LmStudioEmbeddingProvider(
        LmStudioHttpClient client,
        IOptions<UnifiedEmbeddingConfig> config,
        IOptions<LmStudioConfig> lmStudioConfig,
        ILogger<LmStudioEmbeddingProvider> logger)
        : base(config, logger)
    {
        _client = client;
        _lmStudioConfig = lmStudioConfig.Value;
    }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        Logger.LogInformation("Initializing LM Studio embedding provider with model: {Model}", ModelName);

        // Verify connection and model
        var healthy = await _client.IsHealthyAsync(ct);
        if (!healthy)
            throw new InvalidOperationException("LM Studio server is not healthy");

        // Auto-discover embedding model if not configured
        if (string.IsNullOrEmpty(ModelName))
        {
            var models = await _client.DiscoverEmbeddingModelsAsync(ct);
            var preferred = Config.LmStudio.PreferredEmbeddingModels;

            var selected = preferred
                .Select(p => models.FirstOrDefault(m => m.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(m => m != null)
                ?? models.FirstOrDefault();

            if (selected == null)
                throw new InvalidOperationException("No embedding models found in LM Studio");

            _lmStudioConfig.DefaultEmbeddingModel = selected.Name;
            _dimension = selected.Dimensions;

            Logger.LogInformation("Auto-selected embedding model: {Model} ({Dimension}d)", selected.Name, selected.Dimensions);
        }
        else
        {
            // Try to get dimension from model info
            var info = await _client.GetModelInfoAsync(ModelName, ct);
            if (info?.Capabilities != null)
                _dimension = EstimateDimension(ModelName);
        }

        Logger.LogInformation("LM Studio embedding provider initialized: {Model} ({Dimension}d)", ModelName, EmbeddingDimension);
    }

    public override async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return await _client.EmbedAsync(text, ct);
    }

    public override async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        return await _client.EmbedBatchAsync(textList, ct);
    }

    public override async Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        var info = await _client.GetModelInfoAsync(ModelName, ct);
        return info?.Capabilities?.MaxContextLength ?? 8192;
    }

    private static int EstimateDimension(string modelName)
    {
        var lower = modelName.ToLowerInvariant();

        if (lower.Contains("bge-m3") || lower.Contains("jina-embeddings-v3"))
            return 1024;
        if (lower.Contains("bge-large") || lower.Contains("e5-large") ||
            lower.Contains("gte-large") || lower.Contains("jina-"))
            return 1024;
        if (lower.Contains("bge-base") || lower.Contains("e5-base") ||
            lower.Contains("gte-base") || lower.Contains("nomic") ||
            lower.Contains("multilingual-e5"))
            return 768;
        if (lower.Contains("bge-small") || lower.Contains("e5-small") ||
            lower.Contains("gte-small") || lower.Contains("all-minilm") ||
            lower.Contains("minilm"))
            return 384;

        return 768; // Default
    }
}

/// <summary>
///     OpenAI embedding provider
/// </summary>
public class OpenAIEmbeddingProvider : EmbeddingProviderBase
{
    private readonly HttpClient _httpClient;
    private readonly OpenAIEmbeddingConfig _openAIConfig;
    private int _dimension = -1;

    public override string ProviderName => "OpenAI";
    public override string ModelName => Config.Model;

    public override int EmbeddingDimension
    {
        get
        {
            if (_dimension == -1)
                _dimension = EstimateDimension(ModelName);
            return _dimension;
        }
    }

    public OpenAIEmbeddingProvider(
        HttpClient httpClient,
        IOptions<UnifiedEmbeddingConfig> config,
        ILogger<OpenAIEmbeddingProvider> logger)
        : base(config, logger)
    {
        _httpClient = httpClient;
        _openAIConfig = config.Value.OpenAI;
    }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_openAIConfig.ApiKey))
            throw new InvalidOperationException("OpenAI API key not configured");

        _httpClient.BaseAddress = new Uri(_openAIConfig.BaseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _openAIConfig.ApiKey);
        if (!string.IsNullOrEmpty(_openAIConfig.Organization))
            _httpClient.DefaultRequestHeaders.Add("OpenAI-Organization", _openAIConfig.Organization);

        // Auto-detect dimension based on model
        _dimension = EstimateDimension(ModelName);
        Logger.LogInformation("OpenAI embedding provider initialized: {Model} ({Dimension}d)", ModelName, EmbeddingDimension);
    }

    public override async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var embeddings = await EmbedBatchAsync([text], ct);
        return embeddings[0];
    }

    public override async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        var request = new
        {
            model = ModelName,
            input = textList,
            encoding_format = "float"
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/embeddings", content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(responseJson);

        return result?.Data?.Select(d => d.Embedding).ToArray() ?? [];
    }

    public override async Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        return ModelName switch
        {
            var m when m.Contains("3-large") => 8192,
            var m when m.Contains("3-small") => 8192,
            var m when m.Contains("ada-002") => 8191,
            _ => 8192
        };
    }

    private static int EstimateDimension(string modelName)
    {
        var lower = modelName.ToLowerInvariant();
        return lower switch
        {
            var m when m.Contains("3-large") => 3072,
            var m when m.Contains("3-small") => 1536,
            var m when m.Contains("ada-002") => 1536,
            _ => 1536
        };
    }
}

/// <summary>
///     Azure OpenAI embedding provider
/// </summary>
public class AzureOpenAIEmbeddingProvider : EmbeddingProviderBase
{
    private readonly HttpClient _httpClient;
    private readonly AzureOpenAIEmbeddingConfig _azureConfig;
    private int _dimension = -1;

    public override string ProviderName => "Azure OpenAI";
    public override string ModelName => Config.Model;

    public override int EmbeddingDimension
    {
        get
        {
            if (_dimension == -1)
                _dimension = EstimateDimension(ModelName);
            return _dimension;
        }
    }

    public AzureOpenAIEmbeddingProvider(
        HttpClient httpClient,
        IOptions<UnifiedEmbeddingConfig> config,
        ILogger<AzureOpenAIEmbeddingProvider> logger)
        : base(config, logger)
    {
        _httpClient = httpClient;
        _azureConfig = config.Value.AzureOpenAI;
    }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_azureConfig.ApiKey) || string.IsNullOrEmpty(_azureConfig.Endpoint))
            throw new InvalidOperationException("Azure OpenAI API key and endpoint must be configured");

        _httpClient.BaseAddress = new Uri(_azureConfig.Endpoint.TrimEnd('/') + "/");
        _httpClient.DefaultRequestHeaders.Add("api-key", _azureConfig.ApiKey);

        _dimension = EstimateDimension(ModelName);
        Logger.LogInformation("Azure OpenAI embedding provider initialized: {Model} ({Dimension}d)", ModelName, EmbeddingDimension);
    }

    public override async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var embeddings = await EmbedBatchAsync([text], ct);
        return embeddings[0];
    }

    public override async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        var url = $"openai/deployments/{_azureConfig.DeploymentName}/embeddings?api-version={_azureConfig.ApiVersion}";

        var request = new
        {
            input = textList,
            encoding_format = "float"
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(responseJson);

        return result?.Data?.Select(d => d.Embedding).ToArray() ?? [];
    }

    public override Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        return Task.FromResult(8191);
    }

    private static int EstimateDimension(string modelName)
    {
        var lower = modelName.ToLowerInvariant();
        return lower switch
        {
            var m when m.Contains("3-large") => 3072,
            var m when m.Contains("3-small") => 1536,
            var m when m.Contains("ada-002") => 1536,
            _ => 1536
        };
    }
}

/// <summary>
///     Ollama embedding provider
/// </summary>
public class OllamaEmbeddingProvider : EmbeddingProviderBase
{
    private readonly OllamaService _ollamaService;
    private readonly OllamaEmbeddingConfig _ollamaConfig;

    public override string ProviderName => "Ollama";
    public override string ModelName => Config.Model;

    public override int EmbeddingDimension
    {
        get
        {
            // Use Ollama's known dimensions
            var lower = ModelName.ToLowerInvariant();
            return lower switch
            {
                var m when m.Contains("bge-m3") => 1024,
                var m when m.Contains("nomic-embed-text") => 768,
                var m when m.Contains("mxbai-embed-large") => 1024,
                var m when m.Contains("snowflake-arctic-embed") => 1024,
                var m when m.Contains("all-minilm") => 384,
                _ => 768
            };
        }
    }

    public OllamaEmbeddingProvider(
        OllamaService ollamaService,
        IOptions<UnifiedEmbeddingConfig> config,
        ILogger<OllamaEmbeddingProvider> logger)
        : base(config, logger)
    {
        _ollamaService = ollamaService;
        _ollamaConfig = config.Value.Ollama;
    }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        Logger.LogInformation("Ollama embedding provider initialized: {Model} ({Dimension}d)", ModelName, EmbeddingDimension);
        await Task.CompletedTask;
    }

    public override async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return await _ollamaService.EmbedAsync(text, cancellationToken: ct);
    }

    public override async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var results = new List<float[]>();
        foreach (var text in texts)
        {
            results.Add(await EmbedAsync(text, ct));
        }
        return results.ToArray();
    }

    public override async Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        return _ollamaService.GetEmbedContextWindow();
    }
}

/// <summary>
///     HuggingFace Inference API embedding provider
/// </summary>
public class HuggingFaceEmbeddingProvider : EmbeddingProviderBase
{
    private readonly HttpClient _httpClient;
    private readonly HuggingFaceEmbeddingConfig _hfConfig;

    public override string ProviderName => "HuggingFace";
    public override string ModelName => Config.Model;

    public override int EmbeddingDimension
    {
        get
        {
            var lower = ModelName.ToLowerInvariant();
            return lower switch
            {
                var m when m.Contains("large") => 1024,
                var m when m.Contains("base") => 768,
                var m when m.Contains("small") || m.Contains("mini") => 384,
                _ => 768
            };
        }
    }

    public HuggingFaceEmbeddingProvider(
        HttpClient httpClient,
        IOptions<UnifiedEmbeddingConfig> config,
        ILogger<HuggingFaceEmbeddingProvider> logger)
        : base(config, logger)
    {
        _httpClient = httpClient;
        _hfConfig = config.Value.HuggingFace;
    }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_hfConfig.ApiKey))
            throw new InvalidOperationException("HuggingFace API key not configured");

        _httpClient.BaseAddress = new Uri(_hfConfig.BaseUrl.TrimEnd('/') + "/");
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _hfConfig.ApiKey);

        Logger.LogInformation("HuggingFace embedding provider initialized: {Model} ({Dimension}d)", ModelName, EmbeddingDimension);
    }

    public override async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var embeddings = await EmbedBatchAsync([text], ct);
        return embeddings[0];
    }

    public override async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        var url = $"/pipeline/feature-extraction/{ModelName}";

        var request = new
        {
            inputs = textList,
            options = new { wait_for_model = true }
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HuggingFace API error: {response.StatusCode} - {error}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);

        // HF returns float[][] directly
        var embeddings = JsonSerializer.Deserialize<float[][]>(responseJson);

        return embeddings ?? [];
    }

    public override Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        return Task.FromResult(512); // Default for most HF models
    }
}

/// <summary>
///     ONNX Runtime embedding provider (local)
/// </summary>
public class OnnxEmbeddingProvider : EmbeddingProviderBase
{
    private readonly OnnxEmbeddingService _onnxService;
    private readonly OnnxEmbeddingConfig _onnxConfig;

    public override string ProviderName => "ONNX Runtime";
    public override string ModelName => Config.Model;

    public override int EmbeddingDimension => _onnxService.EmbeddingDimension;

    public OnnxEmbeddingProvider(
        OnnxEmbeddingService onnxService,
        IOptions<UnifiedEmbeddingConfig> config,
        ILogger<OnnxEmbeddingProvider> logger)
        : base(config, logger)
    {
        _onnxService = onnxService;
        _onnxConfig = config.Value.Onnx;
    }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        await _onnxService.InitializeAsync(ct);
        Logger.LogInformation("ONNX embedding provider initialized: {Model} ({Dimension}d)", ModelName, EmbeddingDimension);
    }

    public override async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return await _onnxService.EmbedAsync(text, ct);
    }

    public override async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        return await _onnxService.EmbedBatchAsync(texts, ct);
    }

    public override Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_onnxService.MaxLength);
    }
}

/// <summary>
///     OpenAI embedding response DTO
/// </summary>
internal record OpenAIEmbeddingResponse
{
    [JsonPropertyName("data")] public List<OpenAIEmbeddingData>? Data { get; init; }
    [JsonPropertyName("usage")] public OpenAIUsage? Usage { get; init; }
}

internal record OpenAIEmbeddingData
{
    [JsonPropertyName("embedding")] public float[]? Embedding { get; init; }
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("object")] public string? Object { get; init; }
}

internal record OpenAIUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; init; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; init; }
}