using Mostlylucid.DocSummarizer.Services.Embeddings;
using Mostlylucid.DocSummarizer.Services.LmStudio;
using Mostlylucid.DocSummarizer.Services.Providers;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Adapter to bridge the new IEmbeddingClient to the legacy IEmbeddingService interface
/// </summary>
public sealed class EmbeddingClientAdapter : IEmbeddingService
{
    private readonly IEmbeddingClient _client;

    public EmbeddingClientAdapter(IEmbeddingClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public int EmbeddingDimension => _client.EmbeddingDimension;

    public Task InitializeAsync(CancellationToken ct = default)
        => _client.InitializeAsync(ct);

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => _client.EmbedAsync(text, ct);

    public Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
        => _client.EmbedBatchAsync(texts, ct);
}

/// <summary>
///     Adapter to bridge the new ILlmClient to the legacy ILlmService interface
/// </summary>
public sealed class LlmClientAdapter : ILlmService
{
    private readonly ILlmClient _client;

    public LlmClientAdapter(ILlmClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string ProviderName => _client.ProviderName;

    public Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        var lmStudioOptions = options != null ? ConvertToLmStudioOptions(options) : null;
        return _client.GenerateAsync(prompt, lmStudioOptions, ct);
    }

    public IAsyncEnumerable<string> GenerateStreamingAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        var lmStudioOptions = options != null ? ConvertToLmStudioOptions(options) : null;
        return _client.GenerateStreamingAsync(prompt, lmStudioOptions, ct);
    }

    public Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        var lmStudioOptions = options != null ? ConvertToLmStudioOptions(options) : null;
        return _client.GenerateJsonAsync<T>(prompt, lmStudioOptions, ct);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => _client.IsAvailableAsync(ct);

    public Task<int> GetContextWindowAsync(CancellationToken ct = default)
        => _client.GetContextWindowAsync(ct);

    private static Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions ConvertToLmStudioOptions(LlmOptions options)
    {
        return new Mostlylucid.DocSummarizer.Services.LmStudio.LlmOptions
        {
            Model = options.Model,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            SystemPrompt = options.SystemPrompt,
            JsonMode = options.JsonMode,
            Role = options.Role,
            TopP = null,
            FrequencyPenalty = null,
            PresencePenalty = null,
            StopSequences = null
        };
    }
}