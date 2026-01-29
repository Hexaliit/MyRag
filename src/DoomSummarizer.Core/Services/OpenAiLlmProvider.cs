using System.Text;
using System.Text.Json;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// OpenAI LLM provider (cloud, paid). REST API — no SDK dependency.
/// Supports GPT-4o-mini (fast/cheap), GPT-4o (main), GPT-4-turbo.
/// </summary>
public class OpenAiLlmProvider : ILlmProvider
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    private const string Endpoint = "https://api.openai.com/v1/chat/completions";

    private readonly string _apiKey;
    private readonly string _mainModel;
    private readonly string _sentinelModel;

    public string Name => "openai";

    public OpenAiLlmProvider(ApiKeyEntry entry)
    {
        _apiKey = entry.ApiKey ?? throw new ArgumentException("OpenAI API key required");

        // SearchEngineId repurposed: stores "main_model|sentinel_model"
        // Default: gpt-4o-mini for both (cheap, fast)
        var models = (entry.SearchEngineId ?? "gpt-4o-mini|gpt-4o-mini").Split('|');
        _mainModel = models[0];
        _sentinelModel = models.Length > 1 ? models[1] : models[0];
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var model = request.Role == "sentinel" ? _sentinelModel : _mainModel;

        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });
        messages.Add(new { role = "user", content = request.Prompt });

        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens
        };

        if (request.JsonMode)
            payload["response_format"] = new { type = "json_object" };

        var json = JsonSerializer.Serialize(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");

        var response = await Http.SendAsync(httpRequest, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
            throw new HttpRequestException(
                $"OpenAI API {(int)response.StatusCode}: {errorBody}",
                null, response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(responseJson);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }
}
