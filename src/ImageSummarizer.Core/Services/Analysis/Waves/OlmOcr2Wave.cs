using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// OlmOCR-2 wave (OpenAI-compatible VLM).
/// Runs as the final OCR escalation just before VisionLlmWave.
/// Priority: 51 (right before VisionLlmWave at 50).
/// </summary>
public class OlmOcr2Wave : IAnalysisWave
{
    private readonly OcrConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OlmOcr2Wave>? _logger;

    public string Name => "OlmOcr2Wave";
    public int Priority => 51;
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "ocr", "vlm", "olmocr2" };

    public OlmOcr2Wave(
        IOptions<ImageConfig> imageConfig,
        ILogger<OlmOcr2Wave>? logger = null)
    {
        _config = imageConfig.Value.Ocr;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.OlmOcr2BaseUrl),
            Timeout = TimeSpan.FromSeconds(_config.OlmOcr2TimeoutSeconds)
        };

        if (!string.IsNullOrWhiteSpace(_config.OlmOcr2ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _config.OlmOcr2ApiKey);
        }
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        if (!_config.EnableOlmOcr2)
            return false;

        if (context.IsWaveSkippedByRouting(Name))
            return false;

        if (context.HasSignal("ocr.olmocr2.text") || context.HasSignal("ocr.olmocr2.markdown"))
            return false;

        var nanonetsText = context.GetValue<string>("ocr.nanonets.text");
        if (!string.IsNullOrWhiteSpace(nanonetsText))
            return false;

        var ocrGarbled = context.GetValue<bool>("ocr.quality.is_garbled");
        var needsCorrection = context.GetValue<bool>("ocr.quality.correction_needed") ||
                              context.GetValue<bool>("ocr.quality.llm_escalation_recommended");
        var noTextDetected = context.GetValue<bool>("ocr.quality.no_text_detected");
        var textLikeliness = context.GetValue<double>("content.text_likeliness");

        var existingText = context.GetValue<string>("ocr.full_text")
                           ?? context.GetValue<string>("ocr.voting.consensus_text")
                           ?? context.GetValue<string>("ocr.ml.fused_text")
                           ?? context.GetValue<string>("florence2.ocr_text");

        if (ocrGarbled || needsCorrection)
            return true;

        if (noTextDetected && textLikeliness > 0.3)
            return true;

        return textLikeliness > 0.3 && string.IsNullOrWhiteSpace(existingText);
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        try
        {
            var markdown = await ExtractMarkdownAsync(imagePath, ct);
            if (string.IsNullOrWhiteSpace(markdown))
                return signals;

            var cleanedMarkdown = StripCodeFences(markdown);
            var plainText = StripMarkdown(cleanedMarkdown);
            var extractedText = _config.OlmOcr2PreferMarkdown
                ? cleanedMarkdown
                : plainText;

            signals.Add(new Signal
            {
                Key = "ocr.olmocr2.markdown",
                Value = cleanedMarkdown,
                Confidence = 0.92,
                Source = Name,
                Tags = new List<string> { "ocr", "markdown", "olmocr2" },
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = _config.OlmOcr2ModelName,
                    ["output_format"] = "markdown",
                    ["length"] = cleanedMarkdown.Length
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.olmocr2.text",
                Value = plainText,
                Confidence = 0.88,
                Source = Name,
                Tags = new List<string> { "ocr", "text", "olmocr2" },
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = _config.OlmOcr2ModelName,
                    ["text_length"] = plainText.Length
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.markdown",
                Value = cleanedMarkdown,
                Confidence = 0.92,
                Source = Name,
                Tags = new List<string> { "ocr", "markdown" },
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = _config.OlmOcr2ModelName
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.text",
                Value = plainText,
                Confidence = 0.88,
                Source = Name,
                Tags = new List<string> { "ocr", "text" },
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = _config.OlmOcr2ModelName
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.full_text",
                Value = plainText,
                Confidence = 0.88,
                Source = Name,
                Tags = new List<string> { "ocr", SignalTags.Content }
            });

            var existingContent = context.GetValue<string>("content.extracted_text");
            if (string.IsNullOrWhiteSpace(existingContent))
            {
                signals.Add(new Signal
                {
                    Key = "content.extracted_text",
                    Value = extractedText,
                    Confidence = 0.82,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "text" }
                });
            }

            if (!string.IsNullOrWhiteSpace(cleanedMarkdown))
            {
                signals.Add(new Signal
                {
                    Key = "content.extracted_markdown",
                    Value = cleanedMarkdown,
                    Confidence = 0.88,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "markdown" }
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OlmOCR-2 failed for {Path}", imagePath);
            signals.Add(new Signal
            {
                Key = "ocr.olmocr2.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "error" }
            });
        }

        return signals;
    }

    private async Task<string> ExtractMarkdownAsync(string imagePath, CancellationToken ct)
    {
        var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
        var base64Image = Convert.ToBase64String(imageBytes);

        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        var mediaType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

        var prompt = _config.OlmOcr2PreferMarkdown
            ? "Extract all visible text from this image. Return Markdown only. Preserve layout, headings, lists, and tables. Do not wrap the output in code fences or explanations."
            : "Extract all visible text from this image. Return plain text only.";

        var request = new
        {
            model = _config.OlmOcr2ModelName,
            temperature = 0.0,
            max_tokens = _config.OlmOcr2MaxTokens,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:{mediaType};base64,{base64Image}"
                            }
                        }
                    }
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogWarning("OlmOCR-2 API error: {Status} {Body}", response.StatusCode, errorContent);
            return string.Empty;
        }

        var result = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(ct);
        return result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;
    }

    private static string StripCodeFences(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        var trimmed = input.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var lines = trimmed.Split('\n');
        if (lines.Length < 2)
            return trimmed;

        return string.Join('\n', lines.Skip(1).Reverse().Skip(1).Reverse()).Trim();
    }

    private static string StripMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var text = markdown;
        text = Regex.Replace(text, @"```[\s\S]*?```", " ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"`([^`]*)`", "$1");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]+\)", " ");
        text = Regex.Replace(text, @"\[[^\]]*\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*\d+\.\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*\|", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\|\s*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*:?-{3,}:?\s*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\s{2,}", " ");
        return text.Trim();
    }

    private record OpenAiChatResponse(
        [property: JsonPropertyName("choices")] List<OpenAiChoice> Choices);

    private record OpenAiChoice(
        [property: JsonPropertyName("message")] OpenAiMessage Message);

    private record OpenAiMessage(
        [property: JsonPropertyName("content")] string Content);
}
