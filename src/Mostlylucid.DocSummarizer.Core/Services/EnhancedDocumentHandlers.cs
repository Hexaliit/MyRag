using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Core.Services;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Enhanced PDF handler that uses native extraction with VLM OCR fallback.
///     Handles scanned PDFs and complex documents that fail native extraction.
///     Higher priority than default PdfDocumentHandler (10 vs 0).
/// </summary>
public class EnhancedPdfDocumentHandler : IDocumentHandler
{
    private readonly ILogger<EnhancedPdfDocumentHandler>? _logger;
    private readonly DocumentToMarkdownService _markdownService;

    public EnhancedPdfDocumentHandler(
        DocumentToMarkdownService markdownService,
        ILogger<EnhancedPdfDocumentHandler>? logger = null)
    {
        _markdownService = markdownService;
        _logger = logger;
    }

    public IReadOnlyList<string> SupportedExtensions => [".pdf"];
    public int Priority => 10; // Higher than default PdfDocumentHandler (0)
    public string HandlerName => "EnhancedPDF";

    public bool CanHandle(string filePath)
    {
        return File.Exists(filePath);
    }

    public async Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        _logger?.LogDebug("Processing PDF with enhanced handler: {FilePath}", filePath);

        var result = await _markdownService.ConvertAsync(
            filePath,
            new ConversionOptions
            {
                ExtractTables = true,
                RenderDpi = 300
            },
            ct: options.CancellationToken);

        if (!result.Success)
        {
            _logger?.LogWarning("Enhanced PDF processing failed: {Error}. Falling back to basic extraction.",
                result.Error);

            // Return empty content with error metadata
            return new DocumentContent
            {
                Markdown = $"<!-- PDF processing failed: {result.Error} -->",
                Title = Path.GetFileNameWithoutExtension(filePath),
                ContentType = "pdf",
                Metadata = new Dictionary<string, object>
                {
                    ["error"] = result.Error ?? "Unknown error",
                    ["extractionMethod"] = "failed"
                }
            };
        }

        _logger?.LogInformation(
            "Enhanced PDF processed: {Pages} pages, {Method} extraction, {Chars} chars",
            result.PageCount,
            result.ExtractionMethod,
            result.Markdown?.Length ?? 0);

        // Extract title from first heading if present
        var title = ExtractTitleFromMarkdown(result.Markdown) ?? Path.GetFileNameWithoutExtension(filePath);

        return new DocumentContent
        {
            Markdown = result.Markdown ?? "",
            Title = title,
            ContentType = "pdf",
            Metadata = new Dictionary<string, object>
            {
                ["extractionMethod"] = result.ExtractionMethod,
                ["pageCount"] = result.PageCount,
                ["processingTimeMs"] = result.ProcessingTime.TotalMilliseconds,
                ["textDensity"] = result.AverageTextDensity,
                ["tableCount"] = result.ExtractedTables?.Count ?? 0
            }
        };
    }

    private static string? ExtractTitleFromMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return null;

        var lines = markdown.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# "))
                return trimmed[2..].Trim();
        }

        return null;
    }
}

/// <summary>
///     Image document handler that uses VLM OCR to extract text from images.
///     Supports common image formats: PNG, JPG, JPEG, WebP, GIF, BMP, TIFF.
/// </summary>
public class VlmImageOcrHandler : IDocumentHandler
{
    private readonly ILogger<VlmImageOcrHandler>? _logger;
    private readonly DocumentToMarkdownService _markdownService;

    public VlmImageOcrHandler(
        DocumentToMarkdownService markdownService,
        ILogger<VlmImageOcrHandler>? logger = null)
    {
        _markdownService = markdownService;
        _logger = logger;
    }

    public IReadOnlyList<string> SupportedExtensions =>
        [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tiff", ".tif"];

    public int Priority => 10;
    public string HandlerName => "ImageOCR";

    public bool CanHandle(string filePath)
    {
        return File.Exists(filePath);
    }

    public async Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        _logger?.LogDebug("Processing image with VLM OCR: {FilePath}", filePath);

        var result = await _markdownService.ConvertAsync(
            filePath,
            new ConversionOptions(),
            ct: options.CancellationToken);

        if (!result.Success)
        {
            _logger?.LogWarning("Image OCR failed: {Error}", result.Error);

            return new DocumentContent
            {
                Markdown = $"<!-- Image OCR failed: {result.Error} -->",
                Title = Path.GetFileNameWithoutExtension(filePath),
                ContentType = "image",
                Metadata = new Dictionary<string, object>
                {
                    ["error"] = result.Error ?? "Unknown error"
                }
            };
        }

        _logger?.LogInformation("Image OCR completed: {Chars} chars extracted", result.Markdown?.Length ?? 0);

        // Get OCR model info from page results if available
        var model = result.PageResults?.FirstOrDefault()?.Model ?? "unknown";
        var confidence = result.PageResults?.FirstOrDefault()?.Confidence ?? 0.0;

        return new DocumentContent
        {
            Markdown = result.Markdown ?? "",
            Title = Path.GetFileNameWithoutExtension(filePath),
            ContentType = "image",
            Metadata = new Dictionary<string, object>
            {
                ["ocrModel"] = model,
                ["ocrConfidence"] = confidence,
                ["processingTimeMs"] = result.ProcessingTime.TotalMilliseconds
            }
        };
    }
}

/// <summary>
///     Extension methods to register enhanced document handlers
/// </summary>
public static class EnhancedDocumentHandlerExtensions
{
    /// <summary>
    ///     Register enhanced document handlers (PDF with OCR fallback, Image OCR)
    ///     These have higher priority than default handlers.
    /// </summary>
    public static IDocumentHandlerRegistry RegisterEnhancedHandlers(
        this IDocumentHandlerRegistry registry,
        DocumentToMarkdownService markdownService,
        ILogger<EnhancedPdfDocumentHandler>? pdfLogger = null,
        ILogger<VlmImageOcrHandler>? imageLogger = null)
    {
        registry.Register(new EnhancedPdfDocumentHandler(markdownService, pdfLogger));
        registry.Register(new VlmImageOcrHandler(markdownService, imageLogger));
        return registry;
    }
}