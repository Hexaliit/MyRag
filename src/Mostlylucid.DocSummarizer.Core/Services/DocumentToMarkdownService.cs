using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
#if !SLIM_BUILD
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
#endif
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Core.Models;
using UglyToad.PdfPig;

#if !SLIM_BUILD
// Alias to avoid ambiguity with OpenXml types
using WordTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using WordTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using WordTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
#endif

namespace Mostlylucid.DocSummarizer.Core.Services;

/// <summary>
/// Converts documents (PDF, images, DOCX) to structured Markdown.
/// Uses native extraction for text-based documents, falls back to VLM OCR for scanned/complex documents.
/// </summary>
public class DocumentToMarkdownService
{
#if !SLIM_BUILD
    private readonly PdfPageRenderer _pdfRenderer;
#endif
    private readonly ITableExtractorFactory? _tableExtractorFactory;
    private readonly ILogger<DocumentToMarkdownService>? _logger;

    /// <summary>
    /// Minimum text density (chars per page) to consider native extraction successful.
    /// Below this threshold, VLM OCR is used instead.
    /// </summary>
    public int MinTextDensityForNative { get; set; } = 100;

    /// <summary>
    /// Whether to always use VLM OCR (skip native extraction).
    /// </summary>
    public bool ForceVlmOcr { get; set; } = false;

    /// <summary>
    /// Callback to process each rendered page image with VLM OCR.
    /// Receives image path, returns markdown text.
    /// </summary>
    public Func<string, CancellationToken, Task<VlmOcrResult>>? VlmOcrCallback { get; set; }

#if !SLIM_BUILD
    public DocumentToMarkdownService(
        PdfPageRenderer pdfRenderer,
        ITableExtractorFactory? tableExtractorFactory = null,
        ILogger<DocumentToMarkdownService>? logger = null)
    {
        _pdfRenderer = pdfRenderer;
        _tableExtractorFactory = tableExtractorFactory;
        _logger = logger;
    }
#else
    public DocumentToMarkdownService(
        ITableExtractorFactory? tableExtractorFactory = null,
        ILogger<DocumentToMarkdownService>? logger = null)
    {
        _tableExtractorFactory = tableExtractorFactory;
        _logger = logger;
    }
#endif

    /// <summary>
    /// Convert a document to Markdown.
    /// </summary>
    /// <param name="documentPath">Path to PDF, image, or DOCX</param>
    /// <param name="options">Conversion options</param>
    /// <param name="progress">Progress callback</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Conversion result with Markdown content</returns>
    public async Task<DocumentToMarkdownResult> ConvertAsync(
        string documentPath,
        ConversionOptions? options = null,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new ConversionOptions();
        var sw = Stopwatch.StartNew();

        var extension = Path.GetExtension(documentPath).ToLowerInvariant();

        _logger?.LogInformation("Converting {Document} to Markdown", Path.GetFileName(documentPath));

        try
        {
            var result = extension switch
            {
                ".pdf" => await ConvertPdfAsync(documentPath, options, progress, ct),
#if !SLIM_BUILD
                ".docx" => await ConvertDocxAsync(documentPath, options, progress, ct),
                ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tiff" or ".tif"
                    => await ConvertImageAsync(documentPath, options, progress, ct),
#endif
                _ => throw new NotSupportedException($"Unsupported file type: {extension}")
            };

            sw.Stop();
            result.ProcessingTime = sw.Elapsed;

            _logger?.LogInformation(
                "Converted {Document}: {Pages} pages, {Chars} chars, {Method} in {Ms}ms",
                Path.GetFileName(documentPath),
                result.PageCount,
                result.Markdown?.Length ?? 0,
                result.ExtractionMethod,
                sw.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to convert {Document}", documentPath);
            return new DocumentToMarkdownResult
            {
                SourcePath = documentPath,
                Success = false,
                Error = ex.Message,
                ProcessingTime = sw.Elapsed
            };
        }
    }

    /// <summary>
    /// Convert a PDF document to Markdown.
    /// </summary>
    private async Task<DocumentToMarkdownResult> ConvertPdfAsync(
        string pdfPath,
        ConversionOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken ct)
    {
        // First, check if native extraction works well
        var nativeResult = await TryNativeExtractionAsync(pdfPath, options, ct);

        if (!ForceVlmOcr && nativeResult.Success && nativeResult.HasSufficientText)
        {
            _logger?.LogDebug("Using native extraction for {Pdf}", Path.GetFileName(pdfPath));
            progress?.Report(new ConversionProgress
            {
                CurrentPage = nativeResult.PageCount,
                TotalPages = nativeResult.PageCount,
                Stage = "Native extraction complete"
            });
            return nativeResult;
        }

        // Fall back to VLM OCR
        _logger?.LogDebug("Falling back to VLM OCR for {Pdf} (native text density: {Density})",
            Path.GetFileName(pdfPath), nativeResult.AverageTextDensity);

        return await ConvertPdfWithVlmAsync(pdfPath, options, progress, ct);
    }

    /// <summary>
    /// Try native PDF text extraction.
    /// </summary>
    private async Task<DocumentToMarkdownResult> TryNativeExtractionAsync(
        string pdfPath,
        ConversionOptions options,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var totalChars = 0;
        var pageCount = 0;
        var tables = new List<ExtractedTable>();

        await Task.Run(() =>
        {
            using var document = PdfDocument.Open(pdfPath);
            pageCount = document.NumberOfPages;

            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                var text = page.Text;
                totalChars += text?.Length ?? 0;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (sb.Length > 0)
                        sb.AppendLine("\n---\n"); // Page separator

                    sb.AppendLine(text);
                }
            }
        }, ct);

        // Extract tables if factory available
        if (_tableExtractorFactory != null && options.ExtractTables)
        {
            var extractor = await _tableExtractorFactory.GetExtractorForFileAsync(pdfPath, ct);
            if (extractor != null)
            {
                var tableResult = await extractor.ExtractTablesAsync(pdfPath, ct: ct);
                tables = tableResult.Tables;

                // Append tables as markdown
                foreach (var table in tables)
                {
                    sb.AppendLine($"\n### Table {table.TableNumber} (Page {table.PageOrSection})\n");
                    sb.AppendLine(TableToMarkdown(table));
                }
            }
        }

        var avgDensity = pageCount > 0 ? totalChars / pageCount : 0;

        return new DocumentToMarkdownResult
        {
            SourcePath = pdfPath,
            Success = true,
            Markdown = sb.ToString(),
            PageCount = pageCount,
            ExtractionMethod = "native",
            AverageTextDensity = avgDensity,
            HasSufficientText = avgDensity >= MinTextDensityForNative,
            ExtractedTables = tables
        };
    }

    /// <summary>
    /// Convert PDF using VLM OCR on rendered pages.
    /// </summary>
    private async Task<DocumentToMarkdownResult> ConvertPdfWithVlmAsync(
        string pdfPath,
        ConversionOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken ct)
    {
        if (VlmOcrCallback == null)
        {
            return new DocumentToMarkdownResult
            {
                SourcePath = pdfPath,
                Success = false,
                Error = "VLM OCR callback not configured. Set VlmOcrCallback property."
            };
        }

#if SLIM_BUILD
        // Slim build: VLM OCR not available (no PDFtoImage)
        return new DocumentToMarkdownResult
        {
            SourcePath = pdfPath,
            Success = false,
            Error = "VLM OCR requires the complete build (PDF page rendering not available in slim mode)."
        };
    }
#else
        // Render all pages
        var renderedPages = await _pdfRenderer.RenderAllPagesAsync(
            pdfPath,
            options.RenderDpi,
            new Progress<(int current, int total)>(p =>
            {
                progress?.Report(new ConversionProgress
                {
                    CurrentPage = p.current,
                    TotalPages = p.total,
                    Stage = "Rendering pages"
                });
            }),
            ct);

        try
        {
            var sb = new StringBuilder();
            var pageResults = new List<PageOcrResult>();

            // Process each page with VLM OCR
            for (int i = 0; i < renderedPages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var page = renderedPages[i];
                progress?.Report(new ConversionProgress
                {
                    CurrentPage = i + 1,
                    TotalPages = renderedPages.Count,
                    Stage = $"OCR page {i + 1}"
                });

                var vlmResult = await VlmOcrCallback(page.ImagePath, ct);

                if (vlmResult.Success && !string.IsNullOrWhiteSpace(vlmResult.Markdown))
                {
                    if (sb.Length > 0)
                        sb.AppendLine("\n---\n"); // Page separator

                    sb.AppendLine(vlmResult.Markdown);

                    pageResults.Add(new PageOcrResult
                    {
                        PageNumber = page.PageNumber,
                        Markdown = vlmResult.Markdown,
                        Model = vlmResult.Model,
                        Confidence = vlmResult.Confidence
                    });
                }
            }

            return new DocumentToMarkdownResult
            {
                SourcePath = pdfPath,
                Success = true,
                Markdown = sb.ToString(),
                PageCount = renderedPages.Count,
                ExtractionMethod = "vlm_ocr",
                PageResults = pageResults,
                HasSufficientText = sb.Length > 0
            };
        }
        finally
        {
            // Clean up rendered images
            _pdfRenderer.CleanupRenderedPages(renderedPages);
        }
    }
#endif

#if !SLIM_BUILD
    /// <summary>
    /// Convert a DOCX document to Markdown.
    /// </summary>
    private async Task<DocumentToMarkdownResult> ConvertDocxAsync(
        string docxPath,
        ConversionOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new ConversionProgress
        {
            CurrentPage = 1,
            TotalPages = 1,
            Stage = "Extracting DOCX content"
        });

        var sb = new StringBuilder();
        var tables = new List<ExtractedTable>();
        string? title = null;
        var totalChars = 0;

        await Task.Run(() =>
        {
            using var doc = WordprocessingDocument.Open(docxPath, false);

            // Extract title from document properties
            title = doc.PackageProperties?.Title;

            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null)
                return;

            var listState = new ListProcessingState();

            foreach (var element in body.ChildElements)
            {
                ct.ThrowIfCancellationRequested();

                switch (element)
                {
                    case Paragraph para:
                        var markdown = ConvertParagraphToMarkdown(para, doc.MainDocumentPart!, listState);
                        if (!string.IsNullOrEmpty(markdown))
                        {
                            sb.AppendLine(markdown);
                            totalChars += markdown.Length;
                        }
                        break;

                    case WordTable table:
                        // Close any open list
                        if (listState.IsInList)
                        {
                            listState.Reset();
                            sb.AppendLine();
                        }
                        var tableMarkdown = ConvertTableToMarkdown(table);
                        if (!string.IsNullOrEmpty(tableMarkdown))
                        {
                            sb.AppendLine();
                            sb.AppendLine(tableMarkdown);
                            sb.AppendLine();
                            totalChars += tableMarkdown.Length;
                        }
                        break;

                    case SdtBlock sdtBlock:
                        // Handle structured document tags (table of contents, etc.)
                        foreach (var sdtContent in sdtBlock.Descendants<Paragraph>())
                        {
                            var sdtMarkdown = ConvertParagraphToMarkdown(sdtContent, doc.MainDocumentPart!, listState);
                            if (!string.IsNullOrEmpty(sdtMarkdown))
                            {
                                sb.AppendLine(sdtMarkdown);
                                totalChars += sdtMarkdown.Length;
                            }
                        }
                        break;
                }
            }
        }, ct);

        // Also extract tables using the table extractor for more structured data
        if (_tableExtractorFactory != null && options.ExtractTables)
        {
            var extractor = await _tableExtractorFactory.GetExtractorForFileAsync(docxPath, ct);
            if (extractor != null)
            {
                var tableResult = await extractor.ExtractTablesAsync(docxPath, ct: ct);
                tables = tableResult.Tables;
            }
        }

        // Estimate page count (rough approximation: ~3000 chars per page)
        var estimatedPages = Math.Max(1, totalChars / 3000);

        return new DocumentToMarkdownResult
        {
            SourcePath = docxPath,
            Success = true,
            Markdown = sb.ToString().Trim(),
            PageCount = estimatedPages,
            ExtractionMethod = "native",
            ExtractedTables = tables,
            HasSufficientText = totalChars > 0,
            AverageTextDensity = estimatedPages > 0 ? totalChars / estimatedPages : totalChars
        };
    }

    /// <summary>
    /// Convert a Word paragraph to Markdown.
    /// </summary>
    private string ConvertParagraphToMarkdown(Paragraph para, MainDocumentPart mainPart, ListProcessingState listState)
    {
        var text = GetParagraphText(para, mainPart);
        if (string.IsNullOrWhiteSpace(text))
        {
            // Empty paragraph - might end a list
            if (listState.IsInList)
            {
                listState.ConsecutiveEmptyLines++;
                if (listState.ConsecutiveEmptyLines >= 2)
                {
                    listState.Reset();
                }
            }
            return "";
        }

        listState.ConsecutiveEmptyLines = 0;

        var props = para.ParagraphProperties;
        var styleId = props?.ParagraphStyleId?.Val?.Value;

        // Check for headings
        if (!string.IsNullOrEmpty(styleId))
        {
            var headingLevel = GetHeadingLevel(styleId);
            if (headingLevel > 0)
            {
                if (listState.IsInList)
                    listState.Reset();
                return new string('#', headingLevel) + " " + text;
            }
        }

        // Check for list items
        var numProps = props?.NumberingProperties;
        if (numProps != null)
        {
            var numId = numProps.NumberingId?.Val?.Value;
            var ilvl = numProps.NumberingLevelReference?.Val?.Value ?? 0;

            if (numId.HasValue)
            {
                var isOrdered = IsOrderedList(mainPart, numId.Value, ilvl);
                var indent = new string(' ', ilvl * 2);

                if (!listState.IsInList || listState.NumberingId != numId.Value)
                {
                    listState.IsInList = true;
                    listState.NumberingId = numId.Value;
                    listState.ItemCounts.Clear();
                }

                if (isOrdered)
                {
                    if (!listState.ItemCounts.ContainsKey(ilvl))
                        listState.ItemCounts[ilvl] = 0;
                    listState.ItemCounts[ilvl]++;
                    return $"{indent}{listState.ItemCounts[ilvl]}. {text}";
                }
                else
                {
                    return $"{indent}- {text}";
                }
            }
        }

        // Regular paragraph
        if (listState.IsInList)
            listState.Reset();

        return text;
    }

    /// <summary>
    /// Get formatted text from a paragraph, handling runs with formatting.
    /// </summary>
    private string GetParagraphText(Paragraph para, MainDocumentPart mainPart)
    {
        var sb = new StringBuilder();

        foreach (var element in para.ChildElements)
        {
            switch (element)
            {
                case Run run:
                    var runText = GetRunText(run, mainPart);
                    sb.Append(runText);
                    break;

                case Hyperlink hyperlink:
                    var linkText = GetHyperlinkMarkdown(hyperlink, mainPart);
                    sb.Append(linkText);
                    break;

                case BookmarkStart:
                case BookmarkEnd:
                case ProofError:
                    // Skip these elements
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Get formatted text from a run element.
    /// </summary>
    private string GetRunText(Run run, MainDocumentPart mainPart)
    {
        var sb = new StringBuilder();

        foreach (var element in run.ChildElements)
        {
            switch (element)
            {
                case Text text:
                    sb.Append(text.Text);
                    break;

                case Break br:
                    if (br.Type?.Value == BreakValues.Page)
                        sb.Append("\n\n---\n\n");
                    else
                        sb.Append("\n");
                    break;

                case TabChar:
                    sb.Append("    ");
                    break;

                case Drawing drawing:
                    // Handle images
                    var imageMarkdown = GetImageMarkdown(drawing, mainPart);
                    if (!string.IsNullOrEmpty(imageMarkdown))
                        sb.Append(imageMarkdown);
                    break;

                case SymbolChar symbol:
                    // Handle special symbols
                    sb.Append(GetSymbolChar(symbol));
                    break;
            }
        }

        var text2 = sb.ToString();
        if (string.IsNullOrEmpty(text2))
            return "";

        // Apply formatting
        var props = run.RunProperties;
        if (props != null)
        {
            var isBold = props.Bold != null && (props.Bold.Val == null || props.Bold.Val.Value);
            var isItalic = props.Italic != null && (props.Italic.Val == null || props.Italic.Val.Value);
            var isStrike = props.Strike != null && (props.Strike.Val == null || props.Strike.Val.Value);
            var isCode = props.GetFirstChild<Shading>()?.Fill?.Value == "E6E6E6" ||
                         props.GetFirstChild<RunFonts>()?.Ascii?.Value?.Contains("Consolas") == true ||
                         props.GetFirstChild<RunFonts>()?.Ascii?.Value?.Contains("Courier") == true;

            if (isCode)
                text2 = $"`{text2}`";
            if (isBold && isItalic)
                text2 = $"***{text2}***";
            else if (isBold)
                text2 = $"**{text2}**";
            else if (isItalic)
                text2 = $"*{text2}*";
            if (isStrike)
                text2 = $"~~{text2}~~";
        }

        return text2;
    }

    /// <summary>
    /// Get markdown for a hyperlink.
    /// </summary>
    private string GetHyperlinkMarkdown(Hyperlink hyperlink, MainDocumentPart mainPart)
    {
        var text = string.Join("", hyperlink.Descendants<Text>().Select(t => t.Text));
        if (string.IsNullOrEmpty(text))
            return "";

        var relId = hyperlink.Id?.Value;
        if (!string.IsNullOrEmpty(relId))
        {
            var rel = mainPart.HyperlinkRelationships.FirstOrDefault(r => r.Id == relId);
            if (rel != null)
            {
                return $"[{text}]({rel.Uri})";
            }
        }

        // Internal bookmark link
        var anchor = hyperlink.Anchor?.Value;
        if (!string.IsNullOrEmpty(anchor))
        {
            return $"[{text}](#{anchor})";
        }

        return text;
    }

    /// <summary>
    /// Get markdown for an embedded image.
    /// </summary>
    private string GetImageMarkdown(Drawing drawing, MainDocumentPart mainPart)
    {
        var blip = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault();
        if (blip?.Embed?.Value == null)
            return "";

        var imagePart = mainPart.GetPartById(blip.Embed.Value) as ImagePart;
        if (imagePart == null)
            return "";

        // Get image description/alt text
        var docProperties = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().FirstOrDefault();
        var altText = docProperties?.Description?.Value ?? docProperties?.Name?.Value ?? "image";

        // For embedded images, we can't easily extract them to markdown
        // Return a placeholder with the alt text
        return $"![{EscapeMarkdown(altText)}](embedded-image)";
    }

    /// <summary>
    /// Convert a Word table to Markdown.
    /// </summary>
    private string ConvertTableToMarkdown(WordTable table)
    {
        var rows = table.Elements<WordTableRow>().ToList();
        if (rows.Count == 0)
            return "";

        var sb = new StringBuilder();
        var isFirstRow = true;

        foreach (var row in rows)
        {
            var cells = row.Elements<WordTableCell>().ToList();
            var cellTexts = cells.Select(c => GetCellText(c)).ToList();

            sb.AppendLine("| " + string.Join(" | ", cellTexts) + " |");

            if (isFirstRow)
            {
                // Add separator row
                sb.AppendLine("| " + string.Join(" | ", cells.Select(_ => "---")) + " |");
                isFirstRow = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get text content from a table cell.
    /// </summary>
    private string GetCellText(WordTableCell cell)
    {
        var texts = new List<string>();

        foreach (var para in cell.Elements<Paragraph>())
        {
            var text = string.Join("", para.Descendants<Text>().Select(t => t.Text));
            if (!string.IsNullOrWhiteSpace(text))
                texts.Add(text.Trim());
        }

        var result = string.Join(" ", texts);
        // Escape pipe characters in table cells
        return result.Replace("|", "\\|");
    }

    /// <summary>
    /// Get heading level from style ID (e.g., "Heading1" → 1).
    /// </summary>
    private static int GetHeadingLevel(string styleId)
    {
        if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            var levelStr = styleId.Substring(7);
            if (int.TryParse(levelStr, out var level) && level >= 1 && level <= 6)
                return level;
        }

        // Also check for Title style
        if (styleId.Equals("Title", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (styleId.Equals("Subtitle", StringComparison.OrdinalIgnoreCase))
            return 2;

        return 0;
    }

    /// <summary>
    /// Check if a numbering definition is an ordered list.
    /// </summary>
    private static bool IsOrderedList(MainDocumentPart mainPart, int numId, int level)
    {
        var numberingPart = mainPart.NumberingDefinitionsPart;
        if (numberingPart == null)
            return false;

        var numbering = numberingPart.Numbering;
        var numInstance = numbering?.Elements<NumberingInstance>()
            .FirstOrDefault(n => n.NumberID?.Value == numId);

        if (numInstance == null)
            return false;

        var abstractNumId = numInstance.AbstractNumId?.Val?.Value;
        if (!abstractNumId.HasValue)
            return false;

        var abstractNum = numbering?.Elements<AbstractNum>()
            .FirstOrDefault(a => a.AbstractNumberId?.Value == abstractNumId.Value);

        var lvl = abstractNum?.Elements<Level>()
            .FirstOrDefault(l => l.LevelIndex?.Value == level);

        var numFmt = lvl?.NumberingFormat?.Val?.Value;

        // Ordered list formats
        return numFmt == NumberFormatValues.Decimal ||
               numFmt == NumberFormatValues.UpperLetter ||
               numFmt == NumberFormatValues.LowerLetter ||
               numFmt == NumberFormatValues.UpperRoman ||
               numFmt == NumberFormatValues.LowerRoman;
    }

    /// <summary>
    /// Get character representation for a symbol.
    /// </summary>
    private static string GetSymbolChar(SymbolChar symbol)
    {
        // Common symbol mappings
        return symbol.Char?.Value switch
        {
            "F0B7" => "•",  // Bullet
            "F0A7" => "§",  // Section
            "F0D8" => "→",  // Arrow
            _ => "•"
        };
    }

    /// <summary>
    /// Escape special markdown characters.
    /// </summary>
    private static string EscapeMarkdown(string text)
    {
        return Regex.Replace(text, @"([\\`*_\[\]()#+\-.!])", @"\$1");
    }

    /// <summary>
    /// State for tracking list processing.
    /// </summary>
    private class ListProcessingState
    {
        public bool IsInList { get; set; }
        public int NumberingId { get; set; }
        public Dictionary<int, int> ItemCounts { get; } = new();
        public int ConsecutiveEmptyLines { get; set; }

        public void Reset()
        {
            IsInList = false;
            NumberingId = 0;
            ItemCounts.Clear();
            ConsecutiveEmptyLines = 0;
        }
    }

    /// <summary>
    /// Convert an image to Markdown using VLM OCR.
    /// </summary>
    private async Task<DocumentToMarkdownResult> ConvertImageAsync(
        string imagePath,
        ConversionOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken ct)
    {
        if (VlmOcrCallback == null)
        {
            return new DocumentToMarkdownResult
            {
                SourcePath = imagePath,
                Success = false,
                Error = "VLM OCR callback not configured. Set VlmOcrCallback property."
            };
        }

        progress?.Report(new ConversionProgress
        {
            CurrentPage = 1,
            TotalPages = 1,
            Stage = "Processing image"
        });

        var vlmResult = await VlmOcrCallback(imagePath, ct);

        return new DocumentToMarkdownResult
        {
            SourcePath = imagePath,
            Success = vlmResult.Success,
            Markdown = vlmResult.Markdown,
            PageCount = 1,
            ExtractionMethod = "vlm_ocr",
            Error = vlmResult.Error,
            HasSufficientText = !string.IsNullOrWhiteSpace(vlmResult.Markdown),
            PageResults = vlmResult.Success ? new List<PageOcrResult>
            {
                new()
                {
                    PageNumber = 1,
                    Markdown = vlmResult.Markdown ?? "",
                    Model = vlmResult.Model,
                    Confidence = vlmResult.Confidence
                }
            } : null
        };
    }
#endif

    /// <summary>
    /// Convert an extracted table to Markdown format.
    /// </summary>
    private static string TableToMarkdown(ExtractedTable table)
    {
        if (table.Rows.Count == 0)
            return "";

        var sb = new StringBuilder();

        // Header row
        var headerRow = table.Rows[0];
        sb.AppendLine("| " + string.Join(" | ", headerRow.Select(c => c.Text ?? "")) + " |");

        // Separator
        sb.AppendLine("| " + string.Join(" | ", headerRow.Select(_ => "---")) + " |");

        // Data rows
        for (int i = 1; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            sb.AppendLine("| " + string.Join(" | ", row.Select(c => c.Text ?? "")) + " |");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Options for document to Markdown conversion.
/// </summary>
public class ConversionOptions
{
    /// <summary>DPI for rendering PDF pages (default: 300)</summary>
    public int RenderDpi { get; set; } = 300;

    /// <summary>Extract tables from documents</summary>
    public bool ExtractTables { get; set; } = true;

    /// <summary>Specific pages to process (null = all)</summary>
    public List<int>? Pages { get; set; }

    /// <summary>Preferred VLM model for OCR</summary>
    public string? PreferredModel { get; set; }
}

/// <summary>
/// Progress information during conversion.
/// </summary>
public class ConversionProgress
{
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public string Stage { get; set; } = "";
    public double ProgressPercent => TotalPages > 0 ? (double)CurrentPage / TotalPages * 100 : 0;
}

/// <summary>
/// Result of document to Markdown conversion.
/// </summary>
public class DocumentToMarkdownResult
{
    public string? SourcePath { get; init; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Markdown { get; set; }
    public int PageCount { get; set; }
    public string ExtractionMethod { get; set; } = "unknown";
    public TimeSpan ProcessingTime { get; set; }
    public int AverageTextDensity { get; set; }
    public bool HasSufficientText { get; set; }
    public List<ExtractedTable>? ExtractedTables { get; set; }
    public List<PageOcrResult>? PageResults { get; set; }
}

/// <summary>
/// Result of VLM OCR on a single image.
/// </summary>
public class VlmOcrResult
{
    public bool Success { get; set; }
    public string? Markdown { get; set; }
    public string? Error { get; set; }
    public string? Model { get; set; }
    public double Confidence { get; set; }
}

/// <summary>
/// OCR result for a single page.
/// </summary>
public class PageOcrResult
{
    public int PageNumber { get; set; }
    public string Markdown { get; set; } = "";
    public string? Model { get; set; }
    public double Confidence { get; set; }
}
