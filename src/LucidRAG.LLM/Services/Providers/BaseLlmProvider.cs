using System.Diagnostics;
using System.Diagnostics.Metrics;
using LucidRAG.LLM.Config;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace LucidRAG.LLM.Services.Providers;

/// <summary>
/// Base class for named LLM providers with shared functionality.
/// Includes Polly resilience (retry + circuit breaker) and OpenTelemetry observability.
/// </summary>
public abstract class BaseLlmProvider : INamedLlmProvider
{
    protected readonly ILlmService Inner;
    protected readonly IPromptService PromptService;
    protected readonly ModelConfig ModelConfig;
    protected readonly BackendConfig BackendConfig;
    protected readonly ILogger Logger;
    protected readonly ResiliencePipeline<string> ResiliencePipeline;

    protected BaseLlmProvider(
        string name,
        ILlmService inner,
        IPromptService promptService,
        ModelConfig modelConfig,
        BackendConfig backendConfig,
        ILogger logger)
    {
        Name = name;
        Inner = inner;
        PromptService = promptService;
        ModelConfig = modelConfig;
        BackendConfig = backendConfig;
        Logger = logger;
        ResiliencePipeline = BuildResiliencePipeline();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public abstract LlmBackendType BackendType { get; }

    /// <inheritdoc />
    public string ModelId => ModelConfig.Model;

    /// <inheritdoc />
    public bool SupportsVision => ModelConfig.SupportsVision;

    /// <inheritdoc />
    public string ProviderName => $"{Name}:{Inner.ProviderName}";

    /// <summary>
    /// Build Polly resilience pipeline with retry (exponential backoff + jitter) and circuit breaker.
    /// </summary>
    private ResiliencePipeline<string> BuildResiliencePipeline()
    {
        return new ResiliencePipelineBuilder<string>()
            // Retry with exponential backoff and jitter
            .AddRetry(new RetryStrategyOptions<string>
            {
                MaxRetryAttempts = BackendConfig.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(BackendConfig.InitialRetryDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(BackendConfig.MaxRetryDelayMs),
                ShouldHandle = new PredicateBuilder<string>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutException>()
                    .Handle<InvalidOperationException>(ex =>
                        ex.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)),
                OnRetry = args =>
                {
                    Logger.LogWarning(
                        "LLM request retry {Attempt}/{MaxAttempts} for provider {Provider} after {Delay:F1}s - {Error}",
                        args.AttemptNumber,
                        BackendConfig.MaxRetries,
                        Name,
                        args.RetryDelay.TotalSeconds,
                        args.Outcome.Exception?.Message ?? "Unknown error");

                    RetryCounter.Add(1,
                        new KeyValuePair<string, object?>("provider", Name),
                        new KeyValuePair<string, object?>("attempt", args.AttemptNumber));

                    return ValueTask.CompletedTask;
                }
            })
            // Circuit breaker to fail fast when provider is consistently failing
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<string>
            {
                FailureRatio = 0.8, // Open circuit if 80% of requests fail
                MinimumThroughput = BackendConfig.CircuitBreakerThreshold,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(BackendConfig.CircuitBreakerDurationSeconds),
                ShouldHandle = new PredicateBuilder<string>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutException>()
                    .Handle<InvalidOperationException>(),
                OnOpened = args =>
                {
                    Logger.LogWarning(
                        "Circuit breaker OPENED for provider {Provider} - will retry after {Duration}s",
                        Name, args.BreakDuration.TotalSeconds);

                    CircuitBreakerCounter.Add(1,
                        new KeyValuePair<string, object?>("provider", Name),
                        new KeyValuePair<string, object?>("state", "opened"));

                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    Logger.LogInformation("Circuit breaker CLOSED for provider {Provider}", Name);

                    CircuitBreakerCounter.Add(1,
                        new KeyValuePair<string, object?>("provider", Name),
                        new KeyValuePair<string, object?>("state", "closed"));

                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    Logger.LogInformation("Circuit breaker HALF-OPEN for provider {Provider} - testing availability", Name);

                    CircuitBreakerCounter.Add(1,
                        new KeyValuePair<string, object?>("provider", Name),
                        new KeyValuePair<string, object?>("state", "half_open"));

                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <inheritdoc />
    public virtual async Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("LlmGenerate", ActivityKind.Client);
        activity?.SetTag("llm.provider", Name);
        activity?.SetTag("llm.model", ModelId);
        activity?.SetTag("llm.backend", BackendType.ToString());
        activity?.SetTag("llm.prompt_length", prompt.Length);

        var sw = Stopwatch.StartNew();
        var effectiveOptions = MergeOptions(options);

        try
        {
            var result = await ResiliencePipeline.ExecuteAsync(
                async token => await Inner.GenerateAsync(prompt, effectiveOptions, token),
                ct);

            sw.Stop();

            // Record success metrics
            activity?.SetTag("llm.response_length", result.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            RequestCounter.Add(1,
                new KeyValuePair<string, object?>("provider", Name),
                new KeyValuePair<string, object?>("status", "success"));

            RequestDurationHistogram.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", Name));

            // Estimate tokens (rough: ~4 chars per token for English)
            var estimatedPromptTokens = prompt.Length / 4;
            var estimatedResponseTokens = result.Length / 4;

            PromptTokensHistogram.Record(estimatedPromptTokens,
                new KeyValuePair<string, object?>("provider", Name));

            ResponseTokensHistogram.Record(estimatedResponseTokens,
                new KeyValuePair<string, object?>("provider", Name));

            return result;
        }
        catch (BrokenCircuitException ex)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, "CircuitBreakerOpen");

            RequestCounter.Add(1,
                new KeyValuePair<string, object?>("provider", Name),
                new KeyValuePair<string, object?>("status", "circuit_breaker"));

            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("provider", Name),
                new KeyValuePair<string, object?>("error_type", "circuit_breaker"));

            Logger.LogError(ex, "Circuit breaker open for provider {Provider}", Name);
            throw new InvalidOperationException(
                $"Provider '{Name}' is temporarily unavailable (circuit breaker open). " +
                $"The provider has been failing consistently. Will automatically retry after cooldown.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            RequestCounter.Add(1,
                new KeyValuePair<string, object?>("provider", Name),
                new KeyValuePair<string, object?>("status", "error"));

            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("provider", Name),
                new KeyValuePair<string, object?>("error_type", ex.GetType().Name));

            RequestDurationHistogram.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", Name));

            Logger.LogError(ex, "LLM generation failed for provider {Provider}", Name);
            throw;
        }
    }

    /// <inheritdoc />
    public virtual async Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        using var activity = ActivitySource.StartActivity("LlmGenerateJson", ActivityKind.Client);
        activity?.SetTag("llm.provider", Name);
        activity?.SetTag("llm.model", ModelId);
        activity?.SetTag("llm.return_type", typeof(T).Name);

        var sw = Stopwatch.StartNew();
        var effectiveOptions = MergeOptions(options);

        try
        {
            var result = await Inner.GenerateJsonAsync<T>(prompt, effectiveOptions, ct);

            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Ok);

            JsonRequestCounter.Add(1,
                new KeyValuePair<string, object?>("provider", Name),
                new KeyValuePair<string, object?>("status", "success"));

            RequestDurationHistogram.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", Name));

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            JsonRequestCounter.Add(1,
                new KeyValuePair<string, object?>("provider", Name),
                new KeyValuePair<string, object?>("status", "error"));

            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("provider", Name),
                new KeyValuePair<string, object?>("error_type", ex.GetType().Name));

            Logger.LogError(ex, "LLM JSON generation failed for provider {Provider}", Name);
            throw;
        }
    }

    /// <inheritdoc />
    public virtual async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("LlmAvailabilityCheck", ActivityKind.Client);
        activity?.SetTag("llm.provider", Name);

        try
        {
            var available = await Inner.IsAvailableAsync(ct);
            activity?.SetTag("llm.available", available);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return available;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Logger.LogDebug(ex, "Availability check failed for provider {Provider}", Name);
            return false;
        }
    }

    /// <inheritdoc />
    public virtual Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        return Task.FromResult(ModelConfig.ContextWindow);
    }

    /// <inheritdoc />
    public virtual async Task<string> GenerateWithPromptAsync(
        string promptName,
        Dictionary<string, object> variables,
        LlmOptions? options = null,
        CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("LlmGenerateWithPrompt", ActivityKind.Client);
        activity?.SetTag("llm.provider", Name);
        activity?.SetTag("llm.prompt_name", promptName);

        var (systemPrompt, template) = PromptService.RenderPrompt(promptName, variables, BackendType);
        var promptOptions = PromptService.GetOptionsForPrompt(promptName, BackendType);

        var effectiveOptions = MergeOptions(options, promptOptions);
        if (!string.IsNullOrEmpty(systemPrompt))
            effectiveOptions.SystemPrompt = systemPrompt;

        Logger.LogDebug("Generating with prompt '{PromptName}' using provider '{Provider}'",
            promptName, Name);

        NamedPromptCounter.Add(1,
            new KeyValuePair<string, object?>("provider", Name),
            new KeyValuePair<string, object?>("prompt", promptName));

        return await GenerateAsync(template, effectiveOptions, ct);
    }

    /// <inheritdoc />
    public virtual async Task<T?> GenerateJsonWithPromptAsync<T>(
        string promptName,
        Dictionary<string, object> variables,
        LlmOptions? options = null,
        CancellationToken ct = default) where T : class
    {
        using var activity = ActivitySource.StartActivity("LlmGenerateJsonWithPrompt", ActivityKind.Client);
        activity?.SetTag("llm.provider", Name);
        activity?.SetTag("llm.prompt_name", promptName);
        activity?.SetTag("llm.return_type", typeof(T).Name);

        var (systemPrompt, template) = PromptService.RenderPrompt(promptName, variables, BackendType);
        var promptOptions = PromptService.GetOptionsForPrompt(promptName, BackendType);

        var effectiveOptions = MergeOptions(options, promptOptions);
        if (!string.IsNullOrEmpty(systemPrompt))
            effectiveOptions.SystemPrompt = systemPrompt;

        Logger.LogDebug("Generating JSON with prompt '{PromptName}' using provider '{Provider}'",
            promptName, Name);

        NamedPromptCounter.Add(1,
            new KeyValuePair<string, object?>("provider", Name),
            new KeyValuePair<string, object?>("prompt", promptName));

        return await GenerateJsonAsync<T>(template, effectiveOptions, ct);
    }

    /// <summary>
    /// Merge user options with model defaults.
    /// </summary>
    protected LlmOptions MergeOptions(LlmOptions? userOptions, LlmOptions? promptOptions = null)
    {
        return new LlmOptions
        {
            Model = userOptions?.Model ?? ModelConfig.Model,
            Temperature = userOptions?.Temperature ?? promptOptions?.Temperature ?? ModelConfig.Temperature,
            MaxTokens = userOptions?.MaxTokens ?? promptOptions?.MaxTokens ?? ModelConfig.MaxTokens,
            SystemPrompt = userOptions?.SystemPrompt ?? promptOptions?.SystemPrompt
        };
    }

    #region OpenTelemetry Instrumentation

    /// <summary>ActivitySource for distributed tracing</summary>
    private static readonly ActivitySource ActivitySource = new("LucidRAG.LLM", "1.0.0");

    /// <summary>Meter for metrics</summary>
    private static readonly Meter Meter = new("LucidRAG.LLM", "1.0.0");

    /// <summary>Counter for LLM generation requests</summary>
    private static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>(
        "lucidrag.llm.requests",
        "requests",
        "Total number of LLM generation requests");

    /// <summary>Counter for JSON generation requests</summary>
    private static readonly Counter<long> JsonRequestCounter = Meter.CreateCounter<long>(
        "lucidrag.llm.json_requests",
        "requests",
        "Total number of LLM JSON generation requests");

    /// <summary>Counter for named prompt usage</summary>
    private static readonly Counter<long> NamedPromptCounter = Meter.CreateCounter<long>(
        "lucidrag.llm.named_prompts",
        "requests",
        "Usage of named prompts from the prompt library");

    /// <summary>Counter for errors by type</summary>
    private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
        "lucidrag.llm.errors",
        "errors",
        "Total number of LLM errors by type");

    /// <summary>Counter for retry attempts</summary>
    private static readonly Counter<long> RetryCounter = Meter.CreateCounter<long>(
        "lucidrag.llm.retries",
        "retries",
        "Total number of retry attempts");

    /// <summary>Counter for circuit breaker state changes</summary>
    private static readonly Counter<long> CircuitBreakerCounter = Meter.CreateCounter<long>(
        "lucidrag.llm.circuit_breaker",
        "events",
        "Circuit breaker state changes");

    /// <summary>Histogram for request duration</summary>
    private static readonly Histogram<double> RequestDurationHistogram = Meter.CreateHistogram<double>(
        "lucidrag.llm.duration",
        "ms",
        "Duration of LLM operations in milliseconds");

    /// <summary>Histogram for prompt token count</summary>
    private static readonly Histogram<long> PromptTokensHistogram = Meter.CreateHistogram<long>(
        "lucidrag.llm.prompt_tokens",
        "tokens",
        "Estimated number of prompt tokens sent");

    /// <summary>Histogram for response token count</summary>
    private static readonly Histogram<long> ResponseTokensHistogram = Meter.CreateHistogram<long>(
        "lucidrag.llm.response_tokens",
        "tokens",
        "Estimated number of response tokens generated");

    #endregion
}
