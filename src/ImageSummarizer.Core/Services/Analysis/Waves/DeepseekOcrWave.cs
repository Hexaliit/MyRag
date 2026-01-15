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
/// DeepSeek OCR wave (Ollama VLM via OpenAI-compatible API).
/// Uses deepseek-ocr:latest for high-quality OCR with Markdown output.
/// Priority: 53 (after NanonetsOcrWave at 54, before OlmOcr2Wave at 51).
/// </summary>
public class DeepseekOcrWave : IAnalysisWave
{
    private readonly OcrConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeepseekOcrWave>? _logger;

    public string Name => "DeepseekOcrWave";
    public int Priority => 53;
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "ocr", "vlm", "deepseek" };

    public DeepseekOcrWave(
        IOptions<ImageConfig> imageConfig,
        ILogger<DeepseekOcrWave>? logger = null)
    {
        _config = imageConfig.Value.Ocr;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.DeepseekOcrBaseUrl),
            Timeout = TimeSpan.FromSeconds(_config.DeepseekOcrTimeoutSeconds)
        };

        if (!string.IsNullOrWhiteSpace(_config.DeepseekOcrApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _config.DeepseekOcrApiKey);
        }
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        if (!_config.EnableDeepseekOcr)
            return false;

        // Force run if benchmark mode with ForceRunAllSystems
        if (_config.Benchmark.Enabled && _config.Benchmark.ForceRunAllSystems)
            return true;

        if (context.IsWaveSkippedByRouting(Name))
            return false;

        // Skip if we already have DeepSeek results
        if (context.HasSignal("ocr.deepseek.text") || context.HasSignal("ocr.deepseek.markdown"))
            return false;

        // Run if OCR quality is poor or needs escalation
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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger?.LogInformation("Running DeepSeek OCR on {Path}", imagePath);

            var markdown = await ExtractMarkdownAsync(imagePath, ct);
            stopwatch.Stop();

            if (string.IsNullOrWhiteSpace(markdown))
            {
                signals.Add(new Signal
                {
                    Key = "ocr.deepseek.empty",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "deepseek" }
                });
                return signals;
            }

            var cleanedMarkdown = StripCodeFences(markdown);
            var plainText = StripMarkdown(cleanedMarkdown);
            var extractedText = _config.DeepseekOcrPreferMarkdown
                ? cleanedMarkdown
                : plainText;

            // Emit DeepSeek-specific signals
            signals.Add(new Signal
            {
                Key = "ocr.deepseek.markdown",
                Value = cleanedMarkdown,
                Confidence = 0.9,
                Source = Name,
                Tags = new List<string> { "ocr", "markdown", "deepseek" },
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = _config.DeepseekOcrModelName,
                    ["output_format"] = "markdown",
                    ["length"] = cleanedMarkdown.Length,
                    ["duration_ms"] = stopwatch.ElapsedMilliseconds
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.deepseek.text",
                Value = plainText,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "ocr", "text", "deepseek" },
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = _config.DeepseekOcrModelName,
                    ["text_length"] = plainText.Length,
                    ["duration_ms"] = stopwatch.ElapsedMilliseconds
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.deepseek.duration_ms",
                Value = stopwatch.ElapsedMilliseconds,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "timing", "deepseek" }
            });

            // Only emit generic OCR signals if NOT in benchmark mode
            // In benchmark mode, each system should only write to its own namespace
            // to avoid signal pollution (last wave would overwrite Tesseract's ocr.full_text)
            if (!(_config.Benchmark.Enabled && _config.Benchmark.ForceRunAllSystems))
            {
                signals.Add(new Signal
                {
                    Key = "ocr.markdown",
                    Value = cleanedMarkdown,
                    Confidence = 0.88,
                    Source = Name,
                    Tags = new List<string> { "ocr", "markdown" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["model"] = _config.DeepseekOcrModelName
                    }
                });

                signals.Add(new Signal
                {
                    Key = "ocr.text",
                    Value = plainText,
                    Confidence = 0.83,
                    Source = Name,
                    Tags = new List<string> { "ocr", "text" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["model"] = _config.DeepseekOcrModelName
                    }
                });

                signals.Add(new Signal
                {
                    Key = "ocr.full_text",
                    Value = plainText,
                    Confidence = 0.83,
                    Source = Name,
                    Tags = new List<string> { "ocr", SignalTags.Content }
                });
            }

            // Emit content signals if not already present
            var existingContent = context.GetValue<string>("content.extracted_text");
            if (string.IsNullOrWhiteSpace(existingContent))
            {
                signals.Add(new Signal
                {
                    Key = "content.extracted_text",
                    Value = extractedText,
                    Confidence = 0.8,
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
                    Confidence = 0.85,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "markdown" }
                });
            }

            _logger?.LogInformation(
                "DeepSeek OCR completed: {Chars} chars, {Words} words in {Duration}ms",
                plainText.Length,
                plainText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "DeepSeek OCR failed for {Path}", imagePath);
            signals.Add(new Signal
            {
                Key = "ocr.deepseek.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "error", "deepseek" }
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

        var prompt = _config.DeepseekOcrPreferMarkdown
            ? "Extract all visible text from this image. Return Markdown only. Preserve layout, headings, lists, and tables. Do not wrap the output in code fences or explanations."
            : "Extract all visible text from this image. Return plain text only.";

        var request = new
        {
            model = _config.DeepseekOcrModelName,
            temperature = 0.0,
            max_tokens = _config.DeepseekOcrMaxTokens,
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
            _logger?.LogWarning("DeepSeek OCR API error: {Status} {Body}", response.StatusCode, errorContent);
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
