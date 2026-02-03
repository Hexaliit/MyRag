using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Mostlylucid.DocSummarizer.LLamaSharp.Config;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.LLamaSharp.Services;

/// <summary>
///     Local GGUF model inference via LLamaSharp implementing ILlmService.
///     Models are lazy-loaded on first use and kept for the session lifetime.
///     Uses GGUF embedded chat templates to prevent prompt leaks.
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

    // Ensure native logging is suppressed exactly once (llama.cpp is extremely verbose)
    private static int _loggingConfigured;

    /// <summary>
    ///     End-of-turn tokens used by supported GGUF models (Qwen/ChatML, Phi-4, Gemma).
    /// </summary>
    private static readonly string[] EndOfTurnTokens = ["<|im_end|>", "<|end|>", "<end_of_turn>"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LLamaSharpLlmService(LLamaSharpConfig config, LLamaSharpModelDownloader downloader)
    {
        _config = config;
        _downloader = downloader;
        SuppressNativeLogging();
    }

    /// <summary>
    ///     Configure native backend and suppress llama.cpp debug output.
    ///     Called once before any model loading.
    ///     Prefers CUDA GPU acceleration when available, with automatic CPU fallback.
    ///     Without log suppression, every StatelessExecutor call dumps dozens of lines
    ///     of model metadata, tensor info, and memory allocation details to stderr.
    /// </summary>
    private static void SuppressNativeLogging()
    {
        if (Interlocked.CompareExchange(ref _loggingConfigured, 1, 0) == 0)
        {
            // Configure backend selection before any native API calls.
            // WithCuda() prefers CUDA when the Backend.Cuda12 package is present;
            // auto-fallback (enabled by default) drops to CPU if no NVIDIA GPU is found.
            NativeLibraryConfig.All.WithCuda();

            NativeLogConfig.llama_log_set((_, _) => { });
        }
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

        // Build system prompt (include JSON mode instruction if needed)
        var systemPrompt = options?.SystemPrompt ?? "";
        if (options?.JsonMode ?? false)
        {
            const string jsonInstruction = "Respond with valid JSON only. No markdown, no code blocks, just the JSON object.";
            systemPrompt = string.IsNullOrEmpty(systemPrompt)
                ? jsonInstruction
                : $"{systemPrompt}\n\n{jsonInstruction}";
        }

        // Use GGUF embedded chat template to format the prompt with proper role boundaries.
        // This prevents prompt leaks by clearly marking system/user/assistant turns.
        var formattedPrompt = FormatWithChatTemplate(weights, systemPrompt, prompt);

        var executor = new StatelessExecutor(weights, modelParams);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = temperature,
                TopP = 0.9f,
                MinP = 0.05f,
                RepeatPenalty = 1.1f,
            },
            // Stop on end-of-turn tokens from all supported model families
            AntiPrompts = isSentinel
                ? ["```", "\n\n\n", ..EndOfTurnTokens]
                : ["\n\n\n", ..EndOfTurnTokens],
        };

        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(formattedPrompt, inferenceParams, ct))
        {
            sb.Append(token);
        }

        return CleanResponse(sb.ToString());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> GenerateStreamingAsync(
        string prompt, LlmOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var isSentinel = options?.Role is "sentinel";
        var (weights, modelParams) = await EnsureModelLoadedAsync(isSentinel, ct);

        var maxTokens = options?.MaxTokens ?? (isSentinel ? 1024 : 2048);
        var temperature = (float)(options?.Temperature ?? (isSentinel ? 0.1 : 0.4));

        var systemPrompt = options?.SystemPrompt ?? "";
        if (options?.JsonMode ?? false)
        {
            const string jsonInstruction = "Respond with valid JSON only. No markdown, no code blocks, just the JSON object.";
            systemPrompt = string.IsNullOrEmpty(systemPrompt)
                ? jsonInstruction
                : $"{systemPrompt}\n\n{jsonInstruction}";
        }

        var formattedPrompt = FormatWithChatTemplate(weights, systemPrompt, prompt);
        var executor = new StatelessExecutor(weights, modelParams);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = temperature,
                TopP = 0.9f,
                MinP = 0.05f,
                RepeatPenalty = 1.1f,
            },
            AntiPrompts = isSentinel
                ? ["```", "\n\n\n", ..EndOfTurnTokens]
                : ["\n\n\n", ..EndOfTurnTokens],
        };

        await foreach (var token in executor.InferAsync(formattedPrompt, inferenceParams, ct))
        {
            // Skip end-of-turn tokens in streaming output
            var isEot = false;
            foreach (var eot in EndOfTurnTokens)
            {
                if (token.Contains(eot, StringComparison.Ordinal))
                {
                    isEot = true;
                    break;
                }
            }
            if (!isEot)
                yield return token;
        }
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
    ///     Format a prompt using the GGUF model's embedded chat template.
    ///     This reads the template from model metadata and applies it with proper role markers,
    ///     preventing system instructions from leaking into the model's response.
    /// </summary>
    private static string FormatWithChatTemplate(LLamaWeights weights, string systemPrompt, string userPrompt)
    {
        try
        {
            var template = new LLamaTemplate(weights);
            if (!string.IsNullOrEmpty(systemPrompt))
                template.Add("system", systemPrompt);
            template.Add("user", userPrompt);
            template.AddAssistant = true;

            var bytes = template.Apply();
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Fallback: plain concatenation (should not happen with instruct-tuned GGUF models)
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                sb.AppendLine(systemPrompt);
                sb.AppendLine();
            }
            sb.Append(userPrompt);
            return sb.ToString();
        }
    }

    /// <summary>
    ///     Strip trailing end-of-turn tokens and whitespace from model output.
    /// </summary>
    private static string CleanResponse(string raw)
    {
        var result = raw.Trim();
        // Models may emit their end-of-turn marker before the anti-prompt catches it
        foreach (var eot in EndOfTurnTokens)
        {
            if (result.EndsWith(eot, StringComparison.Ordinal))
            {
                result = result[..^eot.Length].TrimEnd();
            }
        }
        return result;
    }

    public void Dispose()
    {
        _sentinelWeights?.Dispose();
        _synthesisWeights?.Dispose();
        _loadLock.Dispose();
    }
}
