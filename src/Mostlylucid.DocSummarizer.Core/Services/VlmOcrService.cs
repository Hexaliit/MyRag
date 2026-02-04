using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Core.Services;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Service for performing VLM-based OCR using Ollama vision models.
///     Converts document images to structured Markdown.
/// </summary>
public class VlmOcrService : IVlmOcrService
{
    /// <summary>
    ///     System prompt for OCR extraction.
    /// </summary>
    private const string OcrSystemPrompt =
        @"You are an expert document OCR system. Your task is to extract ALL text from this document image and convert it to clean, well-structured Markdown.

Guidelines:
1. Preserve the document structure: headings, paragraphs, lists, tables
2. Use appropriate Markdown formatting:
   - # for main headings, ## for subheadings
   - **bold** for emphasis
   - - or * for bullet lists
   - | for tables with proper alignment
3. Preserve all text content accurately - do not summarize or omit text
4. Handle multi-column layouts by reading left-to-right, top-to-bottom
5. For tables, ensure proper column alignment
6. If text is unclear, use [unclear] marker
7. Skip purely decorative elements (logos, borders, page numbers)

Output ONLY the Markdown content. Do not include explanations or preamble.";

    private readonly HttpClient _httpClient;
    private readonly ILogger<VlmOcrService>? _logger;
    private readonly string _visionModel;

    public VlmOcrService(
        VlmOcrConfig config,
        ILogger<VlmOcrService>? logger = null)
    {
        _logger = logger;
        _visionModel = config.Model;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
    }

    /// <summary>
    ///     Perform OCR on an image file and return structured Markdown.
    /// </summary>
    public async Task<VlmOcrResult> OcrImageToMarkdownAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        try
        {
            _logger?.LogDebug("Starting VLM OCR on {ImagePath} with model {Model}",
                Path.GetFileName(imagePath), _visionModel);

            // Read and encode image
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Image = Convert.ToBase64String(imageBytes);

            // Prepare Ollama vision request
            var request = new OllamaVisionRequest
            {
                Model = _visionModel,
                Prompt = OcrSystemPrompt,
                Images = [base64Image],
                Stream = false,
                Options = new OllamaVisionOptions
                {
                    Temperature = 0.1, // Low temperature for accurate transcription
                    NumPredict = 8192 // Allow long outputs for full documents
                }
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogError("VLM OCR failed: {StatusCode} - {Error}",
                    response.StatusCode, errorBody);
                return new VlmOcrResult
                {
                    Success = false,
                    Error = $"Ollama request failed: {response.StatusCode} - {errorBody}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaVisionResponse>(ct);

            if (result == null || string.IsNullOrWhiteSpace(result.Response))
                return new VlmOcrResult
                {
                    Success = false,
                    Error = "No response from vision model"
                };

            var markdown = CleanMarkdownOutput(result.Response);

            _logger?.LogInformation("VLM OCR completed for {ImagePath}: {Chars} chars extracted",
                Path.GetFileName(imagePath), markdown.Length);

            return new VlmOcrResult
            {
                Success = true,
                Markdown = markdown,
                Model = _visionModel,
                Confidence = EstimateConfidence(markdown)
            };
        }
        catch (OperationCanceledException)
        {
            return new VlmOcrResult
            {
                Success = false,
                Error = "Operation cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "VLM OCR failed for {ImagePath}", imagePath);
            return new VlmOcrResult
            {
                Success = false,
                Error = $"OCR failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    ///     Check if the VLM service is available.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode)
                return false;

            var tags = await response.Content.ReadFromJsonAsync<VlmOcrTagsResponse>(ct);
            return tags?.Models?.Any(m =>
                m.Name?.StartsWith(_visionModel.Split(':')[0], StringComparison.OrdinalIgnoreCase) == true) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Clean up markdown output from the model.
    /// </summary>
    private static string CleanMarkdownOutput(string response)
    {
        var result = response.Trim();

        // Remove markdown code blocks if model wrapped output
        if (result.StartsWith("```markdown", StringComparison.OrdinalIgnoreCase))
            result = result[11..];
        else if (result.StartsWith("```md", StringComparison.OrdinalIgnoreCase))
            result = result[5..];
        else if (result.StartsWith("```")) result = result[3..];

        if (result.EndsWith("```")) result = result[..^3];

        return result.Trim();
    }

    /// <summary>
    ///     Estimate OCR confidence based on output characteristics.
    /// </summary>
    private static double EstimateConfidence(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return 0.0;

        var confidence = 0.5; // Base confidence

        // Longer outputs suggest successful extraction
        if (markdown.Length > 100) confidence += 0.1;
        if (markdown.Length > 500) confidence += 0.1;
        if (markdown.Length > 1000) confidence += 0.1;

        // Presence of structure suggests good extraction
        if (markdown.Contains('#')) confidence += 0.05; // Headings
        if (markdown.Contains('|')) confidence += 0.05; // Tables
        if (markdown.Contains("- ") || markdown.Contains("* ")) confidence += 0.05; // Lists

        // [unclear] markers reduce confidence
        var unclearCount = markdown.Split("[unclear]").Length - 1;
        confidence -= unclearCount * 0.1;

        return Math.Clamp(confidence, 0.0, 1.0);
    }
}

/// <summary>
///     Interface for VLM OCR services.
/// </summary>
public interface IVlmOcrService
{
    Task<VlmOcrResult> OcrImageToMarkdownAsync(string imagePath, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

// Ollama Vision API models
internal class OllamaVisionRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";

    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";

    [JsonPropertyName("images")] public string[] Images { get; set; } = [];

    [JsonPropertyName("stream")] public bool Stream { get; set; }

    [JsonPropertyName("options")] public OllamaVisionOptions? Options { get; set; }
}

internal class OllamaVisionOptions
{
    [JsonPropertyName("temperature")] public double Temperature { get; set; }

    [JsonPropertyName("num_predict")] public int NumPredict { get; set; }
}

internal class OllamaVisionResponse
{
    [JsonPropertyName("model")] public string? Model { get; set; }

    [JsonPropertyName("response")] public string? Response { get; set; }

    [JsonPropertyName("done")] public bool Done { get; set; }
}

internal class VlmOcrTagsResponse
{
    [JsonPropertyName("models")] public List<VlmOcrTagModel>? Models { get; set; }
}

internal class VlmOcrTagModel
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}