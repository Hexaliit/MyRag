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
    private readonly OllamaConfig _ollamaConfig;

    private record ProviderEntry(
        ILlmProvider Provider, string? BudgetServiceName, bool IsLocal, ApiKeyEntry? ServiceEntry);

    public bool HasCloudProvider => _providers.Any(p => !p.IsLocal);

    private LlmRouter(ApiBudgetService? budget, OllamaConfig ollamaConfig)
    {
        _budget = budget;
        _ollamaConfig = ollamaConfig;
    }

    /// <summary>
    /// Build a router from config. Always includes Ollama as final fallback.
    /// Cloud providers are added if their API keys are configured and enabled.
    /// </summary>
    public static LlmRouter Build(OllamaConfig ollamaConfig, ApiKeyService keys, ApiBudgetService? budget)
    {
        var router = new LlmRouter(budget, ollamaConfig);

        // Add cloud providers in priority order (if configured)
        if (keys.IsAvailable("anthropic"))
        {
            var entry = AutoFillContextSizes(keys.GetService("anthropic")!);
            var keyLen = entry.ApiKey?.Length ?? 0;
            var keyPfx = Markup.Escape(entry.ApiKey?[..Math.Min(14, keyLen)] ?? "null");
            AnsiConsole.MarkupLine($"[grey]LLM: Anthropic (key:{keyPfx}... len={keyLen}, models:{entry.SearchEngineId})[/]");
            router._providers.Add(new(new AnthropicLlmProvider(entry), "anthropic", false, entry));
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]LLM: Anthropic not available[/]");
        }

        if (keys.IsAvailable("openai"))
        {
            var entry = AutoFillContextSizes(keys.GetService("openai")!);
            AnsiConsole.MarkupLine($"[grey]LLM: OpenAI configured (key: {entry.ApiKey?[..10]}..., models: {entry.SearchEngineId})[/]");
            router._providers.Add(new(new OpenAiLlmProvider(entry), "openai", false, entry));
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]LLM: OpenAI not available[/]");
        }

        // Ollama always available as free fallback
        router._providers.Add(new(new OllamaLlmProvider(ollamaConfig), null, true, null));

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
            if (!entry.IsLocal && _budget != null && entry.BudgetServiceName != null)
            {
                var check = await _budget.CheckBudgetAsync(entry.BudgetServiceName);
                if (!check.IsAllowed)
                {
                    AnsiConsole.MarkupLine($"[yellow]{entry.Provider.Name}: {check.DenialReason} — trying next[/]");
                    continue;
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
        var ctx = GetContextSize(sentinel ? "sentinel" : "main");
        var availableTokens = ctx - 800;
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
