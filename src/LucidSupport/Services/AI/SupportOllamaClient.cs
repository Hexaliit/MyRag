using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LucidSupport.Services.AI;

/// <summary>
///     Lightweight Ollama client for the sentinel model.
///     Thin HttpClient wrapper with timeout and graceful fallback.
/// </summary>
public sealed class SupportOllamaClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly int _maxTokens;

    public SupportOllamaClient(string baseUrl, string model, int maxTokens)
    {
        _model = model;
        _maxTokens = maxTokens;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>Generate a completion from the sentinel model.</summary>
    public async Task<string?> GenerateAsync(string prompt, string? system = null, CancellationToken ct = default)
    {
        var request = new OllamaGenerateRequest
        {
            Model = _model,
            Prompt = prompt,
            System = system,
            Stream = false,
            Options = new OllamaRequestOptions
            {
                Temperature = 0.2,
                NumPredict = _maxTokens
            }
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
            return result?.Response?.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Check if Ollama is reachable and the model is available.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    // ── JSON types ──

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
        [JsonPropertyName("system")] public string? System { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("options")] public OllamaRequestOptions? Options { get; set; }
    }

    private sealed class OllamaRequestOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("num_predict")] public int NumPredict { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
        [JsonPropertyName("done")] public bool Done { get; set; }
    }
}
