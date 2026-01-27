using System.Text.Json;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Loads and manages API service definitions.
/// Keys are loaded from (in priority order):
/// 1. .NET user secrets (highest — explicitly managed per-project)
/// 2. Environment variables (DOOM_*, OPENAI_API_KEY, ANTHROPIC_API_KEY)
/// 3. DoomConfig JSON (keys array)
/// </summary>
public class ApiKeyService
{
    private const string UserSecretsId = "bc19ab4d-717e-4cf3-bdd3-a5ee817de654";

    private readonly Dictionary<string, ApiKeyEntry> _services = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Get a service definition by name. Returns null if not configured.</summary>
    public ApiKeyEntry? GetService(string name) =>
        _services.TryGetValue(name, out var svc) ? svc : null;

    /// <summary>Check if a named service has a valid API key and is enabled.</summary>
    public bool IsAvailable(string name) =>
        _services.TryGetValue(name, out var svc) && svc.Enabled && !string.IsNullOrEmpty(svc.ApiKey);

    /// <summary>Shortcut: Google Search needs both key and CX.</summary>
    public bool HasGoogleSearch =>
        IsAvailable("google_search") && !string.IsNullOrEmpty(GetService("google_search")?.SearchEngineId);

    public bool HasGooglePlaces => IsAvailable("google_places");

    /// <summary>
    /// Load services from all sources (env > user-secrets > config).
    /// </summary>
    public static ApiKeyService Load(DoomConfig config)
    {
        var svc = new ApiKeyService();

        // 1. Config JSON entries (lowest priority)
        foreach (var entry in config.Keys)
        {
            if (!string.IsNullOrEmpty(entry.Name))
                svc._services[entry.Name] = entry;
        }

        // Ensure known services exist with defaults even if not in config
        svc.EnsureDefaults();

        // 2. Environment variables (middle priority — can be stale)
        svc.MergeKey("google_search", Env("DOOM_GOOGLE_SEARCH"), Env("DOOM_GOOGLE_SEARCH_CX"));
        svc.MergeKey("google_places", Env("DOOM_GOOGLE_PLACES"), null);
        svc.MergeKey("openai", Env("OPENAI_API_KEY"), Env("DOOM_OPENAI_MODELS"));
        svc.MergeKey("anthropic", Env("ANTHROPIC_API_KEY"), Env("DOOM_ANTHROPIC_MODELS"));
        svc.MergeKey("brave_search", Env("DOOM_BRAVE_SEARCH"), null);
        svc.MergeKey("serper", Env("DOOM_SERPER"), null);
        svc.MergeKey("newsapi", Env("DOOM_NEWSAPI"), null);
        svc.MergeKey("newsdata", Env("DOOM_NEWSDATA"), null);
        svc.MergeKey("tavily", Env("DOOM_TAVILY"), null);
        svc.MergeKey("jina", Env("DOOM_JINA"), null);

        // 3. User secrets (highest priority — explicitly managed per-project)
        try
        {
            var secrets = LoadUserSecrets();
            if (secrets != null)
            {
                svc.MergeKey("google_search", secrets.GoogleSearch, secrets.GoogleSearchCx);
                svc.MergeKey("google_places", secrets.GooglePlaces, null);
                svc.MergeKey("openai", secrets.OpenAi, secrets.OpenAiModels);
                svc.MergeKey("anthropic", secrets.Anthropic, secrets.AnthropicModels);
                svc.MergeKey("brave_search", secrets.BraveSearch, null);
                svc.MergeKey("serper", secrets.Serper, null);
                svc.MergeKey("newsapi", secrets.NewsApi, null);
                svc.MergeKey("newsdata", secrets.NewsData, null);
                svc.MergeKey("tavily", secrets.Tavily, null);
                svc.MergeKey("jina", secrets.Jina, null);
            }
        }
        catch
        {
            // User secrets not available — not fatal
        }

        // Google Places can share the same key as Google Search
        if (!svc.HasGooglePlaces && svc.IsAvailable("google_search"))
        {
            var gsearch = svc.GetService("google_search")!;
            var gplaces = svc.GetService("google_places")!;
            if (string.IsNullOrEmpty(gplaces.ApiKey))
                svc._services["google_places"] = gplaces with { ApiKey = gsearch.ApiKey };
        }

        return svc;
    }

    public void PrintStatus()
    {
        foreach (var (name, entry) in _services.OrderBy(kv => kv.Key))
        {
            var hasKey = !string.IsNullOrEmpty(entry.ApiKey);
            var ready = name == "google_search"
                ? hasKey && !string.IsNullOrEmpty(entry.SearchEngineId)
                : hasKey;

            var status = !entry.Enabled
                ? "[dim]disabled[/]"
                : ready
                    ? $"[green]ready[/] ({entry.MaxRequestsPerDay}/day, ${entry.CostPerRequest:F3}/req)"
                    : "[yellow]no API key[/]";

            AnsiConsole.MarkupLine($"  {name}: {status}");
        }
    }

    /// <summary>Get all enabled and configured services.</summary>
    public IEnumerable<ApiKeyEntry> GetConfiguredServices() =>
        _services.Values.Where(s => s.Enabled && !string.IsNullOrEmpty(s.ApiKey));

    private void EnsureDefaults()
    {
        if (!_services.ContainsKey("google_search"))
        {
            _services["google_search"] = new ApiKeyEntry
            {
                Name = "google_search",
                Enabled = true,
                MaxRequestsPerDay = 100,
                CostPerRequest = 0.005 // $5/1000
            };
        }

        if (!_services.ContainsKey("google_places"))
        {
            _services["google_places"] = new ApiKeyEntry
            {
                Name = "google_places",
                Enabled = true,
                MaxRequestsPerDay = 50,
                CostPerRequest = 0.017 // $17/1000
            };
        }

        if (!_services.ContainsKey("openai"))
        {
            _services["openai"] = new ApiKeyEntry
            {
                Name = "openai",
                SearchEngineId = "gpt-4o-mini|gpt-4o-mini", // main|sentinel model
                Enabled = true,
                MaxRequestsPerDay = 200,
                CostPerRequest = 0.0003 // ~$0.30/1M input tokens for gpt-4o-mini
            };
        }

        if (!_services.ContainsKey("anthropic"))
        {
            _services["anthropic"] = new ApiKeyEntry
            {
                Name = "anthropic",
                SearchEngineId = "claude-sonnet-4-20250514|claude-3-5-haiku-latest", // main|sentinel
                Enabled = true,
                MaxRequestsPerDay = 200,
                CostPerRequest = 0.003 // ~$3/1M input tokens for Sonnet 3.5
            };
        }

        // Free search APIs — strict free-tier budgets
        if (!_services.ContainsKey("brave_search"))
        {
            _services["brave_search"] = new ApiKeyEntry
            {
                Name = "brave_search",
                Enabled = true,
                MaxRequestsPerDay = 60,  // Free: 2,000/month ≈ 66/day, stay under
                MaxRequests = 0,         // Unlimited lifetime on free tier
                CostPerRequest = 0       // Free
            };
        }

        if (!_services.ContainsKey("serper"))
        {
            _services["serper"] = new ApiKeyEntry
            {
                Name = "serper",
                Enabled = true,
                MaxRequestsPerDay = 40,  // Conservative: 2,500 total free queries
                MaxRequests = 2400,      // Stay under 2,500 lifetime cap
                CostPerRequest = 0       // Free tier
            };
        }

        if (!_services.ContainsKey("newsapi"))
        {
            _services["newsapi"] = new ApiKeyEntry
            {
                Name = "newsapi",
                Enabled = true,
                MaxRequestsPerDay = 90,  // Free: 100/day, stay under
                MaxRequests = 0,
                CostPerRequest = 0       // Free dev tier
            };
        }

        if (!_services.ContainsKey("newsdata"))
        {
            _services["newsdata"] = new ApiKeyEntry
            {
                Name = "newsdata",
                Enabled = true,
                MaxRequestsPerDay = 180, // Free: 200 credits/day, stay under
                MaxRequests = 0,
                CostPerRequest = 0       // Free tier
            };
        }

        if (!_services.ContainsKey("tavily"))
        {
            _services["tavily"] = new ApiKeyEntry
            {
                Name = "tavily",
                Enabled = true,
                MaxRequestsPerDay = 30,  // Free: 1,000/month ≈ 33/day, stay under
                MaxRequests = 0,
                CostPerRequest = 0       // Free tier
            };
        }

        if (!_services.ContainsKey("jina"))
        {
            _services["jina"] = new ApiKeyEntry
            {
                Name = "jina",
                Enabled = true,
                MaxRequestsPerDay = 50,  // Conservative — free tier rate-limited
                MaxRequests = 0,
                CostPerRequest = 0       // Free
            };
        }
    }

    private void MergeKey(string name, string? apiKey, string? searchEngineId)
    {
        if (string.IsNullOrEmpty(apiKey) && string.IsNullOrEmpty(searchEngineId))
            return;

        var existing = _services.GetValueOrDefault(name) ?? new ApiKeyEntry { Name = name };

        _services[name] = existing with
        {
            ApiKey = !string.IsNullOrEmpty(apiKey) ? apiKey : existing.ApiKey,
            SearchEngineId = !string.IsNullOrEmpty(searchEngineId) ? searchEngineId : existing.SearchEngineId
        };
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>
    /// Parsed secrets from the .NET user secrets file.
    /// </summary>
    private record SecretValues(
        string? GoogleSearch, string? GoogleSearchCx, string? GooglePlaces,
        string? OpenAi, string? OpenAiModels,
        string? Anthropic, string? AnthropicModels,
        string? BraveSearch, string? Serper, string? NewsApi,
        string? NewsData, string? Tavily, string? Jina);

    /// <summary>
    /// Read the .NET user secrets JSON file directly.
    /// Maps flat key names to service definitions.
    /// </summary>
    private static SecretValues? LoadUserSecrets()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var secretsPath = Path.Combine(appData, "Microsoft", "UserSecrets", UserSecretsId, "secrets.json");

        if (!File.Exists(secretsPath))
            return null;

        var json = File.ReadAllText(secretsPath);
        using var doc = JsonDocument.Parse(json);

        string? searchKey = null, searchCx = null, placesKey = null;
        string? openAiKey = null, openAiModels = null;
        string? anthropicKey = null, anthropicModels = null;
        string? braveKey = null, serperKey = null, newsApiKey = null;
        string? newsDataKey = null, tavilyKey = null, jinaKey = null;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var key = prop.Name;
            var val = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;

            // Skip stub/placeholder values
            if (val != null && val.StartsWith("YOUR_", StringComparison.Ordinal))
                continue;

            if (key.EndsWith("GoogleSearch", StringComparison.OrdinalIgnoreCase) && !key.Contains("Cx"))
                searchKey = val;
            else if (key.EndsWith("GoogleSearchCx", StringComparison.OrdinalIgnoreCase))
                searchCx = val;
            else if (key.EndsWith("GooglePlaces", StringComparison.OrdinalIgnoreCase))
                placesKey = val;
            else if (key.EndsWith("OpenAi", StringComparison.OrdinalIgnoreCase) ||
                     key.EndsWith("OpenAI", StringComparison.Ordinal))
                openAiKey = val;
            else if (key.EndsWith("OpenAiModels", StringComparison.OrdinalIgnoreCase))
                openAiModels = val;
            else if (key.EndsWith("Anthropic", StringComparison.OrdinalIgnoreCase) &&
                     !key.Contains("Models"))
                anthropicKey = val;
            else if (key.EndsWith("AnthropicModels", StringComparison.OrdinalIgnoreCase))
                anthropicModels = val;
            else if (key.EndsWith("BraveSearch", StringComparison.OrdinalIgnoreCase))
                braveKey = val;
            else if (key.EndsWith("Serper", StringComparison.OrdinalIgnoreCase))
                serperKey = val;
            else if (key.EndsWith("NewsApi", StringComparison.OrdinalIgnoreCase))
                newsApiKey = val;
            else if (key.EndsWith("NewsData", StringComparison.OrdinalIgnoreCase))
                newsDataKey = val;
            else if (key.EndsWith("Tavily", StringComparison.OrdinalIgnoreCase))
                tavilyKey = val;
            else if (key.EndsWith("Jina", StringComparison.OrdinalIgnoreCase))
                jinaKey = val;
        }

        return new SecretValues(searchKey, searchCx, placesKey,
            openAiKey, openAiModels, anthropicKey, anthropicModels,
            braveKey, serperKey, newsApiKey, newsDataKey, tavilyKey, jinaKey);
    }
}
