using System.Runtime.CompilerServices;
using System.Text.Json;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Anthropic.Config;
using Mostlylucid.DocSummarizer.Anthropic.Services;
using Mostlylucid.DocSummarizer.OpenAI.Config;
using Mostlylucid.DocSummarizer.OpenAI.Services;
using Mostlylucid.DocSummarizer.Resilience;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
/// Routes LLM calls across providers with budget enforcement and fallback.
/// Provider priority: configured cloud → Ollama (free, always available).
/// Budget is checked before each cloud API call.
/// Internally delegates to DocSummarizer's ILlmService implementations.
/// </summary>
public class LlmRouter : ILlmService
{
    private readonly List<ProviderEntry> _providers = [];
    private readonly IApiBudget? _budget;
    private readonly ICircuitBreaker? _circuit;
    private readonly OllamaConfig _ollamaConfig;

    private record ProviderEntry(
        ILlmService Service, string? BudgetServiceName, bool IsLocal, ApiKeyEntry? ServiceEntry);

    public bool HasCloudProvider => _providers.Any(p => !p.IsLocal);

    /// <summary>
    /// Human-readable description of the active LLM configuration (for status display).
    /// Shows primary provider and fallback chain.
    /// </summary>
    public string StatusDescription
    {
        get
        {
            var primary = _providers.FirstOrDefault();
            if (primary == null) return "no providers";

            var desc = primary.Service.ProviderName switch
            {
                "Ollama" => $"Ollama: {_ollamaConfig.Model} (sentinel: {_ollamaConfig.SentinelModel})",
                "LLamaSharp" => $"local GGUF (LLamaSharp)",
                _ => primary.Service.ProviderName
            };

            var fallbacks = _providers.Skip(1)
                .Select(p => p.IsLocal ? p.Service.ProviderName : (p.ServiceEntry?.SearchEngineId?.Split('|').FirstOrDefault() ?? p.Service.ProviderName))
                .ToList();

            if (fallbacks.Count > 0)
                desc += $" (fallback: {string.Join(" → ", fallbacks)})";

            return desc;
        }
    }

    private LlmRouter(IApiBudget? budget, ICircuitBreaker? circuit, OllamaConfig ollamaConfig)
    {
        _budget = budget;
        _circuit = circuit;
        _ollamaConfig = ollamaConfig;
    }

    /// <summary>
    /// Build a router from config.
    /// Provider priority: Ollama (if running) → LLamaSharp (local GGUF fallback) → Cloud (paid).
    /// Ollama is preferred because it keeps models warm in memory across calls.
    /// LLamaSharp is the zero-dependency fallback when Ollama isn't available.
    /// Cloud providers are validated at startup and added as fallbacks only.
    /// </summary>
    /// <param name="ollamaConfig">Ollama configuration for local server.</param>
    /// <param name="keys">API key service for cloud providers.</param>
    /// <param name="budget">Budget enforcement for cloud API calls.</param>
    /// <param name="circuit">Circuit breaker for persistent failure detection.</param>
    /// <param name="localLlmService">Optional local LLM provider (e.g. LLamaSharp) added as fallback behind Ollama.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<LlmRouter> BuildAsync(
        OllamaConfig ollamaConfig, ApiKeyService keys, IApiBudget? budget,
        ICircuitBreaker? circuit = null, ILlmService? localLlmService = null,
        CancellationToken ct = default)
    {
        var router = new LlmRouter(budget, circuit, ollamaConfig);

        // Priority 1: Ollama — local server, keeps models warm, fast inference
        var docSumOllamaService = new Mostlylucid.DocSummarizer.Services.OllamaService(
            ollamaConfig.Model,
            ollamaConfig.EmbedModel,
            ollamaConfig.BaseUrl,
            TimeSpan.FromSeconds(ollamaConfig.TimeoutSeconds));
        var ollamaLlm = new OllamaLlmService(docSumOllamaService,
            new Mostlylucid.DocSummarizer.Config.OllamaConfig
            {
                BaseUrl = ollamaConfig.BaseUrl,
                Model = ollamaConfig.Model,
                Temperature = ollamaConfig.Temperature
            });

        var ollamaAvailable = await ollamaLlm.IsAvailableAsync(ct);
        if (ollamaAvailable)
            router._providers.Add(new(ollamaLlm, null, true, null));

        // Priority 2: LLamaSharp — local GGUF fallback (zero-config, but cold-starts are slow)
        if (localLlmService != null && await localLlmService.IsAvailableAsync(ct))
        {
            router._providers.Add(new(localLlmService, null, true, null));
        }

        // If Ollama wasn't available at startup, still add it as last-resort local
        // (it may come online later, and will be tried after LLamaSharp)
        if (!ollamaAvailable)
            router._providers.Add(new(ollamaLlm, null, true, null));

        // Priority 3+: Cloud providers as fallbacks (tried only if local providers fail)
        if (keys.IsAvailable("anthropic"))
        {
            var entry = AutoFillContextSizes(keys.GetService("anthropic")!);
            var models = (entry.SearchEngineId ?? "claude-3-5-haiku-latest").Split('|');
            var service = new AnthropicLlmService(new AnthropicConfig
            {
                ApiKey = entry.ApiKey!,
                Model = models[0]
            });
            if (await service.IsAvailableAsync(ct))
                router._providers.Add(new(service, "anthropic", false, entry));
        }

        if (keys.IsAvailable("openai"))
        {
            var entry = AutoFillContextSizes(keys.GetService("openai")!);
            var models = (entry.SearchEngineId ?? "gpt-4o-mini").Split('|');
            var service = new OpenAILlmService(new OpenAIConfig
            {
                ApiKey = entry.ApiKey!,
                Model = models[0]
            });
            if (await service.IsAvailableAsync(ct))
                router._providers.Add(new(service, "openai", false, entry));
        }

        return router;
    }

    /// <summary>
    /// Auto-fill context sizes from known model names if not explicitly set.
    /// </summary>
    private static ApiKeyEntry AutoFillContextSizes(ApiKeyEntry entry)
    {
        var models = (entry.SearchEngineId ?? "").Split('|');
        var mainModel = models.Length > 0 ? models[0] : null;
        var sentinelModel = models.Length > 1 ? models[1] : mainModel;

        var mainCtx = entry.ContextSize > 0 ? entry.ContextSize : InferContextSize(mainModel);
        var sentinelCtx = entry.SentinelContextSize > 0 ? entry.SentinelContextSize : InferContextSize(sentinelModel);

        return entry with
        {
            ContextSize = mainCtx,
            SentinelContextSize = sentinelCtx
        };
    }

    /// <summary>
    /// Generate a completion using the best available provider.
    /// Tries providers in order, with budget enforcement for cloud providers.
    /// </summary>
    public async Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        foreach (var entry in _providers)
        {
            // Budget check for cloud providers
            if (!entry.IsLocal && entry.BudgetServiceName != null)
            {
                // Check persistent circuit first
                if (_circuit != null && !await _circuit.IsServiceAvailableAsync(entry.BudgetServiceName))
                    continue;

                if (_budget != null)
                {
                    var check = await _budget.CheckBudgetAsync(entry.BudgetServiceName);
                    if (!check.IsAllowed)
                    {
                        if (_circuit != null)
                        {
                            var failureType = ICircuitBreaker.ClassifyBudgetDenial(check.DenialReason);
                            await _circuit.TripCircuitAsync(entry.BudgetServiceName, failureType, check.DenialReason);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"{entry.Service.ProviderName}: {check.DenialReason} -- trying next");
                        }
                        continue;
                    }
                }
            }

            try
            {
                var result = await entry.Service.GenerateAsync(prompt, options, ct);

                // Record usage for cloud providers
                if (!entry.IsLocal && _budget != null && entry.BudgetServiceName != null)
                    await _budget.RecordUsageAsync(entry.BudgetServiceName);

                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // User cancelled, don't fallback
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"{entry.Service.ProviderName} failed: {ex.Message} -- trying next provider");
            }
        }

        throw new InvalidOperationException("All LLM providers failed. Is Ollama running? Or enable local GGUF models via LLamaSharp.");
    }

    /// <summary>
    /// Generate with a specific role hint (main/sentinel).
    /// Convenience overload used by DoomSummarizer's OllamaService.
    /// </summary>
    public Task<string> GenerateAsync(string prompt, string? systemPrompt,
        double temperature, string role = "main", bool jsonMode = false,
        CancellationToken ct = default) =>
        GenerateAsync(prompt, new LlmOptions
        {
            SystemPrompt = systemPrompt,
            Temperature = temperature,
            Role = role,
            JsonMode = jsonMode
        }, ct);

    /// <summary>
    /// Stream tokens from the best available provider with fallback chain.
    /// Tries providers in order, with budget enforcement for cloud providers.
    /// </summary>
    public async IAsyncEnumerable<string> GenerateStreamingAsync(
        string prompt, LlmOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Exception? lastException = null;

        foreach (var entry in _providers)
        {
            // Budget check for cloud providers
            if (!entry.IsLocal && entry.BudgetServiceName != null)
            {
                if (_circuit != null && !await _circuit.IsServiceAvailableAsync(entry.BudgetServiceName))
                    continue;

                if (_budget != null)
                {
                    var check = await _budget.CheckBudgetAsync(entry.BudgetServiceName);
                    if (!check.IsAllowed)
                        continue;
                }
            }

            IAsyncEnumerable<string>? stream = null;
            try
            {
                stream = entry.Service.GenerateStreamingAsync(prompt, options, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"{entry.Service.ProviderName} streaming init failed: {ex.Message} -- trying next provider");
                lastException = ex;
                continue;
            }

            var yielded = false;
            await using var enumerator = stream.GetAsyncEnumerator(ct);
            while (true)
            {
                string current;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;
                    current = enumerator.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (yielded)
                        throw; // Already started streaming, can't fallback
                    System.Diagnostics.Debug.WriteLine(
                        $"{entry.Service.ProviderName} streaming failed: {ex.Message} -- trying next provider");
                    lastException = ex;
                    break;
                }

                yielded = true;
                yield return current;
            }

            if (yielded)
            {
                // Record usage for cloud providers
                if (!entry.IsLocal && _budget != null && entry.BudgetServiceName != null)
                    await _budget.RecordUsageAsync(entry.BudgetServiceName);
                yield break;
            }
        }

        throw new InvalidOperationException(
            "All LLM providers failed for streaming. Is Ollama running?",
            lastException);
    }

    /// <summary>
    /// Check if any provider is available.
    /// </summary>
    public async Task<bool> IsAnyAvailableAsync(CancellationToken ct = default)
    {
        foreach (var entry in _providers)
        {
            if (await entry.Service.IsAvailableAsync(ct))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Get the effective context window size for a given role.
    /// Returns the context size of the first available provider.
    /// Falls back to Ollama config when no cloud provider has it set.
    /// </summary>
    public int GetContextSize(string role = "main")
    {
        foreach (var entry in _providers)
        {
            if (entry.ServiceEntry != null)
            {
                var ctx = role == "sentinel"
                    ? entry.ServiceEntry.SentinelContextSize
                    : entry.ServiceEntry.ContextSize;
                if (ctx > 0) return ctx;
            }

            // For Ollama (local), get from OllamaConfig
            if (entry.IsLocal)
                return role == "sentinel" ? _ollamaConfig.SentinelContextSize : _ollamaConfig.ContextSize;
        }

        return 8192; // safe fallback
    }

    /// <summary>
    /// Compute max chars of evidence per item for the current provider.
    /// </summary>
    public int MaxEvidenceCharsPerItem(bool sentinel, int itemCount)
    {
        // Reserve 400 tokens for compact prompt template overhead (reduced from 800)
        var ctx = GetContextSize(sentinel ? "sentinel" : "main");
        var availableTokens = ctx - 400;
        var perItem = Math.Max(100, availableTokens / Math.Max(1, itemCount));
        return (int)(perItem * 3.5);
    }

    /// <summary>
    /// List available providers and their status.
    /// </summary>
    public async Task PrintStatusAsync(CancellationToken ct = default)
    {
        foreach (var entry in _providers)
        {
            var available = await entry.Service.IsAvailableAsync(ct);
            var type = entry.IsLocal ? "local" : "cloud";
            var budgetInfo = !entry.IsLocal && entry.BudgetServiceName != null
                ? " (budget-controlled)"
                : "";
            var ctx = entry.ServiceEntry?.ContextSize ?? (entry.IsLocal ? _ollamaConfig.ContextSize : 0);
            var ctxInfo = ctx > 0 ? $", {ctx / 1000}K ctx" : "";
            var status = available ? "available" : "unavailable";
            System.Diagnostics.Debug.WriteLine($"  LLM {entry.Service.ProviderName} ({type}{budgetInfo}{ctxInfo}): {status}");
        }
    }

    // ── ILlmService implementation ──────────────────────────────────────

    /// <inheritdoc />
    public string ProviderName => "LlmRouter";

    /// <inheritdoc />
    Task<string> ILlmService.GenerateAsync(string prompt, LlmOptions? options, CancellationToken ct)
        => GenerateAsync(prompt, options, ct);

    /// <inheritdoc />
    IAsyncEnumerable<string> ILlmService.GenerateStreamingAsync(string prompt, LlmOptions? options, CancellationToken ct)
        => GenerateStreamingAsync(prompt, options, ct);

    /// <inheritdoc />
    async Task<T?> ILlmService.GenerateJsonAsync<T>(string prompt, LlmOptions? options, CancellationToken ct)
        where T : class
    {
        var jsonOptions = options ?? new LlmOptions();
        jsonOptions.JsonMode = true;
        var json = await GenerateAsync(prompt, jsonOptions, ct);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    /// <inheritdoc />
    async Task<bool> ILlmService.IsAvailableAsync(CancellationToken ct)
        => await IsAnyAvailableAsync(ct);

    /// <inheritdoc />
    Task<int> ILlmService.GetContextWindowAsync(CancellationToken ct)
        => Task.FromResult(GetContextSize());

    // Known context window sizes for common models
    private static readonly Dictionary<string, int> KnownContextSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o"] = 128000,
        ["gpt-4o-mini"] = 128000,
        ["gpt-4-turbo"] = 128000,
        ["gpt-3.5-turbo"] = 16385,
        ["claude-sonnet-4-20250514"] = 200000,
        ["claude-3-5-sonnet-latest"] = 200000,
        ["claude-3-5-sonnet-20241022"] = 200000,
        ["claude-3-5-haiku-latest"] = 200000,
        ["claude-3-haiku-20240307"] = 200000,
        ["claude-3-opus-20240229"] = 200000,
        ["claude-opus-4-20250514"] = 200000,
    };

    /// <summary>
    /// Auto-detect context size from model name if not explicitly set.
    /// </summary>
    internal static int InferContextSize(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;

        foreach (var (known, size) in KnownContextSizes)
        {
            if (modelName.Contains(known, StringComparison.OrdinalIgnoreCase))
                return size;
        }

        // Heuristic: most modern cloud models have at least 128K
        if (modelName.StartsWith("gpt-4", StringComparison.OrdinalIgnoreCase))
            return 128000;
        if (modelName.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return 200000;

        return 0;
    }
}
