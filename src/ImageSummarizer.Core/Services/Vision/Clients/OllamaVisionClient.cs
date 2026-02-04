using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Services.Vision.Clients;

/// <summary>
///     Ollama local vision client for image analysis
///     Uses local Ollama models like minicpm-v, llava, bakllava, etc.
/// </summary>
public class OllamaVisionClient : IVisionClient
{
    private readonly string _baseUrl;
    private readonly string _defaultModel;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaVisionClient> _logger;

    public OllamaVisionClient(IConfiguration configuration, ILogger<OllamaVisionClient> logger)
    {
        _logger = logger;
        _baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _defaultModel = configuration["Ollama:VisionModel"] ?? "minicpm-v:8b";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    public string Provider => "Ollama";

    public async Task<(bool Available, string? Message)> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        try
        {
            // Check if Ollama is running
            var response = await _httpClient.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode) return (false, $"Ollama not responding at {_baseUrl}");

            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(ct);
            if (tags?.Models == null) return (false, "Could not retrieve model list from Ollama");

            // Check if default vision model is installed
            var hasVisionModel = tags.Models.Any(m =>
                m.Name.StartsWith(_defaultModel.Split(':')[0], StringComparison.OrdinalIgnoreCase));

            if (!hasVisionModel)
                return (false, $"Vision model '{_defaultModel}' not found. Install with: ollama pull {_defaultModel}");

            return (true, $"Ollama ready with {_defaultModel}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    public async Task<VisionResult> AnalyzeImageAsync(
        string imagePath,
        string prompt,
        string? model = null,
        double? temperature = null,
        CancellationToken ct = default)
    {
        try
        {
            // Read image and convert to base64
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Image = Convert.ToBase64String(imageBytes);

            var modelToUse = model ?? _defaultModel;

            // Ollama uses "options" object for parameters like temperature
            // Temperature: 0.0 = deterministic, 1.0 = default, 2.0 = very creative
            var request = new
            {
                model = modelToUse,
                prompt,
                images = new[] { base64Image },
                stream = false,
                options = temperature.HasValue ? new { temperature = temperature.Value } : null
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);

            if (result == null || string.IsNullOrWhiteSpace(result.Response))
                return new VisionResult(
                    false,
                    "No response from Ollama",
                    null,
                    modelToUse,
                    Provider);

            _logger.LogInformation("Ollama vision analysis completed for {ImagePath} using {Model}", imagePath,
                modelToUse);

            return new VisionResult(
                true,
                null,
                result.Response,
                modelToUse,
                Provider,
                Metadata: new Dictionary<string, object>
                {
                    { "done", result.Done }
                });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {BaseUrl}", _baseUrl);
            return new VisionResult(
                false,
                $"Connection failed: {ex.Message}",
                null,
                Provider: Provider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama vision analysis failed for {ImagePath}", imagePath);
            return new VisionResult(
                false,
                $"Analysis failed: {ex.Message}",
                null,
                Provider: Provider);
        }
    }
}

// Ollama API response models
internal record OllamaGenerateResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("response")]
    string Response,
    [property: JsonPropertyName("done")] bool Done);

internal record OllamaTagsResponse(
    [property: JsonPropertyName("models")] List<OllamaModel>? Models);

internal record OllamaModel(
    [property: JsonPropertyName("name")] string Name);