using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Plugins.Runtime;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
/// Document type classification for adaptive retrieval and template selection.
/// Mirrors FrontMatterDetector.DocumentProfileType but lightweight (no LLM dependency).
/// </summary>
public enum IngestDocumentType
{
    Unknown,
    Fiction,
    NonFiction,
    Academic,
    Technical
}

/// <summary>
/// Local file/folder ingestion for the scroll command.
/// When -s points to a file or folder, or the prompt is a file path,
/// we auto-ingest into a named collection then query against it.
/// </summary>
public sealed partial class ScrollCommand
{
    /// <summary>
    /// Classify document type from early content using heuristic scoring.
    /// Same approach as FrontMatterDetector.DetectDocumentType but standalone.
    /// </summary>
    internal static IngestDocumentType DetectDocumentType(string earlyContent)
    {
        if (string.IsNullOrWhiteSpace(earlyContent))
            return IngestDocumentType.Unknown;

        var lower = earlyContent.ToLowerInvariant();

        var fictionScore = 0;
        var academicScore = 0;
        var technicalScore = 0;
        var nonfictionScore = 0;

        // Fiction indicators
        if (Regex.IsMatch(lower, @"\bchapter\s+(one|two|three|four|five|1|2|3|4|5|i|ii|iii|iv|v)\b"))
            fictionScore += 5;
        if (Regex.IsMatch(lower, @"\b(he|she)\s+(walked|looked|said|thought|felt|turned|whispered|smiled|sighed)\b"))
            fictionScore += 3;
        if (Regex.IsMatch(lower, @"""[^""]{5,80}[,.]""\s*\w+\s+(said|asked|replied|whispered|exclaimed)"))
            fictionScore += 5;
        if (Regex.IsMatch(lower, @"\b(mr\.|mrs\.|miss|sir|lady|lord|captain)\s+[a-z]"))
            fictionScore += 2;

        // Academic indicators
        if (lower.Contains("abstract") && lower.Contains("keywords"))
            academicScore += 8;
        if (lower.Contains("in partial fulfillment") || lower.Contains("thesis committee"))
            academicScore += 10;
        if (Regex.IsMatch(lower, @"\[\d+\]") && lower.Contains("references"))
            academicScore += 5;
        if (lower.Contains("methodology") || lower.Contains("literature review"))
            academicScore += 3;

        // Technical indicators
        if (lower.Contains("installation") || lower.Contains("getting started"))
            technicalScore += 5;
        if (lower.Contains("```") && (lower.Contains("api") || lower.Contains("configuration")))
            technicalScore += 5;
        if (lower.Contains("docker") || lower.Contains("npm") || lower.Contains("nuget"))
            technicalScore += 3;

        // Non-fiction book indicators
        if (lower.Contains("isbn") || lower.Contains("published by"))
            nonfictionScore += 5;
        if (lower.Contains("foreword") || lower.Contains("preface") || lower.Contains("introduction"))
            nonfictionScore += 3;

        var best = new[] {
            (IngestDocumentType.Fiction, fictionScore),
            (IngestDocumentType.Academic, academicScore),
            (IngestDocumentType.Technical, technicalScore),
            (IngestDocumentType.NonFiction, nonfictionScore)
        }.OrderByDescending(x => x.Item2).First();

        return best.Item2 >= 3 ? best.Item1 : IngestDocumentType.Unknown;
    }
    /// <summary>
    /// Detect whether any of the given paths are local files or directories.
    /// Returns the list of resolved file paths and a suggested collection name.
    /// </summary>
    internal static (List<string> files, string collectionName) ResolveLocalSources(
        string[] sources, string? explicitName)
    {
        var files = new List<string>();

        foreach (var source in sources)
        {
            if (File.Exists(source))
            {
                files.Add(Path.GetFullPath(source));
            }
            else if (Directory.Exists(source))
            {
                // Base extensions (always available, even slim CLI)
                var baseExtensions = new[] { ".pdf", ".docx", ".md", ".txt", ".html", ".pptx" };

                // Plugin extensions (from all registered processor plugins)
                var pluginExtensions = PluginDiscovery.DiscoverAllProcessorPlugins()
                    .SelectMany(p => p.Metadata.SupportedExtensions);

                // Union for directory scanning
                var extensions = baseExtensions
                    .Union(pluginExtensions, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.StartsWith('.') ? $"*{e}" : e)
                    .ToArray();

                foreach (var ext in extensions)
                    files.AddRange(Directory.EnumerateFiles(source, ext, SearchOption.AllDirectories));
            }
        }

        if (files.Count == 0)
            return (files, "");

        // Derive collection name: explicit --name, or auto from path
        var name = explicitName
                   ?? CollectionNaming.Auto(sources.First(s => File.Exists(s) || Directory.Exists(s)));

        return (files, name);
    }

    /// <summary>
    /// Ingest local files into a named collection.
    /// Extracts text, chunks by page/section, computes embeddings, stores in SQLite + Lucene FTS.
    /// Returns the source filter string, item count, and detected document type.
    /// </summary>
    internal static async Task<(string sourceFilter, int itemCount, IngestDocumentType docType)> IngestLocalFilesAsync(
        List<string> files,
        string collectionName,
        CommandBootstrap boot,
        ProgressTask progressTask,
        bool force,
        CancellationToken ct)
    {
        var sourceTag = $"file:{collectionName}";

        // Skip ingestion if collection already has items (use --force to re-ingest)
        var existing = await boot.Storage.GetRecentItemsAsync(days: 36500, source: sourceTag);
        if (existing.Count > 0 && !force)
        {
            // Detect document type from cached content
            var sampleText = string.Join("\n", existing.Take(5)
                .Select(i => $"{i.Title} {i.Content ?? i.Summary ?? ""}"));
            if (sampleText.Length > 5000) sampleText = sampleText[..5000];
            var cachedDocType = DetectDocumentType(sampleText);

            progressTask.Value = 100;
            progressTask.Description = $"[green]Collection '{collectionName}' already has {existing.Count} segments (use --force to re-ingest)[/]";
            return (sourceTag, existing.Count, cachedDocType);
        }

        var processor = await ItemProcessor.CreateAsync(boot.Embedding, boot.Storage, ct: ct);

        // Set up document handlers
        var handlers = new Mostlylucid.DocSummarizer.Services.DocumentHandlerRegistry();
        handlers.RegisterDefaultHandlers();

        var totalIngested = 0;
        var increment = files.Count > 0 ? 80.0 / files.Count : 80.0;
        var detectedDocType = IngestDocumentType.Unknown;
        var docTypeDetected = false;

        // Set up Lucene index for the collection
        var lucenePath = Path.Combine(boot.Storage.DataPath, "lucene", collectionName);
        Directory.CreateDirectory(lucenePath);
        using var lucene = new LuceneSearchService(lucenePath);
        lucene.Open();

        // Collect all items for batch embedding
        var pendingItems = new List<(ContentItem item, string embedText)>();

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var handler = handlers.GetHandler(ext);
            if (handler == null)
            {
                progressTask.Increment(increment);
                continue;
            }

            progressTask.Description = $"[cyan]Ingesting {Path.GetFileName(filePath)}[/]";

            try
            {
                var options = new DocumentHandlerOptions { CancellationToken = ct };
                var content = await handler.ProcessAsync(filePath, options);

                if (string.IsNullOrWhiteSpace(content.Markdown))
                {
                    progressTask.Increment(increment);
                    continue;
                }

                // Detect document type from the first file's content (once)
                if (!docTypeDetected)
                {
                    var sample = content.Markdown.Length > 5000 ? content.Markdown[..5000] : content.Markdown;
                    detectedDocType = DetectDocumentType(sample);
                    docTypeDetected = true;
                }

                // Chunk the content with adaptive size based on document type
                var chunks = ChunkDocument(content.Markdown, content.Title, filePath, detectedDocType);

                foreach (var chunk in chunks)
                {
                    var item = new ContentItem
                    {
                        Id = $"file:{collectionName}:{GenerateChunkId(filePath, chunk.index)}",
                        Source = sourceTag,
                        Title = chunk.title,
                        Url = $"file://{filePath}",
                        Content = chunk.text,
                        Summary = chunk.text.Length > 300 ? chunk.text[..300] + "..." : chunk.text,
                        IsEnriched = true,
                        CreatedAt = File.GetCreationTimeUtc(filePath),
                        FetchedAt = DateTimeOffset.UtcNow
                    };

                    // TreeRAG: include hierarchical context in embedding text
                    // This helps embeddings capture section-level meaning
                    var textToEmbed = $"{chunk.title}: {chunk.text}".Trim();
                    if (textToEmbed.Length > 1500)
                        textToEmbed = textToEmbed[..1500];

                    pendingItems.Add((item, textToEmbed));
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Failed to process {Markup.Escape(Path.GetFileName(filePath))}: {Markup.Escape(ex.Message)}[/]");
            }

            progressTask.Increment(increment);
        }

        // Batch embed all chunks (much faster than sequential single calls)
        if (pendingItems.Count > 0)
        {
            progressTask.Description = $"[cyan]Computing embeddings for {pendingItems.Count} chunks[/]";
            var texts = pendingItems.Select(p => p.embedText).ToList();
            var embeddings = await boot.Embedding.EmbedBatchAsync(texts, ct);

            for (var i = 0; i < pendingItems.Count; i++)
            {
                var (item, _) = pendingItems[i];
                if (i < embeddings.Length)
                    item.Embedding = embeddings[i];

                processor.ScoreSentimentAndTopic(item);
                await processor.IndexItemAsync(item);
                lucene.IndexItem(item);
                totalIngested++;
            }
        }

        lucene.Commit();

        // NER entity extraction: extract character names, locations, organizations from chunks.
        // For fiction, this populates the knowledge graph with character names so queries about
        // "characters" can leverage entity-aware retrieval.
        var entityCount = 0;
        if (boot.EntityStore != null && pendingItems.Count > 0)
        {
            try
            {
                using var nerService = new NerService();
                if (nerService.IsAvailable)
                {
                    await nerService.InitializeAsync(ct);
                    progressTask.Description = $"[cyan]Extracting entities from {pendingItems.Count} chunks[/]";

                    foreach (var (item, _) in pendingItems)
                    {
                        ct.ThrowIfCancellationRequested();
                        var textForNer = $"{item.Title} {item.Content?[..Math.Min(item.Content.Length, 2000)] ?? ""}";
                        var entities = await nerService.ExtractEntitiesAsync(textForNer, ct);
                        if (entities.Count > 0)
                        {
                            await processor.PersistEntitiesAsync(item, entities);
                            entityCount += entities.Count;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]NER extraction skipped: {Markup.Escape(ex.Message)}[/]");
            }
        }

        progressTask.Value = 100;
        var typeLabel = detectedDocType != IngestDocumentType.Unknown ? $" ({detectedDocType})" : "";
        var entityLabel = entityCount > 0 ? $", {entityCount} entities" : "";
        progressTask.Description = $"[green]Ingested {totalIngested} segments from {files.Count} file(s){typeLabel}{entityLabel}[/]";

        return (sourceTag, totalIngested, detectedDocType);
    }

    /// <summary>
    /// Split a document into retrieval-sized chunks.
    /// Uses page markers for PDFs, heading/paragraph splits for text.
    /// Chunk size adapts to document type: books use larger chunks for narrative continuity.
    /// </summary>
    private static List<(string title, string text, int index)> ChunkDocument(
        string markdown, string docTitle, string filePath,
        IngestDocumentType docType = IngestDocumentType.Unknown)
    {
        var chunks = new List<(string title, string text, int index)>();

        // Books need larger chunks to preserve narrative context
        var targetChunkChars = docType is IngestDocumentType.Fiction or IngestDocumentType.NonFiction
            ? 5000 : 2000;
        const int minChunkChars = 200;

        // Try page-based chunking (PDFs have <!-- PAGE:N --> markers)
        var pages = SplitByPageMarkers(markdown);
        if (pages.Count > 1)
        {
            // Group pages into chunks of ~targetChunkChars
            var currentText = new StringBuilder();
            var startPage = 1;
            var chunkIdx = 0;

            for (var i = 0; i < pages.Count; i++)
            {
                if (currentText.Length > 0 && currentText.Length + pages[i].Length > targetChunkChars)
                {
                    // Emit current chunk
                    var endPage = startPage + (i - 1 >= 0 ? 1 : 0);
                    var title = startPage == endPage
                        ? $"{docTitle} (p.{startPage})"
                        : $"{docTitle} (p.{startPage}-{startPage + chunkIdx})";
                    chunks.Add((title, currentText.ToString().Trim(), chunks.Count));
                    currentText.Clear();
                    startPage = i + 1;
                    chunkIdx = 0;
                }

                currentText.AppendLine(pages[i]);
                chunkIdx++;
            }

            // Remaining text
            if (currentText.Length > minChunkChars)
            {
                chunks.Add(($"{docTitle} (p.{startPage}+)", currentText.ToString().Trim(), chunks.Count));
            }

            return chunks;
        }

        // Paragraph-based chunking for non-PDF documents
        var paragraphs = markdown.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var buffer = new StringBuilder();
        var currentHeading = docTitle;

        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Track headings for chunk titles
            if (trimmed.StartsWith('#'))
            {
                var headingText = trimmed.TrimStart('#').Trim();
                if (!string.IsNullOrEmpty(headingText))
                    currentHeading = headingText;
            }

            if (buffer.Length > 0 && buffer.Length + trimmed.Length > targetChunkChars)
            {
                chunks.Add((currentHeading, buffer.ToString().Trim(), chunks.Count));
                buffer.Clear();
            }

            buffer.AppendLine(trimmed);
            buffer.AppendLine();
        }

        if (buffer.Length > minChunkChars)
        {
            chunks.Add((currentHeading, buffer.ToString().Trim(), chunks.Count));
        }

        // If nothing was chunked, store the whole document
        if (chunks.Count == 0 && markdown.Length > minChunkChars)
        {
            chunks.Add((docTitle, markdown.Trim(), 0));
        }

        return chunks;
    }

    /// <summary>
    /// Split markdown by <!-- PAGE:N --> markers.
    /// </summary>
    private static List<string> SplitByPageMarkers(string markdown)
    {
        var pages = new List<string>();
        var parts = System.Text.RegularExpressions.Regex.Split(markdown, @"<!--\s*PAGE:\d+\s*-->");

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                pages.Add(trimmed);
        }

        return pages;
    }

    private static string GenerateChunkId(string filePath, int chunkIndex)
    {
        var input = $"{filePath}:{chunkIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
