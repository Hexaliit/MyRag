using System.Text;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Core.Services;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Scanning.Waves;

/// <summary>
///     Wave for VLM-based OCR extraction.
///     Renders document pages as images and uses vision LLMs to extract structured markdown.
///     Supports multiple providers: Ollama, Anthropic, OpenAI, Nanonets.
/// </summary>
public class VlmOcrWave : IDocumentWave
{
    private readonly IDocumentEscalationService _escalationService;
    private readonly ILogger<VlmOcrWave>? _logger;
    private readonly PdfPageRenderer _pdfRenderer;
    private readonly IVlmOcrService _vlmOcrService;

    public VlmOcrWave(
        IVlmOcrService vlmOcrService,
        PdfPageRenderer pdfRenderer,
        IDocumentEscalationService escalationService,
        ILogger<VlmOcrWave>? logger = null)
    {
        _vlmOcrService = vlmOcrService;
        _pdfRenderer = pdfRenderer;
        _escalationService = escalationService;
        _logger = logger;
    }

    public string Name => "vlm-ocr";
    public string Description => "Vision LLM OCR for scanned/complex documents";
    public int Priority => 50; // Runs after native extraction
    public IReadOnlyList<string> Tags => ["extraction", "ocr", "vlm", "ai"];
    public IReadOnlyList<string> RequiredSignals => []; // Can run standalone for images

    public IReadOnlyList<string> EmittedSignals =>
    [
        "content.markdown",
        "extraction.method",
        "ocr.model",
        "ocr.confidence",
        "ocr.pages_processed"
    ];

    public bool ShouldRun(DocumentScanContext context)
    {
        // Always run for images
        if (IsImageExtension(context.Extension))
            return true;

        // Run for PDFs/DOCX if:
        // 1. Force VLM is enabled
        // 2. Native extraction flagged needs_ocr
        // 3. Text density is too low
        if (context.ForceVlmOcr)
            return true;

        if (context.HasSignal("extraction.needs_ocr") &&
            context.GetSignal<bool>("extraction.needs_ocr"))
            return true;

        var textDensity = context.GetSignal<int>("extraction.text_density");
        return textDensity < 100;
    }

    public async Task<IReadOnlyList<DocumentSignal>> ProcessAsync(
        string documentPath,
        DocumentScanContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DocumentSignal>();

        try
        {
            // Select model based on document characteristics
            var model = (_escalationService as DocumentEscalationService)?.SelectModel(context)
                        ?? "minicpm-v:8b";

            _logger?.LogInformation("VLM OCR processing {Document} with model {Model}",
                Path.GetFileName(documentPath), model);

            var ext = context.Extension;

            if (IsImageExtension(ext))
            {
                // Direct image OCR
                var result = await _vlmOcrService.OcrImageToMarkdownAsync(documentPath, ct);
                AddOcrResultSignals(signals, result, model, 1);
            }
            else if (ext == ".pdf")
            {
                // Render PDF pages and OCR each
                var pagesResult = await ProcessPdfWithOcrAsync(documentPath, context, model, ct);
                signals.AddRange(pagesResult);
            }
            else
            {
                signals.Add(new DocumentSignal
                {
                    Key = "ocr.error",
                    Value = $"Unsupported file type for VLM OCR: {ext}",
                    Confidence = 1.0,
                    Source = Name
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "VLM OCR failed for {Document}", documentPath);
            signals.Add(new DocumentSignal
            {
                Key = "ocr.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name
            });
        }

        return signals;
    }

    private async Task<List<DocumentSignal>> ProcessPdfWithOcrAsync(
        string pdfPath,
        DocumentScanContext context,
        string model,
        CancellationToken ct)
    {
        var signals = new List<DocumentSignal>();
        var sb = new StringBuilder();
        var totalConfidence = 0.0;
        var pagesProcessed = 0;

        // Render all pages
        var renderedPages = await _pdfRenderer.RenderAllPagesAsync(
            pdfPath,
            context.Options.RenderDpi,
            ct: ct);

        try
        {
            foreach (var page in renderedPages)
            {
                ct.ThrowIfCancellationRequested();

                _logger?.LogDebug("OCR processing page {Page}/{Total}",
                    page.PageNumber, renderedPages.Count);

                var result = await _vlmOcrService.OcrImageToMarkdownAsync(page.ImagePath, ct);

                if (result.Success && !string.IsNullOrWhiteSpace(result.Markdown))
                {
                    if (sb.Length > 0)
                        sb.AppendLine("\n---\n"); // Page separator

                    sb.AppendLine($"<!-- PAGE:{page.PageNumber} -->");
                    sb.AppendLine(result.Markdown);

                    totalConfidence += result.Confidence;
                    pagesProcessed++;
                }
                else
                {
                    _logger?.LogWarning("OCR failed for page {Page}: {Error}",
                        page.PageNumber, result.Error);
                }
            }

            var avgConfidence = pagesProcessed > 0 ? totalConfidence / pagesProcessed : 0;

            signals.Add(new DocumentSignal
            {
                Key = "content.markdown",
                Value = sb.ToString(),
                Confidence = avgConfidence,
                Source = Name,
                Tags = ["content", "ocr"],
                Metadata = new Dictionary<string, object>
                {
                    ["pages_total"] = renderedPages.Count,
                    ["pages_successful"] = pagesProcessed
                }
            });

            signals.Add(new DocumentSignal
            {
                Key = "extraction.method",
                Value = "vlm_ocr",
                Confidence = 1.0,
                Source = Name
            });

            signals.Add(new DocumentSignal
            {
                Key = "ocr.model",
                Value = model,
                Confidence = 1.0,
                Source = Name
            });

            signals.Add(new DocumentSignal
            {
                Key = "ocr.confidence",
                Value = avgConfidence,
                Confidence = 1.0,
                Source = Name
            });

            signals.Add(new DocumentSignal
            {
                Key = "ocr.pages_processed",
                Value = pagesProcessed,
                Confidence = 1.0,
                Source = Name
            });

            _logger?.LogInformation(
                "VLM OCR completed: {Processed}/{Total} pages, avg confidence {Confidence:P0}",
                pagesProcessed, renderedPages.Count, avgConfidence);
        }
        finally
        {
            // Clean up rendered images
            _pdfRenderer.CleanupRenderedPages(renderedPages);
        }

        return signals;
    }

    private void AddOcrResultSignals(
        List<DocumentSignal> signals,
        VlmOcrResult result,
        string model,
        int pageCount)
    {
        if (result.Success)
        {
            signals.Add(new DocumentSignal
            {
                Key = "content.markdown",
                Value = result.Markdown ?? "",
                Confidence = result.Confidence,
                Source = Name,
                Tags = ["content", "ocr"]
            });

            signals.Add(new DocumentSignal
            {
                Key = "extraction.method",
                Value = "vlm_ocr",
                Confidence = 1.0,
                Source = Name
            });

            signals.Add(new DocumentSignal
            {
                Key = "ocr.model",
                Value = result.Model ?? model,
                Confidence = 1.0,
                Source = Name
            });

            signals.Add(new DocumentSignal
            {
                Key = "ocr.confidence",
                Value = result.Confidence,
                Confidence = 1.0,
                Source = Name
            });

            signals.Add(new DocumentSignal
            {
                Key = "ocr.pages_processed",
                Value = pageCount,
                Confidence = 1.0,
                Source = Name
            });
        }
        else
        {
            signals.Add(new DocumentSignal
            {
                Key = "ocr.error",
                Value = result.Error ?? "Unknown error",
                Confidence = 1.0,
                Source = Name
            });
        }
    }

    private static bool IsImageExtension(string extension)
    {
        return extension is ".png" or ".jpg" or ".jpeg" or ".webp"
            or ".gif" or ".bmp" or ".tiff" or ".tif";
    }
}