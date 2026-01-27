using System.Text;
using System.Text.Json;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Anthropic Claude LLM provider (cloud, paid). REST API — no SDK dependency.
/// Supports Claude 3.5 Sonnet, Claude 3 Haiku, Claude 3 Opus.
/// </summary>
public class AnthropicLlmProvider : ILlmProvider
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private readonly string _apiKey;
    private readonly string _mainModel;
    private readonly string _sentinelModel;

    public string Name => "anthropic";

    public AnthropicLlmProvider(ApiKeyEntry entry)
    {
        _apiKey = entry.ApiKey ?? throw new ArgumentException("Anthropic API key required");

        // SearchEngineId repurposed: stores "main_model|sentinel_model"
        // Default: claude-3-5-haiku for both (cheap, fast)
        var models = (entry.SearchEngineId ?? "claude-3-5-haiku-latest|claude-3-5-haiku-latest").Split('|');
        _mainModel = models[0];
        _sentinelModel = models.Length > 1 ? models[1] : models[0];
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Anthropic doesn't have a lightweight health check endpoint.
        // Just verify the key is non-empty.
        return !string.IsNullOrEmpty(_apiKey);
    }

    public async Task<string> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var model = request.Role == "sentinel" ? _sentinelModel : _mainModel;

        var messages = new List<object>
        {
            new { role = "user", content = request.Prompt }
        };

        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature
        };

        if (!string.IsNullOrEmpty(request.SystemPrompt))
            payload["system"] = request.SystemPrompt;

        // Anthropic doesn't have a native JSON mode, but we can instruct via prompt
        // (The sentinel/JSON prompts already request JSON format explicitly)

        var json = JsonSerializer.Serialize(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("x-api-key", _apiKey);
        httpRequest.Headers.Add("anthropic-version", ApiVersion);

        var response = await Http.SendAsync(httpRequest, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
            throw new HttpRequestException(
                $"Anthropic API {(int)response.StatusCode}: {errorBody}",
                null, response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(responseJson);

        // Anthropic returns: { content: [{ type: "text", text: "..." }] }
        var contentArray = doc.RootElement.GetProperty("content");
        var sb = new StringBuilder();
        foreach (var block in contentArray.EnumerateArray())
        {
            if (block.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        }

        return sb.ToString();
    }
}
