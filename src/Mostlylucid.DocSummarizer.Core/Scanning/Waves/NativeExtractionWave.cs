using System.Text;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace Mostlylucid.DocSummarizer.Scanning.Waves;

/// <summary>
/// Wave for native text extraction from documents.
/// Uses PdfPig for PDFs and OpenXML for DOCX.
/// Fast and local - no external API calls.
/// </summary>
public class NativeExtractionWave : IDocumentWave
{
    private readonly ILogger<NativeExtractionWave>? _logger;

    public NativeExtractionWave(ILogger<NativeExtractionWave>? logger = null)
    {
        _logger = logger;
    }

    public string Name => "native-extraction";
    public string Description => "Fast native text extraction (PdfPig/OpenXML)";
    public int Priority => 100; // Runs first
    public IReadOnlyList<string> Tags => ["extraction", "native", "fast"];
    public IReadOnlyList<string> RequiredSignals => [];
    public IReadOnlyList<string> EmittedSignals =>
    [
        "content.markdown",
        "extraction.method",
        "extraction.text_density",
        "document.page_count",
        "document.title"
    ];

    public bool ShouldRun(DocumentScanContext context)
    {
        // Run for PDFs and DOCX
        var ext = context.Extension;
        return ext is ".pdf" or ".docx";
    }

    public async Task<IReadOnlyList<DocumentSignal>> ProcessAsync(
        string documentPath,
        DocumentScanContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DocumentSignal>();

        try
        {
            var ext = Path.GetExtension(documentPath).ToLowerInvariant();

            var (markdown, pageCount, textDensity, title) = ext switch
            {
                ".pdf" => await ExtractPdfAsync(documentPath, ct),
                ".docx" => await ExtractDocxAsync(documentPath, ct),
                _ => ("", 0, 0, Path.GetFileNameWithoutExtension(documentPath))
            };

            // Determine confidence based on text density
            var confidence = CalculateConfidence(textDensity);

            signals.Add(new DocumentSignal
            {
                Key = "content.markdown",
                Value = markdown,
                Confidence = confidence,
                Source = Name,
                Tags = ["content", "text"],
                Metadata = new Dictionary<string, object>
                {
                    ["char_count"] = markdown.Length,
                    ["line_count"] = markdown.Split('\n').Length
                }
            });

            signals.Add(new DocumentSignal
            {
                Key = "extraction.method",
                Value = "native",
                Confidence = 1.0,
                Source = Name
            });

            signals.Add(new DocumentSignal
            {
                Key = "extraction.text_density",
                Value = textDensity,
                Confidence = 1.0,
                Source = Name
            });

            signals.Add(new DocumentSignal
            {
                Key = "document.page_count",
                Value = pageCount,
                Confidence = 1.0,
                Source = Name
            });

            signals.Add(new DocumentSignal
            {
                Key = "document.title",
                Value = title,
                Confidence = 0.8,
                Source = Name
            });

            // Signal if text density suggests scanned document
            if (textDensity < 100)
            {
                signals.Add(new DocumentSignal
                {
                    Key = "extraction.needs_ocr",
                    Value = true,
                    Confidence = 0.9,
                    Source = Name,
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "Low text density suggests scanned/image-based document"
                    }
                });
            }

            _logger?.LogInformation(
                "Native extraction: {Pages} pages, {Density} chars/page, confidence {Confidence:P0}",
                pageCount, textDensity, confidence);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Native extraction failed for {Document}", documentPath);
            signals.Add(new DocumentSignal
            {
                Key = "extraction.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name
            });
        }

        return signals;
    }

    /// <summary>Maximum pages to extract text from. Prevents OOM on very large PDFs.</summary>
    private const int MaxPdfPages = 2000;

    /// <summary>Maximum accumulated text size in characters (~10 MB of UTF-16).</summary>
    private const int MaxTextChars = 5 * 1024 * 1024;

    private async Task<(string markdown, int pageCount, int textDensity, string title)> ExtractPdfAsync(
        string pdfPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var totalChars = 0;
            string? title = null;

            using var document = PdfDocument.Open(pdfPath);
            var pageCount = document.NumberOfPages;

            if (document.Information?.Title != null)
                title = document.Information.Title;

            // Pre-allocate StringBuilder: ~2 KB per page estimate
            var estimatedChars = Math.Min(pageCount, MaxPdfPages) * 2048;
            var sb = new StringBuilder(Math.Min(estimatedChars, MaxTextChars));

            var pagesExtracted = 0;
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                pagesExtracted++;
                if (pagesExtracted > MaxPdfPages || sb.Length > MaxTextChars)
                {
                    _logger?.LogWarning(
                        "PDF {File} truncated at page {Page}/{Total} (text limit reached)",
                        Path.GetFileName(pdfPath), pagesExtracted - 1, pageCount);
                    break;
                }

                var text = page.Text;
                totalChars += text?.Length ?? 0;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine($"<!-- PAGE:{page.Number} -->");
                    sb.AppendLine(NormalizePdfText(text));
                    sb.AppendLine();
                }
            }

            var textDensity = pageCount > 0 ? totalChars / pageCount : 0;
            return (sb.ToString().Trim(), pageCount, textDensity, title ?? Path.GetFileNameWithoutExtension(pdfPath));
        }, ct);
    }

    private async Task<(string markdown, int pageCount, int textDensity, string title)> ExtractDocxAsync(
        string docxPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var sb = new StringBuilder();
            string? title = null;

            using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(docxPath, false);

            // Try to get title from core properties
            if (doc.PackageProperties?.Title != null)
                title = doc.PackageProperties.Title;

            var body = doc.MainDocumentPart?.Document?.Body;
            if (body != null)
            {
                foreach (var para in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
                {
                    ct.ThrowIfCancellationRequested();

                    var text = para.InnerText;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Check if it's a heading
                        var style = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                        if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            var level = style.Replace("Heading", "").Trim();
                            if (int.TryParse(level, out var headingLevel) && headingLevel >= 1 && headingLevel <= 6)
                            {
                                sb.Append(new string('#', headingLevel));
                                sb.Append(' ');
                            }
                        }
                        sb.AppendLine(text);
                    }
                }
            }

            var content = sb.ToString().Trim();
            // Estimate page count (rough approximation)
            var estimatedPages = Math.Max(1, content.Length / 3000);
            var textDensity = estimatedPages > 0 ? content.Length / estimatedPages : content.Length;

            return (content, estimatedPages, textDensity, title ?? Path.GetFileNameWithoutExtension(docxPath));
        }, ct);
    }

    private static string NormalizePdfText(string text)
    {
        var lines = text.Split('\n');
        var result = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                result.AppendLine();
                result.AppendLine();
                continue;
            }

            result.Append(line);

            var endsWithPunctuation = line.EndsWith('.') || line.EndsWith('!') ||
                                      line.EndsWith('?') || line.EndsWith('"');
            var isShortLine = line.Length < 60;
            var nextIsCapital = i + 1 < lines.Length &&
                                lines[i + 1].TrimStart().Length > 0 &&
                                char.IsUpper(lines[i + 1].TrimStart()[0]);

            if (endsWithPunctuation && (isShortLine || nextIsCapital))
            {
                result.AppendLine();
                result.AppendLine();
            }
            else if (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]))
            {
                result.Append(' ');
            }
            else
            {
                result.AppendLine();
            }
        }

        return result.ToString().Trim();
    }

    private static double CalculateConfidence(int textDensity)
    {
        // High text density = high confidence in native extraction
        if (textDensity >= 500) return 0.95;
        if (textDensity >= 300) return 0.85;
        if (textDensity >= 100) return 0.7;
        if (textDensity >= 50) return 0.5;
        return 0.3; // Very low density - likely needs OCR
    }
}
