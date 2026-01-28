using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Routes LLM calls across providers with budget enforcement and fallback.
/// Provider priority: configured cloud → Ollama (free, always available).
/// Budget is checked before each cloud API call.
/// </summary>
public class LlmRouter
{
    private readonly List<ProviderEntry> _providers = [];
    private readonly ApiBudgetService? _budget;
    private readonly CircuitBreakerService? _circuit;
    private readonly OllamaConfig _ollamaConfig;

    private record ProviderEntry(
        ILlmProvider Provider, string? BudgetServiceName, bool IsLocal, ApiKeyEntry? ServiceEntry);

    public bool HasCloudProvider => _providers.Any(p => !p.IsLocal);

    /// <summary>
    /// Human-readable description of the active LLM configuration (for status display).
    /// </summary>
    public string StatusDescription
    {
        get
        {
            var cloudFallback = _providers.FirstOrDefault(p => !p.IsLocal);
            if (cloudFallback != null)
            {
                var model = cloudFallback.ServiceEntry?.SearchEngineId?.Split('|')[0] ?? "unknown";
                return $"Ollama ({_ollamaConfig.Model}) + {cloudFallback.BudgetServiceName} ({model})";
            }
            return $"Ollama ({_ollamaConfig.Model})";
        }
    }

    private LlmRouter(ApiBudgetService? budget, CircuitBreakerService? circuit, OllamaConfig ollamaConfig)
    {
        _budget = budget;
        _circuit = circuit;
        _ollamaConfig = ollamaConfig;
    }

    /// <summary>
    /// Build a router from config. Ollama is the PRIMARY provider (local, free).
    /// Cloud providers are validated at startup and added as fallbacks only —
    /// they are used when Ollama fails or is unavailable.
    /// </summary>
    public static async Task<LlmRouter> BuildAsync(
        OllamaConfig ollamaConfig, ApiKeyService keys, ApiBudgetService? budget,
        CircuitBreakerService? circuit = null, CancellationToken ct = default)
    {
        var router = new LlmRouter(budget, circuit, ollamaConfig);

        // Ollama is the primary provider — local, free, no API costs
        router._providers.Add(new(new OllamaLlmProvider(ollamaConfig), null, true, null));

        // Cloud providers as fallbacks (tried only if Ollama fails)
        if (keys.IsAvailable("anthropic"))
        {
            var entry = AutoFillContextSizes(keys.GetService("anthropic")!);
            var provider = new AnthropicLlmProvider(entry);
            if (await provider.IsAvailableAsync(ct))
                router._providers.Add(new(provider, "anthropic", false, entry));
        }

        if (keys.IsAvailable("openai"))
        {
            var entry = AutoFillContextSizes(keys.GetService("openai")!);
            var provider = new OpenAiLlmProvider(entry);
            if (await provider.IsAvailableAsync(ct))
                router._providers.Add(new(provider, "openai", false, entry));
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
    /// Tries cloud providers first (if budget allows), falls back to Ollama.
    /// </summary>
    public async Task<string> GenerateAsync(LlmRequest request, CancellationToken ct = default)
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
                            var failureType = CircuitBreakerService.ClassifyBudgetDenial(check.DenialReason);
                            await _circuit.TripCircuitAsync(entry.BudgetServiceName, failureType, check.DenialReason);
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[yellow]{entry.Provider.Name}: {check.DenialReason} — trying next[/]");
                        }
                        continue;
                    }
                }
            }

            try
            {
                var result = await entry.Provider.GenerateAsync(request, ct);

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
                AnsiConsole.MarkupLine(
                    $"[yellow]{entry.Provider.Name} failed: {ex.Message} — trying next provider[/]");
            }
        }

        throw new InvalidOperationException("All LLM providers failed. Is Ollama running?");
    }

    /// <summary>
    /// Generate with a specific role hint (main/sentinel).
    /// </summary>
    public Task<string> GenerateAsync(string prompt, string? systemPrompt = null,
        double temperature = 0.4, string role = "main", bool jsonMode = false,
        CancellationToken ct = default) =>
        GenerateAsync(new LlmRequest
        {
            Prompt = prompt,
            SystemPrompt = systemPrompt,
            Temperature = temperature,
            Role = role,
            JsonMode = jsonMode
        }, ct);

    /// <summary>
    /// Check if any provider is available.
    /// </summary>
    public async Task<bool> IsAnyAvailableAsync(CancellationToken ct = default)
    {
        foreach (var entry in _providers)
        {
            if (await entry.Provider.IsAvailableAsync(ct))
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
            var available = await entry.Provider.IsAvailableAsync(ct);
            var type = entry.IsLocal ? "local" : "cloud";
            var budgetInfo = !entry.IsLocal && entry.BudgetServiceName != null
                ? " (budget-controlled)"
                : "";
            var ctx = entry.ServiceEntry?.ContextSize ?? (entry.IsLocal ? _ollamaConfig.ContextSize : 0);
            var ctxInfo = ctx > 0 ? $", {ctx / 1000}K ctx" : "";
            var status = available ? "[green]available[/]" : "[yellow]unavailable[/]";
            AnsiConsole.MarkupLine($"  LLM {entry.Provider.Name} ({type}{budgetInfo}{ctxInfo}): {status}");
        }
    }

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
