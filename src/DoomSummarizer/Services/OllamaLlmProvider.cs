using System.Text;
using System.Text.Json;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Ollama LLM provider (local, free).
/// Wraps the existing Ollama REST API HTTP calls.
/// </summary>
public class OllamaLlmProvider(OllamaConfig config) : ILlmProvider
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(300)
    };

    public string Name => "ollama";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await Http.GetAsync($"{config.BaseUrl}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var model = request.Role == "sentinel" ? config.SentinelModel : config.Model;

        var payload = new
        {
            model,
            prompt = request.Prompt,
            system = request.SystemPrompt ?? "",
            stream = false,
            options = new
            {
                temperature = request.Temperature,
                num_predict = request.MaxTokens
            },
            format = request.JsonMode ? "json" : (string?)null
        };

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        var response = await Http.PostAsync($"{config.BaseUrl}/api/generate", content, cts.Token);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("response").GetString() ?? "";
    }
}
