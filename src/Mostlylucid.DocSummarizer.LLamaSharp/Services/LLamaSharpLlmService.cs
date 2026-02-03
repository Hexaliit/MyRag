using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Mostlylucid.DocSummarizer.LLamaSharp.Config;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.LLamaSharp.Services;

/// <summary>
///     Local GGUF model inference via LLamaSharp implementing ILlmService.
///     Models are lazy-loaded on first use and kept for the session lifetime.
/// </summary>
public sealed class LLamaSharpLlmService : ILlmService, IDisposable
{
    private readonly LLamaSharpConfig _config;
    private readonly LLamaSharpModelDownloader _downloader;

    // Lazy-loaded models (loaded on first use, kept for session)
    private LLamaWeights? _synthesisWeights;
    private LLamaWeights? _sentinelWeights;
    private ModelParams? _synthesisParams;
    private ModelParams? _sentinelParams;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LLamaSharpLlmService(LLamaSharpConfig config, LLamaSharpModelDownloader downloader)
    {
        _config = config;
        _downloader = downloader;
    }

    /// <inheritdoc />
    public string ProviderName => "LLamaSharp";

    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        var isSentinel = options?.Role is "sentinel";
        var (weights, modelParams) = await EnsureModelLoadedAsync(isSentinel, ct);

        var maxTokens = options?.MaxTokens ?? (isSentinel ? 1024 : 2048);
        var temperature = (float)(options?.Temperature ?? (isSentinel ? 0.1 : 0.4));

        // Build the full prompt with system prompt if provided
        var fullPrompt = BuildPrompt(prompt, options?.SystemPrompt, options?.JsonMode ?? false);

        var executor = new StatelessExecutor(weights, modelParams);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = temperature,
                TopP = 0.9f,
                RepeatPenalty = 1.1f,
            },
            AntiPrompts = isSentinel ? ["```", "\n\n\n"] : ["\n\n\n"],
        };

        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(fullPrompt, inferenceParams, ct))
        {
            sb.Append(token);
        }

        return sb.ToString().Trim();
    }

    /// <inheritdoc />
    public async Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        var jsonOptions = new LlmOptions
        {
            Model = options?.Model,
            Temperature = 0.1,
            MaxTokens = options?.MaxTokens ?? 1024,
            SystemPrompt = options?.SystemPrompt,
            JsonMode = true,
            Role = options?.Role ?? "sentinel",
        };

        var result = await GenerateAsync(prompt, jsonOptions, ct);

        // Extract JSON from response (models may include preamble text)
        var jsonStart = result.IndexOf('{');
        var jsonEnd = result.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var jsonStr = result[jsonStart..(jsonEnd + 1)];
            try
            {
                return JsonSerializer.Deserialize<T>(jsonStr, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_config.Enabled)
            return Task.FromResult(false);

        // Available if auto-download is enabled OR at least one model exists
        if (_config.AutoDownload)
            return Task.FromResult(true);

        var modelDir = _config.ResolvedModelDirectory;
        var hasModels = Directory.Exists(modelDir) &&
                        Directory.EnumerateFiles(modelDir, "*.gguf").Any();
        return Task.FromResult(hasModels);
    }

    /// <inheritdoc />
    public Task<int> GetContextWindowAsync(CancellationToken ct = default)
        => Task.FromResult((int)_config.ContextSize);

    /// <summary>
    ///     Ensure the model for the given role is loaded. Downloads if necessary.
    /// </summary>
    internal async Task<(LLamaWeights weights, ModelParams modelParams)> EnsureModelLoadedAsync(
        bool sentinel, CancellationToken ct)
    {
        await _loadLock.WaitAsync(ct);
        try
        {
            var weights = sentinel ? _sentinelWeights : _synthesisWeights;
            if (weights != null)
                return (weights, sentinel ? _sentinelParams! : _synthesisParams!);

            var modelInfo = sentinel
                ? LLamaSharpModelRegistry.GetSentinel(_config.SentinelModel)
                : LLamaSharpModelRegistry.GetSynthesis(_config.SynthesisModel);

            var modelPath = await _downloader.EnsureModelAsync(modelInfo, ct: ct);

            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = sentinel ? 4096u : _config.ContextSize,
                GpuLayerCount = _config.GpuLayerCount,
                BatchSize = (uint)_config.BatchSize,
            };

            weights = await LLamaWeights.LoadFromFileAsync(modelParams, ct);

            if (sentinel)
            {
                _sentinelWeights = weights;
                _sentinelParams = modelParams;
            }
            else
            {
                _synthesisWeights = weights;
                _synthesisParams = modelParams;
            }

            return (weights, modelParams);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    ///     Build a prompt with optional system instructions.
    ///     Chat templates are auto-detected from GGUF metadata by LLamaSharp.
    /// </summary>
    private static string BuildPrompt(string prompt, string? systemPrompt, bool jsonMode)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            sb.AppendLine(systemPrompt);
            sb.AppendLine();
        }

        sb.Append(prompt);

        if (jsonMode)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Respond with valid JSON only. No markdown, no code blocks, just the JSON object.");
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        _sentinelWeights?.Dispose();
        _synthesisWeights?.Dispose();
        _loadLock.Dispose();
    }
}
