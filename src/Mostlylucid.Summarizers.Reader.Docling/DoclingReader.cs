using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Helpers;
using DoomSummarizer.Plugins;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Summarizers.Reader.Docling;

/// <summary>
/// Document reader that delegates to an external Docling service for enhanced
/// OCR, layout analysis, and structured extraction.
/// Docling handles scanned PDFs, complex tables, and image-heavy documents
/// better than pure .NET extraction.
/// </summary>
public class DoclingReader : IDocumentReader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DoclingReader> _logger;
    private readonly string _baseUrl;

    public DoclingReader(HttpClient httpClient, ILogger<DoclingReader> logger, string baseUrl = "http://localhost:5001")
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public string ReaderName => "docling";
    private static readonly string[] _extensions = [".pdf", ".docx", ".png", ".jpg", ".jpeg", ".tiff", ".bmp"];
    public IReadOnlyList<string> SupportedExtensions => _extensions;
    public int Priority => 200; // Higher priority when available

    public async Task<ReaderResult> ReadAsync(string filePath, ReaderOptions? options = null, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        return await ReadAsync(stream, Path.GetFileName(filePath), options, ct);
    }

    public async Task<ReaderResult> ReadAsync(Stream stream, string fileName, ReaderOptions? options = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string>();

        try
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(stream); // Disposed by MultipartFormDataContent
            content.Add(streamContent, "file", fileName);

            var response = await _httpClient.PostAsync($"{_baseUrl}/v1/convert", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                warnings.Add($"Docling service returned {response.StatusCode}");
                return new ReaderResult
                {
                    Markdown = "",
                    Title = Path.GetFileNameWithoutExtension(fileName),
                    Warnings = warnings,
                    WordCount = 0
                };
            }

            var result = await response.Content.ReadFromJsonAsync<DoclingResponse>(DoclingJsonContext.Default.DoclingResponse, ct);
            if (result == null)
            {
                warnings.Add("Docling returned null response");
                return new ReaderResult
                {
                    Markdown = "",
                    Title = Path.GetFileNameWithoutExtension(fileName),
                    Warnings = warnings,
                    WordCount = 0
                };
            }

            var markdown = result.Markdown ?? "";
            if (!string.IsNullOrWhiteSpace(result.Title))
                metadata["title"] = result.Title;
            if (result.PageCount > 0)
                metadata["page_count"] = result.PageCount.ToString();

            var wordCount = WordCounter.Count(markdown);

            return new ReaderResult
            {
                Markdown = markdown,
                Title = result.Title ?? Path.GetFileNameWithoutExtension(fileName),
                Metadata = metadata,
                Warnings = warnings,
                WordCount = wordCount
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Docling service unavailable at {BaseUrl}", _baseUrl);
            warnings.Add($"Docling service unavailable: {ex.Message}");
            return new ReaderResult
            {
                Markdown = "",
                Title = Path.GetFileNameWithoutExtension(fileName),
                Warnings = warnings,
                WordCount = 0
            };
        }
    }

    /// <summary>
    /// Check if the Docling service is available.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Response from the Docling conversion service.
/// </summary>
public class DoclingResponse
{
    [JsonPropertyName("markdown")]
    public string? Markdown { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("page_count")]
    public int PageCount { get; set; }

    [JsonPropertyName("tables")]
    public List<DoclingTable>? Tables { get; set; }
}

public class DoclingTable
{
    [JsonPropertyName("csv")]
    public string? Csv { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}

[JsonSerializable(typeof(DoclingResponse))]
[JsonSerializable(typeof(DoclingTable))]
internal partial class DoclingJsonContext : JsonSerializerContext;
