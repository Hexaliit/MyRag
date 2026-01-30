using LucidRAG.LLM.Services.LoadBalancing;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests.LoadBalancing;

/// <summary>
/// Shared test helpers for load balancing tests.
/// </summary>
internal static class LoadBalancingTestHelpers
{
    /// <summary>
    /// Create an unhealthy endpoint (3 consecutive failures).
    /// </summary>
    internal static EndpointState CreateUnhealthy(string url, string name)
    {
        var ep = new EndpointState(url, name);
        ep.RecordFailure();
        ep.RecordFailure();
        ep.RecordFailure();
        return ep;
    }

    /// <summary>
    /// Create a standard endpoint list for tests.
    /// </summary>
    internal static List<EndpointState> CreateEndpoints(params (string url, string name)[] entries) =>
        entries.Select(e => new EndpointState(e.url, e.name)).ToList();
}

/// <summary>
/// Fake ILlmService for testing without real HTTP.
/// </summary>
internal class FakeLlmService : ILlmService
{
    private readonly string _name;
    private readonly bool _shouldFail;

    public FakeLlmService(string name, bool shouldFail = false)
    {
        _name = name;
        _shouldFail = shouldFail;
    }

    public string ProviderName => $"Fake({_name})";

    public Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        if (_shouldFail) throw new HttpRequestException($"Endpoint {_name} unavailable");
        return Task.FromResult($"{_name}:{prompt}");
    }

    public Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default) where T : class
    {
        if (_shouldFail) throw new HttpRequestException($"Endpoint {_name} unavailable");
        var json = $"{{\"Value\":\"{_name}:{prompt}\"}}";
        return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(json));
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(!_shouldFail);

    public Task<int> GetContextWindowAsync(CancellationToken ct = default)
        => Task.FromResult(8192);
}
