using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services.LmStudio;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Mostlylucid.DocSummarizer.Services.LmStudio;

/// <summary>
///     LM Studio HTTP client using OpenAI-compatible REST API.
///     Features: Chat completions, embeddings, streaming, model discovery, health checks.
///     Resilience: Retry with decorrelated jitter backoff + circuit breaker via Polly.
///     Observability: OpenTelemetry tracing and metrics.
/// </summary>
public class LmStudioHttpClient : ILMStudioClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly LmStudioConfig _config;
    private readonly ResiliencePipeline _chatResiliencePipeline;
    private readonly ResiliencePipeline _embeddingResiliencePipeline;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LmStudioHttpClient> _logger;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    /// <inheritdoc />
    public string ProviderName => "LM Studio";

    /// <inheritdoc />
    public string ModelName => _config.EmbeddingModel;

    /// <inheritdoc />
    public int EmbeddingDimension => _config.EmbeddingDimension;

    public LmStudioHttpClient(
        IOptions<LmStudioConfig> config,
        ILogger<LmStudioHttpClient> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds > 0 ? _config.TimeoutSeconds : 300);

        var handler = CreateHttpHandler();
        _httpClient = httpClientFactory?.CreateClient() ?? new HttpClient(handler, disposeHandler: httpClientFactory == null)
        {
            BaseAddress = new Uri(_config.BaseUrl.TrimEnd('/') + "/"),
            Timeout = _timeout + TimeSpan.FromSeconds(30)
        };

        // Default headers
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

        _chatResiliencePipeline = BuildChatResiliencePipeline();
        _embeddingResiliencePipeline = BuildEmbeddingResiliencePipeline();
    }

    private static SocketsHttpHandler CreateHttpHandler()
    {
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true
        };
    }

    private ResiliencePipeline BuildChatResiliencePipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = _config.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(_config.InitialRetryDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(_config.MaxRetryDelayMs),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutException>(),
                OnRetry = args =>
                {
                    _logger.LogWarning("[LM Studio] Chat retry {Attempt}/{MaxRetries} after {Delay}s: {Error}",
                        args.AttemptNumber, _config.MaxRetries, args.RetryDelay.TotalSeconds,
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = _config.CircuitBreakerFailureRatio,
                MinimumThroughput = _config.CircuitBreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(_config.CircuitBreakerBreakDurationSeconds),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutException>(),
                OnOpened = args =>
                {
                    _logger.LogWarning("[LM Studio] Circuit breaker OPENED - service unavailable. Retry after {Duration}s",
                        args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    _logger.LogInformation("[LM Studio] Circuit breaker CLOSED - service available");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(_config.TimeoutSeconds))
            .Build();
    }

    private ResiliencePipeline BuildEmbeddingResiliencePipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = _config.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(_config.InitialRetryDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(_config.MaxRetryDelayMs),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutException>(),
                OnRetry = args =>
                {
                    _logger.LogWarning("[LM Studio] Embedding retry {Attempt}/{MaxRetries} after {Delay}s: {Error}",
                        args.AttemptNumber, _config.MaxRetries, args.RetryDelay.TotalSeconds,
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = _config.CircuitBreakerFailureRatio,
                MinimumThroughput = _config.CircuitBreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(_config.CircuitBreakerBreakDurationSeconds),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutException>(),
                OnOpened = args =>
                {
                    _logger.LogWarning("[LM Studio] Embedding circuit breaker OPENED. Retry after {Duration}s",
                        args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(Math.Max(_config.TimeoutSeconds, 60)))
            .Build();
    }

    #region Chat Completions

    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions? options = null, CancellationToken ct = default)
    {
        return await _chatResiliencePipeline.ExecuteAsync(
            async token => await ExecuteChatCompletionAsync(prompt, options, token),
            ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> GenerateStreamingAsync(string prompt, Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("LmStudio.Streaming", ActivityKind.Client);
        activity?.SetTag("llm.provider", "lmstudio");
        activity?.SetTag("llm.model", options?.Model ?? _config.ChatModel);
        activity?.SetTag("llm.prompt_length", prompt.Length);

        var sw = Stopwatch.StartNew();
        var request = BuildChatRequest(prompt, options, stream: true);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        HttpResponseMessage? response = null;
        StreamReader? reader = null;

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            response = await _httpClient.PostAsync("/v1/chat/completions", content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cts.Token);
                throw new HttpRequestException($"LM Studio API error: {response.StatusCode} - {error}");
            }

            var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            reader = new StreamReader(stream);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Timeout");
            ChatMetrics.RecordError("timeout");
            response?.Dispose();
            sw.Stop();
            ChatMetrics.RecordDuration(sw.Elapsed.TotalMilliseconds);
            throw new TimeoutException($"Streaming timed out after {_timeout.TotalMinutes:F0} minutes");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ChatMetrics.RecordError(ex.GetType().Name);
            reader?.Dispose();
            response?.Dispose();
            sw.Stop();
            ChatMetrics.RecordDuration(sw.Elapsed.TotalMilliseconds);
            throw;
        }

        try
        {
            await foreach (var token in ReadStreamAsync(reader!, cts.Token))
            {
                yield return token;
            }
        }
        finally
        {
            reader?.Dispose();
            response?.Dispose();
            sw.Stop();
            ChatMetrics.RecordDuration(sw.Elapsed.TotalMilliseconds);
        }
    }

    private async IAsyncEnumerable<string> ReadStreamAsync(StreamReader reader, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("data: "))
                line = line[6..];
            if (line == "[DONE]") break;

            var chunk = JsonSerializer.Deserialize<ChatStreamResponse>(line, JsonOptions);
            if (chunk?.Choices?.Count > 0 && chunk.Choices[0].Delta?.Content != null)
            {
                var token = chunk.Choices[0].Delta.Content;
                yield return token;
            }
        }
    }

    /// <inheritdoc />
    public async Task<T?> GenerateJsonAsync<T>(string prompt, Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        var jsonOptions = options ?? new Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions();
        jsonOptions.JsonMode = true;

        var response = await GenerateAsync(prompt, jsonOptions, ct);

        if (string.IsNullOrWhiteSpace(response))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(response, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[LM Studio] Failed to parse JSON response: {Response}", response[..Math.Min(500, response.Length)]);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/v1/models", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        var model = await GetModelInfoAsync(_config.ChatModel, ct);
        return model?.Details?.ContextLength ?? model?.Capabilities?.MaxContextLength ?? 8192;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var models = await ListModelsAsyncInternal(ct);
        return models.Data?.Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).ToList() ?? [];
    }

    #endregion

    #region Embeddings

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var healthy = await IsHealthyAsync(ct);
        if (!healthy)
            throw new InvalidOperationException("LM Studio server is not reachable or healthy");

        _logger.LogInformation("[LM Studio] Initialized successfully. Base URL: {BaseUrl}", _config.BaseUrl);
    }

    /// <inheritdoc />
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return await _embeddingResiliencePipeline.ExecuteAsync(
            async _ => await ExecuteEmbeddingAsync(text, ct),
            ct);
    }

    /// <inheritdoc />
    public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0)
            return [];

        // LM Studio OpenAI-compatible API supports batch embeddings
        return await _embeddingResiliencePipeline.ExecuteAsync(
            async _ => await ExecuteBatchEmbeddingAsync(textList, ct),
            ct);
    }

    /// <inheritdoc />
    async Task<int> IEmbeddingClient.GetContextWindowAsync(CancellationToken ct)
    {
        var models = await DiscoverEmbeddingModelsAsync(ct);
        var currentModel = models.FirstOrDefault(m => m.Name == _config.EmbeddingModel);
        return currentModel?.MaxContextLength ?? (_config.EmbeddingContextWindow > 0 ? _config.EmbeddingContextWindow : 8192);
    }

    #endregion

    #region LM Studio Specific

    /// <inheritdoc />
    public async Task<LmStudioModelList> ListModelsDetailedAsync(CancellationToken ct = default)
    {
        return await ListModelsAsyncInternal(ct);
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/v1/models", ct);
            if (!response.IsSuccessStatusCode) return false;

            var models = await ListModelsAsyncInternal(ct);
            return models.Data?.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<LmStudioModelInfo?> GetModelInfoAsync(string modelName, CancellationToken ct = default)
    {
        try
        {
            var models = await ListModelsAsyncInternal(ct);
            var model = models.Data?.FirstOrDefault(m => m.Id == modelName);
            if (model == null) return null;

            return new LmStudioModelInfo
            {
                Id = model.Id,
                Details = model.Details,
                Capabilities = model.Details?.Capabilities != null
                    ? new LmStudioModelCapabilities
                    {
                        SupportsChat = model.Details.Capabilities.Contains("chat"),
                        SupportsCompletion = model.Details.Capabilities.Contains("completion"),
                        SupportsEmbedding = model.Details.Capabilities.Contains("embedding"),
                        SupportsTools = model.Details.Capabilities.Contains("tools"),
                        SupportsVision = model.Details.Capabilities.Contains("vision"),
                        MaxContextLength = model.Details.ContextLength ?? 8192
                    }
                    : null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LmStudioEmbeddingModel>> DiscoverEmbeddingModelsAsync(CancellationToken ct = default)
    {
        var models = await ListModelsAsyncInternal(ct);
        if (models.Data == null) return [];

        var embeddingModels = models.Data
            .Where(m => IsEmbeddingModel(m.Id))
            .Select(m => new LmStudioEmbeddingModel
            {
                Name = m.Id,
                Dimensions = EstimateDimension(m.Id),
                MaxContextLength = EstimateContextWindow(m.Id),
                Description = $"Embedding model: {m.Id}",
                Family = "LM Studio"
            })
            .ToList();

        return embeddingModels;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LmStudioChatModel>> DiscoverChatModelsAsync(CancellationToken ct = default)
    {
        var models = await ListModelsAsyncInternal(ct);
        if (models.Data == null) return [];

        var chatModels = models.Data
            .Where(m => !IsEmbeddingModel(m.Id))
            .Select(m => new LmStudioChatModel
            {
                Name = m.Id,
                MaxContextLength = EstimateContextWindow(m.Id),
                SupportsTools = false,
                SupportsVision = false,
                Family = "LM Studio",
                Description = $"Chat model: {m.Id}"
            })
            .ToList();

        return chatModels;
    }

    #endregion

    #region Private Helpers

    private ChatCompletionRequest BuildChatRequest(string prompt, Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions? options, bool stream)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(options?.SystemPrompt))
        {
            messages.Add(new ChatMessage { Role = "system", Content = options.SystemPrompt });
        }

        messages.Add(new ChatMessage { Role = "user", Content = prompt });

        return new ChatCompletionRequest
        {
            Model = options?.Model ?? _config.ChatModel,
            Messages = messages,
            Temperature = options?.Temperature ?? _config.Temperature,
            MaxTokens = options?.MaxTokens ?? _config.MaxTokens,
            TopP = options?.TopP,
            FrequencyPenalty = options?.FrequencyPenalty,
            PresencePenalty = options?.PresencePenalty,
            Stop = options?.StopSequences?.ToArray(),
            Stream = stream,
            ResponseFormat = options?.JsonMode == true ? new ResponseFormat { Type = "json_object" } : null
        };
    }

    private async Task<string> ExecuteChatCompletionAsync(string prompt, Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions? options, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("LmStudio.Chat", ActivityKind.Client);
        activity?.SetTag("llm.provider", "lmstudio");
        activity?.SetTag("llm.model", options?.Model ?? _config.ChatModel);
        activity?.SetTag("llm.prompt_length", prompt.Length);

        var sw = Stopwatch.StartNew();
        var request = BuildChatRequest(prompt, options, stream: false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/v1/chat/completions", content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cts.Token);
                throw new HttpRequestException($"LM Studio API error: {response.StatusCode} - {error}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
            var result = JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson, JsonOptions);

            var text = result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? "";

            activity?.SetTag("llm.response_length", text.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            ChatMetrics.RecordSuccess(sw.Elapsed.TotalMilliseconds, text.Length);
            return text;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Timeout");
            ChatMetrics.RecordError("timeout");
            throw new TimeoutException($"Chat completion timed out after {_timeout.TotalMinutes:F0} minutes");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ChatMetrics.RecordError(ex.GetType().Name);
            throw;
        }
        finally
        {
            sw.Stop();
            ChatMetrics.RecordDuration(sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<float[]> ExecuteEmbeddingAsync(string text, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("LmStudio.Embed", ActivityKind.Client);
        activity?.SetTag("llm.provider", "lmstudio");
        activity?.SetTag("llm.model", _config.EmbeddingModel);
        activity?.SetTag("llm.text_length", text.Length);

        var request = new EmbeddingRequest
        {
            Model = _config.EmbeddingModel,
            Input = text,
            EncodingFormat = "float"
        };

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/v1/embeddings", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"LM Studio embedding error: {response.StatusCode} - {error}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<EmbeddingResponse>(responseJson, JsonOptions);

            var embedding = result?.Data?.FirstOrDefault()?.Embedding ?? [];

            activity?.SetTag("llm.embedding_dimension", embedding.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            EmbeddingMetrics.RecordSuccess(embedding.Length);
            return embedding;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            EmbeddingMetrics.RecordError(ex.GetType().Name);
            throw;
        }
    }

    private async Task<float[][]> ExecuteBatchEmbeddingAsync(List<string> texts, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("LmStudio.EmbedBatch", ActivityKind.Client);
        activity?.SetTag("llm.provider", "lmstudio");
        activity?.SetTag("llm.model", _config.EmbeddingModel);
        activity?.SetTag("llm.batch_size", texts.Count);

        var request = new EmbeddingRequest
        {
            Model = _config.EmbeddingModel,
            Input = texts,
            EncodingFormat = "float"
        };

        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/v1/embeddings", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"LM Studio batch embedding error: {response.StatusCode} - {error}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<EmbeddingResponse>(responseJson, JsonOptions);

            var embeddings = result?.Data?.Select(d => d.Embedding).ToArray() ?? [];

            activity?.SetTag("llm.embedding_count", embeddings.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            EmbeddingMetrics.RecordBatchSuccess(embeddings.Length);
            return embeddings;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            EmbeddingMetrics.RecordError(ex.GetType().Name);
            throw;
        }
    }

    private async Task<LmStudioModelList> ListModelsAsyncInternal(CancellationToken ct)
    {
        var response = await _httpClient.GetAsync("/v1/models", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<LmStudioModelList>(json, JsonOptions)
               ?? new LmStudioModelList { Data = Array.Empty<LmStudioModel>() };
    }

    private static bool IsEmbeddingModel(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        var embeddingKeywords = new[]
        {
            "embed", "embedding", "bge-", "e5-", "gte-", "jina-",
            "nomic-", "multilingual", "sentence-", "contriever",
            "instructor", "e5_", "bge_", "gte_"
        };

        return embeddingKeywords.Any(k => lower.Contains(k));
    }

    private static int EstimateContextWindow(string modelId)
    {
        var lower = modelId.ToLowerInvariant();

        // Known large context models
        if (lower.Contains("llama3") || lower.Contains("llama-3") ||
            lower.Contains("ministral") || lower.Contains("phi3") ||
            lower.Contains("command-r") || lower.Contains("qwen2") ||
            lower.Contains("mistral-nemo"))
            return 128000;

        if (lower.Contains("gemma2") || lower.Contains("qwen") ||
            lower.Contains("mistral") || lower.Contains("yi-"))
            return 32000;

        if (lower.Contains("gemma") || lower.Contains("phi"))
            return 8192;

        // Embedding models
        if (IsEmbeddingModel(modelId))
        {
            if (lower.Contains("bge-m3") || lower.Contains("jina-") ||
                lower.Contains("gte-large") || lower.Contains("e5-large"))
                return 8192;
            if (lower.Contains("nomic"))
                return 8192;
            return 512;
        }

        return 4096;
    }

    private static int EstimateDimension(string modelId)
    {
        var lower = modelId.ToLowerInvariant();

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

    private static string DetermineModelType(string modelId)
    {
        var lower = modelId.ToLowerInvariant();

        if (IsEmbeddingModel(modelId)) return "embedding";
        if (lower.Contains("vl") || lower.Contains("vision") ||
            lower.Contains("llava") || lower.Contains("minicpm") ||
            lower.Contains("qwen2-vl") || lower.Contains("bakllava"))
            return "vision";
        if (lower.Contains("code") || lower.Contains("coder") ||
            lower.Contains("wizard") || lower.Contains("starcoder"))
            return "code";

        return "chat";
    }

    private static string? ExtractParameterSize(string modelId)
    {
        // Extract patterns like "7b", "13b", "70b", "1.5b", "32b", etc.
        var match = System.Text.RegularExpressions.Regex.Match(modelId, @"(\d+\.?\d*)b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string? ExtractQuantization(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        var quantPatterns = new[] { "q4_k_m", "q4_k_s", "q5_k_m", "q5_k_s", "q6_k", "q8_0", "q2_k", "q3_k", "fp16", "f16", "gguf" };
        foreach (var pattern in quantPatterns)
        {
            if (lower.Contains(pattern)) return pattern.ToUpperInvariant();
        }
        return null;
    }

    #endregion

    #region IDisposable / IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _httpClient?.Dispose();
        await Task.CompletedTask;
    }

    #endregion

    #region OpenTelemetry

    private static readonly ActivitySource ActivitySource = new("Mostlylucid.DocSummarizer.LmStudio", "1.0.0");
    private static readonly Meter Meter = new("Mostlylucid.DocSummarizer.LmStudio", "1.0.0");

    private static readonly Counter<long> ChatCounter = Meter.CreateCounter<long>(
        "docsummarizer.lmstudio.chat.requests", "requests", "Total chat completion requests");

    private static readonly Counter<long> EmbeddingCounter = Meter.CreateCounter<long>(
        "docsummarizer.lmstudio.embedding.requests", "requests", "Total embedding requests");

    private static readonly Histogram<double> ChatDurationHistogram = Meter.CreateHistogram<double>(
        "docsummarizer.lmstudio.chat.duration", "ms", "Chat completion duration");

    private static readonly Histogram<double> EmbeddingDurationHistogram = Meter.CreateHistogram<double>(
        "docsummarizer.lmstudio.embedding.duration", "ms", "Embedding duration");

    private static readonly Histogram<long> ChatTokensHistogram = Meter.CreateHistogram<long>(
        "docsummarizer.lmstudio.chat.tokens", "tokens", "Token counts");

    private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
        "docsummarizer.lmstudio.errors", "errors", "Error counts");

    private static class ChatMetrics
    {
        public static void RecordSuccess(double durationMs, int responseLength)
        {
            ChatCounter.Add(1);
            ChatDurationHistogram.Record(durationMs);
            ChatTokensHistogram.Record(responseLength / 4); // rough estimate
        }

        public static void RecordStreaming(double durationMs, int totalChars)
        {
            ChatCounter.Add(1);
            ChatDurationHistogram.Record(durationMs);
            ChatTokensHistogram.Record(totalChars / 4);
        }

        public static void RecordError(string errorType)
        {
            ErrorCounter.Add(1, new KeyValuePair<string, object?>("error_type", errorType));
        }

        public static void RecordDuration(double durationMs)
        {
            ChatDurationHistogram.Record(durationMs);
        }
    }

    private static class EmbeddingMetrics
    {
        public static void RecordSuccess(int dimension)
        {
            EmbeddingCounter.Add(1);
        }

        public static void RecordBatchSuccess(int count)
        {
            EmbeddingCounter.Add(count);
        }

        public static void RecordError(string errorType)
        {
            ErrorCounter.Add(1, new KeyValuePair<string, object?>("error_type", errorType));
        }
    }

    #endregion

    #region DTOs

    private record ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; init; } = [];
        [JsonPropertyName("temperature")] public double? Temperature { get; init; }
        [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
        [JsonPropertyName("top_p")] public double? TopP { get; init; }
        [JsonPropertyName("frequency_penalty")] public double? FrequencyPenalty { get; init; }
        [JsonPropertyName("presence_penalty")] public double? PresencePenalty { get; init; }
        [JsonPropertyName("stop")] public string[]? Stop { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("response_format")] public ResponseFormat? ResponseFormat { get; init; }
    }

    private record ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; init; } = "";
        [JsonPropertyName("content")] public string Content { get; init; } = "";
    }

    private record ResponseFormat
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "json_object";
    }

    private record ChatCompletionResponse
    {
        [JsonPropertyName("choices")] public List<ChatChoice>? Choices { get; init; }
        [JsonPropertyName("usage")] public Usage? Usage { get; init; }
    }

    private record ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; init; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
        [JsonPropertyName("index")] public int Index { get; init; }
    }

    private record Usage
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; init; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; init; }
        [JsonPropertyName("total_tokens")] public int TotalTokens { get; init; }
    }

    private record ChatStreamResponse
    {
        [JsonPropertyName("choices")] public List<StreamChoice>? Choices { get; init; }
    }

    private record StreamChoice
    {
        [JsonPropertyName("delta")] public Delta? Delta { get; init; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
    }

    private record Delta
    {
        [JsonPropertyName("content")] public string? Content { get; init; }
        [JsonPropertyName("role")] public string? Role { get; init; }
    }

    private record EmbeddingRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("input")] public object Input { get; init; } = "";
        [JsonPropertyName("encoding_format")] public string EncodingFormat { get; init; } = "float";
    }

    private record EmbeddingResponse
    {
        [JsonPropertyName("data")] public List<EmbeddingData>? Data { get; init; }
        [JsonPropertyName("usage")] public Usage? Usage { get; init; }
    }

    private record EmbeddingData
    {
        [JsonPropertyName("embedding")] public float[]? Embedding { get; init; }
        [JsonPropertyName("index")] public int Index { get; init; }
        [JsonPropertyName("object")] public string? Object { get; init; }
    }

    #endregion
}